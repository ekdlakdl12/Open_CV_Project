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

        private readonly string[] _carModelNames = CarModelData.Names;
        private readonly ConcurrentDictionary<int, string> _modelCache = new();
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
        private const double BaseLineRatio = 0.7;

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

        private void OpenVideo()
        {
            var d = new OpenFileDialog();
            if (d.ShowDialog() == true)
            {
                _video.Open(d.FileName);
                _modelCache.Clear();
                _countedIds.Clear();
                _countL = _countF = _countR = 0;

                SeedDemoData(); // 시연 데이터 즉시 삽입
                _timer.Start();
            }
        }

        private void SeedDemoData()
        {
            if (_dbCollection == null) return;
            Task.Run(async () => {
                try
                {
                    var demoRecords = new List<VehicleRecord> {
                        new VehicleRecord { TrackId = 999, FirstDetectedTime = DateTime.Now.ToString("HH:mm:ss"), SystemTime = DateTime.Now, LaneNumber = 1, VehicleType = "CAR", ModelName = "BMW M6 Convertible 2010", Speed = 145.5, ViolationReason = "과속" },
                        new VehicleRecord { TrackId = 888, FirstDetectedTime = DateTime.Now.ToString("HH:mm:ss"), SystemTime = DateTime.Now, LaneNumber = 1, VehicleType = "TRUCK", ModelName = "Hyundai Xcient", Speed = 82.3, ViolationReason = "지정차로 위반(상위차로 진입)" },
                        new VehicleRecord { TrackId = 777, FirstDetectedTime = DateTime.Now.ToString("HH:mm:ss"), SystemTime = DateTime.Now, LaneNumber = 3, VehicleType = "CAR", ModelName = "Genesis G80", Speed = 95.2, ViolationReason = "정상" },
                        new VehicleRecord { TrackId = 666, FirstDetectedTime = DateTime.Now.ToString("HH:mm:ss"), SystemTime = DateTime.Now, LaneNumber = 4, VehicleType = "CAR", ModelName = "Avante CN7", Speed = 100.1, ViolationReason = "정상" },
                        new VehicleRecord { TrackId = 555, FirstDetectedTime = DateTime.Now.ToString("HH:mm:ss"), SystemTime = DateTime.Now, LaneNumber = 2, VehicleType = "BUS", ModelName = "Hyundai Universe", Speed = 88.0, ViolationReason = "정상" }
                    };
                    foreach (var r in demoRecords)
                    {
                        var filter = Builders<VehicleRecord>.Filter.Eq("TrackId", r.TrackId);
                        await _dbCollection.ReplaceOneAsync(filter, r, new ReplaceOptions { IsUpsert = true });
                    }
                }
                catch { }
            });
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (!_video.Read(_frame) || _frame.Empty()) return;
            long nowMs = Environment.TickCount64;

            // 1. 차선 인식 (YOLOP)
            if (!_isLaneBusy && _yolop != null && (nowMs - _lastLaneInferMs) >= LaneInferIntervalMs)
            {
                _isLaneBusy = true; _lastLaneInferMs = nowMs;
                Mat laneFrame = _frame.Clone();
                Task.Run(() => {
                    try
                    {
                        var r = _yolop.Infer(laneFrame);
                        lock (_lock)
                        {
                            _driveMask?.Dispose(); _laneProb?.Dispose();
                            _driveMask = r.DrivableMaskOrig; _laneProb = r.LaneProbOrig;
                            _laneAnalyzer.TotalLanes = this.TotalLanes;
                            _laneAnalyzer.EgoLane = this.CurrentLane;
                            _laneAnalyzer.SetDrivableMask(_driveMask);
                            _laneOk = false;
                            if (_laneProb != null && !_laneProb.Empty())
                                _laneOk = _laneAnalyzer.Analyze(_laneProb, new Size(laneFrame.Width, laneFrame.Height));
                        }
                    }
                    finally { laneFrame.Dispose(); _isLaneBusy = false; }
                });
            }

            // 2. 객체 인식 및 실시간 DB 업데이트
            if (!_isBusy && _detector != null)
            {
                _isBusy = true;
                Mat clone = _frame.Clone();
                double vTime = _video.PosMsec;
                Task.Run(() => {
                    try
                    {
                        var dets = _detector.Detect(clone);
                        lock (_trackedObjects)
                        {
                            TrackAndMatch(dets, vTime);
                            lock (_detLock) { _currentDetections = dets.ToList(); }

                            foreach (var d in dets.Where(x => x.ClassId == 2 && !_modelCache.ContainsKey(x.TrackId)))
                            {
                                Rect s = d.Box;
                                Rect safe = new Rect(Math.Max(0, s.X), Math.Max(0, s.Y), Math.Min(clone.Width - s.X, s.Width), Math.Min(clone.Height - s.Y, s.Height));
                                if (safe.Width > 20 && safe.Height > 20)
                                {
                                    using var crop = new Mat(clone, safe);
                                    _modelCache.TryAdd(d.TrackId, GetSpecificCarModel(crop));
                                }
                            }
                            // [수정] 데이터 삽입 조건 완화 (30 -> 5)
                            foreach (var track in _trackedObjects.Values.Where(t => t.UpdateCount > 5))
                            {
                                UpdateRealtimeDb(track);
                            }
                        }
                    }
                    finally { clone.Dispose(); _isBusy = false; }
                });
            }

            lock (_trackedObjects) { UpdateCounting(_frame.Width, _frame.Height); }
            List<Detection> drawList; lock (_detLock) { drawList = _currentDetections.ToList(); }
            DrawOutput(_frame, drawList);
            FrameImage = _frame.ToBitmapSource();
        }

        private void UpdateRealtimeDb(TrackedObject track)
        {
            if (_dbCollection == null) return;
            _modelCache.TryGetValue(track.Id, out string? model);
            string violation = track.CheckViolation();

            var filter = Builders<VehicleRecord>.Filter.Eq("TrackId", track.Id);
            var update = Builders<VehicleRecord>.Update
                .Set("SystemTime", DateTime.Now)
                .Set("Speed", Math.Round(track.SpeedInKmh, 1))
                .Set("ViolationReason", violation)
                .Set("LaneNumber", track.CurrentLane)
                .Set("VehicleType", GetTypeName(track.LastClassId))
                .SetOnInsert("FirstDetectedTime", track.FirstDetectedTime)
                .SetOnInsert("ModelName", model ?? "Unknown");

            Task.Run(async () => {
                try { await _dbCollection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }); } catch { }
            });
        }

        private void DrawOutput(Mat frame, List<Detection> detections)
        {
            Mat? dLoc = null; Mat? lLoc = null; bool lOk;
            lock (_lock) { lOk = _laneOk; if (_driveMask != null) dLoc = _driveMask.Clone(); if (_laneProb != null) lLoc = _laneProb.Clone(); }
            try
            {
                if (dLoc != null && !dLoc.Empty())
                {
                    using var ov = frame.Clone(); ov.SetTo(new Scalar(0, 255, 0), dLoc);
                    Cv2.AddWeighted(ov, 0.2, frame, 0.8, 0, frame);
                }
                if (lLoc != null && !lLoc.Empty())
                {
                    using var p8 = new Mat(); lLoc.ConvertTo(p8, MatType.CV_8UC1, 255.0);
                    using var m = new Mat(); Cv2.Threshold(p8, m, 180, 255, ThresholdTypes.Binary);
                    using var lo = frame.Clone(); lo.SetTo(new Scalar(0, 0, 255), m);
                    Cv2.AddWeighted(lo, 0.1, frame, 0.9, 0, frame);
                }
                lock (_lock) { if (lOk) _laneAnalyzer.DrawOnFrame(frame); }
            }
            finally { dLoc?.Dispose(); lLoc?.Dispose(); }

            Cv2.Rectangle(frame, new Rect(40, 40, 220, 80), Scalar.Black, -1);
            Cv2.PutText(frame, $"Lanes: {TotalLanes} / Ego: {CurrentLane}", new Point(50, 70), HersheyFonts.HersheySimplex, 0.5, Scalar.White, 1);
            Cv2.Line(frame, 0, (int)(frame.Height * BaseLineRatio), frame.Width, (int)(frame.Height * BaseLineRatio), Scalar.Red, 2);

            foreach (var d in detections)
            {
                lock (_trackedObjects)
                {
                    if (!_trackedObjects.TryGetValue(d.TrackId, out var t)) continue;
                    Cv2.Rectangle(frame, d.Box, Scalar.Yellow, 2);
                    _modelCache.TryGetValue(d.TrackId, out string? m);
                    string l1 = $"[{t.FirstDetectedTime}] {GetTypeName(t.LastClassId)}";
                    string l2 = $"{m ?? "Analysing..."} | {t.SpeedInKmh:F1}km/h | L{t.CurrentLane}";
                    Scalar bgColor = t.CheckViolation() != "정상" ? Scalar.Red : Scalar.Black;
                    DrawInfoText(frame, d.Box, l1, l2, bgColor);
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
            foreach (var t in _trackedObjects.Values.ToList())
            {
                int best = -1; float maxIou = 0.2f;
                for (int i = 0; i < dets.Count; i++)
                {
                    if (used.Contains(i)) continue;
                    float iou = YoloV8Onnx.IoU(t.LastBox, dets[i].Box);
                    if (iou > maxIou) { maxIou = iou; best = i; }
                }
                if (best != -1)
                {
                    int ln = -1; Point bc = new Point(dets[best].Box.X + dets[best].Box.Width / 2, dets[best].Box.Y + dets[best].Box.Height);
                    lock (_lock) { if (_laneOk) ln = _laneAnalyzer.TryGetLaneNumberForPoint(bc); }
                    dets[best].TrackId = t.Id; t.Update(dets[best].ClassId, dets[best].Box, time, ln);
                    used.Add(best);
                }
                else t.Missed();
            }
            foreach (var d in dets.Where((_, i) => !used.Contains(i)))
            {
                var nt = new TrackedObject(d.ClassId, d.Box, time, d.ClassName);
                d.TrackId = nt.Id; _trackedObjects[nt.Id] = nt;
            }
            _trackedObjects.Where(kv => kv.Value.ShouldBeDeleted).ToList().ForEach(k => {
                _trackedObjects.Remove(k.Key); _modelCache.TryRemove(k.Key, out _);
            });
        }

        private string GetSpecificCarModel(Mat cropImg)
        {
            if (_classSession == null) return "Car";
            try
            {
                using var rgb = new Mat(); Cv2.CvtColor(cropImg, rgb, ColorConversionCodes.BGR2RGB);
                using var res = new Mat(); Cv2.Resize(rgb, res, new Size(160, 160));
                var input = new DenseTensor<float>(new[] { 1, 3, 160, 160 });
                for (int y = 0; y < 160; y++) for (int x = 0; x < 160; x++)
                    {
                        var p = res.At<Vec3b>(y, x);
                        input[0, 0, y, x] = p.Item0 / 255f; input[0, 1, y, x] = p.Item1 / 255f; input[0, 2, y, x] = p.Item2 / 255f;
                    }
                lock (_sessionLock)
                {
                    using var r = _classSession.Run(new[] { NamedOnnxValue.CreateFromTensor(_classSession.InputMetadata.Keys.First(), input) });
                    var o = r.First().AsTensor<float>(); float[] s = new float[206];
                    for (int i = 0; i < o.Dimensions[2]; i++) for (int c = 0; c < 206; c++) s[c] += o[0, 4 + c, i];
                    return _carModelNames[Array.IndexOf(s, s.Max())];
                }
            }
            catch { return "Vehicle"; }
        }

        private void UpdateCounting(int w, int h)
        {
            int lineY = (int)(h * BaseLineRatio);
            foreach (var t in _trackedObjects.Values)
            {
                if (_countedIds.Contains(t.Id)) continue;
                var center = new Point(t.LastBox.X + t.LastBox.Width / 2, t.LastBox.Y + t.LastBox.Height / 2);
                if (center.Y > lineY)
                {
                    _countedIds.Add(t.Id);
                    if (t.Direction == "L") _countL++; else if (t.Direction == "R") _countR++; else _countF++;
                }
            }
            CountText = $"L:{_countL} | F:{_countF} | R:{_countR}";
        }

        private string GetTypeName(int id) => id switch { 2 => "CAR", 5 => "BUS", 7 => "TRUCK", _ => "Vehicle" };
        public void Dispose() { _timer.Stop(); _classSession?.Dispose(); _frame.Dispose(); _video.Dispose(); _driveMask?.Dispose(); _laneProb?.Dispose(); }
    }
}