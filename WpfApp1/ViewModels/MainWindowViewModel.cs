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
using System.Net.Http;
using Newtonsoft.Json;
// using System.Net.Http.Headers; // 일반적으로 필요 없으므로 제거


namespace WpfApp1.ViewModels
{
    public class MainWindowViewModel : ViewModelBase, IDisposable
    {
        private const string OnnxPath = "yolov8n.onnx";

        // ===== 튜닝값 및 상수 =====
        private const int RenderEveryNFrames = 1;
        private const int LogEveryMs = 2000;
        private const float SPEED_SCALE_FACTOR = 8.0f;
        private const float RegionApplyHeightRatio = 0.65f; // 카운팅 영역 시작 Y 비율

        // 카운팅 영역 경계 비율
        private const float SrcX3 = 0.05f;
        private const float SrcX4 = 0.95f;
        private const float CenterBoundaryLeftRatio = 0.4f;
        private const float CenterBoundaryRightRatio = 0.6f;

        // 시각화 색상 정의 (B, G, R 순서)
        private static readonly Scalar ColorBox = new(0, 255, 255);
        private static readonly Scalar ColorLabel = new(0, 255, 0);
        private static readonly Scalar ColorSpeedHigh = new(0, 0, 255);
        private static readonly Scalar ColorSpeedNormal = new(0, 165, 255);


        // ===== 내부 필드 및 Properties =====
        private readonly DispatcherTimer _timer = new();
        private readonly Stopwatch _swLog = new();
        private readonly Stopwatch _swTick = new();
        private readonly Stopwatch _swInfer = new();

        private readonly VideoPlayerService _video = new();
        private readonly YoloDetectService _detector = new(OnnxPath);

        private readonly Mat _frame = new();
        private readonly HttpClient _httpClient = new HttpClient();

        private int _frameCount = 0;
        private volatile bool _inferRunning = false;
        private volatile bool _pythonCallRunning = false;

        private readonly object _detLock = new();
        private List<Detection> _lastDetections = new();
        private readonly Dictionary<int, TrackedObject> _trackedObjects = new();

        private List<int> _lastLaneCoords = new List<int>(); // Python에서 받은 차선 좌표 저장

        private int _countLeft = 0;
        private int _countFront = 0;
        private int _countRight = 0;

        private long _inferMsAcc = 0;
        private int _inferCount = 0;
        private long _renderMsAcc = 0;
        private int _renderCount = 0;
        private readonly Stopwatch _swRender = new();


        public RelayCommand OpenVideoCommand { get; }
        public RelayCommand StopCommand { get; }

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

        private string _countText = "L:0 | F:0 | R:0";
        public string CountText
        {
            get => _countText;
            set { _countText = value; OnPropertyChanged(); }
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
            CountText = "L:0 | F:0 | R:0";

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
            CountText = "L:0 | F:0 | R:0";
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
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

            // 3) 차량 영역 카운팅 로직 실행 
            if (!_frame.Empty())
            {
                CountVehicleRegions(_frame.Width, _frame.Height);
            }


            // 4) Render (시각화)
            if (RenderEveryNFrames <= 1 || (_frameCount % RenderEveryNFrames == 0))
            {
                // ✅ 새로 추가된 부분: Python 서버 호출 (비동기)
                if (!_pythonCallRunning && !_frame.Empty())
                {
                    Mat frameClone = _frame.Clone();
                    Task.Run(() => CallPythonLaneDetection(frameClone));
                }

                // 4-1. 차선 시각화 (렌더링)
                DrawLaneVisualization(_frame);

                // 4-2. 카운팅 영역 경계선 그리기 (디버깅용)
                DrawCountingBoundaries(_frame, _frame.Width, _frame.Height);

                // 4-3. 차량 박스 및 텍스트 렌더링 루프 (유지)
                foreach (var d in detsSnapshot)
                {
                    _trackedObjects.TryGetValue(d.TrackId, out var track);

                    string speedLineText = "";
                    int correctedSpeed = 0;
                    if (track != null)
                    {
                        correctedSpeed = (int)(track.RelativeSpeed * SPEED_SCALE_FACTOR);
                        speedLineText = $"{correctedSpeed:0} km/h";
                    }

                    string label = d.ClassId switch
                    {
                        2 => "CAR",
                        5 => "BUS",
                        7 => "TRUCK",
                        _ => "OBJ"
                    };

                    string idLineText = track != null
                                        ? $"ID:{track.Id} {label}"
                                        : $"{label} {d.Score:0.00}";


                    Cv2.Rectangle(_frame, d.Box, ColorBox, 2);

                    int yPos1 = Math.Max(20, d.Box.Y - 20);

                    Cv2.PutText(
                        _frame, idLineText, new OpenCvSharp.Point(d.Box.X, yPos1),
                        HersheyFonts.HersheySimplex, 0.7, ColorLabel, 2);

                    if (track != null)
                    {
                        int yPos2 = Math.Max(20, d.Box.Y - 5);
                        Scalar currentSpeedColor = (correctedSpeed >= 80) ? ColorSpeedHigh : ColorSpeedNormal;

                        Cv2.PutText(
                            _frame, speedLineText, new OpenCvSharp.Point(d.Box.X, yPos2),
                            HersheyFonts.HersheySimplex, 0.7, currentSpeedColor, 2);
                    }

                } // 렌더링 루프 종료

                _swRender.Restart();
                FrameImage = _frame.ToBitmapSource();
                _swRender.Stop();

                _renderMsAcc += _swRender.ElapsedMilliseconds;
                _renderCount++;

                DetectionsCount = detsSnapshot.Count;
            }

            // 5) 로그 및 기타
            if (_swLog.ElapsedMilliseconds > LogEveryMs)
            {
                double avgInferMs = (_inferCount == 0) ? 0 : (double)_inferMsAcc / _inferCount;
                double avgRenderMs = (_renderCount == 0) ? 0 : (double)_renderMsAcc / _renderCount;
                string fps = (_swLog.ElapsedMilliseconds == 0) ? "0.0" : (_frameCount / (_swLog.ElapsedMilliseconds / 1000.0)).ToString("0.0");

                StatusText = $"Playing... ({_video.Fps:0.0} fps) | Video: {fps} fps | Infer Avg: {avgInferMs:0.0} ms | Render Avg: {avgRenderMs:0.0} ms";
                _swLog.Restart();
                _frameCount = 0;
                _inferMsAcc = 0;
                _inferCount = 0;
                _renderMsAcc = 0;
                _renderCount = 0;
            }
        }

