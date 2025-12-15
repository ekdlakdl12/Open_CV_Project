using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using WpfApp1.Models;
using WpfApp1.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WpfApp1.ViewModels
{
    public class MainWindowViewModel : ViewModelBase, IDisposable
    {
        private const string OnnxPath = "yolov8n.onnx";

        // ✅ 튜닝값
        private const int RenderEveryNFrames = 1;   // 화면은 가능한 매 프레임(부담되면 2로)
        private const int LogEveryMs = 2000;        // 로그 2초마다
        private const int MaxSkipFramesPerTick = 5; // 한 번에 너무 많이 스킵 방지

        private readonly DispatcherTimer _timer = new();
        private readonly Stopwatch _swLog = new();
        private readonly Stopwatch _swTick = new();     // Tick 처리시간 측정
        private readonly Stopwatch _swInfer = new();    // 추론시간 측정

        private readonly VideoPlayerService _video = new();
        private readonly YoloDetectService _detector = new(OnnxPath);

        private readonly Mat _frame = new();

        private int _frameCount = 0;

        // ✅ 추론 관련(비동기)
        private volatile bool _inferRunning = false;
        private readonly object _detLock = new();
        private List<Detection> _lastDetections = new();

        // ✅ 성능 로그 누적
        private long _inferMsAcc = 0;
        private int _inferCount = 0;
        private long _renderMsAcc = 0;
        private int _renderCount = 0;
        private readonly Stopwatch _swRender = new();

        // ===== Commands =====
        public RelayCommand OpenVideoCommand { get; }
        public RelayCommand StopCommand { get; }

        // ===== Bind Properties =====
        private string _videoPath = "파일을 선택하세요";
        public string VideoPath
        {
            get => _videoPath;
            set { _videoPath = value; OnPropertyChanged(); }
        }

        private BitmapSource? _frameImage;
        public BitmapSource? FrameImage
        {
            get => _frameImage;
            set { _frameImage = value; OnPropertyChanged(); }
        }

        private string _statusText = "Ready";
        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        private int _detectionsCount;
        public int DetectionsCount
        {
            get => _detectionsCount;
            set { _detectionsCount = value; OnPropertyChanged(); }
        }

        public MainWindowViewModel()
        {
            OpenVideoCommand = new RelayCommand(OpenVideo);
            StopCommand = new RelayCommand(Stop);

            _timer.Tick += Timer_Tick;
        }

        private void OpenVideo()
        {
            var dlg = new OpenFileDialog
            {
                Title = "동영상 파일 선택",
                Filter = "Video Files|*.mp4;*.avi;*.mkv;*.mov;*.wmv|All Files|*.*"
            };

            if (dlg.ShowDialog() != true) return;

            VideoPath = dlg.FileName;
            Start(VideoPath);
        }

        private void Start(string path)
        {
            Stop();

            _video.Open(path);

            // ✅ 타이머는 “최대한 자주” 돌리고, 실제 재생 속도는 “프레임 드랍”으로 맞춘다
            _timer.Interval = TimeSpan.FromMilliseconds(1);

            _frameCount = 0;
            _inferMsAcc = 0;
            _inferCount = 0;
            _renderMsAcc = 0;
            _renderCount = 0;

            lock (_detLock) _lastDetections.Clear();
            _inferRunning = false;

            _swLog.Restart();

            StatusText = $"Playing... ({_video.Fps:0.0} fps)";
            _timer.Start();
        }

        private void Stop()
        {
            _timer.Stop();
            _video.Close();
            StatusText = "Stopped";
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            // 목표 프레임 시간(ms)
            double targetMs = 1000.0 / _video.Fps;

            _swTick.Restart();

            if (!_video.Read(_frame))
            {
                Stop();
                return;
            }

            _frameCount++;

            // =========================
            // 1) 비동기 추론 (UI 스레드에서 추론 금지)
            // =========================
            if (!_inferRunning)
            {
                _inferRunning = true;

                // ✅ Mat은 다른 스레드로 넘길 때 Clone해서 넘겨야 안전
                Mat inferMat = _frame.Clone();

                Task.Run(() =>
                {
                    try
                    {
                        _swInfer.Restart();
                        var dets = _detector.Detect(inferMat);
                        _swInfer.Stop();

                        lock (_detLock)
                        {
                            _lastDetections = dets;
                        }

                        _inferMsAcc += _swInfer.ElapsedMilliseconds;
                        _inferCount++;
                    }
                    catch
                    {
                        // 필요하면 Debug.WriteLine(ex) 처리
                    }
                    finally
                    {
                        inferMat.Dispose();
                        _inferRunning = false;
                    }
                });
            }

            // =========================
            // 2) Render (N프레임마다)
            // =========================
            if (RenderEveryNFrames <= 1 || (_frameCount % RenderEveryNFrames == 0))
            {
                List<Detection> detsSnapshot;
                lock (_detLock)
                {
                    detsSnapshot = _lastDetections;
                }

                foreach (var d in detsSnapshot)
                {
                    string label = d.ClassId switch
                    {
                        2 => "CAR",
                        5 => "BUS",
                        7 => "TRUCK",
                        _ => "OBJ"
                    };

                    Cv2.Rectangle(_frame, d.Box, Scalar.LimeGreen, 2);
                    Cv2.PutText(
                        _frame,
                        $"{label} {d.Score:0.00}",
                        new OpenCvSharp.Point(d.Box.X, Math.Max(20, d.Box.Y - 5)),
                        HersheyFonts.HersheySimplex,
                        0.7,
                        Scalar.Red,
                        2);
                }

                _swRender.Restart();
                FrameImage = _frame.ToBitmapSource();
                _swRender.Stop();

                _renderMsAcc += _swRender.ElapsedMilliseconds;
                _renderCount++;

                DetectionsCount = detsSnapshot.Count;
            }

            // =========================
            // 3) 프레임 드랍으로 “실시간 재생처럼” 따라가기
            // =========================
            _swTick.Stop();
            double spentMs = _swTick.Elapsed.TotalMilliseconds;

            // 처리 시간이 목표(ms)보다 크면 그만큼 프레임을 스킵
            if (spentMs > targetMs)
            {
                int needSkip = (int)(spentMs / targetMs) - 1; // -1: 이미 1프레임은 처리했으니까
                if (needSkip > 0)
                {
                    needSkip = Math.Min(needSkip, MaxSkipFramesPerTick);
                    _video.GrabFrames(needSkip);
                }
            }

            // =========================
            // 4) 로그(부하 최소): 2초마다 평균만
            // =========================
            if (_swLog.ElapsedMilliseconds >= LogEveryMs)
            {
                double avgInfer = _inferCount > 0 ? (double)_inferMsAcc / _inferCount : 0.0;
                double avgRender = _renderCount > 0 ? (double)_renderMsAcc / _renderCount : 0.0;

                Debug.WriteLine(
                    $"[LOG] frames={_frameCount}, posFrame={_video.PosFrame}, posMs={_video.PosMsec:0}, " +
                    $"dets={DetectionsCount}, avgInferMs={avgInfer:0.0}, avgRenderMs={avgRender:0.0}, " +
                    $"targetMs={targetMs:0.0}, tickMs={spentMs:0.0}"
                );

                _inferMsAcc = 0;
                _inferCount = 0;
                _renderMsAcc = 0;
                _renderCount = 0;
                _swLog.Restart();
            }
        }

        public void Dispose()
        {
            Stop();
            _frame.Dispose();
            _detector.Dispose();
            _video.Dispose();
        }
    }
}
