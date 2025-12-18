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
                // 품질 개선을 위해 임계값을 0.25f로 재설정
                _detector = new YoloDetectService(BaseOnnxPath, 640, 0.25f, 0.45f);
            }
            catch { }
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
                        // 1. 추론 및 추적
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
                // 2. 카운팅 및 시각화
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
                int bestIdx = -1; float bestIou = 0.25f;
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
            CountText = $"L:{_countL} | F:{_countF} | R:{_countR} | Active:{_currentDetections.Count}";
        }

        private void DrawOutput(Mat frame)
        {
            int lineY = (int)(frame.Height * 0.65f);
            Cv2.Line(frame, 0, lineY, frame.Width, lineY, Scalar.Blue, 2);

            foreach (var d in _currentDetections)
            {
                if (!_trackedObjects.TryGetValue(d.TrackId, out var track)) continue;

                // 속도 보정 (8.0 계수 적용)
                string info = $"ID:{track.Id} {(int)(track.RelativeSpeed * 8)}km/h";
                Cv2.Rectangle(frame, d.Box, Scalar.Yellow, 2);
                Cv2.PutText(frame, info, new Point(d.Box.X, d.Box.Y - 10), HersheyFonts.HersheySimplex, 0.5, Scalar.Green, 2);
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