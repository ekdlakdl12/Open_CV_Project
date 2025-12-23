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
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Tesseract;
using WpfApp1.Models;
using WpfApp1.Services;

namespace WpfApp1.ViewModels
{
    // 탐지 정보를 담는 클래스
    public class LocalDetection
    {
        public System.Windows.Rect Box { get; set; }
        public int ClassId { get; set; }
        public float Confidence { get; set; }
        public int TrackId { get; set; }
    }

    // 객체 추적 및 속도 계산 클래스
    public class LocalTrackedObject
    {
        public int Id { get; set; }
        public System.Windows.Rect LastBox { get; set; }
        public double LastTime { get; set; }
        public double Speed { get; set; }
        public int LastClassId { get; set; }
        public int MissedFrames { get; set; }

        // 팩트체크: 속도가 100을 넘으면 위반으로 간주
        public bool IsViolating => Speed > 100;
        public bool ShouldBeDeleted => MissedFrames > 15;

        private static int _nextId = 1;

        public LocalTrackedObject(LocalDetection det, double time)
        {
            Id = _nextId++;
            LastBox = det.Box;
            LastTime = time;
            LastClassId = det.ClassId;
        }

        public void Update(LocalDetection det, double time)
        {
            double dt = (time - LastTime) / 1000.0;
            if (dt > 0)
            {
                // 이전 프레임과 현재 프레임 사이의 거리 계산 (픽셀 기준)
                double dist = Math.Sqrt(Math.Pow(det.Box.X - LastBox.X, 2) + Math.Pow(det.Box.Y - LastBox.Y, 2));
                // 픽셀 거리를 실제 속도와 유사하게 변환 (테스트 환경에 따라 0.5 계수 조정 가능)
                Speed = (dist / dt) * 0.5;
            }
            LastBox = det.Box;
            LastTime = time;
            LastClassId = det.ClassId;
            MissedFrames = 0;
        }

        public void Missed() => MissedFrames++;
    }

    public class MainWindowViewModel : ViewModelBase, IDisposable
    {
        private const string ClassOnnxPath = "Scripts/best.onnx";
        private readonly VideoPlayerService _video = new();
        private YoloDetectService? _detector;
        private InferenceSession? _classSession;
        private TesseractEngine? _ocrEngine;
        private readonly object _sessionLock = new object();
        private IMongoCollection<VehicleRecord>? _dbCollection;

