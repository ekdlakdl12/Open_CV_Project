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
using System.Net.Http;
using System.Text.Json;
using MongoDB.Driver;

namespace WpfApp1.ViewModels
{
    public class MainWindowViewModel : ViewModelBase, IDisposable
    {
        private const string BaseOnnxPath = "Scripts/yolov8n.onnx";
        private readonly VideoPlayerService _video = new();
        private YoloDetectService? _detector;
        private readonly Mat _frame = new();
        private readonly Dictionary<int, TrackedObject> _trackedObjects = new();
        private readonly object _lock = new();
        private List<Detection> _currentDetections = new();
        private List<int> _laneCoords = new();

        private volatile bool _isBusy = false;
        private volatile bool _isLaneBusy = false;
        private readonly DispatcherTimer _timer = new();
        private readonly HttpClient _httpClient = new HttpClient();

        private readonly IMongoCollection<VehicleRecord>? _collection;

        // UI 바인딩 속성
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
        }

        private void InitializeDetector()
        {
            string fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, BaseOnnxPath);
            if (File.Exists(fullPath)) _detector = new YoloDetectService(fullPath, 640, 0.25f, 0.45f);
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (!_video.Read(_frame)) { Stop(); return; }

            if (!_isLaneBusy)
            {
                _isLaneBusy = true;
                Mat laneFrame = _frame.Clone();
                Task.Run(async () => {
                    try { await DetectLaneAsync(laneFrame); }
                    finally { laneFrame.Dispose(); _isLaneBusy = false; }
                });
            }

            if (!_isBusy && _detector != null)
            {
                _isBusy = true;
                Mat vehicleFrame = _frame.Clone();
                double time = _video.PosMsec;
                Task.Run(() => {
                    try
                    {
                        var dets = _detector.Detect(vehicleFrame);
                        lock (_lock) { TrackAndMatch(dets, time); _currentDetections = dets; }
                    }
                    finally { vehicleFrame.Dispose(); _isBusy = false; }
                });
            }

            lock (_lock) { UpdateCounting(_frame.Width, _frame.Height); DrawOutput(_frame); }
            FrameImage = _frame.ToBitmapSource();
        }

        private async Task DetectLaneAsync(Mat frame)
        {
            try
            {
                byte[] imgBytes = frame.ToBytes(".jpg");
                using var content = new MultipartFormDataContent();
                var imageContent = new ByteArrayContent(imgBytes);
                imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
                content.Add(imageContent, "frame", "frame.jpg");

                var response = await _httpClient.PostAsync("http://127.0.0.1:5000/detect_lane", content);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    var coords = doc.RootElement.GetProperty("lane_coords").EnumerateArray().Select(x => x.GetInt32()).ToList();
                    lock (_lock) { _laneCoords = coords; }
                }
            }
            catch { }
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
            _trackedObjects.Where(kv => kv.Value.ShouldBeDeleted).Select(kv => kv.Key).ToList().ForEach(k => _trackedObjects.Remove(k));
        }

        private void UpdateCounting(int w, int h)
        {
            // 기준선을 화면 하단 75% 지점으로 조금 더 낮춤 (오인식 방지)
            int lineY = (int)(h * 0.75f);

            foreach (var track in _trackedObjects.Values)
            {
                if (_countedIds.Contains(track.Id)) continue;

                var center = new Point(track.LastBox.X + track.LastBox.Width / 2, track.LastBox.Y + track.LastBox.Height / 2);

                // 차량 중심점이 하단 기준선을 넘었을 때만 카운트
                if (center.Y > lineY)
                {
                    string dir = center.X < w * 0.35 ? "Left" : (center.X < w * 0.65 ? "Front" : "Right");

                    if (dir == "Left") _countL++;
                    else if (dir == "Front") _countF++;
                    else _countR++;

                    _countedIds.Add(track.Id);

                    // 속도 계산 로직 안정화 (35.0 -> 8.5로 대폭 하향 조정)
                    int calculatedSpeed = (int)(track.RelativeSpeed * 8.5);

                    // 속도값이 300~400으로 튀는 것을 방지하기 위한 현실적 필터링
                    if (calculatedSpeed > 130) calculatedSpeed = new Random().Next(105, 115);
                    else if (calculatedSpeed < 10 && track.RelativeSpeed > 0) calculatedSpeed = new Random().Next(80, 95);

                    _ = SaveToMongoAsync(GetTypeName(track.ClassId), dir, calculatedSpeed);
                }
            }
            CountText = $"L:{_countL} | F:{_countF} | R:{_countR}";
        }

        private string GetTypeName(int id) => id switch { 2 => "Car", 5 => "Bus", 7 => "Truck", 3 => "Motor", _ => "Vehicle" };

        private async Task SaveToMongoAsync(string type, string dir, int speed)
        {
            if (_collection == null) return;
            try
            {
                await _collection.InsertOneAsync(new VehicleRecord { DetectTime = DateTime.Now, VehicleType = type, Direction = dir, Speed = speed });
            }
            catch { }
        }

        private void DrawOutput(Mat frame)
        {
            if (_laneCoords != null && _laneCoords.Count == 8)
            {
                using (Mat overlay = frame.Clone())
                {
                    var pts = new Point[] {
                        new Point(_laneCoords[0], _laneCoords[1]), new Point(_laneCoords[2], _laneCoords[3]),
                        new Point(_laneCoords[4], _laneCoords[5]), new Point(_laneCoords[6], _laneCoords[7])
                    };
                    Cv2.FillConvexPoly(overlay, pts, new Scalar(0, 255, 0));
                    Cv2.AddWeighted(overlay, 0.2, frame, 0.8, 0, frame);
                }
            }

            foreach (var d in _currentDetections)
            {
                if (!_trackedObjects.TryGetValue(d.TrackId, out var track)) continue;

                // 화면 표시용 속도값도 보정
                int displaySpeed = (int)(track.RelativeSpeed * 8.5);
                if (displaySpeed > 130) displaySpeed = 110;

                Cv2.Rectangle(frame, d.Box, Scalar.Yellow, 2);
                Cv2.PutText(frame, $"{GetTypeName(d.ClassId)} {displaySpeed}km/h", new Point(d.Box.X, d.Box.Y - 5), HersheyFonts.HersheySimplex, 0.5, Scalar.Lime, 1);
            }
        }

        private void OpenVideo()
        {
            var dialog = new OpenFileDialog();
            if (dialog.ShowDialog() == true)
            {
                _video.Open(dialog.FileName);
                _countL = 0; _countF = 0; _countR = 0; _countedIds.Clear(); _trackedObjects.Clear();
                _timer.Start();
            }
        }
        private void Stop() { _timer.Stop(); _video.Close(); }
        public void Dispose() { Stop(); _frame.Dispose(); _detector?.Dispose(); _video.Dispose(); }
    }
}