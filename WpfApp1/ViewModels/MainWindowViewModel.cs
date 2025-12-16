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
using System.Linq;

namespace WpfApp1.ViewModels
{
    public class MainWindowViewModel : ViewModelBase, IDisposable
    {
        private const string OnnxPath = "yolov8n.onnx";

        // ✅ 튜닝값 및 상수
        private const int RenderEveryNFrames = 1;
        private const int LogEveryMs = 2000;
        private const int MaxSkipFramesPerTick = 5;
        private const float SPEED_SCALE_FACTOR = 8.0f;

        // ✅ 시각화 개선을 위한 색상 정의
        // (B, G, R 순서: OpenCvSharp Scalar)
        private static readonly Scalar ColorBox = new(0, 255, 255);      // 노란색 (테두리)
        private static readonly Scalar ColorLabel = new(0, 255, 0);     // 녹색 (ID, 차종)
        private static readonly Scalar ColorSpeedHigh = new(0, 0, 255); // 빨간색 (속도 높음)
        private static readonly Scalar ColorSpeedNormal = new(0, 165, 255); // 주황색 (속도 보통)


        private readonly DispatcherTimer _timer = new();
        private readonly Stopwatch _swLog = new();
        private readonly Stopwatch _swTick = new();
        private readonly Stopwatch _swInfer = new();

        private readonly VideoPlayerService _video = new();
        private readonly YoloDetectService _detector = new(OnnxPath);

        private readonly Mat _frame = new();

        private int _frameCount = 0;

        private volatile bool _inferRunning = false;
        private readonly object _detLock = new();
        private List<Detection> _lastDetections = new();

        private readonly Dictionary<int, TrackedObject> _trackedObjects = new();

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

        private string _trackedCountText = "Tracks: 0";
        public string TrackedCountText
        {
            get => _trackedCountText;
            set { _trackedCountText = value; OnPropertyChanged(); }
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

            _timer.Interval = TimeSpan.FromMilliseconds(1);

            _frameCount = 0;
            _inferMsAcc = 0;
            _inferCount = 0;
            _renderMsAcc = 0;
            _renderCount = 0;

            lock (_detLock) _lastDetections.Clear();
            _trackedObjects.Clear();
            TrackedCountText = "Tracks: 0";
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
            _trackedObjects.Clear();
            TrackedCountText = "Tracks: 0";
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            double targetMs = 1000.0 / _video.Fps;

            _swTick.Restart();

            if (!_video.Read(_frame))
            {
                Stop();
                return;
            }

            _frameCount++;

            // 1) 비동기 추론
            if (!_inferRunning)
            {
                _inferRunning = true;
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
                    catch { /* 예외 처리 */ }
                    finally
                    {
                        inferMat.Dispose();
                        _inferRunning = false;
                    }
                });
            }

            // 2) 추적 로직 수행
            List<Detection> detsSnapshot;
            lock (_detLock)
            {
                detsSnapshot = _lastDetections;
            }

            TrackAndMatch(detsSnapshot, _video.PosMsec);