        // 206개 차종 리스트
        private readonly string[] _carModelNames = {
            "AM General Hummer SUV 2000", "Acura RL Sedan 2012", "Acura TL Sedan 2012", "Acura TL Type-S 2008",
            "Acura TSX Sedan 2012", "Acura Integra Type R 2001", "Acura ZDX Hatchback 2012", "Aston Martin V8 Vantage Convertible 2012",
            "Aston Martin V8 Vantage Coupe 2012", "Aston Martin Virage Convertible 2012", "Aston Martin Virage Coupe 2012",
            "Audi RS 4 Convertible 2008", "Audi A5 Coupe 2012", "Audi TTS Coupe 2012", "Audi R8 Coupe 2012",
            "Audi V8 Sedan 1994", "Audi 100 Sedan 1994", "Audi 100 Transmission 1994", "Audi TT RS Coupe 2012",
            "Audi S4 Sedan 2012", "Audi S4 Sedan 2007", "Audi S5 Convertible 2012", "Audi S5 Coupe 2012",
            "Audi S6 Sedan 2011", "Audi A7 Sedan 2012", "Audi A8 Sedan 2012", "Audi Quatro Coupe 1982",
            "Audi TT Hatchback 2011", "Audi R8 Coupe 2012", "BMW ActiveHybrid 5 Sedan 2012", "BMW 1 Series Convertible 2012",
            "BMW 1 Series Coupe 2012", "BMW 3 Series Sedan 2012", "BMW 3 Series Wagon 2012", "BMW 6 Series Convertible 2007",
            "BMW X5 SUV 2007", "BMW X6 SUV 2012", "BMW M3 Coupe 2012", "BMW M5 Sedan 2010", "BMW M6 Convertible 2010",
            "BMW X3 SUV 2012", "BMW Z4 Convertible 2012", "Bentley Continental Supersports Conv. Convertible 2012",
            "Bentley Arnage Sedan 2009", "Bentley Continental GT Coupe 2012", "Bentley Continental GT Coupe 2007",
            "Bentley Continental Flying Spur Sedan 2007", "Bugatti Veyron 16.4 Coupe 2009", "Bugatti Veyron 16.4 Convertible 2009",
            "Buick Rainier SUV 2007", "Buick Verano Sedan 2012", "Buick Regal GS Sedan 2012", "Buick Lucerne Sedan 2012",
            "Buick Enclave SUV 2012", "Cadillac CTS-V Sedan 2012", "Cadillac SRX SUV 2012", "Cadillac Escalade EXT Crew Cab 2007",
            "Chevrolet Silverado 1500 Hybrid Crew Cab 2012", "Chevrolet Corvette ZR1 2012", "Chevrolet Corvette Ron Fellows Edition Z06 2007",
            "Chevrolet Traverse SUV 2012", "Chevrolet Camaro Convertible 2012", "Chevrolet HHR SS 2010", "Chevrolet Impala Sedan 2007",
            "Chevrolet Tahoe SUV 2012", "Chevrolet Sonic Sedan 2012", "Chevrolet Express Cargo Van 2007", "Chevrolet Avalanche Crew Cab 2012",
            "Chevrolet Cobalt SS 2010", "Chevrolet Malibu Hybrid Sedan 2010", "Chevrolet TrailBlazer SS 2009",
            "Chevrolet Silverado 2500HD Regular Cab 2012", "Chevrolet Silverado 1500 Classic Extended Cab 2007",
            "Chevrolet Express Van 2007", "Chevrolet Monte Carlo Coupe 2007", "Chevrolet Malibu Sedan 2007",
            "Chevrolet Silverado 1500 Extended Cab 2012", "Chevrolet Silverado 1500 Regular Cab 2012",
            "Chrysler Aspen SUV 2009", "Chrysler Town and Country Minivan 2012", "Chrysler 300 SRT-8 2010",
            "Chrysler Crossfire Convertible 2008", "Chrysler PT Cruiser Convertible 2008", "Dodge Sebring Convertible 2010",
            "Dodge Caliber Wagon 2012", "Dodge Caliber Wagon 2007", "Dodge Caravan Minivan 2007", "Dodge Ram SRT-10 2007",
            "Dodge Neon SRT-4 2005", "Dodge Magnum Wagon 2008", "Dodge Challenger Coupe 2011", "Dodge Durango SUV 2012",
            "Dodge Durango SUV 2007", "Dodge Journey SUV 2012", "Dodge Charger Sedan 2012", "Dodge Charger SRT-8 2009",
            "Eagle Talon Hatchback 1998", "FIAT 500 Abarth 2012", "FIAT 500 Convertible 2012", "Ferrari FF Coupe 2012",
            "Ferrari California Convertible 2012", "Ferrari 458 Italia Convertible 2012", "Ferrari 458 Italia Coupe 2012",
            "Fisker Karma Sedan 2012", "Ford F-450 Super Duty Crew Cab 2012", "Ford Mustang Convertible 2007",
            "Ford Fiesta Sedan 2012", "Ford Ranger SuperCab 2011", "Ford F-150 Regular Cab 2012", "Ford F-150 STX 2007",
            "Ford Focus Sedan 2007", "Ford E-Series Wagon 2012", "Ford Expedition EL SUV 2009", "Ford Edge SUV 2012",
            "Ford Ranger Regular Cab 2007", "Ford GT Coupe 2006", "Ford Freestar Minivan 2007", "Ford Mustang Coupe 2007",
            "Ford Focus ST Hatchback 2012", "Ford Fusion Sedan 2012", "Ford Taurus Sedan 2007", "GMC Terrain SUV 2012",
            "GMC Savana Van 2012", "GMC Yukon Hybrid SUV 2012", "GMC Acadia SUV 2012", "GMC Canyon Extended Cab 2012",
            "Geo Metro Hatchback 1993", "HUMMER H3T Crew Cab 2010", "HUMMER H2 SUT 2007", "Honda Odyssey Minivan 2012",
            "Honda Odyssey Minivan 2007", "Honda Accord Coupe 2012", "Honda Accord Sedan 2012", "Honda Civic Sedan 2012",
            "Honda Civic Coupe 2012", "Honda Civic Si Coupe 2012", "Honda Civic Si Sedan 2007", "Honda CR-V SUV 2012",
            "Hyundai Genesis Sedan 2012", "Hyundai Equus Sedan 2012", "Hyundai Accent Sedan 2012", "Hyundai Veloster Hatchback 2012",
            "Hyundai Santa Fe SUV 2012", "Hyundai Tucson SUV 2012", "Hyundai Veracruz SUV 2012", "Hyundai Sonata Hybrid Sedan 2012",
            "Hyundai Elantra Sedan 2007", "Hyundai Azera Sedan 2012", "Infiniti G Coupe IPL 2012", "Infiniti QX56 SUV 2011",
            "Isuzu Ascender SUV 2006", "Jaguar XK XKR 2012", "Jeep Liberty SUV 2012", "Jeep Grand Cherokee SUV 2012",
            "Jeep Patriot SUV 2012", "Jeep Wrangler SUV 2012", "Lamborghini Reventon Coupe 2008", "Lamborghini Aventador Coupe 2012",
            "Lamborghini Gallardo LP 560-4 2012", "Lamborghini Diablo Coupe 2001", "Land Rover Range Rover SUV 2012",
            "Land Rover LR4 SUV 2012", "Lincoln Town Car Sedan 2011", "Lexus RX 350 SUV 2012", "Lexus GS 350 Sedan 2012",
            "Lexus IS-F Sedan 2010", "Maybach Landaulet Convertible 2012", "Mazda Tribute SUV 2011", "McLaren MP4-12C Coupe 2012",
            "Mercedes-Benz 300-Class Convertible 1993", "Mercedes-Benz C-Class Sedan 2012", "Mercedes-Benz SL-Class Coupe 2009",
            "Mercedes-Benz E-Class Sedan 2012", "Mercedes-Benz S-Class Sedan 2012", "Mercedes-Benz Sprinter Van 2012",
            "MINI Cooper Roadster Convertible 2012", "Mitsubishi Lancer Sedan 2012", "Nissan Leaf Hatchback 2012",
            "Nissan NV Passenger Van 2012", "Nissan Juke SUV 2012", "Nissan 240SX Coupe 1998", "Oldsmobile Bravada SUV 2001",
            "Plymouth Neon Coupe 1999", "Porsche Panamera Sedan 2012", "Ram C/V Cargo Van Minivan 2012",
            "Rolls-Royce Phantom Drophead Coupe Convertible 2012", "Rolls-Royce Ghost Sedan 2012", "Rolls-Royce Phantom Sedan 2012",
            "Scion xD Hatchback 2012", "Spyker C8 Laviolette Coupe 2012", "Spyker C8 Aileron Coupe 2012",
            "Suzuki Aerio Sedan 2007", "Suzuki Kizashi Sedan 2012", "Suzuki SX4 Hatchback 2012", "Suzuki SX4 Sedan 2012",
            "Tesla Model S Sedan 2012", "Toyota Sequoia SUV 2012", "Toyota Camry Sedan 2012", "Toyota Corolla Sedan 2012",
            "Toyota 4Runner SUV 2012", "Volkswagen Golf Hatchback 2012", "Volkswagen Golf Hatchback 1991",
            "Volkswagen Beetle Hatchback 2012", "Volvo C30 Hatchback 2012", "Volvo 240 Sedan 1993", "Volvo XC90 SUV 2007",
            "Smart fortwo Convertible 2012"
        };

