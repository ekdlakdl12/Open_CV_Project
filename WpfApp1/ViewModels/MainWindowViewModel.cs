using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.Win32;
using MongoDB.Driver;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WpfApp1.Models;
using WpfApp1.Services;
using WpfApp1.Scripts;

using Rect = OpenCvSharp.Rect;
using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;

namespace WpfApp1.ViewModels
{
    public class MainWindowViewModel : ViewModelBase, IDisposable
    {
        private readonly VideoPlayerService _video = new();
        private YoloDetectService? _detector;
        private YolopDetectService? _yolop;
        private InferenceSession? _classSession;
        private readonly object _sessionLock = new object();
        private readonly object _lock = new object();
        private readonly object _detLock = new object();
        private IMongoCollection<VehicleRecord>? _dbCollection;

        // CarModel 데이터 분리 참조
        private readonly string[] _carModelNames = CarModelData.Names;

        private readonly ConcurrentDictionary<int, string> _modelCache = new();
        private readonly ConcurrentDictionary<int, Scalar> _colorCache = new();
        private readonly Dictionary<int, TrackedObject> _trackedObjects = new();
        private List<Detection> _currentDetections = new();
        private readonly Mat _frame = new();
        private volatile bool _isBusy = false;
        private volatile bool _isLaneBusy = false;
        private readonly DispatcherTimer _timer = new();
        private long _lastLaneInferMs = 0;

        private Mat? _driveMask;
        private Mat? _laneProb;
        private readonly LaneAnalyzer _laneAnalyzer = new();
        private volatile bool _laneOk = false;

        public ObservableCollection<int> TotalLaneOptions { get; } = new() { 1, 2, 3, 4, 5, 6 };
        public ObservableCollection<int> CurrentLaneOptions { get; } = new() { 1, 2, 3, 4, 5, 6 };
        public int TotalLanes { get; set; } = 5;
        public int CurrentLane { get; set; } = 4;

        private int _countL = 0, _countF = 0, _countR = 0;
        private readonly HashSet<int> _countedIds = new();
        private string _countText = "L:0 | F:0 | R:0";
        public string CountText { get => _countText; set { _countText = value; OnPropertyChanged(); } }

        private BitmapSource? _frameImage;
        public BitmapSource? FrameImage { get => _frameImage; set { _frameImage = value; OnPropertyChanged(); } }

        public RelayCommand OpenVideoCommand { get; }
        private const int LaneInferIntervalMs = 200;

        // 기준선 위치 비율: 0.7(처음)과 0.8(이전)의 중간인 0.75로 설정
        private const double BaseLineRatio = 0.75;

        public MainWindowViewModel()
        {
            try
            {
                var client = new MongoClient("mongodb://localhost:27017");
                var database = client.GetDatabase("TrafficControlDB");
                _dbCollection = database.GetCollection<VehicleRecord>("DetectionLogs");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"DB 연결 실패: {ex.Message}");
            }

            OpenVideoCommand = new RelayCommand(OpenVideo);
            _timer.Tick += Timer_Tick;
            _timer.Interval = TimeSpan.FromMilliseconds(1);
            InitializeDetectors();
        }

        private void InitializeDetectors()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _detector = new YoloDetectService(Path.Combine(baseDir, "Scripts/yolov8n.onnx"), 640, 0.35f, 0.45f);
            _yolop = new YolopDetectService(Path.Combine(baseDir, "Scripts/yolop-640-640.onnx"), 640, 0.35f, 0.45f);
            string clsPath = Path.Combine(baseDir, "Scripts/best.onnx");
            if (File.Exists(clsPath)) _classSession = new InferenceSession(clsPath);
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (!_video.Read(_frame) || _frame.Empty()) return;
            long nowMs = Environment.TickCount64;