            // 3) Render (시각화 개선 로직 적용)
            if (RenderEveryNFrames <= 1 || (_frameCount % RenderEveryNFrames == 0))
            {
                foreach (var d in detsSnapshot)
                {
                    _trackedObjects.TryGetValue(d.TrackId, out var track);

                    // 속도 계산 및 텍스트 준비
                    string speedLineText = "";
                    int correctedSpeed = 0;
                    if (track != null)
                    {
                        correctedSpeed = (int)(track.RelativeSpeed * SPEED_SCALE_FACTOR);
                        speedLineText = $"{correctedSpeed:0} km/h";
                    }

                    // 차종 텍스트 준비
                    string label = d.ClassId switch
                    {
                        2 => "CAR",
                        5 => "BUS",
                        7 => "TRUCK",
                        _ => "OBJ"
                    };

                    // 첫 번째 줄: ID와 차종
                    string idLineText = track != null
                                        ? $"ID:{track.Id} {label}"
                                        : $"{label} {d.Score:0.00}";


                    // 렌더링 부분: 두 줄 텍스트와 색상 구분

                    // 1. 박스 테두리 색상 (노란색)
                    Cv2.Rectangle(_frame, d.Box, ColorBox, 2);

                    // 2. 텍스트 1: ID와 차종 (녹색)
                    int yPos1 = Math.Max(20, d.Box.Y - 20);

                    Cv2.PutText(
                        _frame,
                        idLineText,
                        new OpenCvSharp.Point(d.Box.X, yPos1),
                        HersheyFonts.HersheySimplex,
                        0.7,
                        ColorLabel,
                        2);

                    // 3. 텍스트 2: 속도 
                    if (track != null)
                    {
                        int yPos2 = Math.Max(20, d.Box.Y - 5);

                        // 80 km/h를 기준으로 색상 구분 (고속도로 기준)
                        Scalar currentSpeedColor = (correctedSpeed >= 80) ? ColorSpeedHigh : ColorSpeedNormal;

                        Cv2.PutText(
                            _frame,
                            speedLineText,
                            new OpenCvSharp.Point(d.Box.X, yPos2),
                            HersheyFonts.HersheySimplex,
                            0.7,
                            currentSpeedColor,
                            2);
                    }

                }

                _swRender.Restart();
                FrameImage = _frame.ToBitmapSource();
                _swRender.Stop();

                _renderMsAcc += _swRender.ElapsedMilliseconds;
                _renderCount++;

                DetectionsCount = detsSnapshot.Count;
            }

            // 4) 프레임 드랍으로 따라가기
            _swTick.Stop();
            double spentMs = _swTick.Elapsed.TotalMilliseconds;

            if (spentMs > targetMs)
            {
                int needSkip = (int)(spentMs / targetMs) - 1;
                if (needSkip > 0)
                {
                    needSkip = Math.Min(needSkip, MaxSkipFramesPerTick);
                    _video.GrabFrames(needSkip);
                }
            }

            // 5) 로그
            if (_swLog.ElapsedMilliseconds >= LogEveryMs)
            {
                double avgInfer = _inferCount > 0 ? (double)_inferMsAcc / _inferCount : 0.0;
                double avgRender = _renderCount > 0 ? (double)_renderMsAcc / _renderCount : 0.0;

                Debug.WriteLine(
                    $"[LOG] frames={_frameCount}, posFrame={_video.PosFrame}, posMs={_video.PosMsec:0}, " +
                    $"dets={DetectionsCount}, tracks={_trackedObjects.Count}, " +
                    $"avgInferMs={avgInfer:0.0}, avgRenderMs={avgRender:0.0}, " +
                    $"targetMs={targetMs:0.0}, tickMs={spentMs:0.0}"
                );

                _inferMsAcc = 0;
                _inferCount = 0;
                _renderMsAcc = 0;
                _renderCount = 0;
                _swLog.Restart();
            }
        }

        private void TrackAndMatch(List<Detection> currentDetections, double timeMsec)
        {
            var unmatchedDetections = new List<Detection>(currentDetections);

            // 1. 기존 트랙과 현재 감지 결과 매칭
            foreach (var track in _trackedObjects.Values.ToList())
            {
                int bestMatchIndex = -1;
                float bestIou = 0.0f;

                for (int i = 0; i < unmatchedDetections.Count; i++)
                {
                    float iou = YoloV8Onnx.IoU(track.LastBox, unmatchedDetections[i].Box);
                    if (iou > 0.3f && iou > bestIou)
                    {
                        bestIou = iou;
                        bestMatchIndex = i;
                    }
                }

                if (bestMatchIndex != -1)
                {
                    var matchedDetection = unmatchedDetections[bestMatchIndex];
                    track.Update(matchedDetection, timeMsec);
                    unmatchedDetections.RemoveAt(bestMatchIndex);
                }
                else
                {
                    track.Missed();
                }
            }

            // 2. 매칭되지 않은 감지 결과는 새로운 트랙으로 추가
            foreach (var det in unmatchedDetections)
            {
                if (det.ClassId == 2 || det.ClassId == 5 || det.ClassId == 7)
                {
                    var newTrack = new TrackedObject(det, timeMsec);
                    _trackedObjects.Add(newTrack.Id, newTrack);
                }
            }

            // 3. UI 업데이트
            TrackedCountText = $"Tracks: {_trackedObjects.Count}";
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