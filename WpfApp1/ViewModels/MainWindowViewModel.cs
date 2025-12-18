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

        private volatile bool _isBusy = false;
        private readonly DispatcherTimer _timer = new();

        // UI 바인딩 속성
        private string _countText = "L:0 | F:0 | R:0 | Dets:0";
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
                // 경로 체크 후 로드
                string fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, BaseOnnxPath);
                if (File.Exists(fullPath))
                    _detector = new YoloDetectService(fullPath, 640, 0.25f, 0.45f);
                else
                    CountText = "Error: Model file not found";
            }
            catch (Exception ex)
            {
                CountText = $"Error: {ex.Message}";
            }
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (!_video.Read(_frame)) { Stop(); return; }

            if (!_isBusy && _detector != null)
            {
                _isBusy = true;
                Mat clone = _frame.Clone();
                double time = _video.PosMsec;

                Task.Run(() => {
                    try
                    {
                        var dets = _detector.Detect(clone);
                        lock (_lock)
                        {
                            TrackAndMatch(dets, time);
                            _currentDetections = dets;
                        }
                    }
                    finally { clone.Dispose(); _isBusy = false; }
                });
            }

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
                int bestIdx = -1; float bestIou = 0.35f; // 매칭 임계값 강화 (ID 스위칭 방지)
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
                else { track.Missed(); }
            }
            foreach (var d in detections.Where((_, i) => !usedDets.Contains(i)))
            {
                var newTrack = new TrackedObject(d, timeMsec);
                d.TrackId = newTrack.Id;
                _trackedObjects[newTrack.Id] = newTrack;
            }
            var toRemove = _trackedObjects.Where(kv => kv.Value.ShouldBeDeleted).Select(kv => kv.Key).ToList();
            foreach (var k in toRemove) _trackedObjects.Remove(k);
        }

        private void UpdateCounting(int w, int h)
        {
            int lineY = (int)(h * 0.65f);
            foreach (var track in _trackedObjects.Values)
            {
                if (_countedIds.Contains(track.Id)) continue;
                var center = new Point(track.LastBox.X + track.LastBox.Width / 2, track.LastBox.Y + track.LastBox.Height / 2);
                if (center.Y > lineY)
                {
                    if (center.X < w * 0.33) _countL++;
                    else if (center.X < w * 0.66) _countF++;
                    else _countR++;
                    _countedIds.Add(track.Id);
                }
            }
            CountText = $"L:{_countL} | F:{_countF} | R:{_countR} | Dets:{_currentDetections.Count}";
        }

        private void DrawOutput(Mat frame)
        {
            int lineY = (int)(frame.Height * 0.65f);
            Cv2.Line(frame, 0, lineY, frame.Width, lineY, Scalar.Blue, 2);

            foreach (var d in _currentDetections)
            {
                if (!_trackedObjects.TryGetValue(d.TrackId, out var track)) continue;

                // 1. 차종 분류 (Car:2, Bus:5, Truck:7)
                string typeName = d.ClassId switch
                {
                    2 => "Car",
                    5 => "Bus",
                    7 => "Truck",
                    3 => "Motor",
                    _ => "Vehicle"
                };

                // 2. 속도 보정 (현실적인 속도 필터링)
                // 8.0 계수는 픽셀거리를 km/h로 임의 환산한 값임
                double speed = track.RelativeSpeed * 8.0;

                if (speed > 180) speed = 0; // ID 스위칭으로 인한 튀는 현상 제거
                if (speed < 3) speed = 0;   // 정지 상태 미세 흔들림 제거

                string info = $"[{typeName}] ID:{track.Id} {(int)speed}km/h";

                // 3. 가독성 개선 (검은색 배경 박스 + 라임색 텍스트)
                var textSize = Cv2.GetTextSize(info, HersheyFonts.HersheySimplex, 0.45, 1, out _);
                var bgRect = new Rect(d.Box.X, d.Box.Y - textSize.Height - 10, textSize.Width + 10, textSize.Height + 5);

                Cv2.Rectangle(frame, bgRect, Scalar.Black, -1); // 배경
                Cv2.Rectangle(frame, d.Box, Scalar.Yellow, 2);  // 노란 박스
                Cv2.PutText(frame, info, new Point(d.Box.X + 5, d.Box.Y - 7), HersheyFonts.HersheySimplex, 0.45, Scalar.Lime, 1);
            }
        }

        private void OpenVideo()
        {
            var dialog = new OpenFileDialog();
            if (dialog.ShowDialog() == true)
            {
                _video.Open(dialog.FileName);
                _countL = 0; _countF = 0; _countR = 0;
                _countedIds.Clear(); _trackedObjects.Clear();
                _timer.Start();
            }
        }

        private void Stop() { _timer.Stop(); _video.Close(); }
        public void Dispose() { Stop(); _frame.Dispose(); _detector?.Dispose(); _video.Dispose(); }
    }
}