            if (!_isLaneBusy && _yolop != null && (nowMs - _lastLaneInferMs) >= LaneInferIntervalMs)
            {
                _isLaneBusy = true;
                _lastLaneInferMs = nowMs;
                Mat laneFrame = _frame.Clone();
                Task.Run(() =>
                {
                    try
                    {
                        var r = _yolop.Infer(laneFrame);
                        lock (_lock)
                        {
                            _driveMask?.Dispose();
                            _laneProb?.Dispose();
                            _driveMask = r.DrivableMaskOrig;
                            _laneProb = r.LaneProbOrig;

                            _laneAnalyzer.TotalLanes = this.TotalLanes;
                            _laneAnalyzer.EgoLane = this.CurrentLane;
                            _laneAnalyzer.SetDrivableMask(_driveMask);

                            _laneOk = false;
                            if (_laneProb != null && !_laneProb.Empty())
                            {
                                _laneOk = _laneAnalyzer.Analyze(_laneProb, new Size(laneFrame.Width, laneFrame.Height));
                            }
                        }
                    }
                    finally { laneFrame.Dispose(); _isBusy = false; _isLaneBusy = false; }
                });
            }

            if (!_isBusy && _detector != null)
            {
                _isBusy = true;
                Mat clone = _frame.Clone();
                double vTime = _video.PosMsec;
                Task.Run(() =>
                {
                    try
                    {
                        var dets = _detector.Detect(clone);
                        lock (_trackedObjects)
                        {
                            TrackAndMatch(dets, vTime);
                            lock (_detLock) { _currentDetections = dets.ToList(); }

                            foreach (var d in dets.Where(x => x.ClassId == 2 && !_modelCache.ContainsKey(x.TrackId)))
                            {
                                Rect safeBox = new Rect(Math.Max(0, d.Box.X), Math.Max(0, d.Box.Y),
                                    Math.Min(clone.Width - d.Box.X, d.Box.Width),
                                    Math.Min(clone.Height - d.Box.Y, d.Box.Height));
                                if (safeBox.Width > 20 && safeBox.Height > 20)
                                {
                                    using var crop = new Mat(clone, safeBox);
                                    string model = GetSpecificCarModel(crop);
                                    _modelCache.TryAdd(d.TrackId, model);
                                }
                            }
                        }
                    }
                    finally { clone.Dispose(); _isBusy = false; }
                });
            }

