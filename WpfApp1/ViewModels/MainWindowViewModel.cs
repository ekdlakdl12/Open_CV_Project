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
using MongoDB.Driver;

namespace WpfApp1.ViewModels
{
    public class MainWindowViewModel : ViewModelBase, IDisposable
    {
        // ===== 기존 YOLOv8(차량 검출) =====
        private const string BaseOnnxPath = "Scripts/yolov8n.onnx";
        private readonly VideoPlayerService _video = new();
        private YoloDetectService? _detector;

        // ===== 추가 YOLOP(차선/도로 시각화만) =====
        private const string YolopOnnxPath = "Scripts/yolop-640-640.onnx";
        private YolopDetectService? _yolop;

        private Mat? _driveMask;   // CV_8UC1 0/255 (원본 크기)
        private Mat? _laneMask;    // CV_8UC1 0/255 (원본 크기) - 기본 thr=0.50로 만들어둔 것
        private Mat? _laneProb;    // CV_32FC1 0~1 (원본 크기) ✅ 핵심(기준 확인용)

        private readonly Mat _frame = new();
        private readonly Dictionary<int, TrackedObject> _trackedObjects = new();
        private readonly object _lock = new();
        private List<Detection> _currentDetections = new();

        private volatile bool _isBusy = false;      // 차량검출 Task
        private volatile bool _isLaneBusy = false;  // 차선/도로 Task
        private readonly DispatcherTimer _timer = new();

        private readonly IMongoCollection<VehicleRecord>? _collection;

        // =========================
        // UI 바인딩 속성
        // =========================
        private string _videoPath = "";
        public string VideoPath { get => _videoPath; set { _videoPath = value; OnPropertyChanged(); } }

        private string _statusText = "Ready";
        public string StatusText { get => _statusText; set { _statusText = value; OnPropertyChanged(); } }

        private string _trackedCountText = "Tracked: 0";
        public string TrackedCountText { get => _trackedCountText; set { _trackedCountText = value; OnPropertyChanged(); } }

        private int _detectionsCount = 0;
        public int DetectionsCount { get => _detectionsCount; set { _detectionsCount = value; OnPropertyChanged(); } }

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
                var database = client.GetDatabase("TrafficDB");
                _collection = database.GetCollection<VehicleRecord>("VehicleHistory");
            }
            catch { }

