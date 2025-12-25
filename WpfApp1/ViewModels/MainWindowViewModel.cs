using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using WpfApp1.Models;
using WpfApp1.Services;
using WpfApp1.Scripts;

namespace WpfApp1.ViewModels
{
    public class MainWindowViewModel : ViewModelBase, IDisposable
    {
        private readonly DetectionService _detectorService;
        private readonly DataService _dbService;
        private readonly TrafficManager _trafficManager = new();
        private readonly VideoPlayerService _video = new();
        private readonly LaneAnalyzer _laneAnalyzer = new();

        private readonly ConcurrentDictionary<int, string> _modelCache = new();
        private readonly DispatcherTimer _timer = new();
        private readonly Mat _frame = new();

        private Mat? _driveMask;
        private volatile bool _isBusy = false, _isLaneBusy = false, _laneOk = false;
        private List<Detection> _currentDetections = new();
        private readonly object _lock = new object();
        private const double BaseLineRatio = 0.7;

        public ObservableCollection<int> TotalLaneOptions { get; } = new() { 1, 2, 3, 4, 5, 6 };
        public ObservableCollection<int> CurrentLaneOptions { get; } = new();
        public int TotalLanes { get; set; } = 5;
        public int CurrentLane { get; set; } = 4;

        private string _countText = "L:0 | F:0 | R:0";
        public string CountText { get => _countText; set { _countText = value; OnPropertyChanged(); } }

        private BitmapSource? _frameImage;
        public BitmapSource? FrameImage { get => _frameImage; set { _frameImage = value; OnPropertyChanged(); } }

        public RelayCommand OpenVideoCommand { get; }

        public MainWindowViewModel()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _detectorService = new DetectionService(baseDir);
            _dbService = new DataService("mongodb://localhost:27017");

            OpenVideoCommand = new RelayCommand(OpenVideo);
            _timer.Tick += Timer_Tick;
            _timer.Interval = TimeSpan.FromMilliseconds(1);
        }

        private void OpenVideo()
        {
            var d = new OpenFileDialog();
            if (d.ShowDialog() == true)
            {
                _video.Open(d.FileName);
                _timer.Start();
            }
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (!_video.Read(_frame) || _frame.Empty()) return;

            if (!_isLaneBusy)
            {
                _isLaneBusy = true;
                Mat laneFrame = _frame.Clone();
                int t = TotalLanes; int c = CurrentLane;
                Task.Run(() => {
                    try
                    {
                        var r = _detectorService.InferLanes(laneFrame);
                        lock (_lock)
                        {
                            _driveMask?.Dispose();
                            _driveMask = r.DrivableMaskOrig?.Clone();
                            _laneAnalyzer.TotalLanes = t;
                            _laneAnalyzer.EgoLane = c;
                            _laneAnalyzer.SetDrivableMask(_driveMask);
                            _laneOk = r.LaneProbOrig != null && _laneAnalyzer.Analyze(r.LaneProbOrig, laneFrame.Size());
                        }
                        r.Dispose();
                    }
                    finally { laneFrame.Dispose(); _isLaneBusy = false; }
                });
            }

            if (!_isBusy)
            {
                _isBusy = true;
                Mat clone = _frame.Clone();
                double msec = _video.PosMsec;
                Task.Run(() => {
                    try
                    {
                        var dets = _detectorService.DetectObjects(clone);
                        lock (_trafficManager)
                        {
                            _trafficManager.UpdateTracking(dets, msec, _laneAnalyzer, _laneOk);
                            _trafficManager.ProcessCounting(clone.Height, BaseLineRatio);
                            _currentDetections = dets.ToList();

                            foreach (var track in _trafficManager.TrackedObjects.Values.Where(v => v.UpdateCount > 5))
                            {
                                if (!_modelCache.ContainsKey(track.Id) && track.LastClassId == 2)
                                {
                                    Rect s = track.LastBox;
                                    Rect safe = new Rect(Math.Max(0, s.X), Math.Max(0, s.Y), Math.Min(clone.Width - s.X, s.Width), Math.Min(clone.Height - s.Y, s.Height));
                                    if (safe.Width > 20 && safe.Height > 20)
                                    {
                                        using var crop = new Mat(clone, safe);
                                        _modelCache.TryAdd(track.Id, _detectorService.GetCarModel(crop));
                                    }
                                }
                                _modelCache.TryGetValue(track.Id, out string? model);
                                _dbService.UpdateRealtimeDb(track, model);
                            }
                        }
                    }
                    finally { clone.Dispose(); _isBusy = false; }
                });
            }

            DrawOutput(_frame);
            FrameImage = _frame.ToBitmapSource();
            CountText = $"L:{_trafficManager.CountL} | F:{_trafficManager.CountF} | R:{_trafficManager.CountR}";
        }

        private void DrawOutput(Mat frame)
        {
            lock (_lock)
            {
                if (_driveMask != null && !_driveMask.Empty())
                {
                    using var maskRes = new Mat();
                    Cv2.Resize(_driveMask, maskRes, frame.Size());
                    using var green = new Mat(frame.Size(), frame.Type(), new Scalar(0, 255, 0));
                    Cv2.AddWeighted(green, 0.3, frame, 0.7, 0, green);
                    green.CopyTo(frame, maskRes);
                }
                if (_laneOk) _laneAnalyzer.DrawOnFrame(frame);
            }

            lock (_trafficManager)
            {
                foreach (var d in _currentDetections)
                {
                    _trafficManager.TrackedObjects.TryGetValue(d.TrackId, out var t);
                    Scalar color = (t != null && t.CheckViolation() != "정상") ? Scalar.Red : Scalar.Yellow;
                    Cv2.Rectangle(frame, d.Box, color, 2);

                    _modelCache.TryGetValue(d.TrackId, out string? model);
                    string l1 = $"ID:{d.TrackId} | {t?.FirstDetectedTime}";
                    string l2 = $"{model ?? "Analysing..."} | {t?.SpeedInKmh:F1}km/h | L{t?.CurrentLane}";

                    DrawLabel(frame, d.Box, l1, l2, color);
                }
            }

            Cv2.Line(frame, 0, (int)(frame.Height * BaseLineRatio), frame.Width, (int)(frame.Height * BaseLineRatio), Scalar.Red, 2);
        }

        private void DrawLabel(Mat frame, Rect box, string l1, string l2, Scalar color)
        {
            var s1 = Cv2.GetTextSize(l1, HersheyFonts.HersheySimplex, 0.4, 1, out _);
            var s2 = Cv2.GetTextSize(l2, HersheyFonts.HersheySimplex, 0.4, 1, out _);
            int h = s1.Height + s2.Height + 15;
            Rect bg = new Rect(box.X, box.Y - h - 5, Math.Max(s1.Width, s2.Width) + 10, h);
            if (bg.Y < 0) bg.Y = box.Y + box.Height + 5;

            Cv2.Rectangle(frame, bg, color == Scalar.Red ? Scalar.Red : Scalar.Black, -1);
            Cv2.Rectangle(frame, bg, Scalar.White, 1);
            Cv2.PutText(frame, l1, new Point(bg.X + 5, bg.Y + s1.Height + 5), HersheyFonts.HersheySimplex, 0.4, Scalar.White, 1);
            Cv2.PutText(frame, l2, new Point(bg.X + 5, bg.Y + h - 5), HersheyFonts.HersheySimplex, 0.4, Scalar.Cyan, 1);
        }

        public void Dispose() { _timer.Stop(); _detectorService.Dispose(); _frame.Dispose(); _video.Dispose(); }
    }
}