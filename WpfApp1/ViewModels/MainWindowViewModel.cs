using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using WpfApp1.Models;
using WpfApp1.Services;
using WpfApp1.Scripts;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using CvPoint = OpenCvSharp.Point;
using CvRect = OpenCvSharp.Rect;

namespace WpfApp1.ViewModels
{
    public class MainWindowViewModel : ViewModelBase, IDisposable
    {
        // ===== YOLOv8(차량) =====
        private const string BaseOnnxPath = "Scripts/yolov8n.onnx";
        private readonly VideoPlayerService _video = new();
        private YoloDetectService? _detector;

        // ===== YOLOP(차선/도로) =====
        private const string YolopOnnxPath = "Scripts/yolop-640-640.onnx";
        private YolopDetectService? _yolop;

        private Mat? _driveMask;   // CV_8UC1 0/255 (원본 크기)
        private Mat? _laneMask;    // CV_8UC1 0/255 (표시용/옵션)
        private Mat? _laneProb;    // CV_32FC1 0~1 (원본 크기)

        private readonly Mat _frame = new();
        private readonly Dictionary<int, TrackedObject> _trackedObjects = new();
        private readonly object _lock = new();
        private List<Detection> _currentDetections = new();

        private volatile bool _isBusy = false;      // 차량 Task
        private volatile bool _isLaneBusy = false;  // 차선 Task
        private readonly DispatcherTimer _timer = new();

        // ===== 프레임 타이밍 =====
        private TimeSpan _frameInterval = TimeSpan.FromMilliseconds(33); // 기본 30fps

        // ===== YOLOP 추론 빈도 제한 =====
        private readonly int _laneInferIntervalMs = 400; // 200  200ms마다(=5Hz) YOLOP
        private long _lastLaneInferMs = 0;

        // ===== LaneAnalysis 안정화( laneCount jump guard ) =====
        private LaneAnalyzer.LaneAnalysisResult? _laneAnalysis;        // raw 최신
        private LaneAnalyzer.LaneAnalysisResult? _laneAnalysisStable;  // 안정화된 결과
        private int _analysisCandidateCount = 0;
        private int _analysisCandidateLaneCount = -1;
        private const int ANALYSIS_SWITCH_CONFIRM = 4; // 3“laneCount 바뀜”이 3번 연속이면 전환

        // ===== 차량 lane 안정화(TrackId 기준) =====
        private class LaneStableState
        {
            public int StableLane = -1;
            public int CandidateLane = -1;
            public int CandidateCount = 0;
        }
        private readonly Dictionary<int, LaneStableState> _laneStates = new();
        private const int LANE_CHANGE_CONFIRM_FRAMES = 6; // 4 같은 lane이 4번 연속이면 변경

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
        // Lane UI (ComboBox)
        // =========================
        public ObservableCollection<int> TotalLaneOptions { get; } = new ObservableCollection<int>(Enumerable.Range(2, 7)); // 2..8
        public ObservableCollection<int> CurrentLaneOptions { get; } = new ObservableCollection<int>(Enumerable.Range(1, 8)); // 1..8

        private int _totalLanes = 4;
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

        private int _currentLane = 3;
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
        // LaneAnalyzer
        // =========================
        private readonly LaneAnalyzer _laneAnalyzer = new LaneAnalyzer();

        public MainWindowViewModel()
        {
            OpenVideoCommand = new RelayCommand(OpenVideo);
            StopCommand = new RelayCommand(Stop);

            _timer.Tick += Timer_Tick;
            _timer.Interval = _frameInterval;

            // ===== LaneAnalyzer 튜닝(세로 연결 중심) =====
            _laneAnalyzer.RoiYStartRatio = 0.55f;
            _laneAnalyzer.RoiXMarginRatio = 0.04f;

            _laneAnalyzer.ProbThreshold = 0.45f;

            _laneAnalyzer.PreferVerticalDilate = true;
            _laneAnalyzer.BoundaryDilateKx = 5;
            _laneAnalyzer.BoundaryDilateKy = 21;
            _laneAnalyzer.VerticalKernelHalfWidth = 1;

            _laneAnalyzer.CloseK = 5;
            _laneAnalyzer.OpenK = 3;

            _laneAnalyzer.MinRegionArea = 1200;
            _laneAnalyzer.MinRegionWidth = 80;
            _laneAnalyzer.BottomBandH = 20;

            InitializeDetector();
            InitializeYolop();
        }

