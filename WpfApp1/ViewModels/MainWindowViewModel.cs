// MainWindowViewModel.cs (FULL REPLACEMENT)
// - YOLOv8 detect + YOLOP lane + Speed/DB + CarModel(best.onnx) classification integrated
// - Car model classification runs ONCE per track (async queue worker) to prevent freezing
// - Label style: line1 = "xx.xkm/h Honda Ridgeline Crew Cab 2012", line2 = "Car | Lane:4"

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
using CvRect = OpenCvSharp.Rect;
using CvSize = OpenCvSharp.Size;

namespace WpfApp1.ViewModels
{
    public class MainWindowViewModel : ViewModelBase, IDisposable
    {
        private const string BaseOnnxPath = "Scripts/yolov8n.onnx";
        private const string YolopOnnxPath = "Scripts/yolop-640-640.onnx";

        // ✅ car model classifier (best.onnx)
        private const string CarModelOnnxPath = "Scripts/best.onnx";

        // ✅ 파일 없이 MainWindowViewModel.cs에 내장 (너가 넣은 리스트 사용)
        // best.onnx가 "차종 분류 모델"이면 보통 196(Stanford Cars)개 클래스가 맞아야 함.
        // 그런데 네 출력이 [1x210x525]처럼 나오면 YOLO 출력이므로, 아래 디코딩 루틴이 필요함.
        private static readonly string[] CarModelNamesEmbedded = new[]
        {
            "AM General Hummer SUV 2000",
            "Acura RL Sedan 2012",
            "Acura TL Sedan 2012",
            "Acura TL Type-S 2008",
            "Acura TSX Sedan 2012",
            "Acura Integra Type R 2001",
            "Acura ZDX Hatchback 2012",
            "Aston Martin V8 Vantage Convertible 2012",
            "Aston Martin V8 Vantage Coupe 2012",
            "Aston Martin Virage Convertible 2012",
            "Aston Martin Virage Coupe 2012",
            "Audi RS 4 Convertible 2008",
            "Audi A5 Coupe 2012",
            "Audi TTS Coupe 2012",
            "Audi R8 Coupe 2012",
            "Audi V8 Sedan 1994",
            "Audi 100 Sedan 1994",
            "Audi 100 Wagon 1994",
            "Audi TT Hatchback 2011",
            "Audi S6 Sedan 2011",
            "Audi S5 Convertible 2012",
            "Audi S5 Coupe 2012",
            "Audi S4 Sedan 2012",
            "Audi S4 Sedan 2007",
            "Audi TT RS Coupe 2012",
            "BMW ActiveHybrid 5 Sedan 2012",
            "BMW 1 Series Convertible 2012",
            "BMW 1 Series Coupe 2012",
            "BMW 3 Series Sedan 2012",
            "BMW 3 Series Wagon 2012",
            "BMW 6 Series Convertible 2007",
            "BMW X5 SUV 2007",
            "BMW X6 SUV 2012",
            "BMW M3 Coupe 2012",
            "BMW M5 Sedan 2010",
            "BMW M6 Convertible 2010",
            "BMW X3 SUV 2012",
            "BMW Z4 Convertible 2012",
            "Bentley Continental Supersports Conv. Convertible 2012",
            "Bentley Arnage Sedan 2009",
            "Bentley Mulsanne Sedan 2011",
            "Bentley Continental GT Coupe 2012",
            "Bentley Continental GT Coupe 2007",
            "Bentley Continental Flying Spur Sedan 2007",
            "Bugatti Veyron 16.4 Convertible 2009",
            "Bugatti Veyron 16.4 Coupe 2009",
            "Buick Regal GS 2012",
            "Buick Rainier SUV 2007",
            "Buick Verano Sedan 2012",
            "Buick Enclave SUV 2012",
            "Cadillac CTS-V Sedan 2012",
            "Cadillac SRX SUV 2012",
            "Cadillac Escalade EXT Crew Cab 2007",
            "Chevrolet Silverado 1500 Hybrid Crew Cab 2012",
            "Chevrolet Corvette Convertible 2012",
            "Chevrolet Corvette ZR1 2012",
            "Chevrolet Corvette Ron Fellows Edition Z06 2007",
            "Chevrolet Traverse SUV 2012",
            "Chevrolet Camaro Convertible 2012",
            "Chevrolet HHR SS 2010",
            "Chevrolet Impala Sedan 2007",
            "Chevrolet Tahoe Hybrid SUV 2012",
            "Chevrolet Sonic Sedan 2012",
            "Chevrolet Express Cargo Van 2007",
            "Chevrolet Avalanche Crew Cab 2012",
            "Chevrolet Cobalt SS 2010",
            "Chevrolet Malibu Hybrid Sedan 2010",
            "Chevrolet TrailBlazer SS 2009",
            "Chevrolet Silverado 2500HD Regular Cab 2012",
            "Chevrolet Silverado 1500 Classic Extended Cab 2007",
            "Chevrolet Express Van 2007",
            "Chevrolet Monte Carlo Coupe 2007",
            "Chevrolet Malibu Sedan 2007",
            "Chevrolet Silverado 1500 Extended Cab 2012",
            "Chevrolet Silverado 1500 Regular Cab 2012",
            "Chrysler Aspen SUV 2009",
            "Chrysler Sebring Convertible 2010",
            "Chrysler Town and Country Minivan 2012",
            "Chrysler 300 SRT-8 2010",
            "Chrysler Crossfire Convertible 2008",
            "Chrysler PT Cruiser Convertible 2008",
            "Daewoo Nubira Wagon 2002",
            "Dodge Caliber Wagon 2012",
            "Dodge Caliber Wagon 2007",
            "Dodge Caravan Minivan 2007",
            "Dodge Ram SRT-10 2004",
            "Dodge Neon SRT-4 2003",
            "Dodge Durango SUV 2012",
            "Dodge Durango SUV 2007",
            "Dodge Journey SUV 2012",
            "Dodge Dakota Crew Cab 2010",
            "Dodge Dakota Club Cab 2010",
            "Dodge Magnum Wagon 2008",
            "Dodge Challenger Coupe 2011",
            "Dodge Charger Sedan 2012",
            "Dodge Charger SRT-8 2009",
            "Eagle Talon Hatchback 1998",
            "FIAT 500 Abarth 2012",
            "FIAT 500 Convertible 2012",
            "Ferrari FF Coupe 2012",
            "Ferrari California Convertible 2012",
            "Ferrari 458 Italia Convertible 2012",
            "Ferrari 458 Italia Coupe 2012",
            "Fisker Karma Sedan 2012",
            "Ford F-450 Super Duty Crew Cab 2012",
            "Ford Mustang Convertible 2007",
            "Ford Fiesta Sedan 2012",
            "Ford Ranger SuperCab 2011",
            "Ford F-150 Regular Cab 2012",
            "Ford F-150 Regular Cab 2007",
            "Ford Focus Sedan 2007",
            "Ford E-Series Wagon 2012",
            "Ford Edge SUV 2012",
            "Ford Ranger Regular Cab 2011",
            "Ford Expedition EL SUV 2009",
            "Ford Flex SUV 2012",
            "Ford GT Coupe 2006",
            "Ford Freestar Minivan 2007",
            "Ford Expedition SUV 2012",
            "Ford Focus ST Hatchback 2012",
            "Ford Fusion Sedan 2012",
            "Ford Taurus Sedan 2007",
            "GMC Terrain SUV 2012",
            "GMC Savana Van 2012",
            "GMC Yukon Hybrid SUV 2012",
            "GMC Acadia SUV 2012",
            "GMC Canyon Extended Cab 2012",
            "Geo Metro Hatchback 1993",
            "HUMMER H3T Crew Cab 2010",
            "HUMMER H2 SUT Crew Cab 2009",
            "Honda Odyssey Minivan 2012",
            "Honda RidgeLine Crew Cab 2012",
            "Honda Civic Sedan 2012",
            "Honda Civic Coupe 2012",
            "Honda Odyssey Minivan 2007",
            "Honda Insight Hatchback 2012",
            "Honda S2000 Convertible 2009",
            "Hyundai Genesis Sedan 2012",
            "Hyundai Equus Sedan 2012",
            "Hyundai Accent Sedan 2012",
            "Hyundai Veloster Hatchback 2012",
            "Hyundai Santa Fe SUV 2012",
            "Hyundai Tucson SUV 2012",
            "Hyundai Veracruz SUV 2012",
            "Hyundai Sonata Hybrid Sedan 2012",
            "Hyundai Elantra Sedan 2007",
            "Hyundai Azera Sedan 2012",
            "Infiniti G Coupe IPL 2012",
            "Infiniti QX56 SUV 2011",
            "Isuzu Ascender SUV 2006",
            "Jaguar XK Convertible 2012",
            "Jeep Liberty SUV 2012",
            "Jeep Grand Cherokee SUV 2012",
            "Jeep Compass SUV 2012",
            "Jeep Patriot SUV 2012",
            "Jeep Wrangler SUV 2012",
            "Lamborghini Reventon Coupe 2008",
            "Lamborghini Aventador Coupe 2012",
            "Lamborghini Gallardo LP 570-4 Superleggera 2012",
            "Lamborghini Diablo Coupe 2001",
            "Land Rover Range Rover SUV 2012",
            "Land Rover LR2 SUV 2012",
            "Lincoln Town Car Sedan 2011",
            "MINI Cooper Roadster Convertible 2012",
            "Maybach Landaulet Convertible 2012",
            "Mazda Tribute SUV 2011",
            "McLaren MP4-12C Coupe 2012",
            "Mercedes-Benz 300-Class Convertible 1993",
            "Mercedes-Benz C-Class Sedan 2012",
            "Mercedes-Benz SL-Class Coupe 2009",
            "Mercedes-Benz E-Class Sedan 2012",
            "Mercedes-Benz S-Class Sedan 2012",
            "Mercedes-Benz Sprinter Van 2012",
            "Mitsubishi Lancer Sedan 2012",
            "Nissan Leaf Hatchback 2012",
            "Nissan NV Passenger Van 2012",
            "Nissan Juke SUV 2012",
            "Nissan 240SX Coupe 1998",
            "Oldsmobile Cutlass Supreme Silhouette Pontaic Trans Sport Wagon 1993",
            "Plymouth Neon Sedan 1999",
            "Porsche Panamera Sedan 2012",
            "Ram C/V Cargo Van Minivan 2012",
            "Rolls-Royce Phantom Drophead Coupe Convertible 2012",
            "Rolls-Royce Ghost Sedan 2012",
            "Rolls-Royce Phantom Sedan 2012",
            "Scion xD Hatchback 2012",
            "Spyker C8 Laviolette Coupe 2009",
            "Spyker C8 Aileron Coupe 2011",
            "Suzuki Aerio Sedan 2007",
            "Suzuki Kizashi Sedan 2012",
            "Suzuki SX4 Hatchback 2012",
            "Suzuki SX4 Sedan 2012",
            "Tesla Model S Sedan 2012",
            "Toyota Sequoia SUV 2012",
            "Toyota Camry Sedan 2012",
            "Toyota Corolla Sedan 2012",
            "Toyota 4Runner SUV 2012",
            "Volkswagen Golf Hatchback 2012",
            "Volkswagen Golf Hatchback 1991",
            "Volkswagen Beetle Hatchback 2012",
            "Volvo C30 Hatchback 2012",
            "Volvo 240 Sedan 1993",
            "Volvo XC90 SUV 2007",
            "Smart fortwo Convertible 2012"
        };

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

