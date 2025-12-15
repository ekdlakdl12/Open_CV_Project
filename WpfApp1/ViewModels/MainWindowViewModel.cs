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
using System.Windows;
using System.Collections.Generic;
using System.ComponentModel; // INotifyPropertyChanged 구현을 위해 추가
using System.Runtime.CompilerServices; // CallerMemberName 사용을 위해 추가

namespace WpfApp1.ViewModels
{
    // 기존 프로젝트의 ObservableObject를 상속받도록 가정합니다.
    public class MainWindowViewModel : ObservableObject // (ObservableObject는 별도의 파일/위치에 존재해야 합니다.)
    {
        // --- Private Fields ---
        private Process _process;
        private CancellationTokenSource _cancellationTokenSource;
        private string _selectedVideoPath;
        private string _statusMessage;
        private bool _isProcessing;

        // --- Collections ---
        private ObservableCollection<DetectedObject> _detectionResults;
        private ObservableCollection<DetectedObject> _topThreeResults;

        // [핵심 추가] View로 바운딩 박스 데이터를 전달할 Action (ViewModel <-> View 통신)
        public Action<string> DrawingCommandReceived;

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
            _cancellationTokenSource = new CancellationTokenSource();

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

            // 명령어 초기화 (RelayCommand는 별도 파일/위치에 존재해야 합니다.)
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
            if (_process != null && !_process.HasExited)
            {
                // 프로세스 강제 종료
                try
                {
                    _cancellationTokenSource?.Cancel();
                    _process.Kill();
                    _process.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    StatusMessage = $"프로세스 종료 오류: {ex.Message}";
                }

                IsProcessing = false;
                StatusMessage = "분석이 취소되었습니다.";
                _cancellationTokenSource = new CancellationTokenSource(); // CancellationTokenSource 재설정
            }
        }

        // [핵심] Python 프로세스 실행 및 실시간 스트리밍 로직 (UI 텍스트 제거 적용)
        private void ExecuteProcessVideo(object parameter)
        {
            if (string.IsNullOrEmpty(SelectedVideoPath))
            {
                StatusMessage = "영상을 먼저 선택해 주세요.";
                return;
            }

            if (IsProcessing)
            {
                StatusMessage = "이미 영상 분석이 진행 중입니다.";
                return;
            }

            // [핵심 수정] 영상 분석 시작 시 화면 중앙의 안내 메시지 텍스트를 즉시 지웁니다.
            StatusMessage = string.Empty;

            // CancellationTokenSource 초기화
            _cancellationTokenSource = new CancellationTokenSource();

            // --- Python 프로세스 시작 설정 ---
            string pythonScriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scripts", "yolo_detector.py");

            ProcessStartInfo start = new ProcessStartInfo
            {
                // [수정된 경로] 새로운 가상 환경의 python.exe 경로 (사용자 설정 경로 반영)
                FileName = @"C:\Users\Jun_0\anaconda3\envs\yolo_new_env\python.exe",
                Arguments = $"\"{pythonScriptPath}\" \"{SelectedVideoPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            _process = new Process();
            _process.StartInfo = start;
            _process.EnableRaisingEvents = true;

            // 이벤트 핸들러 연결
            _process.OutputDataReceived += (s, e) => Process_OutputDataReceived(s, e, _cancellationTokenSource.Token);
            _process.ErrorDataReceived += Process_ErrorDataReceived;
            _process.Exited += Process_Exited;

            try
            {
                _process.Start();
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
                IsProcessing = true;
            }
            catch (Exception ex)
            {
                StatusMessage = $"분석 시작 오류: {ex.Message}";
                IsProcessing = false;
            }
        }

        // Python에서 받은 프레임별 JSON 데이터를 처리
        private void ProcessFrameData(string frameJson)
        {
            if (string.IsNullOrWhiteSpace(frameJson)) return;

            // UI 스레드에서 업데이트
            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    using (JsonDocument document = JsonDocument.Parse(frameJson))
                    {
                        var root = document.RootElement;

                        if (root.TryGetProperty("type", out var typeElement) && typeElement.GetString() == "frame_data")
                        {
                            // 1. 상태 업데이트
                            if (root.TryGetProperty("frame_id", out var frameIdElement))
                            {
                                int frameId = frameIdElement.GetInt32();
                                StatusMessage = $"스트리밍 중... 프레임 {frameId} 처리 완료.";
                            }

                            // 2. 바운딩 박스 그리기 명령을 View-Codebehind로 전달
                            DrawingCommandReceived?.Invoke(frameJson);
                        }
                        else if (root.TryGetProperty("type", out var summaryType) && summaryType.GetString() == "summary")
                        {
                            // 분석 완료 메시지 및 최종 통계 처리
                            ParseYoloResult(frameJson);
                        }
                    }
                }
                catch (JsonException)
                {
                    // 유효한 JSON이 아닐 경우 (무시)
                }
                catch (Exception ex)
                {
                    StatusMessage = $"프레임 데이터 처리 오류: {ex.Message}";
                }
            });
        }

        // Python Process Output Handler
        private void Process_OutputDataReceived(object sender, DataReceivedEventArgs e, CancellationToken token)
        {
            if (e.Data != null && !token.IsCancellationRequested)
            {
                ProcessFrameData(e.Data);
            }
        }

        // Python Process Error Handler
        private void Process_ErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data != null)
            {
                // UI 스레드에서 오류 메시지 처리
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (e.Data.Contains("Traceback") || e.Data.Contains("Error"))
                    {
                        StatusMessage = $"[Python 오류] {e.Data}";
                        IsProcessing = false;
                        _process?.Kill();
                    }
                });
            }
        }

        // Python Process Exited Handler
        private void Process_Exited(object sender, EventArgs e)
        {
            // UI 스레드에서 상태 업데이트
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (IsProcessing)
                {
                    // 분석이 정상 종료되지 않은 경우
                    StatusMessage = "영상 분석이 예기치 않게 종료되었습니다.";
                }
                IsProcessing = false;
            });
        }

        // 최종 결과 요약 통계 처리
        private void ParseYoloResult(string resultJson)
        {
            if (string.IsNullOrWhiteSpace(resultJson)) return;

            try
            {
                using (JsonDocument document = JsonDocument.Parse(resultJson))
                {
                    var root = document.RootElement;
                    string status = root.GetProperty("status").GetString();

                    if (status == "success")
                    {
                        // 기존 로직: detections 정보가 있다면 업데이트
                        if (root.TryGetProperty("detections", out var detections))
                        {
                            DetectionResults.Clear();
                            foreach (var prop in detections.EnumerateObject())
                            {
                                DetectionResults.Add(new DetectedObject
                                {
                                    ObjectType = prop.Name,
                                    Count = prop.Value.GetInt32()
                                });
                            }
                            UpdateTopThreeResults();
                        }

                        string message = root.GetProperty("message").GetString();
                        StatusMessage = message; // 최종 완료 메시지
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
                StatusMessage = $"최종 JSON 파싱 오류: 받은 데이터가 유효하지 않습니다. ({ex.Message})";
            }
            catch (Exception ex)
            {
                StatusMessage = $"결과 처리 중 알 수 없는 오류 발생: {ex.Message}";
            }
            finally
            {
                IsProcessing = false;
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