        // =========================
        // UI 안전 업데이트
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
                _frameInterval = TimeSpan.FromMilliseconds(10);

            _timer.Interval = _frameInterval;
        }

        // =========================
        // laneProb 뒤집힘 자동 보정(벽 비율 기반)
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
            double r0 = RatioOverThr(prob32f, roi, thr);

            using var invTmp = new Mat(prob32f.Rows, prob32f.Cols, MatType.CV_32FC1);
            Cv2.Subtract(Scalar.All(1.0), prob32f, invTmp);
            double r1 = RatioOverThr(invTmp, roi, thr);

            if (r1 < r0) return invTmp.Clone();
            return prob32f;
        }

        // =========================
        // LaneAnalysis jump guard
        // =========================
        private void UpdateStableLaneAnalysis(LaneAnalyzer.LaneAnalysisResult? raw)
        {
            if (raw == null || raw.Regions == null || raw.Regions.Count == 0)
                return; // ✅ raw가 비었으면 stable 유지

            int rawCount = raw.Regions.Count;

            // ✅ “말도 안 되는 laneCount” 차단
            // - TotalLanes=2인데 rawCount=5 같은 케이스 방지
            if (rawCount > TotalLanes + 1) // +1은 가끔 분할이 하나 더 나오는 것을 허용
                return;

            // stable이 없으면 즉시 채택
            if (_laneAnalysisStable == null || _laneAnalysisStable.Regions == null || _laneAnalysisStable.Regions.Count == 0)
            {
                _laneAnalysisStable = raw;
                _analysisCandidateCount = 0;
                _analysisCandidateLaneCount = -1;
                return;
            }

            int stableCount = _laneAnalysisStable.Regions.Count;

            // 같은 laneCount면 “최신 raw”로 갱신(마스크 업데이트)
            if (rawCount == stableCount)
            {
                _laneAnalysisStable = raw;
                _analysisCandidateCount = 0;
                _analysisCandidateLaneCount = -1;
                return;
            }

            // laneCount가 달라진 경우: 연속 확인 후 전환
            if (_analysisCandidateLaneCount != rawCount)
            {
                _analysisCandidateLaneCount = rawCount;
                _analysisCandidateCount = 1;
            }
            else
            {
                _analysisCandidateCount++;
                if (_analysisCandidateCount >= ANALYSIS_SWITCH_CONFIRM)
                {
                    _laneAnalysisStable = raw;
                    _analysisCandidateCount = 0;
                    _analysisCandidateLaneCount = -1;
                }
            }
        }

        // =========================
        // Main Loop
        // =========================
        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (!_video.Read(_frame) || _frame.Empty())
            {
                Stop();
                return;
            }

            long nowMs = Environment.TickCount64;

            // =========================
            // 1) YOLOP (차선/도로) - ✅ 200ms마다만 실행
            // =========================
            if (!_isLaneBusy && _yolop != null && (nowMs - _lastLaneInferMs) >= _laneInferIntervalMs)
            {
                _lastLaneInferMs = nowMs;
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

                            _laneAnalyzer.TotalLanes = this.TotalLanes;
                            _laneAnalyzer.EgoLane = this.CurrentLane;
                            _laneAnalyzer.SetDrivableMask(_driveMask);

                            if (_laneProb != null && !_laneProb.Empty())
                            {
                                var roi = BuildLaneRoi(laneFrame.Width, laneFrame.Height);
                                var fixedProb = AutoFixLaneProbByWallRatio(_laneProb, roi, _laneAnalyzer.ProbThreshold);
                                if (!ReferenceEquals(fixedProb, _laneProb))
                                {
                                    _laneProb.Dispose();
                                    _laneProb = fixedProb;
                                }

                                _laneAnalysis = _laneAnalyzer.AnalyzeFromProb(_laneProb, laneFrame.Width, laneFrame.Height);

                                // ✅ jump guard로 stable 업데이트
                                UpdateStableLaneAnalysis(_laneAnalysis);
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

            // 삭제 대상 정리 + laneStates도 같이 제거
            var deleteIds = _trackedObjects
                .Where(kv => kv.Value.ShouldBeDeleted)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var id in deleteIds)
            {
                _trackedObjects.Remove(id);
                _laneStates.Remove(id); // ✅ lane 안정화 state도 같이 삭제
                _countedIds.Remove(id);
            }
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

        private int StabilizeLaneForTrack(int trackId, bool inLane, int laneNum)
        {
            if (!_laneStates.TryGetValue(trackId, out var st))
            {
                st = new LaneStableState();
                _laneStates[trackId] = st;
            }

            if (!inLane)
            {
                // 못 찾으면 이전값 유지
                return st.StableLane;
            }

            if (st.StableLane < 0)
            {
                // 첫 값은 즉시 채택
                st.StableLane = laneNum;
                st.CandidateLane = laneNum;
                st.CandidateCount = 0;
                return st.StableLane;
            }

            if (laneNum == st.StableLane)
            {
                // 안정 유지
                st.CandidateLane = laneNum;
                st.CandidateCount = 0;
                return st.StableLane;
            }

            // 다른 lane이 나왔을 때: 연속으로 충분히 나오면 변경
            if (st.CandidateLane != laneNum)
            {
                st.CandidateLane = laneNum;
                st.CandidateCount = 1;
            }
            else
            {
                st.CandidateCount++;
                if (st.CandidateCount >= LANE_CHANGE_CONFIRM_FRAMES)
                {
                    st.StableLane = st.CandidateLane;
                    st.CandidateCount = 0;
                }
            }

            return st.StableLane;
        }

        private void DrawOutput(Mat frame)
        {
            // 1) 도로(초록) 오버레이
            if (_driveMask != null && !_driveMask.Empty())
            {
                using var overlay = frame.Clone();
                overlay.SetTo(new Scalar(0, 255, 0), _driveMask);
                Cv2.AddWeighted(overlay, 0.20, frame, 0.80, 0, frame);
            }

            // 2) 빨간(표시용) 오버레이(너무 빨개지지 않게)
            if (_laneProb != null && !_laneProb.Empty())
            {
                using var prob8u = new Mat();
                _laneProb.ConvertTo(prob8u, MatType.CV_8UC1, 255.0);

                float displayThr = Math.Min(0.85f, _laneAnalyzer.ProbThreshold + 0.30f);

                using var m = new Mat();
                Cv2.Threshold(prob8u, m, 255 * displayThr, 255, ThresholdTypes.Binary);

                using var laneOverlay = frame.Clone();
                laneOverlay.SetTo(new Scalar(0, 0, 255), m);
                Cv2.AddWeighted(laneOverlay, 0.10, frame, 0.90, 0, frame);
            }

            // 3) Analyzer Debug는 ✅ stable 결과로 출력(값 튐 감소)
            if (_laneAnalysisStable != null)
                _laneAnalyzer.DrawDebug(frame, _laneAnalysisStable);

            // 4) 차량 박스 + Lane 라벨링(✅ stable 결과로 매핑)
            foreach (var d in _currentDetections)
            {
                if (!_trackedObjects.TryGetValue(d.TrackId, out var track)) continue;

                int displaySpeed = (int)(track.RelativeSpeed * 8.5);
                if (displaySpeed > 130) displaySpeed = 110;

                var bottomCenter = new CvPoint(
                    d.Box.X + d.Box.Width / 2,
                    d.Box.Y + d.Box.Height
                );

                bool inLane = false;
                int laneNum = -1;

                if (_laneAnalysisStable != null)
                    inLane = _laneAnalyzer.TryGetLaneNumberForPoint(_laneAnalysisStable, bottomCenter, out laneNum);

                int stableLane = StabilizeLaneForTrack(d.TrackId, inLane, laneNum);

                Cv2.Rectangle(frame, d.Box, Scalar.Yellow, 2);

                string label = $"{GetTypeName(d.ClassId)} {displaySpeed}km/h";
                if (stableLane > 0) label += $" | Lane:{stableLane}";
                else label += $" | Lane:?";

                Cv2.PutText(frame,
                    label,
                    new CvPoint(d.Box.X, Math.Max(0, d.Box.Y - 5)),
                    HersheyFonts.HersheySimplex,
                    0.5,
                    (stableLane > 0) ? Scalar.Lime : Scalar.Orange,
                    1);

                Cv2.Circle(frame, bottomCenter, 3, (stableLane > 0) ? Scalar.Lime : Scalar.Orange, -1);
            }

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
                    _laneStates.Clear();

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
                    _laneAnalysisStable = null;
                    _analysisCandidateCount = 0;
                    _analysisCandidateLaneCount = -1;

                    _lastLaneInferMs = 0;

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
            _laneAnalysisStable = null;
        }
    }
}