        // ✅ Python 서버 호출 로직 (최종 수정 반영: OutputArray 및 List<int> to int[] 문제 해결)
        private async Task CallPythonLaneDetection(Mat frame)
        {
            if (_pythonCallRunning) return;
            _pythonCallRunning = true;

            try
            {
                // 1. Mat을 JPEG 바이트 배열로 인코딩 
                // Cv2.ImEncode의 out byte[] 오버로드를 사용하고, 매개변수를 int[]로 변환합니다.
                var pList = new List<int> { (int)ImwriteFlags.JpegQuality, 90 };
                int[] pArray = pList.ToArray(); // List<int>를 int[]로 변환 (CS1503 해결)

                if (!Cv2.ImEncode(".jpg", frame, out byte[] imageBytes, pArray))
                {
                    throw new InvalidOperationException("Failed to encode frame to JPEG.");
                }

                // 2. HTTP 요청 생성
                using var content = new MultipartFormDataContent();
                content.Add(new ByteArrayContent(imageBytes), "frame", "frame.jpg");

                // 3. Python 서버 호출
                var response = await _httpClient.PostAsync("http://127.0.0.1:5000/detect_lane", content);
                response.EnsureSuccessStatusCode();

                var responseString = await response.Content.ReadAsStringAsync();

                // 4. JSON 응답 파싱
                var jsonResponse = JsonConvert.DeserializeObject<dynamic>(responseString);

                if (jsonResponse?.status == "success")
                {
                    // 5. 좌표 데이터 파싱 및 저장 (List<int> 형태)
                    var coords = jsonResponse.lane_coords.ToObject<List<int>>();
                    lock (_lastLaneCoords)
                    {
                        _lastLaneCoords = coords;
                    }
                }
                else
                {
                    Debug.WriteLine($"Python Server Error: {jsonResponse?.message}");
                }
            }
            catch (HttpRequestException httpEx)
            {
                // Python 서버가 실행 중이지 않을 때 발생하는 오류
                Debug.WriteLine($"HTTP Error: Python Server not running or unreachable. {httpEx.Message}");
                lock (_lastLaneCoords) { _lastLaneCoords.Clear(); }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Python Call Error: {ex.Message}");
            }
            finally
            {
                frame.Dispose();
                _pythonCallRunning = false;
            }
        }

        // 차선 시각화 메서드
        private void DrawLaneVisualization(Mat frame)
        {
            List<int> coordsSnapshot;
            lock (_lastLaneCoords)
            {
                coordsSnapshot = new List<int>(_lastLaneCoords);
            }

            // [x1, y1, x2, y2, x3, y3, x4, y4] (8개 좌표)가 없을 경우 리턴
            if (coordsSnapshot.Count != 8) return;

            // 좌표 추출 (p1=LeftTop, p2=RightTop, p3=RightBottom, p4=LeftBottom)
            Point p1 = new(coordsSnapshot[0], coordsSnapshot[1]);
            Point p2 = new(coordsSnapshot[2], coordsSnapshot[3]);
            Point p3 = new(coordsSnapshot[4], coordsSnapshot[5]);
            Point p4 = new(coordsSnapshot[6], coordsSnapshot[7]);

            // 1. 차선 영역 (녹색 다각형) 그리기
            Point[] lanePolygon = { p1, p2, p3, p4 };
            using Mat overlay = Mat.Zeros(frame.Size(), frame.Type());
            Cv2.FillConvexPoly(overlay, lanePolygon, new Scalar(0, 255, 0)); // BGR: Green

            // 2. 투명하게 오버레이
            Cv2.AddWeighted(overlay, 0.35, frame, 1.0, 0, frame);

            // 3. 차선 경계선 (노란색 선) 그리기
            Scalar boundaryColor = new(0, 255, 255); // Yellow
            Cv2.Line(frame, p4, p1, boundaryColor, 4); // Left Lane
            Cv2.Line(frame, p3, p2, boundaryColor, 4); // Right Lane
        }

