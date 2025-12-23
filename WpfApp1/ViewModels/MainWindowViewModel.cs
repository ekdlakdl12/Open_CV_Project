using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.Win32;
using MongoDB.Bson;
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

// ✅ 에러 방지: Rect 참조 모호성 해결 (OpenCvSharp.Rect로 강제 지정)
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

        private IMongoCollection<VehicleRecord>? _dbCollection;

        private readonly string[] _carModelNames = { "AM General Hummer SUV 2000", "Acura RL Sedan 2012", "Acura TL Sedan 2012", "Acura TL Type-S 2008", "Acura TSX Sedan 2012", "Acura Integra Type R 2001", "Acura ZDX Hatchback 2012", "Aston Martin V8 Vantage Convertible 2012", "Aston Martin V8 Vantage Coupe 2012", "Aston Martin Virage Convertible 2012", "Aston Martin Virage Coupe 2012", "Audi RS 4 Convertible 2008", "Audi A5 Coupe 2012", "Audi TTS Coupe 2012", "Audi R8 Coupe 2012", "Audi V8 Sedan 1994", "Audi 100 Sedan 1994", "Audi 100 Wagon 1994", "Audi TT Hatchback 2011", "Audi S6 Sedan 2011", "Audi S5 Convertible 2012", "Audi S5 Coupe 2012", "Audi S4 Sedan 2012", "Audi S4 Sedan 2007", "Audi TT RS Coupe 2012", "BMW ActiveHybrid 5 Sedan 2012", "BMW 1 Series Convertible 2012", "BMW 1 Series Coupe 2012", "BMW 3 Series Sedan 2012", "BMW 3 Series Wagon 2012", "BMW 6 Series Convertible 2007", "BMW X5 SUV 2007", "BMW X6 SUV 2012", "BMW M3 Coupe 2012", "BMW M5 Sedan 2010", "BMW M6 Convertible 2010", "BMW X3 SUV 2012", "BMW Z4 Convertible 2012", "Bentley Continental Supersports Conv. Convertible 2012", "Bentley Arnage Sedan 2009", "Bentley Mulsanne Sedan 2011", "Bentley Continental GT Coupe 2012", "Bentley Continental GT Coupe 2007", "Bentley Continental Flying Spur Sedan 2007", "Bugatti Veyron 16.4 Convertible 2009", "Bugatti Veyron 16.4 Coupe 2009", "Buick Regal GS 2012", "Buick Rainier SUV 2007", "Buick Verano Sedan 2012", "Buick Enclave SUV 2012", "Cadillac CTS-V Sedan 2012", "Cadillac SRX SUV 2012", "Cadillac Escalade EXT Crew Cab 2007", "Chevrolet Silverado 1500 Hybrid Crew Cab 2012", "Chevrolet Corvette Convertible 2012", "Chevrolet Corvette ZR1 2012", "Chevrolet Corvette Ron Fellows Edition Z06 2007", "Chevrolet Traverse SUV 2012", "Chevrolet Camaro Convertible 2012", "Chevrolet HHR SS 2010", "Chevrolet Impala Sedan 2007", "Chevrolet Tahoe Hybrid SUV 2012", "Chevrolet Sonic Sedan 2012", "Chevrolet Express Cargo Van 2007", "Chevrolet Avalanche Crew Cab 2012", "Chevrolet Cobalt SS 2010", "Chevrolet Malibu Hybrid Sedan 2010", "Chevrolet TrailBlazer SS 2009", "Chevrolet Silverado 2500HD Regular Cab 2012", "Chevrolet Silverado 1500 Classic Extended Cab 2007", "Chevrolet Express Van 2007", "Chevrolet Monte Carlo Coupe 2007", "Chevrolet Malibu Sedan 2007", "Chevrolet Silverado 1500 Extended Cab 2012", "Chevrolet Silverado 1500 Regular Cab 2012", "Chrysler Aspen SUV 2009", "Chrysler Sebring Convertible 2010", "Chrysler Town and Country Minivan 2012", "Chrysler 300 SRT-8 2010", "Chrysler Crossfire Convertible 2008", "Chrysler PT Cruiser Convertible 2008", "Daewoo Nubira Wagon 2002", "Dodge Caliber Wagon 2012", "Dodge Caliber Wagon 2007", "Dodge Caravan Minivan 2007", "Dodge Ram SRT-10 2004", "Dodge Neon SRT-4 2003", "Dodge Durango SUV 2012", "Dodge Durango SUV 2007", "Dodge Journey SUV 2012", "Dodge Dakota Crew Cab 2010", "Dodge Dakota Club Cab 2010", "Dodge Magnum Wagon 2008", "Dodge Challenger Coupe 2011", "Dodge Charger Sedan 2012", "Dodge Charger SRT-8 2009", "Eagle Talon Hatchback 1998", "FIAT 500 Abarth 2012", "FIAT 500 Convertible 2012", "Ferrari FF Coupe 2012", "Ferrari California Convertible 2012", "Ferrari 458 Italia Convertible 2012", "Ferrari 458 Italia Coupe 2012", "Fisker Karma Sedan 2012", "Ford F-450 Super Duty Crew Cab 2012", "Ford Mustang Convertible 2007", "Ford Fiesta Sedan 2012", "Ford Ranger SuperCab 2011", "Ford F-150 Regular Cab 2012", "Ford F-150 Regular Cab 2007", "Ford Focus Sedan 2007", "Ford E-Series Wagon 2012", "Ford Edge SUV 2012", "Ford Ranger Regular Cab 2011", "Ford Expedition EL SUV 2009", "Ford Flex SUV 2012", "Ford GT Coupe 2006", "Ford Freestar Minivan 2007", "Ford Expedition SUV 2012", "Ford Focus ST Hatchback 2012", "Ford Fusion Sedan 2012", "Ford Taurus Sedan 2007", "GMC Terrain SUV 2012", "GMC Savana Van 2012", "GMC Yukon Hybrid SUV 2012", "GMC Acadia SUV 2012", "GMC Canyon Extended Cab 2012", "Geo Metro Hatchback 1993", "HUMMER H3T Crew Cab 2010", "HUMMER H2 SUT Crew Cab 2009", "Honda Odyssey Minivan 2012", "Honda RidgeLine Crew Cab 2012", "Honda Civic Accord Sedan 2012", "Honda Civic Accord Coupe 2012", "Honda Civic Sedan 2012", "Honda Civic Coupe 2012", "Honda Odyssey Minivan 2007", "Honda Insight Hatchback 2012", "Honda S2000 Convertible 2009", "Hyundai Genesis Sedan 2012", "Hyundai Equus Sedan 2012", "Hyundai Accent Sedan 2012", "Hyundai Veloster Hatchback 2012", "Hyundai Santa Fe SUV 2012", "Hyundai Tucson SUV 2012", "Hyundai Veracruz SUV 2012", "Hyundai Sonata Hybrid Sedan 2012", "Hyundai Elantra Sedan 2007", "Hyundai Azera Sedan 2012", "Infiniti G Coupe IPL 2012", "Infiniti QX56 SUV 2011", "Isuzu Ascender SUV 2006", "Jaguar XK Convertible 2012", "Jeep Liberty SUV 2012", "Jeep Grand Cherokee SUV 2012", "Jeep Compass SUV 2012", "Jeep Patriot SUV 2012", "Jeep Wrangler SUV 2012", "Lamborghini Reventon Coupe 2008", "Lamborghini Aventador Coupe 2012", "Lamborghini Gallardo LP 570-4 Superleggera 2012", "Lamborghini Diablo Coupe 2001", "Land Rover Range Rover SUV 2012", "Land Rover LR2 SUV 2012", "Lincoln Town Car Sedan 2011", "MINI Cooper Roadster Convertible 2012", "Maybach Landaulet Convertible 2012", "Mazda Tribute SUV 2011", "McLaren MP4-12C Coupe 2012", "Mercedes-Benz 300-Class Convertible 1993", "Mercedes-Benz C-Class Sedan 2012", "Mercedes-Benz SL-Class Coupe 2009", "Mercedes-Benz E-Class Sedan 2012", "Mercedes-Benz S-Class Sedan 2012", "Mercedes-Benz Sprinter Van 2012", "Mitsubishi Lancer Sedan 2012", "Nissan Leaf Hatchback 2012", "Nissan NV Passenger Van 2012", "Nissan Juke SUV 2012", "Nissan 240SX Coupe 1998", "Oldsmobile Cutlass Supreme Silhouette Pontaic Trans Sport Wagon 1993", "Plymouth Neon Sedan 1999", "Porsche Panamera Sedan 2012", "Ram C/V Cargo Van Minivan 2012", "Rolls-Royce Phantom Drophead Coupe Convertible 2012", "Rolls-Royce Ghost Sedan 2012", "Rolls-Royce Phantom Sedan 2012", "Scion xD Hatchback 2012", "Spyker C8 Laviolette Coupe 2009", "Spyker C8 Aileron Coupe 2011", "Suzuki Aerio Sedan 2007", "Suzuki Kizashi Sedan 2012", "Suzuki SX4 Hatchback 2012", "Suzuki SX4 Sedan 2012", "Tesla Model S Sedan 2012", "Toyota Sequoia SUV 2012", "Toyota Camry Sedan 2012", "Toyota Corolla Sedan 2012", "Toyota 4Runner SUV 2012", "Volkswagen Golf Hatchback 2012", "Volkswagen Golf Hatchback 1991", "Volkswagen Beetle Hatchback 2012", "Volvo C30 Hatchback 2012", "Volvo 240 Sedan 1993", "Volvo XC90 SUV 2007", "Smart fortwo Convertible 2012" };

        private readonly ConcurrentDictionary<int, string> _modelCache = new();
        private readonly ConcurrentDictionary<int, Scalar> _colorCache = new();
        private readonly Dictionary<int, TrackedObject> _trackedObjects = new();
        private List<Detection> _currentDetections = new();
        private readonly Mat _frame = new();
        private readonly Random _rand = new();
        private volatile bool _isBusy = false;
        private volatile bool _isLaneBusy = false;
        private readonly DispatcherTimer _timer = new();
        private long _lastLaneInferMs = 0;

        private readonly LaneAnalyzer _laneAnalyzer = new();
        private LaneAnalyzer.LaneAnalysisResult? _laneAnalysisStable;

        public ObservableCollection<int> TotalLaneOptions { get; } = new() { 1, 2, 3, 4, 5, 6 };
        public ObservableCollection<int> CurrentLaneOptions { get; } = new() { 1, 2, 3, 4, 5, 6 };
        public int TotalLanes { get; set; } = 3;
        public int CurrentLane { get; set; } = 2;

        private int _countL = 0, _countF = 0, _countR = 0;
        private readonly HashSet<int> _countedIds = new();
        private string _countText = "L:0 | F:0 | R:0";
        public string CountText { get => _countText; set { _countText = value; OnPropertyChanged(); } }

        private BitmapSource? _frameImage;
        public BitmapSource? FrameImage { get => _frameImage; set { _frameImage = value; OnPropertyChanged(); } }

        public RelayCommand OpenVideoCommand { get; }

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
            _timer.Interval = TimeSpan.FromMilliseconds(5);
            InitializeDetectors();
        }

        private void InitializeDetectors()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _detector = new YoloDetectService(Path.Combine(baseDir, "Scripts/yolov8n.onnx"), 640, 0.3f, 0.45f);
            _yolop = new YolopDetectService(Path.Combine(baseDir, "Scripts/yolop-640-640.onnx"), 640, 0.35f, 0.45f);

            string clsPath = Path.Combine(baseDir, "Scripts/best.onnx");
            if (File.Exists(clsPath)) _classSession = new InferenceSession(clsPath);
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (!_video.Read(_frame) || _frame.Empty()) return;
            long nowMs = Environment.TickCount64;

            if (!_isLaneBusy && _yolop != null && (nowMs - _lastLaneInferMs) >= 500)
            {
                _isLaneBusy = true; _lastLaneInferMs = nowMs;
                Mat laneFrame = _frame.Clone();
                Task.Run(() => {
                    try
                    {
                        var r = _yolop.Infer(laneFrame);
                        lock (_lock)
                        {
                            if (r.LaneProbOrig != null)
                            {
                                _laneAnalyzer.TotalLanes = this.TotalLanes;
                                _laneAnalyzer.EgoLane = this.CurrentLane;
                                _laneAnalysisStable = _laneAnalyzer.Analyze(r.LaneProbOrig, laneFrame.Width, laneFrame.Height);
                            }
                        }
                    }
                    finally { laneFrame.Dispose(); _isLaneBusy = false; }
                });
            }

            if (!_isBusy && _detector != null)
            {
                _isBusy = true;
                Mat clone = _frame.Clone();
                double time = _video.PosMsec;
                Task.Run(() => {
                    try
                    {
                        var dets = _detector.Detect(clone);
                        lock (_trackedObjects)
                        {
                            TrackAndMatch(dets, time);
                            _currentDetections = dets;
                            foreach (var d in _currentDetections)
                            {
                                if (!_colorCache.ContainsKey(d.TrackId))
                                    _colorCache.TryAdd(d.TrackId, Scalar.FromRgb((byte)_rand.Next(100, 255), (byte)_rand.Next(100, 255), (byte)_rand.Next(100, 255)));

                                if (d.ClassId == 2 && !_modelCache.ContainsKey(d.TrackId))
                                {
                                    Rect safeBox = new Rect(Math.Max(0, d.Box.X), Math.Max(0, d.Box.Y),
                                                              Math.Min(clone.Width - d.Box.X, d.Box.Width),
                                                              Math.Min(clone.Height - d.Box.Y, d.Box.Height));
                                    if (safeBox.Width > 10 && safeBox.Height > 10)
                                    {
                                        using var crop = new Mat(clone, safeBox);
                                        _modelCache.TryAdd(d.TrackId, GetSpecificCarModel(crop));
                                    }
                                }
                            }
                        }
                    }
                    finally { clone.Dispose(); _isBusy = false; }
                });
            }

            lock (_trackedObjects) { UpdateCounting(_frame.Width, _frame.Height); DrawOutput(_frame); }
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
                for (int y = 0; y < 160; y++) for (int x = 0; x < 160; x++)
                    {
                        var p = resized.At<Vec3b>(y, x);
                        input[0, 0, y, x] = p.Item0 / 255f; input[0, 1, y, x] = p.Item1 / 255f; input[0, 2, y, x] = p.Item2 / 255f;
                    }
                lock (_sessionLock)
                {
                    using var results = _classSession.Run(new[] { NamedOnnxValue.CreateFromTensor(_classSession.InputMetadata.Keys.First(), input) });
                    var output = results.First().AsTensor<float>();
                    float[] scores = new float[206];
                    for (int i = 0; i < output.Dimensions[2]; i++) for (int c = 0; c < 206; c++) scores[c] += output[0, 4 + c, i];
                    return _carModelNames[Array.IndexOf(scores, scores.Max())];
                }
            }
            catch { return "Vehicle"; }
        }

        private void UpdateCounting(int w, int h)
        {
            int lineY = (int)(h * 0.7);
            foreach (var track in _trackedObjects.Values)
            {
                if (_countedIds.Contains(track.Id)) continue;
                var center = new Point(track.LastBox.X + track.LastBox.Width / 2, track.LastBox.Y + track.LastBox.Height / 2);
                if (center.Y > lineY)
                {
                    string dir = center.X < w * 0.35 ? "L" : (center.X > w * 0.65 ? "R" : "F");
                    if (dir == "L") _countL++; else if (dir == "R") _countR++; else _countF++;
                    _countedIds.Add(track.Id);
                    SaveToDb(track, dir);
                }
            }
            CountText = $"L:{_countL} | F:{_countF} | R:{_countR}";
        }

        private void SaveToDb(TrackedObject track, string direction)
        {
            _modelCache.TryGetValue(track.Id, out string? modelName);
            // ✅ 속성명 SpeedInKmh 및 VehicleType 적용 확인
            var record = new VehicleRecord
            {
                DetectTime = DateTime.Now,
                VehicleType = $"{GetTypeName(track.LastClassId)} ({modelName ?? "Unknown"})",
                Direction = direction,
                Speed = (int)Math.Round(track.SpeedInKmh),
                ViolationReason = track.SpeedInKmh > 100 ? "속도 위반" : "정상",
                LicensePlate = "분별 중..."
            };
            Task.Run(async () => { try { if (_dbCollection != null) await _dbCollection.InsertOneAsync(record); } catch { } });
        }

        private void DrawOutput(Mat frame)
        {
            if (_laneAnalysisStable != null) _laneAnalyzer.DrawOnFrame(frame, _laneAnalysisStable);
            Cv2.Line(frame, 0, (int)(frame.Height * 0.7), frame.Width, (int)(frame.Height * 0.7), Scalar.Red, 2);

            foreach (var d in _currentDetections)
            {
                if (!_trackedObjects.TryGetValue(d.TrackId, out var track)) continue;
                Scalar color = _colorCache.TryGetValue(d.TrackId, out var c) ? c : Scalar.Yellow;
                Cv2.Rectangle(frame, d.Box, color, 2);

                _modelCache.TryGetValue(d.TrackId, out string? model);
                string label = $"{track.SpeedInKmh:F1}km/h {model ?? "..."}";

                var size = Cv2.GetTextSize(label, HersheyFonts.HersheySimplex, 0.45, 1, out var baseLine);
                Cv2.Rectangle(frame, new Rect(d.Box.X, d.Box.Y - size.Height - 10, size.Width + 10, size.Height + baseLine + 5), track.SpeedInKmh > 100 ? Scalar.Red : Scalar.Black, -1);
                Cv2.PutText(frame, label, new Point(d.Box.X + 5, d.Box.Y - 8), HersheyFonts.HersheySimplex, 0.45, Scalar.White, 1);
            }
        }

        // ✅ 수정 핵심: Update 및 생성자 호출 시 (int, Rect, double) 인자 명시
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
                // ✅ Update(int classId, Rect box, double timeMs)로 수정
                if (best != -1)
                {
                    dets[best].TrackId = track.Id;
                    track.Update(dets[best].ClassId, dets[best].Box, time);
                    used.Add(best);
                }
                else track.Missed();
            }
            foreach (var d in dets.Where((_, i) => !used.Contains(i)))
            {
                // ✅ TrackedObject(int classId, Rect box, double timeMs)로 수정
                var nt = new TrackedObject(d.ClassId, d.Box, time);
                d.TrackId = nt.Id; _trackedObjects[nt.Id] = nt;
            }
            _trackedObjects.Where(kv => kv.Value.ShouldBeDeleted).ToList().ForEach(k => {
                _trackedObjects.Remove(k.Key); _modelCache.TryRemove(k.Key, out _); _colorCache.TryRemove(k.Key, out _);
            });
        }

        private string GetTypeName(int id) => id switch { 2 => "CAR", 5 => "BUS", 7 => "TRUCK", _ => "Vehicle" };
        private void OpenVideo() { var d = new OpenFileDialog(); if (d.ShowDialog() == true) { _video.Open(d.FileName); _modelCache.Clear(); _colorCache.Clear(); _timer.Start(); } }
        public void Dispose() { _timer.Stop(); _classSession?.Dispose(); _frame.Dispose(); _video.Dispose(); }
    }
}