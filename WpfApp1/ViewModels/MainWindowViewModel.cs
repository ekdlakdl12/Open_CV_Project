using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows.Input;
using WpfApp1.Models;
using System.Windows;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WpfApp1.ViewModels
{
    // ObservableObject는 별도의 파일/위치에 존재해야 합니다.
    public class MainWindowViewModel : ObservableObject
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

        // [핵심 추가] View로 바운딩 박스 데이터를 전달할 Action
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

        // --- Commands ---
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
                StatusMessage = "영상을 선택해 주세요.";
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

        // [핵심] Python 프로세스 실행 및 실시간 스트리밍 로직
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

            // 초기화
            DetectionResults.Clear();
            TopThreeResults.Clear();
            StatusMessage = "영상 분석 시작 중...";
            _cancellationTokenSource = new CancellationTokenSource();

            // --- Python 프로세스 시작 설정 ---
            string pythonScriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scripts", "yolo_detector.py");

            ProcessStartInfo start = new ProcessStartInfo
            {
                // [경로 확인 필수] 사용자 설정 경로 반영
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
                _process.BeginErrorReadLine(); // 오류 스트림 비동기 읽기 시작 (필수)
                IsProcessing = true;
                StatusMessage = "Python 분석 프로세스가 시작되었습니다...";
            }
            catch (Exception ex)
            {
                StatusMessage = $"분석 시작 오류: Python 실행 파일 경로 또는 권한을 확인하세요. ({ex.Message})";
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
                    StatusMessage = $"프레임 데이터 처리 중 오류 발생: {ex.Message}";
                }
            });
        }

        // Python Process Output Handler (Standard Output: JSON 데이터)
        private void Process_OutputDataReceived(object sender, DataReceivedEventArgs e, CancellationToken token)
        {
            if (e.Data != null && !token.IsCancellationRequested)
            {
                ProcessFrameData(e.Data);
            }
        }

        // [수정된 핵심 로직] Python Process Error Handler (Standard Error: Traceback, 경고 등)
        private void Process_ErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data != null)
            {
                // UI 스레드에서 오류 메시지 처리
                Application.Current.Dispatcher.Invoke(() =>
                {
                    // Python의 경고나 로그는 stderr로 자주 출력되므로,
                    // 무조건 프로세스를 종료하지 않고, 상태바에 표시만 합니다.
                    // 실제 종료는 Process_Exited에서 ExitCode를 보고 판단합니다.

                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        // Traceback이 발생하면 상태 메시지에 누적 출력
                        StatusMessage = $"[Python 로그/경고] {e.Data}";

                        // 디버깅 목적으로 Debug 창에 상세 로그 출력
                        Debug.WriteLine($"Python STDERR: {e.Data}");
                    }
                });
            }
        }

        // [수정된 핵심 로직] Python Process Exited Handler
        private void Process_Exited(object sender, EventArgs e)
        {
            // UI 스레드에서 상태 업데이트
            Application.Current.Dispatcher.Invoke(() =>
            {
                // 프로세스가 실행 중이었다면 (IsProcessing이 true였다면)
                if (IsProcessing)
                {
                    // 비정상 종료 (0이 아니면 오류로 종료된 것)
                    if (_process != null && _process.ExitCode != 0)
                    {
                        StatusMessage = $"🚨 영상 분석이 예기치 않게 종료되었습니다. (Exit Code: {_process.ExitCode}). C:\\temp\\yolo_debug.log 파일을 확인하세요.";
                    }
                    else if (_process != null && _process.ExitCode == 0)
                    {
                        // 정상 종료되었지만 Summary JSON을 놓쳤을 경우
                        if (!StatusMessage.Contains("분석 완료"))
                        {
                            StatusMessage = "분석 완료되었으나 최종 결과 JSON을 받지 못했습니다. (Exit Code 0)";
                        }
                    }
                }
                IsProcessing = false;
            });
        }

        // 최종 결과 요약 통계 처리
        private void ParseYoloResult(string resultJson)
        {
            // ... (기존 ParseYoloResult 로직 유지)
            // 이 메서드는 정상 종료 시 StatusMessage를 "분석 완료"로 업데이트하고 IsProcessing을 false로 설정합니다.
            if (string.IsNullOrWhiteSpace(resultJson)) return;

            try
            {
                using (JsonDocument document = JsonDocument.Parse(resultJson))
                {
                    var root = document.RootElement;
                    string status = root.GetProperty("status").GetString();

                    if (status == "success")
                    {
                        if (root.TryGetProperty("detections", out var detections))
                        {
                            // ... (DetectionResults 및 TopThreeResults 업데이트 로직)
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
                IsProcessing = false; // 최종적으로 IsProcessing을 false로 설정
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