        // ✅ LaneAnalyzer result
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
        // LaneAnalyzer
        // =========================
        private readonly LaneAnalyzer _laneAnalyzer = new LaneAnalyzer();

        // =========================
        // ✅ Car Model Classifier (best.onnx)
        // =========================
        private InferenceSession? _classSession;
        private readonly object _classLock = new();
        private string[] _carModelNames = Array.Empty<string>();

        private class ModelState
        {
            public bool Requested;
            public string Name = "";
        }
        private readonly ConcurrentDictionary<int, ModelState> _modelStates = new();
        private readonly ConcurrentQueue<(int trackId, Mat crop)> _modelQueue = new();
        private CancellationTokenSource? _modelCts;

        private readonly object _statusOnceLock = new();
        private bool _classInfoPrinted = false;

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
            InitializeCarClassifier();   // ✅ best.onnx

            StartDbWorker();
            StartModelWorker();
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

                var opts = new SessionOptions();
                _classSession = new InferenceSession(onnxPath, opts);

                _carModelNames = CarModelNamesEmbedded;

                PrintClassifierInfoOnce();
            }
            catch (Exception ex)
            {
                _classSession = null;
                SafeSetStatus($"best.onnx 로드 실패: {ex.Message}");
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
                var inType = inMeta.ElementType.ToString();

                var outName = _classSession.OutputMetadata.Keys.First();
                var outMeta = _classSession.OutputMetadata[outName];
                var outDims = string.Join("x", outMeta.Dimensions.Select(d => d.ToString()));
                var outType = outMeta.ElementType.ToString();

                SafeSetStatus($"best.onnx OK | IN:{inName}[{inDims}] {inType} | OUT:{outName}[{outDims}] {outType} | labels:{_carModelNames.Length}");
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
                            TrackAndMatch(dets, time, vehicleFrame); // ✅ frame 전달(크롭)
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
                    TryInsertRecord(detections[bestIdx], track);

                    TryEnqueueModelJob(track.Id, detections[bestIdx].Box, frameForCrop);

                    usedDets.Add(bestIdx);
                }
                else track.Missed();
            }

            foreach (var d in detections.Where((_, i) => !usedDets.Contains(i)))
            {
                var newTrack = new TrackedObject(d, timeMsec);
                d.TrackId = newTrack.Id;
                _trackedObjects[newTrack.Id] = newTrack;

                TryEnqueueModelJob(newTrack.Id, d.Box, frameForCrop);
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

                _modelStates.TryRemove(id, out _);
            }
        }

        private void TryEnqueueModelJob(int trackId, CvRect box, Mat frameForCrop)
        {
            if (_classSession == null) return;

            var st = _modelStates.GetOrAdd(trackId, _ => new ModelState());
            if (st.Requested) return;

            if (box.Width < 30 || box.Height < 30) return;

            int x = Math.Max(0, box.X);
            int y = Math.Max(0, box.Y);
            int w = Math.Min(box.Width, frameForCrop.Width - x);
            int h = Math.Min(box.Height, frameForCrop.Height - y);
            if (w <= 0 || h <= 0) return;

            var safe = new CvRect(x, y, w, h);

            Mat crop;
            try
            {
                using var roi = new Mat(frameForCrop, safe);
                crop = roi.Clone();
            }
            catch
            {
                return;
            }

            st.Requested = true;
            _modelQueue.Enqueue((trackId, crop));

            while (_modelQueue.Count > 200 && _modelQueue.TryDequeue(out var old))
            {
                try { old.crop.Dispose(); } catch { }
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

        private static void DrawLabelBox(Mat frame, string text, int x, int y, double scale, Scalar bg, Scalar fg)
        {
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

            Cv2.Rectangle(frame, new CvRect(rectX, rectY, rectW, rectH), bg, -1);
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

            // 4) Vehicles -> labels
            foreach (var d in _currentDetections)
            {
                if (!_trackedObjects.TryGetValue(d.TrackId, out var track)) continue;

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

                string modelName = "";
                if (_modelStates.TryGetValue(d.TrackId, out var ms) && !string.IsNullOrWhiteSpace(ms.Name))
                    modelName = ms.Name;

                string line1 = $"{kmh:0.0}km/h {modelName}".Trim();
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

                    _modelStates.Clear();
                    while (_modelQueue.TryDequeue(out var job))
                    {
                        try { job.crop.Dispose(); } catch { }
                    }

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

            _modelCts?.Cancel();
            _modelCts = null;

            Stop();
            _frame.Dispose();
            _detector?.Dispose();
            _video.Dispose();

            _yolop?.Dispose();
            _driveMask?.Dispose();
            _laneProb?.Dispose();

            _laneAnalysisStable = null;

            try { _classSession?.Dispose(); } catch { }
            _classSession = null;
        }

        // =========================
        // DB Worker
        // =========================
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

        // =========================
        // ✅ Model Worker (best.onnx)
        // =========================
        private void StartModelWorker()
        {
            if (_modelCts != null) return;

            _modelCts = new CancellationTokenSource();
            var token = _modelCts.Token;

            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (_classSession == null)
                        {
                            await Task.Delay(300, token);
                            continue;
                        }

                        if (_modelQueue.TryDequeue(out var job))
                        {
                            string name = "";
                            try
                            {
                                name = GetSpecificCarModel(job.crop);
                            }
                            catch { name = ""; }
                            finally
                            {
                                try { job.crop.Dispose(); } catch { }
                            }

                            var st = _modelStates.GetOrAdd(job.trackId, _ => new ModelState());
                            st.Name = (name ?? "").Trim();
                        }
                        else
                        {
                            await Task.Delay(8, token);
                        }
                    }
                    catch
                    {
                        await Task.Delay(100, token);
                    }
                }
            }, token);
        }

        // ---------- 핵심: best.onnx 출력 형태(분류/YOLO)를 둘 다 처리 ----------
        private string GetSpecificCarModel(Mat cropImg)
        {
            if (_classSession == null) return "";

            try
            {
                // 입력 메타(보통 1x3x160x160)
                var inputName = _classSession.InputMetadata.Keys.First();
                var dims = _classSession.InputMetadata[inputName].Dimensions.ToArray();
                int H = (dims.Length >= 4 && dims[2] > 0) ? dims[2] : 160;
                int W = (dims.Length >= 4 && dims[3] > 0) ? dims[3] : 160;

                using var rgbImg = new Mat();
                Cv2.CvtColor(cropImg, rgbImg, ColorConversionCodes.BGR2RGB);

                using var resized = new Mat();
                Cv2.Resize(rgbImg, resized, new CvSize(W, H));

                var input = new DenseTensor<float>(new[] { 1, 3, H, W });
                for (int y = 0; y < H; y++)
                {
                    for (int x = 0; x < W; x++)
                    {
                        var p = resized.At<Vec3b>(y, x);
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
                    if (first.Value is DenseTensor<float> tf)
                    {
                        int cls = DecodeClassIndex(tf, out float score);
                        if (cls >= 0 && cls < _carModelNames.Length) return _carModelNames[cls];
                        return cls >= 0 ? $"Model#{cls}" : "";
                    }
                    else if (first.Value is DenseTensor<long> tl)
                    {
                        // 거의 안 나오지만 안전 처리
                        var arr = tl.ToArray();
                        int bestIdx = 0;
                        long best = arr[0];
                        for (int i = 1; i < arr.Length; i++)
                        {
                            if (arr[i] > best) { best = arr[i]; bestIdx = i; }
                        }
                        if (bestIdx >= 0 && bestIdx < _carModelNames.Length) return _carModelNames[bestIdx];
                        return $"Model#{bestIdx}";
                    }

                    return "";
                }
            }
            catch
            {
                return "";
            }
        }

        // ✅ tf shape:
        // - Classification: [1, C] or [C]
        // - YOLO-like: [1, Ch, N] (예: 1x210x525) -> best obj*cls 기반으로 classIdx 반환
        private int DecodeClassIndex(DenseTensor<float> tf, out float bestScore)
        {
            bestScore = 0f;

            var dims = tf.Dimensions.ToArray();
            if (dims.Length == 1)
            {
                // [C]
                var arr = tf.ToArray();
                int best = ArgMax(arr, out bestScore);
                return best;
            }
            if (dims.Length == 2)
            {
                // [1, C] or [C, 1]
                if (dims[0] == 1)
                {
                    var arr = tf.ToArray();
                    int best = ArgMax(arr, out bestScore);
                    return best;
                }
                else
                {
                    // [C, 1] 같은 케이스
                    var arr = tf.ToArray();
                    int best = ArgMax(arr, out bestScore);
                    return best;
                }
            }
            if (dims.Length == 3)
            {
                // [1, Ch, N] (대부분)
                // YOLO 가정: ch = 5 + numClasses (x,y,w,h,obj + classes...)
                // best = max over N of (obj * maxClassScore)
                int b = dims[0];
                int ch = dims[1];
                int n = dims[2];
                if (b != 1 || ch < 6 || n < 1)
                {
                    // 예외 shape -> flatten argmax
                    var flat = tf.ToArray();
                    int idx = ArgMax(flat, out bestScore);
                    return idx;
                }

                int numClasses = ch - 5;
                int bestClass = -1;
                float best = float.MinValue;

                for (int i = 0; i < n; i++)
                {
                    float obj = tf[0, 4, i];
                    // obj가 logit이면 sigmoid 필요할 수 있는데,
                    // 일단 스케일만 중요하니 그대로 사용 (원하면 Sigmoid(obj)로 바꿔도 됨)

                    int localBestClass = 0;
                    float localBest = tf[0, 5, i];
                    for (int c = 1; c < numClasses; c++)
                    {
                        float s = tf[0, 5 + c, i];
                        if (s > localBest) { localBest = s; localBestClass = c; }
                    }

                    float score = obj * localBest;
                    if (score > best)
                    {
                        best = score;
                        bestClass = localBestClass;
                    }
                }

                bestScore = best;
                return bestClass;
            }

            // 그 외: flatten
            {
                var arr = tf.ToArray();
                int idx = ArgMax(arr, out bestScore);
                return idx;
            }
        }

        private static int ArgMax(float[] arr, out float best)
        {
            best = float.MinValue;
            int bestIdx = -1;
            for (int i = 0; i < arr.Length; i++)
            {
                if (arr[i] > best) { best = arr[i]; bestIdx = i; }
            }
            return bestIdx;
        }

        // =========================
        // Speed/DB helpers
        // =========================
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
    }
}
