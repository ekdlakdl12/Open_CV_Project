using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using WpfApp1.Models;
using WpfApp1.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Linq;
using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Collections.Concurrent;

namespace WpfApp1.ViewModels
{
    public class MainWindowViewModel : ViewModelBase, IDisposable
    {
        private const string ClassOnnxPath = "Scripts/best.onnx";
        private readonly VideoPlayerService _video = new();
        private YoloDetectService? _detector;
        private InferenceSession? _classSession;
        private readonly object _sessionLock = new object();

        // [팩트체크] 학습 리스트 순서
        private readonly string[] _carModelNames = {
            "Sonata", "Elantra", "Sorento", "Optima", "Accent",
            "Sportage", "Santa Fe", "Genesis", "Carnival", "Veloster"
        };

        private readonly ConcurrentDictionary<int, string> _modelCache = new();
        private readonly Dictionary<int, TrackedObject> _trackedObjects = new();
        private List<Detection> _currentDetections = new();
        private readonly Mat _frame = new();
        private volatile bool _isBusy = false;
        private readonly DispatcherTimer _timer = new();

        private string _countText = "L:0 | F:0 | R:0";
        public string CountText { get => _countText; set { _countText = value; OnPropertyChanged(); } }

        private BitmapSource? _frameImage;
        public BitmapSource? FrameImage { get => _frameImage; set { _frameImage = value; OnPropertyChanged(); } }

        public RelayCommand OpenVideoCommand { get; }

        public MainWindowViewModel()
        {
            OpenVideoCommand = new RelayCommand(OpenVideo);
            _timer.Tick += Timer_Tick;
            _timer.Interval = TimeSpan.FromMilliseconds(1);
            InitializeDetectors();
        }

        private void InitializeDetectors()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string detPath = Path.Combine(baseDir, "Scripts/yolov8n.onnx");
            if (File.Exists(detPath)) _detector = new YoloDetectService(detPath, 640, 0.25f, 0.45f);

            string clsPath = Path.Combine(baseDir, ClassOnnxPath);
            if (File.Exists(clsPath))
            {
                try { _classSession = new InferenceSession(clsPath); } catch { }
            }
        }

        private string GetSpecificCarModel(Mat cropImg)
        {
            if (_classSession == null) return "Unknown";
            try
            {
                using var resized = new Mat();
                Cv2.Resize(cropImg, resized, new Size(640, 640));
                Cv2.CvtColor(resized, resized, ColorConversionCodes.BGR2RGB);

                var input = new DenseTensor<float>(new[] { 1, 3, 640, 640 });
                var indexer = resized.GetGenericIndexer<Vec3b>();
                for (int y = 0; y < 640; y++)
                    for (int x = 0; x < 640; x++)
                    {
                        var p = indexer[y, x];
                        input[0, 0, y, x] = p.Item0 / 255f;
                        input[0, 1, y, x] = p.Item1 / 255f;
                        input[0, 2, y, x] = p.Item2 / 255f;
                    }

                lock (_sessionLock)
                {
                    using var results = _classSession.Run(new[] { NamedOnnxValue.CreateFromTensor(_classSession.InputMetadata.Keys.First(), input) });
                    var output = results.First().AsTensor<float>();

                    float maxScore = -1f;
                    int bestClassIdx = -1;

                    // [수정] 벨로스터 쏠림 방지: 텐서 구조를 더 엄격하게 순회
                    // 1x14x8400 구조에서 4번 인덱스부터가 클래스 점수
                    for (int i = 0; i < 8400; i++)
                    {
                        for (int c = 0; c < 10; c++)
                        {
                            float score = output[0, c + 4, i];
                            if (score > maxScore)
                            {
                                maxScore = score;
                                bestClassIdx = c;
                            }
                        }
                    }

                    if (bestClassIdx < 0 || maxScore < 0.15f) return "Unknown";
                    return _carModelNames[bestClassIdx];
                }
            }
            catch { return "Unknown"; }
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (!_video.Read(_frame)) return;

            if (!_isBusy && _detector != null)
            {
                _isBusy = true;
                Mat clone = _frame.Clone();
                double time = _video.PosMsec;
                Task.Run(() => {
                    try
                    {
                        var dets = _detector.Detect(clone);
                        lock (_trackedObjects)
                        {
                            TrackAndMatch(dets, time); // 속도 계산을 위한 트래킹
                            _currentDetections = dets;

                            foreach (var d in _currentDetections)
                            {
                                if (d.ClassId == 2 && !_modelCache.ContainsKey(d.TrackId))
                                {
                                    int tid = d.TrackId;
                                    Rect safeBox = new Rect(Math.Max(0, d.Box.X), Math.Max(0, d.Box.Y),
                                                           Math.Min(clone.Width - d.Box.X, d.Box.Width),
                                                           Math.Min(clone.Height - d.Box.Y, d.Box.Height));
                                    if (safeBox.Width > 10 && safeBox.Height > 10)
                                    {
                                        using var crop = new Mat(clone, safeBox).Clone();
                                        _modelCache[tid] = GetSpecificCarModel(crop);
                                    }
                                }
                            }
                        }
                    }
                    finally { clone.Dispose(); _isBusy = false; }
                });
            }
            DrawOutput(_frame);
            FrameImage = _frame.ToBitmapSource();
        }

