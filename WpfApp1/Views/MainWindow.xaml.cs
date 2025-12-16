using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Emgu.CV.Util;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Tesseract;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;
using Size = System.Drawing.Size;



namespace WpfApp11
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }
        private void RecognizeButton_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog();
            openFileDialog.Filter = "Image files (*.png;*.jpeg;*.jpg)|*.png;*.jpeg;*.jpg|All files (*.*)|*.*";
            if (openFileDialog.ShowDialog() == true)
            {
                string imagePath = openFileDialog.FileName;
                ProcessImageAndRecognize(imagePath);
            }
        }
        // ... (DeleteObject 및 ToBitmapSource 헬퍼 메서드는 그대로 둡니다.) ...
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);

        public static BitmapSource ToBitmapSource(Bitmap bitmap)
        {
            IntPtr hBitmap = bitmap.GetHbitmap();
            try
            {
                return Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }


        private void ProcessImageAndRecognize(string imagePath)
        {
            string tessDataPath = "./tessdata";
            if (!Directory.Exists(tessDataPath))
            {
                ResultTextBox.Text = $"Error: tessdata directory not found at {tessDataPath}";
                return;
            }

            try
            {
                using (Image<Bgr, Byte> imgOriginal = new Image<Bgr, Byte>(imagePath))
                {
                    OriginalImageControl.Source = ToBitmapSource(imgOriginal.ToBitmap());
                    ResultTextBox.Text = "번호판 영역 검출 중 및 시각화...\n";

                    Image<Gray, Byte> imgGray = imgOriginal.Convert<Gray, Byte>();

                    // 이미지 노이즈 제거 및 선명도 향상을 위한 추가 전처리
                    imgGray = imgGray.SmoothGaussian(3); // 가우시안 블러

                    // 엣지 검출 (Canny 사용)
                    Image<Gray, Byte> imgCanny = new Image<Gray, Byte>(imgGray.Width, imgGray.Height);
                    CvInvoke.Canny(imgGray, imgCanny, 50, 150); // 임계값 조정 가능

                    VectorOfVectorOfPoint contours = new VectorOfVectorOfPoint();
                    Mat hierarchy = new Mat();
                    CvInvoke.FindContours(imgCanny, contours, hierarchy, RetrType.List, ChainApproxMethod.ChainApproxSimple);

                    // 잠재적 번호판 영역을 저장할 리스트
                    List<Rectangle> potentialPlates = new List<Rectangle>();

                    for (int i = 0; i < contours.Size; i++)
                    {
                        Rectangle rect = CvInvoke.BoundingRectangle(contours[i]);
                        double area = rect.Width * rect.Height;
                        double aspectRatio = (double)rect.Width / rect.Height;

                        // 필터링 조건: 면적 최소값 감소, 비율 범위 확대
                        if (area > 1200 && aspectRatio > 1.2 && aspectRatio < 6.0) //
                        {
                            potentialPlates.Add(rect);
                        }
                    }

                    // 디버깅을 위해 잠재적 영역 시각화
                    Image<Bgr, Byte> imgWithRects = imgOriginal.Copy();
                    foreach (var rect in potentialPlates)
                    {
                        CvInvoke.Rectangle(imgWithRects, rect, new Bgr(System.Drawing.Color.Red).MCvScalar, 2);
                    }
                    ProcessedImageControl.Source = ToBitmapSource(imgWithRects.ToBitmap());


                    // 최적의 영역 선택 및 OCR 수행
                    // 이 예시에서는 찾은 첫 번째 영역을 사용하거나, 추가 필터링 로직이 필요
                    if (potentialPlates.Any())
                    {
                        // 실제 OCR은 가장 큰 영역이나 사용자가 원하는 특정 영역에 대해 수행
                        Rectangle bestRect = potentialPlates.OrderByDescending(r => r.Width * r.Height).First();

                        imgOriginal.ROI = bestRect;
                        Image<Bgr, Byte> imgCropped = imgOriginal.Copy();
                        imgOriginal.ROI = Rectangle.Empty;

                        Image<Gray, Byte> imgLPThresh = new Image<Gray, Byte>(imgCropped.Width, imgCropped.Height);
                        CvInvoke.AdaptiveThreshold(imgCropped.Convert<Gray, Byte>(), imgLPThresh, 255,
                                                   AdaptiveThresholdType.GaussianC,
                                                   ThresholdType.BinaryInv, 11, 2);

                        Size kernelSize = new Size(2, 2);

                        // Emgu.CV 4.x 이상에서 ElementShape 네임스페이스나 Rectangle 상수 참조 오류 시:
                        // CvEnum.ElementShape.Rectangle 대신 정수값 0 사용 (0 = Rectangle shape)
                        Mat kernel = CvInvoke.GetStructuringElement(0, kernelSize, new Point(-1, -1));

                        // 닫힘 연산(Closing Operation): 팽창 후 침식
                        CvInvoke.MorphologyEx(imgLPThresh, imgLPThresh, MorphOp.Close, kernel, new Point(-1, -1), 1, BorderType.Default, new MCvScalar(0));
                        // ----------------------------------------

                        // (디버깅용) 닫힘 연산이 적용된 이미지 시각화
                        ProcessedImageControl.Source = ToBitmapSource(imgLPThresh.ToBitmap());

                        // Tesseract OCR 수행 (PageSegMode.SingleBlock 사용)
                        using (var engine = new TesseractEngine(tessDataPath, "kor+eng", EngineMode.Default))
                        using (var bitmap = imgLPThresh.ToBitmap())
                        using (var page = engine.Process(bitmap, PageSegMode.SingleBlock)) //
                        {
                            var recognizedText = page.GetText().Trim();
                            var confidence = page.GetMeanConfidence();

                            // **[숫자/한글 인식 확인 로직 추가]**
                            // 정규식을 사용하여 텍스트에 숫자 [0-9] 또는 한글 [가-힣]이 포함되어 있는지 확인
                            string pattern = @"[0-9가-힣]";
                            if (Regex.IsMatch(recognizedText, pattern))
                            {
                                // 유효한 문자가 인식되면 결과 출력
                                ResultTextBox.Text = $"인식된 텍스트: {recognizedText}\n평균 신뢰도: {confidence:P}";
                            }
                            else
                            {
                                // 유효한 문자가 없으면 다른 메시지 출력
                                ResultTextBox.Text = $"유효한 숫자 또는 한글 문자가 인식되지 않았습니다. 평균 신뢰도: {confidence:P}\n(인식 시도 텍스트: {recognizedText})";
                            }
                        }
                    }
                    else
                    {
                        ResultTextBox.Text += "\n번호판 영역을 찾지 못했습니다. 필터링 조건을 다시 확인해 보세요.";
                    }
                }
            }
            catch (Exception ex)
            {
                ResultTextBox.Text = $"An error occurred: {ex.Message}";
            }
        }
    }
}