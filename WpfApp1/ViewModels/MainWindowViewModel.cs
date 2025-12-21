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
using WpfApp1.Scripts;
using System.Collections.ObjectModel;
using System.Windows;

using CvPoint = OpenCvSharp.Point;
using CvRect = OpenCvSharp.Rect;

namespace WpfApp1.ViewModels
{
    public class MainWindowViewModel : ViewModelBase, IDisposable
    {
        // ===== 기존 YOLOv8(차량 검출) =====
        private const string BaseOnnxPath = "Scripts/yolov8n.onnx";
        private readonly VideoPlayerService _video = new();
        private YoloDetectService? _detector;

        // ===== YOLOP(차선/도로) =====
        private const string YolopOnnxPath = "Scripts/yolop-640-640.onnx";
        private YolopDetectService? _yolop;

        private Mat? _driveMask;   // CV_8UC1 0/255 (원본 크기)
        private Mat? _laneMask;    // CV_8UC1 0/255 (원본 크기) - 현재 표시용만
        private Mat? _laneProb;    // CV_32FC1 0~1 (원본 크기)

        private readonly Mat _frame = new();
        private readonly Dictionary<int, TrackedObject> _trackedObjects = new();
        private readonly object _lock = new();
        private List<Detection> _currentDetections = new();

        private volatile bool _isBusy = false;      // 차량검출 Task
        private volatile bool _isLaneBusy = false;  // 차선/도로 Task
        private readonly DispatcherTimer _timer = new();

        // ===== 프레임 타이밍 =====
        private TimeSpan _frameInterval = TimeSpan.FromMilliseconds(33); // 기본 30fps

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

        // =========================
        // ✅ Lane UI (ComboBox)
        // =========================
        public ObservableCollection<int> TotalLaneOptions { get; } = new ObservableCollection<int>(Enumerable.Range(2, 7)); // 2..8
        public ObservableCollection<int> CurrentLaneOptions { get; } = new ObservableCollection<int>(Enumerable.Range(1, 8)); // 1..8

        private int _totalLanes = 4; // 기본 4
        public int TotalLanes
        {
            get => _totalLanes;
            set
            {
                int v = Math.Clamp(value, 2, 8);
                if (_totalLanes == v) return;
                _totalLanes = v;
                OnPropertyChanged();

                if (CurrentLane > _totalLanes) CurrentLane = _totalLanes;
                if (CurrentLane < 1) CurrentLane = 1;

                OnPropertyChanged(nameof(CurrentLaneLabel));
            }
        }

