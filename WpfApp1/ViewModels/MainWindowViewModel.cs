using Microsoft.Win32;
using MongoDB.Driver;
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

using System.Collections.Concurrent;
using System.Threading;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using CvPoint = OpenCvSharp.Point;

namespace WpfApp1.ViewModels
{
    public class MainWindowViewModel : ViewModelBase, IDisposable
    {
        private const string BaseOnnxPath = "Scripts/yolov8n.onnx";
        private const string YolopOnnxPath = "Scripts/yolop-640-640.onnx";

        // ✅ 차량 모델명 분류기
        private const string CarModelOnnxPath = "Scripts/best.onnx";
        private const string CarModelNamesPath = "Scripts/car_models.txt"; // (있으면 사용)

        private readonly VideoPlayerService _video = new();
        private YoloDetectService? _detector;
        private YolopDetectService? _yolop;

        private Mat? _driveMask;   // CV_8UC1 0/255 (orig size)
        private Mat? _laneProb;    // CV_32FC1 0~1 (orig size)

        private readonly Mat _frame = new();
        private readonly Dictionary<int, TrackedObject> _trackedObjects = new();
        private readonly object _lock = new();

        private List<Detection> _currentDetections = new();

        private volatile bool _isBusy = false;       // yolo v8
        private volatile bool _isLaneBusy = false;   // yolop
        private readonly DispatcherTimer _timer = new();

        // =========================
        // Speed/DB: MongoDB 컬렉션(옵션)
        // =========================
        private IMongoCollection<VehicleRecord>? _records;
        private bool _dbEnabled = true;
        public bool DbEnabled
        {
            get => _dbEnabled;
            set { _dbEnabled = value; OnPropertyChanged(); }
        }

        // ✅ DB 프리징 방지용 큐 + 워커
        private readonly ConcurrentQueue<VehicleRecord> _dbQueue = new();
        private CancellationTokenSource? _dbCts;

        private TimeSpan _frameInterval = TimeSpan.FromMilliseconds(33);

        private readonly int _laneInferIntervalMs = 200;
        private long _lastLaneInferMs = 0;

        // ✅ LaneAnalyzer result (backup point)
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
        // LaneAnalyzer (backup point)
        // =========================
        private readonly LaneAnalyzer _laneAnalyzer = new LaneAnalyzer();

        // =========================
        // ✅ Car Model Classifier (v2_Number 방식)
        // =========================
        private InferenceSession? _classSession;
        private readonly object _classLock = new();
        private string[] _carModelNames = Array.Empty<string>();

        // trackId -> modelName cache
        private readonly ConcurrentDictionary<int, string> _modelCache = new();
        private readonly ConcurrentDictionary<int, long> _modelLastInferMs = new();
        private readonly SemaphoreSlim _classSem = new(1, 1); // 동시 추론 1개 제한
        private const int MODEL_INFER_COOLDOWN_MS = 1200;      // 같은 track 재추론 제한

        // best.onnx meta 1회 출력
        private bool _classInfoPrinted = false;
        private readonly object _statusOnceLock = new();

