using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using WpfApp1.ViewModels;
using System.Text.Json;

namespace WpfApp1.Views
{
    public partial class MainWindow : Window
    {
        private readonly MainWindowViewModel ViewModel;

        public MainWindow()
        {
            InitializeComponent();

            ViewModel = new MainWindowViewModel();
            this.DataContext = ViewModel;

            // ViewModel의 Drawing Command 이벤트 구독 (ViewModel <-> View 통신)
            ViewModel.DrawingCommandReceived += OnDrawingCommandReceived;
        }

        private void SelectVideoButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "Video files (*.mp4;*.avi;*.mov)|*.mp4;*.avi;*.mov|All files (*.*)|*.*";

            if (openFileDialog.ShowDialog() == true)
            {
                if (ViewModel.SelectVideoCommand.CanExecute(openFileDialog.FileName))
                {
                    ViewModel.SelectVideoCommand.Execute(openFileDialog.FileName);

                    // 파일 선택 후 MediaElement 소스 업데이트 및 재생
                    VideoPlayer.Source = new Uri(openFileDialog.FileName);
                    VideoPlayer.Play();
                }
            }
        }

        private void VideoPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            VideoPlayer.Play();
            VideoPlayer.IsMuted = true;
        }

        // [핵심 로직] ViewModel로부터 Drawing 명령을 받아 Canvas에 그리는 메서드
        private void OnDrawingCommandReceived(string frameJson)
        {
            // Canvas 객체 초기화 (이전 박스 제거)
            Application.Current.Dispatcher.Invoke(() =>
            {
                BoundingBoxCanvas.Children.Clear();
            });

            try
            {
                // UI 스레드에서 JSON 파싱 및 그리기 실행
                Application.Current.Dispatcher.Invoke(() =>
                {
                    using (JsonDocument document = JsonDocument.Parse(frameJson))
                    {
                        var root = document.RootElement;

                        if (root.TryGetProperty("type", out var typeElement) && typeElement.GetString() == "frame_data")
                        {
                            if (!root.TryGetProperty("boxes", out var boxesElement) || boxesElement.ValueKind != JsonValueKind.Array)
                            {
                                return;
                            }

                            // 캔버스 및 비디오 해상도 정보
                            double canvasWidth = BoundingBoxCanvas.ActualWidth;
                            double canvasHeight = BoundingBoxCanvas.ActualHeight;
                            double videoWidth = VideoPlayer.NaturalVideoWidth;
                            double videoHeight = VideoPlayer.NaturalVideoHeight;

                            if (videoWidth <= 0 || videoHeight <= 0 || canvasWidth <= 0 || canvasHeight <= 0) return;

                            // 좌표 변환 비율 계산 (Uniform stretch에 따른 레터박스 고려)
                            double videoRatio = videoWidth / videoHeight;
                            double canvasRatio = canvasWidth / canvasHeight;

                            double drawWidth, drawHeight, offsetX, offsetY;

                            if (videoRatio > canvasRatio) // 비디오가 캔버스보다 가로가 긴 경우 (위아래 레터박스)
                            {
                                drawWidth = canvasWidth;
                                drawHeight = canvasWidth / videoRatio;
                                offsetX = 0;
                                offsetY = (canvasHeight - drawHeight) / 2;
                            }
                            else // 비디오가 캔버스보다 세로가 긴 경우 (좌우 레터박스)
                            {
                                drawHeight = canvasHeight;
                                drawWidth = canvasHeight * videoRatio;
                                offsetX = (canvasWidth - drawWidth) / 2;
                                offsetY = 0;
                            }

                            double scale = drawWidth / videoWidth; // 실제 그리기 비율

                            // 바운딩 박스 그리기
                            foreach (var boxElement in boxesElement.EnumerateArray())
                            {
                                if (!boxElement.TryGetProperty("x_min", out var xMin) ||
                                    !boxElement.TryGetProperty("y_min", out var yMin) ||
                                    !boxElement.TryGetProperty("x_max", out var xMax) ||
                                    !boxElement.TryGetProperty("y_max", out var yMax))
                                {
                                    continue;
                                }

                                string className = boxElement.GetProperty("class").GetString();
                                int trackId = boxElement.GetProperty("id").GetInt32();

                                // Canvas에 맞게 좌표 변환 및 오프셋 적용
                                double left = xMin.GetInt32() * scale + offsetX;
                                double top = yMin.GetInt32() * scale + offsetY;
                                double width = (xMax.GetInt32() - xMin.GetInt32()) * scale;
                                double height = (yMax.GetInt32() - yMin.GetInt32()) * scale;

                                // 사각형
                                Rectangle rect = new Rectangle
                                {
                                    Stroke = Brushes.LimeGreen,
                                    StrokeThickness = 3,
                                    Width = width,
                                    Height = height
                                };
                                Canvas.SetLeft(rect, left);
                                Canvas.SetTop(rect, top);
                                BoundingBoxCanvas.Children.Add(rect);

                                // 레이블
                                TextBlock label = new TextBlock
                                {
                                    Text = $"{className} (ID:{trackId})",
                                    Foreground = Brushes.White,
                                    Background = Brushes.LimeGreen,
                                    FontWeight = FontWeights.Bold,
                                    Padding = new Thickness(4, 1, 4, 1)
                                };
                                Canvas.SetLeft(label, left);
                                Canvas.SetTop(label, top - 20);
                                BoundingBoxCanvas.Children.Add(label);
                            }
                        }
                    }
                }); // Dispatcher.Invoke 끝
            }
            catch (JsonException)
            {
                // 유효하지 않은 JSON이 들어올 경우 (Python에서 print가 섞일 경우) 무시
            }
            catch (Exception ex)
            {
                ViewModel.StatusMessage = $"[치명적인 그리기 오류] {ex.Message}";
                Application.Current.Dispatcher.Invoke(() => BoundingBoxCanvas.Children.Clear());
            }
        }
    }
}