        private readonly ConcurrentDictionary<int, string> _modelCache = new();
        private readonly ConcurrentDictionary<int, Scalar> _colorCache = new();
        private readonly Dictionary<int, LocalTrackedObject> _trackedObjects = new();
        private List<LocalDetection> _currentDetections = new();
        private readonly Mat _frame = new();
        private volatile bool _isBusy = false;
        private readonly DispatcherTimer _timer = new();
        private readonly Random _rand = new();

        private int _countL = 0, _countF = 0, _countR = 0;
        private readonly HashSet<int> _countedIds = new();

        private string _countText = "L:0 | F:0 | R:0";
        public string CountText { get => _countText; set { _countText = value; OnPropertyChanged(); } }

        // 속도 위반 시 UI 색상 변경용
        private SolidColorBrush _statusColor = Brushes.White;
        public SolidColorBrush StatusColor { get => _statusColor; set { _statusColor = value; OnPropertyChanged(); } }

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
            catch { }

            InitOcr();
            OpenVideoCommand = new RelayCommand(OpenVideo);
            _timer.Tick += Timer_Tick;
            _timer.Interval = TimeSpan.FromMilliseconds(5);
            InitializeDetectors();
        }

        private void InitOcr()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scripts", "tessdata");
                if (Directory.Exists(path))
                {
                    _ocrEngine = new TesseractEngine(path, "kor+eng", EngineMode.Default);
                }
            }
            catch { }
        }

        private void InitializeDetectors()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string detPath = Path.Combine(baseDir, "Scripts", "yolov8n.onnx");
            if (File.Exists(detPath)) _detector = new YoloDetectService(detPath, 640, 0.3f, 0.45f);
            string clsPath = Path.Combine(baseDir, ClassOnnxPath);
            if (File.Exists(clsPath)) { try { _classSession = new InferenceSession(clsPath); } catch { } }
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (!_video.Read(_frame)) return;

            if (!_isBusy && _detector != null)
            {
                _isBusy = true;
                Mat clone = _frame.Clone();
                double time = _video.PosMsec;
                Task.Run(() => {
                    try
                    {
                        var rawDets = _detector.Detect(clone);
                        var dets = rawDets.Select(d => new LocalDetection
                        {
                            Box = new System.Windows.Rect(d.Box.X, d.Box.Y, d.Box.Width, d.Box.Height),
                            ClassId = d.ClassId,
                            Confidence = d.Confidence
                        }).ToList();

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
                                    System.Windows.Rect safeBox = GetSafeRect(clone, d.Box);
                                    using var crop = new Mat(clone, new OpenCvSharp.Rect((int)safeBox.X, (int)safeBox.Y, (int)safeBox.Width, (int)safeBox.Height)).Clone();
                                    _modelCache.TryAdd(d.TrackId, GetSpecificCarModel(crop));
                                }
                            }
                        }
                    }
                    finally { clone.Dispose(); _isBusy = false; }
                });
            }

            lock (_trackedObjects) { UpdateCounting(_frame); DrawOutput(_frame); }
            FrameImage = _frame.ToBitmapSource();
        }

        private void UpdateCounting(Mat frame)
        {
            int lineY = (int)(frame.Height * 0.7);
            bool anyViolation = false;

            foreach (var track in _trackedObjects.Values)
            {
                if (_countedIds.Contains(track.Id)) continue;
                var center = new OpenCvSharp.Point(track.LastBox.X + track.LastBox.Width / 2, track.LastBox.Y + track.LastBox.Height / 2);

                if (center.Y > lineY)
                {
                    string dir = center.X < frame.Width * 0.35 ? "L" : (center.X > frame.Width * 0.65 ? "R" : "F");
                    if (dir == "L") _countL++; else if (dir == "R") _countR++; else _countF++;
                    _countedIds.Add(track.Id);

                    // ✅ 수정: "정상" 대신 실제 번호판 인식 결과 할당
                    string plateResult = "분석중";
                    if (track.Speed >= 10)
                    {
                        plateResult = RecognizePlate(frame, track.LastBox);
                    }

                    if (track.IsViolating) anyViolation = true;
                    SaveToDb(track, dir, plateResult);
                }
            }

            // ✅ 속도 위반 차량이 감지되면 상태 색상을 빨간색으로 변경
            StatusColor = anyViolation ? Brushes.Red : Brushes.White;
            CountText = $"L:{_countL} | F:{_countF} | R:{_countR}";
        }

        private string RecognizePlate(Mat frame, System.Windows.Rect vehicleBox)
        {
            if (_ocrEngine == null) return "EngineErr";
            try
            {
                System.Windows.Rect safeBox = GetSafeRect(frame, vehicleBox);
                using var carImg = new Mat(frame, new OpenCvSharp.Rect((int)safeBox.X, (int)safeBox.Y, (int)safeBox.Width, (int)safeBox.Height)).Clone();

                // 번호판 예상 영역 추출 (하단 40%)
                int plateY = (int)(carImg.Height * 0.6);
                using var plateArea = new Mat(carImg, new OpenCvSharp.Rect(0, plateY, carImg.Width, carImg.Height - plateY)).Clone();

                using var gray = new Mat();
                Cv2.CvtColor(plateArea, gray, ColorConversionCodes.BGR2GRAY);
                Cv2.Threshold(gray, gray, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);

                using var img = Pix.LoadFromMemory(gray.ToBytes(".png"));
                using var page = _ocrEngine.Process(img);
                string text = page.GetText().Trim().Replace(" ", "").Replace("\n", "");

                return string.IsNullOrEmpty(text) ? "인식불가" : text;
            }
            catch { return "Error"; }
        }

        private void SaveToDb(LocalTrackedObject track, string direction, string plate)
        {
            _modelCache.TryGetValue(track.Id, out string? modelName);
            var record = new VehicleRecord
            {
                DetectTime = DateTime.Now,
                VehicleType = $"{GetTypeName(track.LastClassId)} ({modelName ?? "Unknown"})",
                Direction = direction,
                Speed = (int)Math.Round(track.Speed),
                ViolationReason = track.IsViolating ? "속도 위반" : "정상",
                LicensePlate = plate // ✅ 실제 인식된 번호 저장
            };
            Task.Run(async () => { try { if (_dbCollection != null) await _dbCollection.InsertOneAsync(record); } catch { } });
        }

        private System.Windows.Rect GetSafeRect(Mat img, System.Windows.Rect box) =>
            new System.Windows.Rect(Math.Max(0, box.X), Math.Max(0, box.Y),
            Math.Min(img.Width - box.X, box.Width), Math.Min(img.Height - box.Y, box.Height));

        private void DrawOutput(Mat frame)
        {
            foreach (var d in _currentDetections)
            {
                if (!_trackedObjects.TryGetValue(d.TrackId, out var track)) continue;

                // ✅ 속도 위반 시 박스 색상을 빨간색(Red)으로 고정
                Scalar boxColor = track.IsViolating ? Scalar.Red : (_colorCache.TryGetValue(d.TrackId, out var c) ? c : Scalar.Yellow);

                var r = new OpenCvSharp.Rect((int)d.Box.X, (int)d.Box.Y, (int)d.Box.Width, (int)d.Box.Height);
                Cv2.Rectangle(frame, r, boxColor, 2);

                _modelCache.TryGetValue(d.TrackId, out var model);
                // ✅ 속도 위반 시 텍스트 색상도 빨간색으로 변경
                Scalar textColor = track.IsViolating ? Scalar.Red : Scalar.White;
                Cv2.PutText(frame, $"{track.Speed:F1}km/h {model}", new OpenCvSharp.Point(r.X, r.Y - 10),
                    HersheyFonts.HersheySimplex, 0.5, textColor, 1);
            }
            Cv2.Line(frame, 0, (int)(frame.Height * 0.7), frame.Width, (int)(frame.Height * 0.7), Scalar.Red, 2);
        }

        private string GetSpecificCarModel(Mat cropImg)
        {
            if (_classSession == null) return "Unknown";
            try
            {
                using var rgbImg = new Mat(); Cv2.CvtColor(cropImg, rgbImg, ColorConversionCodes.BGR2RGB);
                using var resized = new Mat(); Cv2.Resize(rgbImg, resized, new OpenCvSharp.Size(160, 160));
                var input = new DenseTensor<float>(new[] { 1, 3, 160, 160 });
                for (int y = 0; y < 160; y++)
                    for (int x = 0; x < 160; x++)
                    {
                        var p = resized.At<Vec3b>(y, x);
                        input[0, 0, y, x] = p.Item0 / 255f;
                        input[0, 1, y, x] = p.Item1 / 255f;
                        input[0, 2, y, x] = p.Item2 / 255f;
                    }
                lock (_sessionLock)
                {
                    using var results = _classSession.Run(new[] { NamedOnnxValue.CreateFromTensor(_classSession.InputMetadata.Keys.First(), input) });
                    var output = results.First().AsTensor<float>();
                    float[] scores = new float[206];
                    for (int i = 0; i < 525; i++)
                        for (int c = 0; c < 206; c++) scores[c] += output[0, 4 + c, i];
                    int maxIdx = Array.IndexOf(scores, scores.Max());
                    return maxIdx < _carModelNames.Length ? _carModelNames[maxIdx] : "Unknown";
                }
            }
            catch { return "Err"; }
        }

        private void TrackAndMatch(List<LocalDetection> dets, double time)
        {
            var used = new HashSet<int>();
            foreach (var track in _trackedObjects.Values.ToList())
            {
                int best = -1; float maxIou = 0.2f;
                for (int i = 0; i < dets.Count; i++)
                {
                    if (used.Contains(i)) continue;
                    float iou = CalculateIoU(track.LastBox, dets[i].Box);
                    if (iou > maxIou) { maxIou = iou; best = i; }
                }
                if (best != -1) { dets[best].TrackId = track.Id; track.Update(dets[best], time); used.Add(best); }
                else track.Missed();
            }
            foreach (var d in dets.Where((_, i) => !used.Contains(i)))
            {
                var nt = new LocalTrackedObject(d, time); d.TrackId = nt.Id; _trackedObjects[nt.Id] = nt;
            }
            _trackedObjects.Where(kv => kv.Value.ShouldBeDeleted).ToList().ForEach(k => _trackedObjects.Remove(k.Key));
        }

        private float CalculateIoU(System.Windows.Rect r1, System.Windows.Rect r2)
        {
            double interX = Math.Max(r1.X, r2.X);
            double interY = Math.Max(r1.Y, r2.Y);
            double interW = Math.Min(r1.X + r1.Width, r2.X + r2.Width) - interX;
            double interH = Math.Min(r1.Y + r1.Height, r2.Y + r2.Height) - interY;
            if (interW <= 0 || interH <= 0) return 0;
            double interArea = interW * interH;
            return (float)(interArea / (r1.Width * r1.Height + r2.Width * r2.Height - interArea));
        }

        private string GetTypeName(int id) => id switch { 2 => "CAR", 5 => "BUS", 7 => "TRUCK", _ => "Vehicle" };

        private void OpenVideo()
        {
            var d = new OpenFileDialog();
            if (d.ShowDialog() == true)
            {
                _video.Open(d.FileName);
                _timer.Start();
            }
        }

        public void Dispose()
        {
            _timer.Stop();
            _ocrEngine?.Dispose();
            _classSession?.Dispose();
            _frame.Dispose();
            _video.Dispose();
        }
    }
}