        private void DrawOutput(Mat frame)
        {
            lock (_trackedObjects)
            {
                foreach (var d in _currentDetections)
                {
                    if (!_trackedObjects.TryGetValue(d.TrackId, out var track)) continue;

                    _modelCache.TryGetValue(d.TrackId, out string? detail);
                    detail ??= "Detecting...";

                    int spd = (int)(track.RelativeSpeed * 8.5); // 속도 계산식 복구
                    string typeName = GetTypeName(d.ClassId);   // CAR, BUS 등 복구

                    string label = $"{typeName} / {detail} / {spd}km/h";

                    Cv2.Rectangle(frame, d.Box, Scalar.Yellow, 2);
                    Cv2.PutText(frame, label, new Point(d.Box.X, d.Box.Y - 5),
                               HersheyFonts.HersheySimplex, 0.5, Scalar.Lime, 1);
                }
            }
        }

        // --- 유틸리티 및 트래킹 로직 ---
        private void TrackAndMatch(List<Detection> dets, double time)
        {
            var used = new HashSet<int>();
            foreach (var track in _trackedObjects.Values.ToList())
            {
                int best = -1; float maxIou = 0.35f;
                for (int i = 0; i < dets.Count; i++)
                {
                    if (used.Contains(i)) continue;
                    float iou = YoloV8Onnx.IoU(track.LastBox, dets[i].Box);
                    if (iou > maxIou) { maxIou = iou; best = i; }
                }
                if (best != -1) { dets[best].TrackId = track.Id; track.Update(dets[best], time); used.Add(best); }
                else track.Missed();
            }
            foreach (var d in dets.Where((_, i) => !used.Contains(i)))
            {
                var nt = new TrackedObject(d, time); d.TrackId = nt.Id; _trackedObjects[nt.Id] = nt;
            }
            _trackedObjects.Where(kv => kv.Value.ShouldBeDeleted).ToList().ForEach(k => { _trackedObjects.Remove(k.Key); _modelCache.TryRemove(k.Key, out _); });
        }

        private string GetTypeName(int id) => id switch { 2 => "CAR", 5 => "BUS", 7 => "TRUCK", 3 => "MOTOR", _ => "Vehicle" };
        private void OpenVideo() { var d = new OpenFileDialog(); if (d.ShowDialog() == true) { _video.Open(d.FileName); _timer.Start(); } }
        public void Dispose() { _timer.Stop(); _classSession?.Dispose(); _frame.Dispose(); _video.Dispose(); }
    }
}