        // 카운팅 영역 경계선 그리기 메서드
        private void DrawCountingBoundaries(Mat frame, int frameWidth, int frameHeight)
        {
            Scalar boundaryColor = new(255, 100, 0);
            int boundaryThickness = 2;

            int roiYStart = (int)(frameHeight * RegionApplyHeightRatio);

            Cv2.Line(frame, new Point(0, roiYStart), new Point(frameWidth, roiYStart), boundaryColor, 1);

            int leftBoundary = (int)(frameWidth * SrcX3);
            int rightBoundary = (int)(frameWidth * SrcX4);
            Cv2.Line(frame, new Point(leftBoundary, roiYStart), new Point(leftBoundary, frameHeight), boundaryColor, boundaryThickness);
            Cv2.Line(frame, new Point(rightBoundary, roiYStart), new Point(rightBoundary, frameHeight), boundaryColor, boundaryThickness);

            int centerBoundaryLeft = (int)(frameWidth * CenterBoundaryLeftRatio);
            int centerBoundaryRight = (int)(frameWidth * CenterBoundaryRightRatio);
            Cv2.Line(frame, new Point(centerBoundaryLeft, roiYStart), new Point(centerBoundaryLeft, frameHeight), new Scalar(0, 0, 255), 1);
            Cv2.Line(frame, new Point(centerBoundaryRight, roiYStart), new Point(centerBoundaryRight, frameHeight), new Scalar(0, 0, 255), 1);

            Cv2.PutText(frame, "LEFT", new Point((leftBoundary + centerBoundaryLeft) / 2 - 20, frameHeight - 10), HersheyFonts.HersheySimplex, 0.5, boundaryColor, 1);
            Cv2.PutText(frame, "FRONT", new Point((centerBoundaryLeft + centerBoundaryRight) / 2 - 30, frameHeight - 10), HersheyFonts.HersheySimplex, 0.5, boundaryColor, 1);
            Cv2.PutText(frame, "RIGHT", new Point((centerBoundaryRight + rightBoundary) / 2 - 25, frameHeight - 10), HersheyFonts.HersheySimplex, 0.5, boundaryColor, 1);
        }

        // 카운팅 로직
        private void CountVehicleRegions(int frameWidth, int frameHeight)
        {
            int roiYStart = (int)(frameHeight * RegionApplyHeightRatio);
            int leftBoundary = (int)(frameWidth * SrcX3);
            int rightBoundary = (int)(frameWidth * SrcX4);
            int centerBoundaryLeft = (int)(frameWidth * CenterBoundaryLeftRatio);
            int centerBoundaryRight = (int)(frameWidth * CenterBoundaryRightRatio);

            _countLeft = 0;
            _countFront = 0;
            _countRight = 0;

            foreach (var track in _trackedObjects.Values.ToList())
            {
                if (track.LastBox.Bottom < roiYStart) continue;

                int vehicleCx = track.LastBox.X + track.LastBox.Width / 2;

                if (vehicleCx < leftBoundary || vehicleCx > rightBoundary) continue;

                if (vehicleCx < centerBoundaryLeft) _countLeft++;
                else if (vehicleCx > centerBoundaryRight) _countRight++;
                else _countFront++;
            }

            CountText = $"L:{_countLeft} | F:{_countFront} | R:{_countRight}";
        }

        // 트래킹 로직
        private void TrackAndMatch(List<Detection> currentDetections, double timeMsec)
        {
            var unmatchedDetections = new List<Detection>(currentDetections);
            var tracksToRemove = new List<int>();

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
                    if (track.ShouldBeDeleted)
                    {
                        tracksToRemove.Add(track.Id);
                    }
                }
            }

            foreach (int id in tracksToRemove)
            {
                _trackedObjects.Remove(id);
            }

            foreach (var det in unmatchedDetections)
            {
                if (det.ClassId == 2 || det.ClassId == 5 || det.ClassId == 7)
                {
                    var newTrack = new TrackedObject(det, timeMsec);
                    _trackedObjects.Add(newTrack.Id, newTrack);
                }
            }

            TrackedCountText = $"Tracks: {_trackedObjects.Count}";
        }

        public void Dispose()
        {
            Stop();
            _frame.Dispose();
            _detector.Dispose();
            _video.Dispose();
            _httpClient.Dispose(); // HttpClient Dispose
        }
    }
}