        private int _currentLane = 3; // 기본 3
        public int CurrentLane
        {
            get => _currentLane;
            set
            {
                int v = Math.Clamp(value, 1, TotalLanes);
                if (_currentLane == v) return;
                _currentLane = v;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CurrentLaneLabel));
            }
        }

        public string CurrentLaneLabel => $"Lane {CurrentLane}/{TotalLanes}";

        // =========================
        // ✅ LaneAnalyzer
        // =========================
        private readonly LaneAnalyzer _laneAnalyzer = new LaneAnalyzer();
        private LaneAnalyzer.LaneAnalysisResult? _laneAnalysis;

        public MainWindowViewModel()
        {
            OpenVideoCommand = new RelayCommand(OpenVideo);
            StopCommand = new RelayCommand(Stop);

            _timer.Tick += Timer_Tick;
            _timer.Interval = _frameInterval; // fps 기반으로 갱신됨

            // ✅ Analyzer 기본 튜닝값
            _laneAnalyzer.RoiYStartRatio = 0.55f;
            _laneAnalyzer.RoiXMarginRatio = 0.04f;

            // ✅ “벽이 너무 많아서 lanes=0”이면 thr를 올려야 함
            _laneAnalyzer.ProbThreshold = 0.45f;

            // ✅ 핵심: 벽 딜레이트를 “세로 연결 중심”으로 (LaneAnalyzer.cs에서 구현)
            _laneAnalyzer.PreferVerticalDilate = true;
            _laneAnalyzer.BoundaryDilateKx = 5;   // 가로는 얇게
            _laneAnalyzer.BoundaryDilateKy = 21;  // 세로는 길게
            _laneAnalyzer.VerticalKernelHalfWidth = 0; // 0이면 1px 세로줄, 1이면 3px(추천)

            // 너무 두꺼운 연결 방지
            _laneAnalyzer.CloseK = 5;
            _laneAnalyzer.OpenK = 3;

            _laneAnalyzer.MinRegionArea = 1200;
            _laneAnalyzer.MinRegionWidth = 80;
            _laneAnalyzer.BottomBandH = 20; // 1은 너무 얇아 sortX가 흔들릴 수 있음

            InitializeDetector();
            InitializeYolop();
        }

        // =========================
        // UI 안전 업데이트 헬퍼
        // =========================
        private void Ui(Action a)
        {
            try
            {
                var disp = Application.Current?.Dispatcher;
                if (disp == null || disp.CheckAccess()) a();
                else disp.Invoke(a);
            }
            catch { }
        }

        private void SafeSetStatus(string msg) => Ui(() => StatusText = msg);

        private void InitializeDetector()
        {
            string fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, BaseOnnxPath);
            if (File.Exists(fullPath))
                _detector = new YoloDetectService(fullPath, 640, 0.25f, 0.45f);
            else
                SafeSetStatus($"YOLOv8 ONNX 없음: {fullPath}");
        }

        private void InitializeYolop()
        {
            string fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, YolopOnnxPath);
            if (File.Exists(fullPath))
                _yolop = new YolopDetectService(fullPath, 640, 0.35f, 0.45f);
            else
                SafeSetStatus($"YOLOP ONNX 없음: {fullPath}");
        }

        private void ApplyTimerIntervalFromVideo()
        {
            double fps = _video.Fps;
            if (fps < 1 || fps > 240) fps = 30;

            _frameInterval = TimeSpan.FromMilliseconds(1000.0 / fps);
            if (_frameInterval < TimeSpan.FromMilliseconds(10))
                _frameInterval = TimeSpan.FromMilliseconds(10); // UI 과부하 방지

            _timer.Interval = _frameInterval;
        }

        // =========================
        // ✅ laneProb 뒤집힘 자동 보정 (벽 비율 기반)
        // =========================
        private CvRect BuildLaneRoi(int frameW, int frameH)
        {
            int y0 = (int)(frameH * _laneAnalyzer.RoiYStartRatio);
            int xMargin = (int)(frameW * _laneAnalyzer.RoiXMarginRatio);

            y0 = Math.Clamp(y0, 0, frameH - 1);
            xMargin = Math.Clamp(xMargin, 0, frameW / 3);

            int x0 = xMargin;
            int w = Math.Max(1, frameW - xMargin * 2);
            int h = Math.Max(1, frameH - y0);

            return new CvRect(x0, y0, w, h);
        }

        private static double RatioOverThr(Mat prob32f, CvRect roi, float thr)
        {
            int step = 8;
            long over = 0, cnt = 0;

            int y0 = Math.Max(0, roi.Y);
            int y1 = Math.Min(prob32f.Rows, roi.Bottom);
            int x0 = Math.Max(0, roi.X);
            int x1 = Math.Min(prob32f.Cols, roi.Right);

            for (int y = y0; y < y1; y += step)
            {
                for (int x = x0; x < x1; x += step)
                {
                    float v = prob32f.At<float>(y, x);
                    if (float.IsNaN(v)) continue;
                    if (v >= thr) over++;
                    cnt++;
                }
            }

            return cnt > 0 ? (double)over / cnt : 0.0;
        }

        private static Mat AutoFixLaneProbByWallRatio(Mat prob32f, CvRect roi, float thr)
        {
            // 원본 prob에서 벽 비율
            double r0 = RatioOverThr(prob32f, roi, thr);

            // 1-prob에서 벽 비율
            using var invTmp = new Mat(prob32f.Rows, prob32f.Cols, MatType.CV_32FC1);
            Cv2.Subtract(Scalar.All(1.0), prob32f, invTmp);
            double r1 = RatioOverThr(invTmp, roi, thr);

            // ✅ 더 “희소한 벽”이 정상일 가능성이 큼
            if (r1 < r0)
                return invTmp.Clone();

            return prob32f;
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (!_video.Read(_frame) || _frame.Empty())
            {
                Stop();
                return;
            }

            // =========================
            // 1) YOLOP (차선/도로)
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
                            _laneProb = r.LaneProbOrig;

                            // ✅ UI 입력값을 Analyzer에 전달
                            _laneAnalyzer.TotalLanes = this.TotalLanes;
                            _laneAnalyzer.EgoLane = this.CurrentLane;

                            // drive mask gate
                            _laneAnalyzer.SetDrivableMask(_driveMask);

                            if (_laneProb != null && !_laneProb.Empty())
                            {
                                // ✅ prob 뒤집힘 자동 보정(벽 비율 비교)
                                var roi = BuildLaneRoi(_frame.Width, _frame.Height);
                                var fixedProb = AutoFixLaneProbByWallRatio(_laneProb, roi, _laneAnalyzer.ProbThreshold);
                                if (!ReferenceEquals(fixedProb, _laneProb))
                                {
                                    _laneProb.Dispose();
                                    _laneProb = fixedProb;
                                }

                                _laneAnalysis = _laneAnalyzer.AnalyzeFromProb(_laneProb, _frame.Width, _frame.Height);
                            }
                            else
                            {
                                _laneAnalysis = null;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        SafeSetStatus($"YOLOP 오류: {ex.Message}");
                    }
                    finally
                    {
                        laneFrame.Dispose();
                        _isLaneBusy = false;
                    }
                });
            }

            // =========================
            // 2) YOLOv8 (차량)
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
                        }

                        Ui(() =>
                        {
                            DetectionsCount = dets.Count;
                            TrackedCountText = $"Tracked: {_trackedObjects.Count}";
                        });
                    }
                    catch (Exception ex)
                    {
                        SafeSetStatus($"YOLOv8 오류: {ex.Message}");
                    }
                    finally
                    {
                        vehicleFrame.Dispose();
                        _isBusy = false;
                    }
                });
            }

            // =========================
            // 3) Draw + Counting
            // =========================
            lock (_lock)
            {
                UpdateCounting(_frame.Width, _frame.Height);
                DrawOutput(_frame);
            }

            var bmp = _frame.ToBitmapSource();
            Ui(() => FrameImage = bmp);
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

                var center = new CvPoint(track.LastBox.X + track.LastBox.Width / 2, track.LastBox.Y + track.LastBox.Height / 2);

                if (center.Y > lineY)
                {
                    string dir = center.X < w * 0.35 ? "Left" : (center.X < w * 0.65 ? "Front" : "Right");

                    if (dir == "Left") _countL++;
                    else if (dir == "Front") _countF++;
                    else _countR++;

                    _countedIds.Add(track.Id);
                }
            }

            Ui(() => CountText = $"L:{_countL} | F:{_countF} | R:{_countR}");
        }

        private string GetTypeName(int id) => id switch
        {
            2 => "Car",
            5 => "Bus",
            7 => "Truck",
            3 => "Motor",
            _ => "Vehicle"
        };

        private void DrawOutput(Mat frame)
        {
            // 1) 도로(초록) 오버레이
            if (_driveMask != null && !_driveMask.Empty())
            {
                using var overlay = frame.Clone();
                overlay.SetTo(new Scalar(0, 255, 0), _driveMask);
                Cv2.AddWeighted(overlay, 0.20, frame, 0.80, 0, frame);
            }

            // 2) 빨간 마스크(표시용) - 분석용 thr와 분리
            if (_laneProb != null && !_laneProb.Empty())
            {
                using var prob8u = new Mat();
                _laneProb.ConvertTo(prob8u, MatType.CV_8UC1, 255.0);

                // ✅ 표시용은 더 빡세게(덜 빨개짐)
                float displayThr = Math.Min(0.85f, _laneAnalyzer.ProbThreshold + 0.30f);

                using var m = new Mat();
                Cv2.Threshold(prob8u, m, 255 * displayThr, 255, ThresholdTypes.Binary);

                using var laneOverlay = frame.Clone();
                laneOverlay.SetTo(new Scalar(0, 0, 255), m);
                Cv2.AddWeighted(laneOverlay, 0.10, frame, 0.90, 0, frame);
            }

            // 3) Analyzer Debug (곡선 + 차선번호 라벨)
            if (_laneAnalysis != null)
                _laneAnalyzer.DrawDebug(frame, _laneAnalysis);

            // 4) 차량 박스 (차선 라벨링은 다음 단계에서: lane region 안에 center가 들어가는지로 매핑)
            foreach (var d in _currentDetections)
            {
                if (!_trackedObjects.TryGetValue(d.TrackId, out var track)) continue;

                int displaySpeed = (int)(track.RelativeSpeed * 8.5);
                if (displaySpeed > 130) displaySpeed = 110;

                Cv2.Rectangle(frame, d.Box, Scalar.Yellow, 2);
                Cv2.PutText(frame,
                    $"{GetTypeName(d.ClassId)} {displaySpeed}km/h",
                    new CvPoint(d.Box.X, d.Box.Y - 5),
                    HersheyFonts.HersheySimplex,
                    0.5,
                    Scalar.Lime,
                    1);
            }

            // UI 값 표시
            Cv2.PutText(frame, $"UI Lane: {CurrentLane}/{TotalLanes}",
                new CvPoint(20, 40), HersheyFonts.HersheySimplex, 0.8, Scalar.White, 2);
        }

        private void OpenVideo()
        {
            var dialog = new OpenFileDialog();
            if (dialog.ShowDialog() == true)
            {
                try
                {
                    VideoPath = dialog.FileName;
                    SafeSetStatus("Opening...");

                    _video.Open(dialog.FileName);
                    ApplyTimerIntervalFromVideo();

                    Ui(() => StatusText = "Playing...");

                    _countL = 0; _countF = 0; _countR = 0;
                    _countedIds.Clear();
                    _trackedObjects.Clear();

                    Ui(() =>
                    {
                        DetectionsCount = 0;
                        TrackedCountText = "Tracked: 0";
                        CountText = "L:0 | F:0 | R:0";
                    });

                    _driveMask?.Dispose(); _driveMask = null;
                    _laneMask?.Dispose(); _laneMask = null;
                    _laneProb?.Dispose(); _laneProb = null;

                    _laneAnalysis = null;

                    _timer.Start();
                }
                catch (Exception ex)
                {
                    SafeSetStatus($"영상 열기 실패: {ex.Message}");
                    Stop();
                }
            }
        }

        private void Stop()
        {
            _timer.Stop();
            _video.Close();
            SafeSetStatus("Stopped");
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

            _laneAnalysis = null;
        }
    }
}