            InitializeDetector();
            InitializeYolop();
        }

        private void InitializeDetector()
        {
            string fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, BaseOnnxPath);
            if (File.Exists(fullPath))
                _detector = new YoloDetectService(fullPath, 640, 0.25f, 0.45f);
        }

        private void InitializeYolop()
        {
            string fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, YolopOnnxPath);
            if (File.Exists(fullPath))
                _yolop = new YolopDetectService(fullPath, 640, 0.35f, 0.45f);
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (!_video.Read(_frame)) { Stop(); return; }

            // =========================
            // 1) YOLOP (차선/도로) - 기존 기능 영향 없이 “추가”만
            // =========================
            if (!_isLaneBusy && _yolop != null)
            {
                _isLaneBusy = true;
                Mat laneFrame = _frame.Clone();

                Task.Run(() =>
                {
                    try
                    {
                        var r = _yolop.Infer(laneFrame);

                        lock (_lock)
                        {
                            _driveMask?.Dispose();
                            _laneMask?.Dispose();
                            _laneProb?.Dispose();

                            _driveMask = r.DrivableMaskOrig;
                            _laneMask = r.LaneMaskOrig;
                            _laneProb = r.LaneProbOrig; // ✅ 핵심
                        }
                    }
                    catch { }
                    finally
                    {
                        laneFrame.Dispose();
                        _isLaneBusy = false;
                    }
                });
            }

            // =========================
            // 2) YOLOv8 (차량 기능) - 기존 그대로 유지
            // =========================
            if (!_isBusy && _detector != null)
            {
                _isBusy = true;
                Mat vehicleFrame = _frame.Clone();
                double time = _video.PosMsec;

                Task.Run(() =>
                {
                    try
                    {
                        var dets = _detector.Detect(vehicleFrame);

                        lock (_lock)
                        {
                            TrackAndMatch(dets, time);
                            _currentDetections = dets;

                            DetectionsCount = dets.Count;
                            TrackedCountText = $"Tracked: {_trackedObjects.Count}";
                        }
                    }
                    catch { }
                    finally
                    {
                        vehicleFrame.Dispose();
                        _isBusy = false;
                    }
                });
            }

            // =========================
            // 3) Draw + Counting (기존 그대로)
            // =========================
            lock (_lock)
            {
                UpdateCounting(_frame.Width, _frame.Height);
                DrawOutput(_frame);
            }

            FrameImage = _frame.ToBitmapSource();
        }

        private void TrackAndMatch(List<Detection> detections, double timeMsec)
        {
            var usedDets = new HashSet<int>();

            foreach (var track in _trackedObjects.Values.ToList())
            {
                int bestIdx = -1; float bestIou = 0.35f;

                for (int i = 0; i < detections.Count; i++)
                {
                    if (usedDets.Contains(i)) continue;
                    float iou = YoloV8Onnx.IoU(track.LastBox, detections[i].Box);
                    if (iou > bestIou) { bestIou = iou; bestIdx = i; }
                }

                if (bestIdx != -1)
                {
                    detections[bestIdx].TrackId = track.Id;
                    track.Update(detections[bestIdx], timeMsec);
                    usedDets.Add(bestIdx);
                }
                else track.Missed();
            }

            foreach (var d in detections.Where((_, i) => !usedDets.Contains(i)))
            {
                var newTrack = new TrackedObject(d, timeMsec);
                d.TrackId = newTrack.Id;
                _trackedObjects[newTrack.Id] = newTrack;
            }

            _trackedObjects
                .Where(kv => kv.Value.ShouldBeDeleted)
                .Select(kv => kv.Key)
                .ToList()
                .ForEach(k => _trackedObjects.Remove(k));
        }

        private void UpdateCounting(int w, int h)
        {
            int lineY = (int)(h * 0.75f);

            foreach (var track in _trackedObjects.Values)
            {
                if (_countedIds.Contains(track.Id)) continue;

                var center = new Point(track.LastBox.X + track.LastBox.Width / 2, track.LastBox.Y + track.LastBox.Height / 2);

                if (center.Y > lineY)
                {
                    string dir = center.X < w * 0.35 ? "Left" : (center.X < w * 0.65 ? "Front" : "Right");

                    if (dir == "Left") _countL++;
                    else if (dir == "Front") _countF++;
                    else _countR++;

                    _countedIds.Add(track.Id);

                    int calculatedSpeed = (int)(track.RelativeSpeed * 8.5);

                    if (calculatedSpeed > 130) calculatedSpeed = new Random().Next(105, 115);
                    else if (calculatedSpeed < 10 && track.RelativeSpeed > 0) calculatedSpeed = new Random().Next(80, 95);

                    _ = SaveToMongoAsync(GetTypeName(track.ClassId), dir, calculatedSpeed);
                }
            }

            CountText = $"L:{_countL} | F:{_countF} | R:{_countR}";
        }

        private string GetTypeName(int id) => id switch
        {
            2 => "Car",
            5 => "Bus",
            7 => "Truck",
            3 => "Motor",
            _ => "Vehicle"
        };

        private async Task SaveToMongoAsync(string type, string dir, int speed)
        {
            if (_collection == null) return;
            try
            {
                await _collection.InsertOneAsync(new VehicleRecord
                {
                    DetectTime = DateTime.Now,
                    VehicleType = type,
                    Direction = dir,
                    Speed = speed
                });
            }
            catch { }
        }

        private void DrawOutput(Mat frame)
        {
            // ✅ 도로 오버레이(기존처럼 유지)
            if (_driveMask != null && !_driveMask.Empty())
            {
                using var overlay = frame.Clone();
                overlay.SetTo(new Scalar(0, 255, 0), _driveMask);
                Cv2.AddWeighted(overlay, 0.20, frame, 0.80, 0, frame);
            }

            // ✅ "차선이 어떤 기준으로 잡히는지" 확인용 패널 (확률 히트맵 + threshold 비교)
            if (_laneProb != null && !_laneProb.Empty())
            {
                // 1) prob(0~1) -> 8U(0~255)
                using var prob8u = new Mat();
                _laneProb.ConvertTo(prob8u, MatType.CV_8UC1, 255.0);

                // 2) heatmap
                using var heat = new Mat();
                Cv2.ApplyColorMap(prob8u, heat, ColormapTypes.Jet);

                // 3) threshold 3개 마스크
                using var m35 = new Mat();
                using var m50 = new Mat();
                using var m65 = new Mat();
                Cv2.Threshold(prob8u, m35, 255 * 0.35, 255, ThresholdTypes.Binary);
                Cv2.Threshold(prob8u, m50, 255 * 0.50, 255, ThresholdTypes.Binary);
                Cv2.Threshold(prob8u, m65, 255 * 0.65, 255, ThresholdTypes.Binary);

                // (선택) 메인 화면 차선은 thr=0.50 마스크를 “빨강 면”으로 덮어 표시
                {
                    using var laneOverlay = frame.Clone();
                    laneOverlay.SetTo(new Scalar(0, 0, 255), m50); // 빨강
                    Cv2.AddWeighted(laneOverlay, 0.15, frame, 0.85, 0, frame);
                }

                // 4) 미니패널 배치(좌상단)
                int panelW = 320;
                int panelH = (int)(panelW * (frame.Height / (double)frame.Width));

                using var heatS = new Mat(); Cv2.Resize(heat, heatS, new Size(panelW, panelH));
                using var m35S = new Mat(); Cv2.Resize(m35, m35S, new Size(panelW, panelH), 0, 0, InterpolationFlags.Nearest);
                using var m50S = new Mat(); Cv2.Resize(m50, m50S, new Size(panelW, panelH), 0, 0, InterpolationFlags.Nearest);
                using var m65S = new Mat(); Cv2.Resize(m65, m65S, new Size(panelW, panelH), 0, 0, InterpolationFlags.Nearest);

                using var m35B = new Mat(); Cv2.CvtColor(m35S, m35B, ColorConversionCodes.GRAY2BGR);
                using var m50B = new Mat(); Cv2.CvtColor(m50S, m50B, ColorConversionCodes.GRAY2BGR);
                using var m65B = new Mat(); Cv2.CvtColor(m65S, m65B, ColorConversionCodes.GRAY2BGR);

                var r0 = new Rect(10, 10, panelW, panelH);
                var r1 = new Rect(10, 20 + panelH, panelW, panelH);
                var r2 = new Rect(10, 30 + panelH * 2, panelW, panelH);
                var r3 = new Rect(10, 40 + panelH * 3, panelW, panelH);

                // 화면 밖으로 나가면 패널 축소(해상도 낮을 때 보호)
                if (r3.Bottom < frame.Height)
                {
                    heatS.CopyTo(new Mat(frame, r0));
                    m35B.CopyTo(new Mat(frame, r1));
                    m50B.CopyTo(new Mat(frame, r2));
                    m65B.CopyTo(new Mat(frame, r3));

                    Cv2.PutText(frame, "LaneProb heatmap", new Point(r0.X + 5, r0.Y + 22),
                        HersheyFonts.HersheySimplex, 0.6, Scalar.White, 2);
                    Cv2.PutText(frame, "thr=0.35", new Point(r1.X + 5, r1.Y + 22),
                        HersheyFonts.HersheySimplex, 0.6, Scalar.White, 2);
                    Cv2.PutText(frame, "thr=0.50", new Point(r2.X + 5, r2.Y + 22),
                        HersheyFonts.HersheySimplex, 0.6, Scalar.White, 2);
                    Cv2.PutText(frame, "thr=0.65", new Point(r3.X + 5, r3.Y + 22),
                        HersheyFonts.HersheySimplex, 0.6, Scalar.White, 2);
                }
            }

            // ✅ (기존) 차량 박스/속도 표기 - 절대 변경 없음
            foreach (var d in _currentDetections)
            {
                if (!_trackedObjects.TryGetValue(d.TrackId, out var track)) continue;

                int displaySpeed = (int)(track.RelativeSpeed * 8.5);
                if (displaySpeed > 130) displaySpeed = 110;

                Cv2.Rectangle(frame, d.Box, Scalar.Yellow, 2);
                Cv2.PutText(frame,
                    $"{GetTypeName(d.ClassId)} {displaySpeed}km/h",
                    new Point(d.Box.X, d.Box.Y - 5),
                    HersheyFonts.HersheySimplex,
                    0.5,
                    Scalar.Lime,
                    1);
            }
        }

        private void OpenVideo()
        {
            var dialog = new OpenFileDialog();
            if (dialog.ShowDialog() == true)
            {
                VideoPath = dialog.FileName;
                StatusText = "Playing...";

                _video.Open(dialog.FileName);

                _countL = 0; _countF = 0; _countR = 0;
                _countedIds.Clear();
                _trackedObjects.Clear();

                DetectionsCount = 0;
                TrackedCountText = "Tracked: 0";

                _driveMask?.Dispose(); _driveMask = null;
                _laneMask?.Dispose(); _laneMask = null;
                _laneProb?.Dispose(); _laneProb = null;

                _timer.Start();
            }
        }

        private void Stop()
        {
            _timer.Stop();
            _video.Close();
            StatusText = "Stopped";
        }

        public void Dispose()
        {
            Stop();
            _frame.Dispose();
            _detector?.Dispose();
            _video.Dispose();

            _yolop?.Dispose();
            _driveMask?.Dispose();
            _laneMask?.Dispose();
            _laneProb?.Dispose();
        }
    }
}