        public MainWindowViewModel()
        {
            // MongoDB 연결 (실패해도 앱은 계속 동작) + timeout(프리징 방지)
            try
            {
                var settings = MongoClientSettings.FromConnectionString("mongodb://localhost:27017");
                settings.ServerSelectionTimeout = TimeSpan.FromMilliseconds(200);
                settings.ConnectTimeout = TimeSpan.FromMilliseconds(200);

                var client = new MongoClient(settings);
                var db = client.GetDatabase("TrafficControlDB");
                _records = db.GetCollection<VehicleRecord>("DetectionLogs");
            }
            catch
            {
                _records = null;
                _dbEnabled = false;
            }

            OpenVideoCommand = new RelayCommand(OpenVideo);
            StopCommand = new RelayCommand(Stop);

            _timer.Tick += Timer_Tick;
            _timer.Interval = _frameInterval;

            // ✅ LaneAnalyzer 튜닝
            _laneAnalyzer.RoiYStartRatio = 0.52f;
            _laneAnalyzer.RoiXMarginRatio = 0.02f;

            _laneAnalyzer.LaneProbThreshold = 0.45f;
            _laneAnalyzer.UseDrivableGate = true;
            _laneAnalyzer.GateErodeK = 9;

            _laneAnalyzer.LaneMaskOpenK = 3;
            _laneAnalyzer.LaneMaskCloseK = 5;

            _laneAnalyzer.CorridorBottomBandH = 60;

            _laneAnalyzer.SampleBandCount = 18;
            _laneAnalyzer.SampleYTopRatio = 0.30f;
            _laneAnalyzer.SampleYBottomRatio = 0.95f;
            _laneAnalyzer.PeakMinStrength = 6;
            _laneAnalyzer.PeakMinGapPx = 18;
            _laneAnalyzer.ExpectedWindowPx = 90;
            _laneAnalyzer.FollowWindowPx = 75;
            _laneAnalyzer.PreferLaneProbForEgoBoundaries = true;

            InitializeDetector();
            InitializeYolop();

            // ✅ CarModel Classifier init
            InitializeCarClassifier();

            // ✅ DB 워커 시작
            StartDbWorker();
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

        private void InitializeCarClassifier()
        {
            try
            {
                string onnxPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CarModelOnnxPath);
                if (!File.Exists(onnxPath))
                {
                    SafeSetStatus($"best.onnx 없음: {onnxPath}");
                    _classSession = null;
                    return;
                }

                // names 로드(있으면 사용)
                string namesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CarModelNamesPath);
                if (File.Exists(namesPath))
                {
                    var lines = File.ReadAllLines(namesPath)
                                    .Select(x => x.Trim())
                                    .Where(x => !string.IsNullOrWhiteSpace(x))
                                    .ToArray();
                    if (lines.Length > 0) _carModelNames = lines;
                }

                // 세션 생성
                _classSession = new InferenceSession(onnxPath);

                // meta 1회 표시
                PrintClassifierInfoOnce();
            }
            catch (Exception ex)
            {
                SafeSetStatus($"best.onnx 로드 실패: {ex.Message}");
                _classSession = null;
            }
        }

        private void PrintClassifierInfoOnce()
        {
            lock (_statusOnceLock)
            {
                if (_classInfoPrinted) return;
                _classInfoPrinted = true;
            }

            try
            {
                if (_classSession == null)
                {
                    SafeSetStatus("best.onnx: session null");
                    return;
                }

                var inName = _classSession.InputMetadata.Keys.First();
                var inMeta = _classSession.InputMetadata[inName];
                var inDims = string.Join("x", inMeta.Dimensions.Select(d => d.ToString()));
                var inType = inMeta.ElementType?.ToString() ?? "unknown";

                var outName = _classSession.OutputMetadata.Keys.First();
                var outMeta = _classSession.OutputMetadata[outName];
                var outDims = string.Join("x", outMeta.Dimensions.Select(d => d.ToString()));
                var outType = outMeta.ElementType?.ToString() ?? "unknown";

                SafeSetStatus($"best.onnx OK | IN:{inName}[{inDims}] {inType} | OUT:{outName}[{outDims}] {outType}");
            }
            catch (Exception ex)
            {
                SafeSetStatus($"best.onnx meta fail: {ex.Message}");
            }
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

            // 1) YOLOP (lane/drivable) 200ms
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
                                _laneAnalysisStable = _laneAnalyzer.Analyze(_laneProb, laneFrame.Width, laneFrame.Height);
                            else
                                _laneAnalysisStable = null;
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

            // 2) YOLOv8 (vehicle)
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
                            TrackAndMatch(dets, time, vehicleFrame); // ✅ frame 같이 넘겨서 crop 가능
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

            // 3) Draw
            lock (_lock)
            {
                UpdateCounting(_frame.Width, _frame.Height);
                DrawOutput(_frame);
            }

            var bmp = _frame.ToBitmapSource();
            Ui(() => FrameImage = bmp);
        }

