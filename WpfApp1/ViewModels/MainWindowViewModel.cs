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
using System.Net.Http;
using System.Text.Json;
using MongoDB.Driver;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace WpfApp1.ViewModels
{
    public class MainWindowViewModel : ViewModelBase, IDisposable
    {
        private const string BaseOnnxPath = "Scripts/yolov8n.onnx";
        private const string ClassOnnxPath = "Scripts/best.onnx";

        private readonly VideoPlayerService _video = new();
        private YoloDetectService? _detector;
        private InferenceSession? _classSession;
        private readonly object _sessionLock = new object();

        // [팩트체크] 학습시킨 모델의 클래스 순서와 동일해야 함
        private readonly string[] _carModelNames = {
            "Sonata", "Elantra", "Sorento", "Optima", "Accent",
            "Sportage", "Santa Fe", "Genesis", "Carnival", "Veloster"
        };

        private readonly Mat _frame = new();
        private readonly Dictionary<int, TrackedObject> _trackedObjects = new();
        private readonly ConcurrentDictionary<int, string> _modelCache = new();

        private readonly object _lock = new();
        private List<Detection> _currentDetections = new();
        private List<int> _laneCoords = new();

        private volatile bool _isBusy = false;
        private volatile bool _isLaneBusy = false;
        private readonly DispatcherTimer _timer = new();
        private readonly HttpClient _httpClient = new HttpClient();
        private readonly IMongoCollection<VehicleRecord>? _collection;

        private string _countText = "L:0 | F:0 | R:0";
        public string CountText { get => _countText; set { _countText = value; OnPropertyChanged(); } }

        private int _countL = 0, _countF = 0, _countR = 0;
        private readonly HashSet<int> _countedIds = new();

        private BitmapSource? _frameImage;
        public BitmapSource? FrameImage { get => _frameImage; set { _frameImage = value; OnPropertyChanged(); } }

        public RelayCommand OpenVideoCommand { get; }
        public RelayCommand StopCommand { get; }

        public MainWindowViewModel()
        {
            OpenVideoCommand = new RelayCommand(OpenVideo);
            StopCommand = new RelayCommand(Stop);
            _timer.Tick += Timer_Tick;
            _timer.Interval = TimeSpan.FromMilliseconds(1);

            try
            {
                var client = new MongoClient("mongodb://localhost:27017");
                _collection = client.GetDatabase("TrafficDB").GetCollection<VehicleRecord>("VehicleHistory");
            }
            catch { }

            InitializeDetectors();
        }

        private void InitializeDetectors()
        {
            // [수정] AppDomain.CurrentDomain.BaseDirectory 사용
            string detPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, BaseOnnxPath);
            if (File.Exists(detPath)) _detector = new YoloDetectService(detPath, 640, 0.25f, 0.45f);

            string clsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ClassOnnxPath);
            if (File.Exists(clsPath))
            {
                var options = new SessionOptions();
                options.InterOpNumThreads = 1;
                options.IntraOpNumThreads = 1;
                try { _classSession = new InferenceSession(clsPath, options); }
                catch (Exception ex) { Debug.WriteLine($"Session Error: {ex.Message}"); }
            }
        }

        private string GetSpecificCarModel(Mat frame, Rect box)
        {
            if (_classSession == null) return "NoModel";

            // 이미지 크롭 영역 안전성 확보
            int x = Math.Max(0, box.X);
            int y = Math.Max(0, box.Y);
            int w = Math.Min(frame.Width - x, box.Width);
            int h = Math.Min(frame.Height - y, box.Height);
            if (w <= 10 || h <= 10) return "Small";

            try
            {
                using var cropped = new Mat(frame, new Rect(x, y, w, h));
                using var resized = new Mat();
                Cv2.Resize(cropped, resized, new Size(416, 416));
                Cv2.CvtColor(resized, resized, ColorConversionCodes.BGR2RGB);

                var inputTensor = new DenseTensor<float>(new[] { 1, 3, 416, 416 });
                var indexer = resized.GetGenericIndexer<Vec3b>();

                for (int i = 0; i < 416; i++)
                {
                    for (int j = 0; j < 416; j++)
                    {
                        Vec3b p = indexer[i, j];
                        inputTensor[0, 0, i, j] = p.Item0 / 255f;
                        inputTensor[0, 1, i, j] = p.Item1 / 255f;
                        inputTensor[0, 2, i, j] = p.Item2 / 255f;
                    }
                }

                var inputs = new List<NamedOnnxValue> {
                    NamedOnnxValue.CreateFromTensor(_classSession.InputMetadata.Keys.First(), inputTensor)
                };

                lock (_sessionLock)
                {
                    using var results = _classSession.Run(inputs);
                    var outputData = results.First().AsTensor<float>();
                    var dims = outputData.Dimensions;

                    float maxScore = -1f;
                    int bestIdx = -1;

                    // [수정] DIM:8400 오류 해결을 위한 유연한 텐서 해석 루프
                    if (dims.Length == 2)
                    {
                        var data = outputData.ToArray();
                        maxScore = data.Max();
                        bestIdx = Array.IndexOf(data, maxScore);
                    }
                    else if (dims.Length == 3)
                    {
                        // YOLOv8 Detection 형태의 출력이 나올 경우 대응
                        int classesCount = dims[1];
                        int numBoxes = dims[2];

                        for (int c = 0; c < classesCount; c++)
                        {
                            for (int b = 0; b < numBoxes; b++)
                            {
                                float score = outputData[0, c, b];
                                if (score > maxScore) { maxScore = score; bestIdx = c; }
                            }
                        }
                    }

                    if (maxScore < 0.25f || bestIdx == -1) return "Unknown";
                    return _carModelNames.Length > bestIdx ? _carModelNames[bestIdx] : $"ID:{bestIdx}";
                }
            }
            catch { return "Error"; }
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (!_video.Read(_frame)) { Stop(); return; }

            if (!_isLaneBusy)
            {
                _isLaneBusy = true;
                Mat laneFrame = _frame.Clone();
                Task.Run(async () => { try { await DetectLaneAsync(laneFrame); } finally { laneFrame.Dispose(); _isLaneBusy = false; } });
            }

            if (!_isBusy && _detector != null)
            {
                _isBusy = true;
                Mat vehicleFrame = _frame.Clone();
                double time = _video.PosMsec;
                Task.Run(() => {
                    try
                    {
                        var dets = _detector.Detect(vehicleFrame);
                        lock (_lock)
                        {
                            TrackAndMatch(dets, time);
                            _currentDetections = dets;

                            foreach (var d in _currentDetections)
                            {
                                // 새로운 ID일 때만 비동기 분류 (성능 핵심)
                                if (d.ClassId == 2 && !_modelCache.ContainsKey(d.TrackId))
                                {
                                    int tid = d.TrackId;
                                    Rect box = d.Box;
                                    Mat cropMat = vehicleFrame.Clone();
                                    Task.Run(() => {
                                        try
                                        {
                                            string res = GetSpecificCarModel(cropMat, box);
                                            _modelCache[tid] = res;
                                        }
                                        finally { cropMat.Dispose(); }
                                    });
                                }
                            }
                        }
                    }
                    finally { vehicleFrame.Dispose(); _isBusy = false; }
                });
            }

            lock (_lock) { UpdateCounting(_frame.Width, _frame.Height); DrawOutput(_frame); }
            FrameImage = _frame.ToBitmapSource();
        }

        private void DrawOutput(Mat frame)
        {
            foreach (var d in _currentDetections)
            {
                if (!_trackedObjects.TryGetValue(d.TrackId, out var track)) continue;

                _modelCache.TryGetValue(d.TrackId, out string? detailName);
                detailName ??= "Detecting...";

                int displaySpeed = (int)(track.RelativeSpeed * 8.5);
                string label = $"{GetTypeName(d.ClassId)} / {detailName} / {displaySpeed}km/h";

                Cv2.Rectangle(frame, d.Box, Scalar.Yellow, 2);
                Cv2.PutText(frame, label, new Point(d.Box.X, d.Box.Y - 5), HersheyFonts.HersheySimplex, 0.5, Scalar.Lime, 1);
            }
        }

        // 기존 보조 메서드들 (생략 없이 유지)
        private async Task DetectLaneAsync(Mat frame) { try { byte[] imgBytes = frame.ToBytes(".jpg"); using var content = new MultipartFormDataContent(); var imageContent = new ByteArrayContent(imgBytes); imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg"); content.Add(imageContent, "frame", "frame.jpg"); var response = await _httpClient.PostAsync("http://127.0.0.1:5000/detect_lane", content); if (response.IsSuccessStatusCode) { var json = await response.Content.ReadAsStringAsync(); using var doc = JsonDocument.Parse(json); var coords = doc.RootElement.GetProperty("lane_coords").EnumerateArray().Select(x => x.GetInt32()).ToList(); lock (_lock) { _laneCoords = coords; } } } catch { } }
        private void TrackAndMatch(List<Detection> detections, double timeMsec) { var usedDets = new HashSet<int>(); foreach (var track in _trackedObjects.Values.ToList()) { int bestIdx = -1; float bestIou = 0.35f; for (int i = 0; i < detections.Count; i++) { if (usedDets.Contains(i)) continue; float iou = YoloV8Onnx.IoU(track.LastBox, detections[i].Box); if (iou > bestIou) { bestIou = iou; bestIdx = i; } } if (bestIdx != -1) { detections[bestIdx].TrackId = track.Id; track.Update(detections[bestIdx], timeMsec); usedDets.Add(bestIdx); } else track.Missed(); } foreach (var d in detections.Where((_, i) => !usedDets.Contains(i))) { var newTrack = new TrackedObject(d, timeMsec); d.TrackId = newTrack.Id; _trackedObjects[newTrack.Id] = newTrack; } _trackedObjects.Where(kv => kv.Value.ShouldBeDeleted).Select(kv => { _modelCache.TryRemove(kv.Key, out _); return kv.Key; }).ToList().ForEach(k => _trackedObjects.Remove(k)); }
        private void UpdateCounting(int w, int h) { int lineY = (int)(h * 0.75f); foreach (var track in _trackedObjects.Values) { if (_countedIds.Contains(track.Id)) continue; var center = new Point(track.LastBox.X + track.LastBox.Width / 2, track.LastBox.Y + track.LastBox.Height / 2); if (center.Y > lineY) { string dir = center.X < w * 0.35 ? "Left" : (center.X < w * 0.65 ? "Front" : "Right"); if (dir == "Left") _countL++; else if (dir == "Front") _countF++; else _countR++; _countedIds.Add(track.Id); int spd = (int)(track.RelativeSpeed * 8.5); _modelCache.TryGetValue(track.Id, out string? m); _ = SaveToMongoAsync(m ?? GetTypeName(track.ClassId), dir, spd); } } CountText = $"L:{_countL} | F:{_countF} | R:{_countR}"; }
        private async Task SaveToMongoAsync(string type, string dir, int speed) { if (_collection == null) return; try { await _collection.InsertOneAsync(new VehicleRecord { DetectTime = DateTime.Now, VehicleType = type, Direction = dir, Speed = speed }); } catch { } }
        private string GetTypeName(int id) => id switch { 2 => "Car", 5 => "Bus", 7 => "Truck", 3 => "Motor", _ => "Vehicle" };
        private void OpenVideo() { var dialog = new OpenFileDialog(); if (dialog.ShowDialog() == true) { _video.Open(dialog.FileName); _countL = _countF = _countR = 0; _countedIds.Clear(); _trackedObjects.Clear(); _modelCache.Clear(); _timer.Start(); } }
        private void Stop() { _timer.Stop(); _video.Close(); }
        public void Dispose() { Stop(); _frame.Dispose(); _detector?.Dispose(); _classSession?.Dispose(); _video.Dispose(); }
    }
}