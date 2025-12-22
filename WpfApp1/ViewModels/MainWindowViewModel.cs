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
        private const string BaseOnnxPath = "Scripts/yolov8n.onnx";
        private readonly VideoPlayerService _video = new();
        private YoloDetectService? _detector;

        private const string YolopOnnxPath = "Scripts/yolop-640-640.onnx";
        private YolopDetectService? _yolop;

        private Mat? _driveMask;   // CV_8UC1 0/255
        private Mat? _laneProb;    // CV_32FC1 0~1

        private readonly Mat _frame = new();
        private readonly Dictionary<int, TrackedObject> _trackedObjects = new();
        private readonly object _lock = new();
        private List<Detection> _currentDetections = new();

        private volatile bool _isBusy = false;
        private volatile bool _isLaneBusy = false;
        private readonly DispatcherTimer _timer = new();

        private TimeSpan _frameInterval = TimeSpan.FromMilliseconds(33);

        private readonly int _laneInferIntervalMs = 200;
        private long _lastLaneInferMs = 0;

        private LaneAnalyzer.LaneAnalysisResult? _laneAnalysisStable;

        private class LaneStableState
        {
            public int StableLane = -1;
            public int CandidateLane = -1;
            public int CandidateCount = 0;
        }
        private readonly Dictionary<int, LaneStableState> _laneStates = new();
        private const int LANE_CHANGE_CONFIRM_FRAMES = 3;

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

        private int _totalLanes = 5;
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

        private int _currentLane = 4;
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
        // LaneAnalyzer (NEW)
        // =========================
        private readonly LaneAnalyzer _laneAnalyzer = new LaneAnalyzer();

        public MainWindowViewModel()
        {
            OpenVideoCommand = new RelayCommand(OpenVideo);
            StopCommand = new RelayCommand(Stop);

            _timer.Tick += Timer_Tick;
            _timer.Interval = _frameInterval;

            // ===== 튜닝: “차로 등분 + laneLine 보정” =====
            _laneAnalyzer.RoiYStartRatio = 0.52f;
            _laneAnalyzer.RoiXMarginRatio = 0.02f;

            _laneAnalyzer.LaneProbThreshold = 0.45f;
            _laneAnalyzer.UseDrivableGate = true;
            _laneAnalyzer.GateErodeK = 9;

            _laneAnalyzer.LaneMaskOpenK = 3;
            _laneAnalyzer.LaneMaskCloseK = 5;

            _laneAnalyzer.CorridorBottomBandH = 60;

            InitializeDetector();
            InitializeYolop();
        }

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

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (!_video.Read(_frame) || _frame.Empty())
            {
                Stop();
                return;
            }

            long nowMs = Environment.TickCount64;

            // =========================
            // 1) YOLOP (lane/drivable) 200ms
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
                            _laneProb?.Dispose();

                            _driveMask = r.DrivableMaskOrig;
                            _laneProb = r.LaneProbOrig;

                            _laneAnalyzer.TotalLanes = this.TotalLanes;
                            _laneAnalyzer.EgoLane = this.CurrentLane;
                            _laneAnalyzer.SetDrivableMask(_driveMask);

                            if (_laneProb != null && !_laneProb.Empty())
                            {
                                // ✅ 항상 “경계선 기반 결과”를 만들기 때문에 stable이 덜 흔들림
                                _laneAnalysisStable = _laneAnalyzer.Analyze(_laneProb, laneFrame.Width, laneFrame.Height);
                            }
                            else
                            {
                                _laneAnalysisStable = null;
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
            // 2) YOLOv8 (vehicle)
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
            // 3) Draw
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

            var deleteIds = _trackedObjects
                .Where(kv => kv.Value.ShouldBeDeleted)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var id in deleteIds)
            {
                _trackedObjects.Remove(id);
                _laneStates.Remove(id);
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

        private int StabilizeLaneForTrack(int trackId, bool hasLane, int laneNum)
        {
            if (!_laneStates.TryGetValue(trackId, out var st))
            {
                st = new LaneStableState();
                _laneStates[trackId] = st;
            }

            if (!hasLane) return st.StableLane;

            if (st.StableLane < 0)
            {
                st.StableLane = laneNum;
                st.CandidateLane = laneNum;
                st.CandidateCount = 0;
                return st.StableLane;
            }

            if (laneNum == st.StableLane)
            {
                st.CandidateLane = laneNum;
                st.CandidateCount = 0;
                return st.StableLane;
            }

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
            // 1) Drivable overlay
            if (_driveMask != null && !_driveMask.Empty())
            {
                using var overlay = frame.Clone();
                overlay.SetTo(new Scalar(0, 255, 0), _driveMask);
                Cv2.AddWeighted(overlay, 0.20, frame, 0.80, 0, frame);
            }

            // 2) LaneProb overlay (표시용만, 얇게)
            if (_laneProb != null && !_laneProb.Empty())
            {
                using var prob8u = new Mat();
                _laneProb.ConvertTo(prob8u, MatType.CV_8UC1, 255.0);

                float displayThr = Math.Min(0.85f, _laneAnalyzer.LaneProbThreshold + 0.25f);

                using var m = new Mat();
                Cv2.Threshold(prob8u, m, 255 * displayThr, 255, ThresholdTypes.Binary);

                using var laneOverlay = frame.Clone();
                laneOverlay.SetTo(new Scalar(0, 0, 255), m);
                Cv2.AddWeighted(laneOverlay, 0.10, frame, 0.90, 0, frame);
            }

            // 3) Analyzer boundary/labels
            if (_laneAnalysisStable != null)
                _laneAnalyzer.DrawOnFrame(frame, _laneAnalysisStable);

            // 4) Vehicles -> lane mapping
            foreach (var d in _currentDetections)
            {
                if (!_trackedObjects.TryGetValue(d.TrackId, out var track)) continue;

                int displaySpeed = (int)(track.RelativeSpeed * 8.5);
                if (displaySpeed > 130) displaySpeed = 110;

                var bottomCenter = new CvPoint(
                    d.Box.X + d.Box.Width / 2,
                    d.Box.Y + d.Box.Height
                );

                bool hasLane = false;
                int laneNum = -1;

                if (_laneAnalysisStable != null)
                    hasLane = _laneAnalyzer.TryGetLaneNumberForPoint(_laneAnalysisStable, bottomCenter, out laneNum);

                int stableLane = StabilizeLaneForTrack(d.TrackId, hasLane, laneNum);

                Cv2.Rectangle(frame, d.Box, Scalar.Yellow, 2);

                string label = $"{GetTypeName(d.ClassId)} {displaySpeed}km/h";
                label += (stableLane > 0) ? $" | Lane:{stableLane}" : " | Lane:?";

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
                    _laneProb?.Dispose(); _laneProb = null;

                    _laneAnalysisStable = null;
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
            _laneProb?.Dispose();

            _laneAnalysisStable = null;
        }
    }
}