        private void TrackAndMatch(List<Detection> detections, double timeMsec, Mat frameForCrop)
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

                    UpdateSpeedAndDirection(track);

                    // ✅ DB는 큐에 넣기만
                    TryInsertRecord(detections[bestIdx], track);

                    // ✅ Car 모델명 분류 (v2_Number 방식)
                    TryQueueCarModelInference(detections[bestIdx], track.Id, frameForCrop);

                    usedDets.Add(bestIdx);
                }
                else track.Missed();
            }

            foreach (var d in detections.Where((_, i) => !usedDets.Contains(i)))
            {
                var newTrack = new TrackedObject(d, timeMsec);
                d.TrackId = newTrack.Id;
                _trackedObjects[newTrack.Id] = newTrack;

                // 새 트랙도 모델명 분류 시도(카인 경우)
                TryQueueCarModelInference(d, newTrack.Id, frameForCrop);
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

                _modelCache.TryRemove(id, out _);
                _modelLastInferMs.TryRemove(id, out _);
            }
        }

        private void TryQueueCarModelInference(Detection det, int trackId, Mat frame)
        {
            // 분류기 없으면 스킵
            if (_classSession == null) return;

            // Car만 (너가 원한 Acura 같은 모델명은 Car 모델 분류기 기준)
            if (det.ClassId != 2) return;

            // 박스 너무 작으면 스킵
            if (det.Box.Width < 30 || det.Box.Height < 30) return;

            long now = Environment.TickCount64;

            // 이미 모델명 있으면 너무 자주 갱신하지 않기
            if (_modelLastInferMs.TryGetValue(trackId, out var last) && (now - last) < MODEL_INFER_COOLDOWN_MS)
                return;

            _modelLastInferMs[trackId] = now;

            // crop 영역 클립
            var r = ClipRect(det.Box, frame.Width, frame.Height);
            if (r.Width <= 0 || r.Height <= 0) return;

            // crop 복사 (비동기에서 frame 수명 문제 방지)
            Mat crop;
            try
            {
                crop = new Mat(frame, r).Clone();
            }
            catch
            {
                return;
            }

            // 비동기 분류 (동시 1개 제한)
            Task.Run(async () =>
            {
                try
                {
                    await _classSem.WaitAsync();

                    // 너무 오래된 crop이면 의미 없으니 스킵할 수도 있음(여기선 그대로)
                    string name = GetSpecificCarModel(crop);

                    if (!string.IsNullOrWhiteSpace(name) && name != "ERR")
                        _modelCache[trackId] = name;
                }
                catch { }
                finally
                {
                    crop.Dispose();
                    _classSem.Release();
                }
            });
        }

        private static OpenCvSharp.Rect ClipRect(OpenCvSharp.Rect r, int w, int h)
        {
            int x = Math.Max(0, r.X);
            int y = Math.Max(0, r.Y);
            int x2 = Math.Min(w, r.X + r.Width);
            int y2 = Math.Min(h, r.Y + r.Height);

            int ww = x2 - x;
            int hh = y2 - y;
            if (ww <= 0 || hh <= 0) return new OpenCvSharp.Rect(0, 0, 0, 0);
            return new OpenCvSharp.Rect(x, y, ww, hh);
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

        // ✅ 2번 프로젝트 스타일: 배경 박스 + 텍스트
        private static void DrawLabelBox(Mat frame, string text, int x, int y, double scale, Scalar bg, Scalar fg)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            var size = Cv2.GetTextSize(text, HersheyFonts.HersheySimplex, scale, 1, out int baseLine);
            int pad = 4;

            int rectX = x;
            int rectY = y - size.Height - pad;
            int rectW = size.Width + pad * 2;
            int rectH = size.Height + baseLine + pad * 2;

            rectY = Math.Max(0, rectY);
            rectX = Math.Max(0, rectX);

            if (rectX + rectW > frame.Width) rectW = frame.Width - rectX;
            if (rectY + rectH > frame.Height) rectH = frame.Height - rectY;
            if (rectW <= 0 || rectH <= 0) return;

            Cv2.Rectangle(frame, new OpenCvSharp.Rect(rectX, rectY, rectW, rectH), bg, -1);
            Cv2.PutText(frame, text, new CvPoint(rectX + pad, rectY + rectH - baseLine - pad),
                        HersheyFonts.HersheySimplex, scale, fg, 1, LineTypes.AntiAlias);
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

            // 2) LaneProb overlay
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

            // 3) LaneAnalyzer boundary/labels
            if (_laneAnalysisStable != null)
                _laneAnalyzer.DrawOnFrame(frame, _laneAnalysisStable);

            // 4) Vehicles -> (1줄:속도+모델명 / 2줄:Car|Lane)
            foreach (var d in _currentDetections)
            {
                if (!_trackedObjects.TryGetValue(d.TrackId, out var track)) continue;

                // 기존 속도 로직 유지(너 코드)
                double kmh = track.RelativeSpeed * 8.5;
                if (kmh > 130) kmh = 110;

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

                // ✅ 모델명 (trackId 캐시에서)
                _modelCache.TryGetValue(d.TrackId, out var modelName);
                modelName ??= "";

                // ✅ 1줄: "속도 + 모델명"
                string line1 = $"{kmh:0.0}km/h";
                if (!string.IsNullOrWhiteSpace(modelName))
                    line1 += $"  {modelName}";

                // ✅ 2줄: "Car/Truck/Bus | Lane:#"
                string typeText = GetTypeName(d.ClassId);
                string laneText = (stableLane > 0) ? $"Lane:{stableLane}" : "Lane:?";
                string line2 = $"{typeText} | {laneText}";

                bool speedWarn = kmh > 100.0;
                var bg1 = speedWarn ? Scalar.Red : Scalar.Black;

                int x = d.Box.X;
                int yTop = Math.Max(10, d.Box.Y);

                DrawLabelBox(frame, line1, x, yTop, 0.45, bg1, Scalar.White);
                DrawLabelBox(frame, line2, x, yTop + 18, 0.45, Scalar.Black, (stableLane > 0) ? Scalar.Lime : Scalar.Orange);

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

                    _modelCache.Clear();
                    _modelLastInferMs.Clear();

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
            _dbCts?.Cancel();
            _dbCts = null;

            Stop();
            _frame.Dispose();
            _detector?.Dispose();
            _video.Dispose();

            _yolop?.Dispose();
            _driveMask?.Dispose();
            _laneProb?.Dispose();

            _laneAnalysisStable = null;

            try { _classSession?.Dispose(); } catch { }
        }

        // DB Worker
        private void StartDbWorker()
        {
            if (_dbCts != null) return;

            _dbCts = new CancellationTokenSource();
            var token = _dbCts.Token;

            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (!_dbEnabled || _records == null)
                        {
                            await Task.Delay(300, token);
                            continue;
                        }

                        if (_dbQueue.TryDequeue(out var rec))
                        {
                            await _records.InsertOneAsync(rec, cancellationToken: token);
                        }
                        else
                        {
                            await Task.Delay(10, token);
                        }
                    }
                    catch
                    {
                        await Task.Delay(200, token);
                    }
                }
            }, token);
        }

        // Speed/DB helpers
        private void UpdateSpeedAndDirection(TrackedObject track)
        {
            // 필요 시 확장
        }

        private void TryInsertRecord(Detection det, TrackedObject track)
        {
            if (!_dbEnabled) return;
            if (_records == null) return;

            try
            {
                var r = new VehicleRecord
                {
                    DetectTime = DateTime.Now,
                    TrackId = track.Id,
                    ClassId = det.ClassId,
                    ClassName = det.ClassName ?? string.Empty,
                    Confidence = det.Confidence,
                    BBoxX = det.Box.X,
                    BBoxY = det.Box.Y,
                    BBoxW = det.Box.Width,
                    BBoxH = det.Box.Height,
                    Speed = (int)Math.Round(track.RelativeSpeed),
                    SpeedKmh = track.RelativeSpeed,
                    Direction = null,
                    IsViolation = false,
                    ViolationReason = null,
                    LicensePlate = null,
                };

                _dbQueue.Enqueue(r);
                while (_dbQueue.Count > 2000 && _dbQueue.TryDequeue(out _)) { }
            }
            catch { }
        }

        // ✅ 차량 모델명 분류 (입/출력 shape 자동 대응 + float/int64 대응)
        private string GetSpecificCarModel(Mat cropImg)
        {
            try
            {
                if (_classSession == null) return "";

                // 입력 메타
                var inputName = _classSession.InputMetadata.Keys.First();
                var dims = _classSession.InputMetadata[inputName].Dimensions.ToArray(); // 보통 [1,3,H,W]
                if (dims.Length < 4) return "";

                int H = (dims[2] > 0) ? dims[2] : 160;
                int W = (dims[3] > 0) ? dims[3] : 160;

                using var rgbImg = new Mat();
                Cv2.CvtColor(cropImg, rgbImg, ColorConversionCodes.BGR2RGB);

                using var resized = new Mat();
                Cv2.Resize(rgbImg, resized, new OpenCvSharp.Size(W, H));

                var input = new DenseTensor<float>(new[] { 1, 3, H, W });

                for (int y = 0; y < H; y++)
                {
                    for (int x = 0; x < W; x++)
                    {
                        var p = resized.At<Vec3b>(y, x); // RGB
                        input[0, 0, y, x] = p.Item0 / 255f;
                        input[0, 1, y, x] = p.Item1 / 255f;
                        input[0, 2, y, x] = p.Item2 / 255f;
                    }
                }

                lock (_classLock)
                {
                    using var results = _classSession.Run(new[]
                    {
                NamedOnnxValue.CreateFromTensor(inputName, input)
            });

                    var first = results.First();

                    // ✅ 1) float 출력 먼저 시도
                    try
                    {
                        var tf = first.AsTensor<float>();
                        var outDims = tf.Dimensions.ToArray();
                        if (outDims.Length != 2 || outDims[0] != 1) return "";

                        int n = outDims[1];
                        var scores = tf.ToArray();

                        int bestIdx = 0;
                        float best = scores[0];
                        for (int i = 1; i < scores.Length; i++)
                        {
                            if (scores[i] > best) { best = scores[i]; bestIdx = i; }
                        }

                        if (_carModelNames != null && _carModelNames.Length > bestIdx)
                            return _carModelNames[bestIdx];

                        return $"Model#{bestIdx}";
                    }
                    catch
                    {
                        // ✅ 2) float이 아니면 long 출력 시도
                        var tl = first.AsTensor<long>();
                        var outDims = tl.Dimensions.ToArray();
                        if (outDims.Length != 2 || outDims[0] != 1) return "";

                        int n = outDims[1];
                        var scores = tl.ToArray();

                        int bestIdx = 0;
                        long best = scores[0];
                        for (int i = 1; i < scores.Length; i++)
                        {
                            if (scores[i] > best) { best = scores[i]; bestIdx = i; }
                        }

                        if (_carModelNames != null && _carModelNames.Length > bestIdx)
                            return _carModelNames[bestIdx];

                        return $"Model#{bestIdx}";
                    }
                }
            }
            catch
            {
                return "ERR";
            }
        }

    }
}
