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
        private readonly object _lock = new object();
        private readonly object _detLock = new object();

        private IMongoCollection<VehicleRecord>? _dbCollection;
        private readonly string _currentCollectionName;

        private readonly Dictionary<int, TrackedObject> _trackedObjects = new();
        private List<Detection> _currentDetections = new();
        private readonly Mat _frame = new();
        private volatile bool _isBusy = false, _isLaneBusy = false;
        private readonly DispatcherTimer _timer = new();
        private long _lastLaneInferMs = 0;

        private Mat? _driveMask, _laneProb;
        private readonly LaneAnalyzer _laneAnalyzer = new();
        private volatile bool _laneOk = false;

        public ObservableCollection<int> TotalLaneOptions { get; } = new() { 1, 2, 3, 4, 5, 6 };
        public ObservableCollection<int> CurrentLaneOptions { get; } = new() { 1, 2, 3, 4, 5, 6 };
        public int TotalLanes { get; set; } = 5;
        public int CurrentLane { get; set; } = 4;

        private int _countL = 0, _countF = 0, _countR = 0;
        private readonly HashSet<int> _countedIds = new();
        public string CountText { get => $"L:{_countL} | F:{_countF} | R:{_countR}"; set { OnPropertyChanged(); } }

        private BitmapSource? _frameImage;
        public BitmapSource? FrameImage { get => _frameImage; set { _frameImage = value; OnPropertyChanged(); } }

        public RelayCommand OpenVideoCommand { get; }
        public RelayCommand StopCommand { get; }

        private const double BaseLineRatio = 0.7;

        public MainWindowViewModel()
        {
            try
            {
                var client = new MongoClient("mongodb://localhost:27017");
                var database = client.GetDatabase("TrafficControlDB");
                _currentCollectionName = $"Logs_{DateTime.Now:yyyyMMdd_HHmmss}";
                _dbCollection = database.GetCollection<VehicleRecord>(_currentCollectionName);
            }
            catch { }

            OpenVideoCommand = new RelayCommand(OpenVideo);
            StopCommand = new RelayCommand(StopVideo);
            _timer.Tick += Timer_Tick;
            _timer.Interval = TimeSpan.FromMilliseconds(1);
            InitializeDetectors();
        }

        private void InitializeDetectors()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _detector = new YoloDetectService(Path.Combine(baseDir, "Scripts/yolov8n.onnx"), 640, 0.40f, 0.45f);
            _yolop = new YolopDetectService(Path.Combine(baseDir, "Scripts/yolop-640-640.onnx"), 640, 0.35f, 0.45f);
        }

        private void OpenVideo() { var d = new OpenFileDialog(); if (d.ShowDialog() == true) { _video.Open(d.FileName); _timer.Start(); } }
        private void StopVideo() { _timer.Stop(); _video.Close(); FrameImage = null; }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (!_video.Read(_frame) || _frame.Empty()) { StopVideo(); return; }
            long nowMs = Environment.TickCount64;

            if (!_isLaneBusy && _yolop != null && (nowMs - _lastLaneInferMs) >= 100)
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
                            _laneOk = (_laneProb != null && !_laneProb.Empty()) && _laneAnalyzer.Analyze(_laneProb, new Size(laneFrame.Width, laneFrame.Height));
                        }
                    }
                    finally { laneFrame.Dispose(); _isLaneBusy = false; }
                });
            }

            if (!_isBusy && _detector != null)
            {
                _isBusy = true; Mat clone = _frame.Clone(); double vTime = _video.PosMsec;
                Task.Run(() => {
                    try
                    {
                        var dets = _detector.Detect(clone);
                        lock (_trackedObjects)
                        {
                            TrackAndMatch(dets, vTime);
                            lock (_detLock) { _currentDetections = dets.ToList(); }
                            foreach (var track in _trackedObjects.Values)
                            {
                                string violation = track.CheckViolation(this.TotalLanes);
                                UpdateRealtimeDb(track, violation);
                            }
                        }
                    }
                    finally { clone.Dispose(); _isBusy = false; }
                });
            }

            UpdateCounting(_frame.Width, _frame.Height);
            DrawOutput(_frame, _currentDetections);
            FrameImage = _frame.ToBitmapSource();
        }

        private void UpdateCounting(int w, int h)
        {
            int lineY = (int)(h * BaseLineRatio);
            lock (_trackedObjects)
            {
                foreach (var t in _trackedObjects.Values)
                {
                    if (_countedIds.Contains(t.Id)) continue;
                    var center = new Point(t.LastBox.X + t.LastBox.Width / 2, t.LastBox.Y + t.LastBox.Height / 2);
                    if (center.Y > lineY)
                    {
                        _countedIds.Add(t.Id);
                        if (center.X < w * 0.35) _countL++; else if (center.X > w * 0.65) _countR++; else _countF++;
                        OnPropertyChanged(nameof(CountText));
                    }
                }
            }
        }

        private void UpdateRealtimeDb(TrackedObject track, string violation)
        {
            if (_dbCollection == null) return;
            // DB 저장 시 상세 모델명 사용
            string vType = track.GetModelName();
            var filter = Builders<VehicleRecord>.Filter.Eq("TrackId", track.Id);
            var update = Builders<VehicleRecord>.Update
                .Set("TrackId", track.Id)
                .Set("SystemTime", DateTime.Now)
                .Set("ViolationReason", violation)
                .Set("LaneNumber", track.CurrentLane)
                .Set("VehicleType", vType)
                .SetOnInsert("FirstDetectedTime", track.FirstDetectedTime);
            Task.Run(async () => { try { await _dbCollection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }); } catch { } });
        }

        private void DrawOutput(Mat frame, List<Detection> detections)
        {
            lock (_lock)
            {
                if (_driveMask != null && !_driveMask.Empty())
                {
                    using var greenOverlay = frame.Clone();
                    greenOverlay.SetTo(new Scalar(0, 255, 0), _driveMask);
                    Cv2.AddWeighted(greenOverlay, 0.3, frame, 0.7, 0, frame);
                }
                if (_laneOk) _laneAnalyzer.DrawOnFrame(frame);
            }

            Cv2.Rectangle(frame, new Rect(40, 40, 220, 80), Scalar.Black, -1);
            Cv2.PutText(frame, $"Lanes: {TotalLanes} / Ego: {CurrentLane}", new Point(50, 70), HersheyFonts.HersheySimplex, 0.5, Scalar.White, 1);
            Cv2.Line(frame, 0, (int)(frame.Height * BaseLineRatio), frame.Width, (int)(frame.Height * BaseLineRatio), Scalar.Red, 2);

            foreach (var det in detections)
            {
                lock (_trackedObjects)
                {
                    if (!_trackedObjects.TryGetValue(det.TrackId, out var t)) continue;
                    Cv2.Rectangle(frame, det.Box, Scalar.Yellow, 2);

                    // 핵심 수정: CarModelData.Names 리스트에서 이름을 가져옴
                    string detailModel = t.GetModelName();

                    string l1 = $"[{t.FirstDetectedTime}] {detailModel}";
                    string l2 = $"{detailModel} | {t.SpeedInKmh:F1}km/h | L{t.CurrentLane}";

                    Scalar bg = Scalar.Black;
                    if (t.LastClassId == 2) { if (t.IsSpeeding) bg = Scalar.Red; }
                    else if (t.ConfirmedViolationReason != "정상") { bg = Scalar.Red; }

                    DrawInfoText(frame, det.Box, l1, l2, bg);
                }
            }
        }

        private void DrawInfoText(Mat frame, Rect box, string l1, string l2, Scalar bgColor)
        {
            var s1 = Cv2.GetTextSize(l1, HersheyFonts.HersheySimplex, 0.4, 1, out _);
            var s2 = Cv2.GetTextSize(l2, HersheyFonts.HersheySimplex, 0.4, 1, out _);
            int mw = Math.Max(s1.Width, s2.Width) + 10;
            Rect bg = new Rect(box.X, box.Y - 45, mw, 40);
            if (bg.Y < 0) bg.Y = box.Y + box.Height + 5;
            Cv2.Rectangle(frame, bg, bgColor, -1);
            Cv2.Rectangle(frame, bg, Scalar.White, 1);
            Cv2.PutText(frame, l1, new Point(bg.X + 5, bg.Y + 15), HersheyFonts.HersheySimplex, 0.4, Scalar.White, 1);
            Cv2.PutText(frame, l2, new Point(bg.X + 5, bg.Y + 33), HersheyFonts.HersheySimplex, 0.4, Scalar.Cyan, 1);
        }

        private void TrackAndMatch(List<Detection> dets, double time)
        {
            var used = new HashSet<int>();
            foreach (var t in _trackedObjects.Values.ToList())
            {
                int best = -1; float maxIou = 0.25f;
                for (int i = 0; i < dets.Count; i++)
                {
                    if (used.Contains(i)) continue;
                    float iou = YoloV8Onnx.IoU(t.LastBox, dets[i].Box);
                    if (iou > maxIou) { maxIou = iou; best = i; }
                }
                if (best != -1)
                {
                    int ln = -1;
                    Point bc = new Point(dets[best].Box.X + dets[best].Box.Width / 2, dets[best].Box.Y + dets[best].Box.Height - 5);
                    lock (_lock) { if (_laneOk) ln = _laneAnalyzer.TryGetLaneNumberForPoint(bc); }
                    dets[best].TrackId = t.Id;
                    t.Update(dets[best].ClassId, dets[best].Box, time, ln);
                    used.Add(best);
                }
                else t.Missed();
            }
            foreach (var d in dets.Where((_, i) => !used.Contains(i)))
            {
                var nt = new TrackedObject(d.ClassId, d.Box, time, d.ClassName);
                d.TrackId = nt.Id; _trackedObjects[nt.Id] = nt;
            }
            _trackedObjects.Where(kv => kv.Value.ShouldBeDeleted).ToList().ForEach(k => _trackedObjects.Remove(k.Key));
        }

        public void Dispose() { _timer.Stop(); _video.Dispose(); _frame.Dispose(); _driveMask?.Dispose(); _laneProb?.Dispose(); }
    }
}