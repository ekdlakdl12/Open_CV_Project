using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using WpfApp1.Models;

namespace WpfApp1.ViewModels
{
    public class MainWindowViewModel : ObservableObject
    {
        // --- Private Fields ---
        private CancellationTokenSource _cancellationTokenSource;
        private string _selectedVideoPath;
        private string _statusMessage;
        private bool _isProcessing;

        // --- Collections ---
        private ObservableCollection<DetectedObject> _detectionResults;
        private ObservableCollection<DetectedObject> _topThreeResults;

        // --- Public Properties (Data Binding Target) ---
        public string SelectedVideoPath
        {
            get => _selectedVideoPath;
            set => SetProperty(ref _selectedVideoPath, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public bool IsProcessing
        {
            get => _isProcessing;
            set => SetProperty(ref _isProcessing, value);
        }

        public ObservableCollection<DetectedObject> DetectionResults
        {
            get => _detectionResults;
            set => SetProperty(ref _detectionResults, value);
        }

        public ObservableCollection<DetectedObject> TopThreeResults
        {
            get => _topThreeResults;
            set => SetProperty(ref _topThreeResults, value);
        }

        // --- Commands (Action Binding Target) ---
        public ICommand SelectVideoCommand { get; private set; }
        public ICommand ProcessVideoCommand { get; private set; }
        public ICommand CancelProcessCommand { get; private set; }

        // --- Constructor ---
        public MainWindowViewModel()
        {
            DetectionResults = new ObservableCollection<DetectedObject>();
            TopThreeResults = new ObservableCollection<DetectedObject>();

            // [테스트 경로] 실제 테스트 영상 파일 경로로 수정하세요.
            string testPath = @"C:\Users\Public\Videos\Sample\test_highway.mp4";
            if (File.Exists(testPath))
            {
                _selectedVideoPath = testPath;
                StatusMessage = $"테스트 영상이 로드되었습니다: {Path.GetFileName(testPath)}";
            }
            else
            {
                StatusMessage = "영상을 선택해 주세요. (테스트 경로 설정 필요)";
            }

            // 명령어 초기화
            SelectVideoCommand = new RelayCommand(ExecuteSelectVideo);
            ProcessVideoCommand = new RelayCommand(ExecuteProcessVideo, CanExecuteProcessVideo);
            CancelProcessCommand = new RelayCommand(ExecuteCancelProcess, CanExecuteCancelProcess);
        }

        // ----------------------------------------------------
        // Command Execution Methods
        // ----------------------------------------------------

        private void ExecuteSelectVideo(object parameter)
        {
            if (parameter is string path && !string.IsNullOrWhiteSpace(path))
            {
                SelectedVideoPath = path;
                StatusMessage = $"선택된 파일: {Path.GetFileName(path)}";
            }
        }

        private bool CanExecuteProcessVideo(object parameter)
        {
            return !string.IsNullOrWhiteSpace(SelectedVideoPath) && !IsProcessing;
        }

        private bool CanExecuteCancelProcess(object parameter)
        {
            return IsProcessing;
        }

        private void ExecuteCancelProcess(object parameter)
        {
            if (_cancellationTokenSource != null)
            {
                _cancellationTokenSource.Cancel();
                StatusMessage = "분석 취소 요청 중...";
            }
        }

        // [핵심] Python 프로세스 실행 및 취소 로직 (FileName에 Anaconda 경로 적용)
        private async void ExecuteProcessVideo(object parameter)
        {
            _cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _cancellationTokenSource.Token;

            IsProcessing = true;
            DetectionResults.Clear();
            StatusMessage = "Python (YOLOv8)을 이용한 영상 분석을 시작합니다...";

            try
            {
                string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                string pythonScriptPath = Path.Combine(baseDirectory, "Scripts", "yolo_detector.py");

                if (!File.Exists(pythonScriptPath))
                {
                    StatusMessage = $"오류: Python 스크립트 '{Path.GetFileName(pythonScriptPath)}'를 Scripts 폴더에서 찾을 수 없습니다.";
                    return;
                }

                // ProcessStartInfo 설정
                ProcessStartInfo start = new ProcessStartInfo
                {
                    // ----------------------------------------------------------------------------------------------------
                    // [최종 수정된 부분] 사용자님이 찾으신 Anaconda 경로 적용
                    // ----------------------------------------------------------------------------------------------------
                    FileName = @"C:\Users\Jun_0\anaconda3\python.exe",

                    Arguments = $"\"{pythonScriptPath}\" \"{SelectedVideoPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                // 

                string resultJson = null;
                string errors = null;
                Process process = null;

                await Task.Run(() =>
                {
                    process = Process.Start(start);

                    // 취소 요청 감지를 위한 Task
                    Task cancellationTask = Task.Run(() =>
                    {
                        try { cancellationToken.ThrowIfCancellationRequested(); }
                        catch (OperationCanceledException)
                        {
                            if (process != null && !process.HasExited) { process.Kill(); return; }
                        }
                    }, cancellationToken);

                    // 프로세스 종료 대기 (10분 타임아웃)
                    if (process.WaitForExit(600000))
                    {
                        resultJson = process.StandardOutput.ReadToEnd();
                        errors = process.StandardError.ReadToEnd();
                    }
                    else if (process != null && !process.HasExited)
                    {
                        process.Kill();
                        errors = "분석 시간이 10분을 초과하여 강제 종료되었습니다.";
                    }

                }, cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                {
                    StatusMessage = "⛔ 영상 분석이 사용자 요청으로 취소되었습니다.";
                    return;
                }

                if (!string.IsNullOrWhiteSpace(errors))
                {
                    StatusMessage = $"[Python 환경/실행 오류] {errors}";
                    return;
                }

                ParseYoloResult(resultJson);

            }
            catch (OperationCanceledException)
            {
                StatusMessage = "⛔ 영상 분석이 사용자 요청으로 취소되었습니다.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"[C# 시스템 오류] 프로세스 실행 실패: {ex.Message}";
            }
            finally
            {
                IsProcessing = false;
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
        }

        // Python에서 받은 JSON 결과를 파싱하여 ObservableCollection에 추가
        private void ParseYoloResult(string resultJson)
        {
            if (string.IsNullOrWhiteSpace(resultJson))
            {
                StatusMessage = "Python으로부터 결과를 받지 못했습니다. (빈 출력)";
                return;
            }

            try
            {
                using (JsonDocument document = JsonDocument.Parse(resultJson))
                {
                    var root = document.RootElement;
                    string status = root.GetProperty("status").GetString();

                    if (status == "success")
                    {
                        var detections = root.GetProperty("detections");
                        int totalFrames = root.GetProperty("total_frames").GetInt32();

                        foreach (var prop in detections.EnumerateObject())
                        {
                            DetectionResults.Add(new DetectedObject
                            {
                                ObjectType = prop.Name,
                                Count = prop.Value.GetInt32()
                            });
                        }

                        StatusMessage = $"영상 분석 완료. 총 {totalFrames} 프레임 분석됨.";
                        UpdateTopThreeResults();
                    }
                    else
                    {
                        string message = root.GetProperty("message").GetString();
                        StatusMessage = $"분석 실패 (Python 스크립트 오류): {message}";
                    }
                }
            }
            catch (JsonException ex)
            {
                StatusMessage = $"JSON 파싱 오류: 받은 데이터가 유효하지 않습니다. ({ex.Message})";
            }
            catch (Exception ex)
            {
                StatusMessage = $"결과 처리 중 알 수 없는 오류 발생: {ex.Message}";
            }
        }

        private void UpdateTopThreeResults()
        {
            TopThreeResults.Clear();

            var top3 = DetectionResults.OrderByDescending(d => d.Count)
                               .Take(3);

            foreach (var obj in top3)
            {
                TopThreeResults.Add(obj);
            }
        }
    }
}