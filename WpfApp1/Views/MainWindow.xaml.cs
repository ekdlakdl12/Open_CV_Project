using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using OpenCvSharp.WpfExtensions;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Tesseract;
using System.Text.RegularExpressions;
using Point = OpenCvSharp.Point;
using Rect = OpenCvSharp.Rect;
using Size = OpenCvSharp.Size;
using Window = System.Windows.Window;

namespace WpfApp11
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        Mat src = new Mat();
        public MainWindow()
        {
            InitializeComponent();
        }
        private void BtnLoad_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFile = new OpenFileDialog { Filter = "이미지|*.jpg;*.jpeg;*.png;*.bmp" };
            if (openFile.ShowDialog() == true)
            {
                if (src != null && !src.IsDisposed) src.Dispose();
                src = Cv2.ImRead(openFile.FileName);
                imgOriginal.Source = src.ToBitmapSource();
                ApplyFilter();
            }
        }

        private void ApplyFilter()
        {
            if (src == null || src.Empty()) return;

            double scale = 8.0;
            int blurSize = 9;
            int closeSize = 11;
            double defaultThresh = 115.0;

            using (Mat scaled = new Mat())
            using (Mat gray = new Mat())
            {
                Cv2.Resize(src, scaled, new OpenCvSharp.Size(), scale, scale, InterpolationFlags.Cubic);
                Cv2.CvtColor(scaled, gray, ColorConversionCodes.BGR2GRAY);
                Cv2.GaussianBlur(gray, gray, new OpenCvSharp.Size(blurSize, blurSize), 0);

                Point[] bestRect = FindOptimalRectangle(gray, defaultThresh, closeSize);

                using (Mat resultDisplay = scaled.Clone())
                {
                    if (bestRect != null)
                    {
                        Rect r = Cv2.BoundingRect(bestRect);
                        r = FixRectRange(r, resultDisplay.Width, resultDisplay.Height);

                        using (Mat roi = new Mat(scaled, r))
                        {
                            string finalResult = "미검출";
                            Mat? bestProcessedMat = null;

                            // 1. 고정값 124 시도
                            finalResult = ProcessAndValidate(roi, 124, out bestProcessedMat, false);

                            // 2. 한글/숫자 조합 미충족 시 자동 이진화(Otsu) 및 임계값 가변 재시도 (최대 5회)
                            if (finalResult == "미검출")
                            {
                                double[] retryThresholds = { 0, 110, 130, 100, 140 }; // 0은 Otsu 자동
                                foreach (double th in retryThresholds)
                                {
                                    if (bestProcessedMat != null && !bestProcessedMat.IsDisposed) bestProcessedMat.Dispose();
                                    finalResult = ProcessAndValidate(roi, th, out bestProcessedMat, th == 0);

                                    if (finalResult != "미검출") break; // 조합 성공 시 중단
                                }
                            }

                            // 최종 결과 시각화
                            if (bestProcessedMat != null)
                            {
                                using (Mat processedRoiColor = new Mat())
                                {
                                    Cv2.CvtColor(bestProcessedMat, processedRoiColor, ColorConversionCodes.GRAY2BGR);
                                    processedRoiColor.CopyTo(new Mat(resultDisplay, r));
                                }
                                bestProcessedMat.Dispose();
                            }

                            Cv2.DrawContours(resultDisplay, new[] { bestRect }, -1, Scalar.Lime, 6);
                            Cv2.PutText(resultDisplay, finalResult, new Point(r.X, r.Y - 20),
                                       HersheyFonts.HersheySimplex, 1.2, Scalar.Yellow, 3);

                            ResultTextBox.Text = finalResult;
                        }
                    }
                    imgResult.Source = resultDisplay.ToBitmapSource();
                }
            }
        }

        // 전처리 및 "한글+숫자 동시 검출" 검증 함수
        private string ProcessAndValidate(Mat roi, double thresh, out Mat resultMat, bool useOtsu)
        {
            Mat gray = new Mat();
            Mat blurred = new Mat();
            Mat binary = new Mat();
            Mat closed = new Mat();

            Cv2.CvtColor(roi, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.GaussianBlur(gray, blurred, new OpenCvSharp.Size(0, 0), 1);

            ThresholdTypes tType = useOtsu ? (ThresholdTypes.Binary | ThresholdTypes.Otsu) : ThresholdTypes.Binary;
            Cv2.Threshold(blurred, binary, thresh, 255, tType);

            var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(6, 6));
            Cv2.MorphologyEx(binary, closed, MorphTypes.Close, kernel);

            resultMat = closed; // 화면 표시용으로 반환

            gray.Dispose(); blurred.Dispose(); binary.Dispose();

            // OCR 실행
            string rawText = ReadOcrRaw(closed);

            // [핵심] 한글과 숫자가 모두 포함되어 있는지 정규식으로 검사
            bool hasKorean = System.Text.RegularExpressions.Regex.IsMatch(rawText, "[가-힣]");
            bool hasNumber = System.Text.RegularExpressions.Regex.IsMatch(rawText, "[0-9]");

            if (hasKorean && hasNumber)
            {
                // 둘 다 있다면 한글과 숫자만 남기고 반환
                return System.Text.RegularExpressions.Regex.Replace(rawText, "[^가-힣0-9]", "");
            }

            return "미검출";
        }

        private string ReadOcrRaw(Mat processedRoi)
        {
            string tessDataPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata");
            using (Mat enlarged = new Mat())
            {
                Cv2.Resize(processedRoi, enlarged, new OpenCvSharp.Size(), 1.5, 1.5, InterpolationFlags.Cubic);
                using (var engine = new TesseractEngine(tessDataPath, "kor+eng", EngineMode.Default))
                {
                    engine.SetVariable("tessedit_pageseg_mode", "6");
                    using (var bitmap = OpenCvSharp.Extensions.BitmapConverter.ToBitmap(enlarged))
                    using (var page = engine.Process(bitmap))
                    {
                        return page.GetText()?.Trim() ?? "";
                    }
                }
            }
        }


        // 이진화 방식에 따라 전처리 및 OCR을 수행하는 공통 함수
        private string ProcessOcrWithThreshold(Mat roi, double thresh, out Mat resultMat, bool useOtsu)
        {
            Mat gray = new Mat();
            Mat blurred = new Mat();
            Mat binary = new Mat();
            Mat closed = new Mat();

            // 1. 그레이스케일
            Cv2.CvtColor(roi, gray, ColorConversionCodes.BGR2GRAY);

            // 2. 가우스 1
            Cv2.GaussianBlur(gray, blurred, new OpenCvSharp.Size(0, 0), 1);

            // 3. 이진화 (useOtsu가 true이면 Thresh 값은 무시되고 자동 계산됨)
            ThresholdTypes tType = useOtsu ? (ThresholdTypes.Binary | ThresholdTypes.Otsu) : ThresholdTypes.Binary;
            Cv2.Threshold(blurred, binary, thresh, 255, tType);

            // 4. 닫힘 6
            var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(6, 6));
            Cv2.MorphologyEx(binary, closed, MorphTypes.Close, kernel);

            resultMat = closed; // 결과 Mat 반환 (화면 출력용)

            // 자원 해제
            gray.Dispose();
            blurred.Dispose();
            binary.Dispose();

            // 5. OCR 실행
            return ReadTextFromProcessedMat(closed);
        }

        private string ReadTextFromProcessedMat(Mat processedRoi)
        {
            string tessDataPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata");
            using (Mat enlarged = new Mat())
            {
                Cv2.Resize(processedRoi, enlarged, new OpenCvSharp.Size(), 1.5, 1.5, InterpolationFlags.Cubic);
                try
                {
                    using (var engine = new TesseractEngine(tessDataPath, "kor+eng", EngineMode.Default))
                    {
                        engine.SetVariable("tessedit_pageseg_mode", "6");
                        using (var bitmap = OpenCvSharp.Extensions.BitmapConverter.ToBitmap(enlarged))
                        using (var page = engine.Process(bitmap))
                        {
                            string originalText = page.GetText()?.Trim() ?? "";

                            // 정규식 적용: 한글(가-힣)과 숫자(0-9)만 남기고 모두 제거
                            string filteredText = System.Text.RegularExpressions.Regex.Replace(originalText, "[^가-힣0-9]", "");

                            // 결과가 비어있지 않으면 필터링된 텍스트 반환
                            if (!string.IsNullOrEmpty(filteredText))
                            {
                                return filteredText;
                            }

                            return "미검출";
                        }
                    }
                }
                catch { return "OCR 에러"; }
            }
        }


        // 사각형 검출 (자동 임계값 및 비율 필터링 포함)
        private Point[] FindOptimalRectangle(Mat gray, double startThresh, int closeSize)
        {
            int[] offsets = { 0, 15, -15, 30, -30, 45, -45 };
            foreach (int offset in offsets)
            {
                double currentThresh = Math.Clamp(startThresh + offset, 10, 245);
                using (Mat thresh = new Mat())
                using (Mat morph = new Mat())
                {
                    Cv2.Threshold(gray, thresh, currentThresh, 255, ThresholdTypes.Binary);
                    Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(closeSize, closeSize));
                    Cv2.MorphologyEx(thresh, morph, MorphTypes.Close, kernel);
                    Cv2.FindContours(morph, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

                    foreach (var contour in contours)
                    {
                        var approx = Cv2.ApproxPolyDP(contour, Cv2.ArcLength(contour, true) * 0.02, true);
                        if (approx.Length == 4 && Math.Abs(Cv2.ContourArea(approx)) > 10000)
                        {
                            Rect r = Cv2.BoundingRect(approx);
                            double ratio = (double)r.Width / r.Height;
                            // 가로가 세로의 2~3배 또는 4~6배인 경우
                            if ((ratio >= 2.0 && ratio <= 3.0) || (ratio >= 4.0 && ratio <= 6.0))
                                return approx;
                        }
                    }
                }
            }
            return null;
        }

        // ROI 영역이 이미지 밖으로 나가는 것 방지
        private Rect FixRectRange(Rect r, int maxW, int maxH)
        {
            int x = Math.Max(0, r.X);
            int y = Math.Max(0, r.Y);
            int w = Math.Min(r.Width, maxW - x);
            int h = Math.Min(r.Height, maxH - y);
            return new Rect(x, y, w, h);
        }
    }
}