            lock (_trackedObjects) { UpdateCounting(_frame.Width, _frame.Height); }
            List<Detection> drawList;
            lock (_detLock) { drawList = _currentDetections.ToList(); }
            DrawOutput(_frame, drawList);
            FrameImage = _frame.ToBitmapSource();
        }

        private string GetSpecificCarModel(Mat cropImg)
        {
            if (_classSession == null) return "Car";
            try
            {
                using var rgbImg = new Mat(); Cv2.CvtColor(cropImg, rgbImg, ColorConversionCodes.BGR2RGB);
                using var resized = new Mat(); Cv2.Resize(rgbImg, resized, new Size(160, 160));
                var input = new DenseTensor<float>(new[] { 1, 3, 160, 160 });
                for (int y = 0; y < 160; y++)
                    for (int x = 0; x < 160; x++)
                    {
                        var p = resized.At<Vec3b>(y, x);
                        input[0, 0, y, x] = p.Item0 / 255f;
                        input[0, 1, y, x] = p.Item1 / 255f;
                        input[0, 2, y, x] = p.Item2 / 255f;
                    }
                lock (_sessionLock)
                {
                    using var results = _classSession.Run(new[] { NamedOnnxValue.CreateFromTensor(_classSession.InputMetadata.Keys.First(), input) });
                    var output = results.First().AsTensor<float>();
                    float[] scores = new float[206];
                    for (int i = 0; i < output.Dimensions[2]; i++)
                        for (int c = 0; c < 206; c++)
                            scores[c] += output[0, 4 + c, i];
                    return _carModelNames[Array.IndexOf(scores, scores.Max())];
                }
            }
            catch { return "Vehicle"; }
        }

        private void UpdateCounting(int w, int h)
        {
            int lineY = (int)(h * BaseLineRatio);
            foreach (var track in _trackedObjects.Values)
            {
                if (_countedIds.Contains(track.Id)) continue;
                var center = new Point(track.LastBox.X + track.LastBox.Width / 2, track.LastBox.Y + track.LastBox.Height / 2);

                if (center.Y > lineY)
                {
                    _countedIds.Add(track.Id);

                    int carLane = -1;
                    lock (_lock)
                    {
                        if (_laneOk) carLane = _laneAnalyzer.TryGetLaneNumberForPoint(center);
                    }

                    if (track.Direction == "L") _countL++;
                    else if (track.Direction == "R") _countR++;
                    else _countF++;

                    SaveToDb(track, track.Direction, carLane);
                }
            }
            CountText = $"L:{_countL} | F:{_countF} | R:{_countR}";
        }

        private void SaveToDb(TrackedObject track, string direction, int currentLane)
        {
            _modelCache.TryGetValue(track.Id, out string? modelName);
            string violation = track.CheckViolation();

            var record = new VehicleRecord
            {
                SystemTime = DateTime.Now,
                FirstDetectedTime = track.FirstDetectedTime,
                VehicleType = GetTypeName(track.LastClassId),
                ModelName = modelName ?? "Unknown",
                Direction = direction,
                Speed = Math.Round(track.SpeedInKmh, 1),
                LaneNumber = currentLane,
                ViolationReason = violation
            };

            Task.Run(async () => {
                try { if (_dbCollection != null) await _dbCollection.InsertOneAsync(record); }
                catch { }
            });
        }

        private void DrawOutput(Mat frame, List<Detection> detections)
        {
            Mat? driveLocal = null;
            Mat? laneProbLocal = null;
            bool laneOk;
            lock (_lock)
            {
                laneOk = _laneOk;
                if (_driveMask != null && !_driveMask.Empty()) driveLocal = _driveMask.Clone();
                if (_laneProb != null && !_laneProb.Empty()) laneProbLocal = _laneProb.Clone();
            }
            try
            {
                if (driveLocal != null && !driveLocal.Empty())
                {
                    using var overlay = frame.Clone();
                    overlay.SetTo(new Scalar(0, 255, 0), driveLocal);
                    Cv2.AddWeighted(overlay, 0.20, frame, 0.80, 0, frame);
                }
                if (laneProbLocal != null && !laneProbLocal.Empty())
                {
                    using var prob8u = new Mat();
                    if (laneProbLocal.Type() == MatType.CV_32FC1 || laneProbLocal.Type() == MatType.CV_32F)
                        laneProbLocal.ConvertTo(prob8u, MatType.CV_8UC1, 255.0);
                    else if (laneProbLocal.Type() == MatType.CV_8UC1) laneProbLocal.CopyTo(prob8u);
                    using var m = new Mat();
                    double thr = 255.0 * Math.Min(0.85, (_laneAnalyzer.LaneProbThreshold + 0.25));
                    Cv2.Threshold(prob8u, m, thr, 255, ThresholdTypes.Binary);
                    using var laneOverlay = frame.Clone();
                    laneOverlay.SetTo(new Scalar(0, 0, 255), m);
                    Cv2.AddWeighted(laneOverlay, 0.10, frame, 0.90, 0, frame);
                }
                lock (_lock) { if (laneOk) _laneAnalyzer.DrawOnFrame(frame); }
            }
            finally { driveLocal?.Dispose(); laneProbLocal?.Dispose(); }

            Cv2.Rectangle(frame, new Rect(40, 40, 220, 80), Scalar.Black, -1);
            Cv2.PutText(frame, $"Lanes: {TotalLanes} / Ego: {CurrentLane}", new Point(50, 70), HersheyFonts.HersheySimplex, 0.5, Scalar.White, 1);

            // 빨간색 기준선 (75% 위치)
            Cv2.Line(frame, 0, (int)(frame.Height * BaseLineRatio), frame.Width, (int)(frame.Height * BaseLineRatio), Scalar.Red, 2);

            foreach (var d in detections)
            {
                lock (_trackedObjects)
                {
                    if (!_trackedObjects.TryGetValue(d.TrackId, out var track)) continue;
                    Cv2.Rectangle(frame, d.Box, Scalar.Yellow, 2);
                    _modelCache.TryGetValue(d.TrackId, out string? model);
                    string l1 = $"[{track.FirstDetectedTime}] {GetTypeName(track.LastClassId)}";
                    string l2 = $"{model ?? "Analysing..."} | {track.SpeedInKmh:F1}km/h | L{track.CurrentLane}";
                    DrawInfoText(frame, d.Box, l1, l2, track.CheckViolation() != "정상" ? Scalar.Red : Scalar.Black);
                }
            }
        }

        private void DrawInfoText(Mat frame, Rect box, string l1, string l2, Scalar bgColor)
        {
            var s1 = Cv2.GetTextSize(l1, HersheyFonts.HersheySimplex, 0.4, 1, out _);
            var s2 = Cv2.GetTextSize(l2, HersheyFonts.HersheySimplex, 0.4, 1, out _);
            int mw = Math.Max(s1.Width, s2.Width) + 10;
            int th = s1.Height + s2.Height + 12;
            Rect bg = new Rect(box.X, box.Y - th - 5, mw, th);
            if (bg.Y < 0) bg.Y = box.Y + box.Height + 5;
            Cv2.Rectangle(frame, bg, bgColor, -1);
            Cv2.Rectangle(frame, bg, Scalar.White, 1);
            Cv2.PutText(frame, l1, new Point(bg.X + 5, bg.Y + s1.Height + 4), HersheyFonts.HersheySimplex, 0.4, Scalar.White, 1);
            Cv2.PutText(frame, l2, new Point(bg.X + 5, bg.Y + th - 4), HersheyFonts.HersheySimplex, 0.4, Scalar.Cyan, 1);
        }

        private void TrackAndMatch(List<Detection> dets, double time)
        {
            var used = new HashSet<int>();
            foreach (var track in _trackedObjects.Values.ToList())
            {
                int best = -1; float maxIou = 0.2f;
                for (int i = 0; i < dets.Count; i++)
                {
                    if (used.Contains(i)) continue;
                    float iou = YoloV8Onnx.IoU(track.LastBox, dets[i].Box);
                    if (iou > maxIou) { maxIou = iou; best = i; }
                }

                if (best != -1)
                {
                    int laneNum = -1;
                    Point bc = new Point(dets[best].Box.X + dets[best].Box.Width / 2, dets[best].Box.Y + dets[best].Box.Height);
                    lock (_lock) { if (_laneOk) laneNum = _laneAnalyzer.TryGetLaneNumberForPoint(bc); }

                    dets[best].TrackId = track.Id;
                    track.Update(dets[best].ClassId, dets[best].Box, time, laneNum);
                    used.Add(best);
                }
                else track.Missed();
            }

            foreach (var d in dets.Where((_, i) => !used.Contains(i)))
            {
                var nt = new TrackedObject(d.ClassId, d.Box, time, d.ClassName);
                d.TrackId = nt.Id;
                _trackedObjects[nt.Id] = nt;
            }

            _trackedObjects.Where(kv => kv.Value.ShouldBeDeleted).ToList().ForEach(k => {
                _trackedObjects.Remove(k.Key);
                _modelCache.TryRemove(k.Key, out _);
                _colorCache.TryRemove(k.Key, out _);
            });
        }

        private string GetTypeName(int id) => id switch { 2 => "CAR", 5 => "BUS", 7 => "TRUCK", _ => "Vehicle" };

        private void OpenVideo()
        {
            var d = new OpenFileDialog();
            if (d.ShowDialog() == true)
            {
                _video.Open(d.FileName);
                _modelCache.Clear();
                _colorCache.Clear();
                _countedIds.Clear();
                _countL = _countF = _countR = 0;
                _timer.Start();
            }
        }

        public void Dispose()
        {
            _timer.Stop();
            _classSession?.Dispose();
            _frame.Dispose();
            _video.Dispose();
            _driveMask?.Dispose();
            _laneProb?.Dispose();
        }
    }
}