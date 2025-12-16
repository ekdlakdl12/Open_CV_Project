using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using System.Drawing;
using System.IO;
using System.Text;
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

        private void ProcessImageAndRecognize(string imagePath)
        {
            // [오류 1 해결] Emgu.CV 및 Emgu.CV.Structure using 문 확인
            string tessDataPath = "./Scripts";

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

                    Image<Gray, Byte> imgGray = imgOriginal.Convert<Gray, Byte>();
                    Image<Gray, Byte> imgBlurred = imgGray.SmoothGaussian(3);
                    Image<Gray, Byte> imgThresh = new Image<Gray, Byte>(imgGray.Width, imgGray.Height);

                    CvInvoke.AdaptiveThreshold(imgBlurred, imgThresh, 255,
                                               AdaptiveThresholdType.GaussianC,
                                               ThresholdType.BinaryInv, 11, 2);

                    ProcessedImageControl.Source = ToBitmapSource(imgThresh.ToBitmap());

                    // Tesseract 엔진 초기화
                    using (var engine = new TesseractEngine(tessDataPath, "kor+eng", EngineMode.Default))
                    {
                        using (var bitmap = imgThresh.ToBitmap())
                        {
                            // [오류 2 해결] Tesseract.Drawing 패키지 사용으로 Bitmap 처리 가능
                            using (var page = engine.Process(bitmap, PageSegMode.SingleWord))
                            {
                                var recognizedText = page.GetText().Trim();
                                ResultTextBox.Text = $"인식된 텍스트: {recognizedText}\n평균 신뢰도: {page.GetMeanConfidence():P}";
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ResultTextBox.Text = $"An error occurred: {ex.Message}";
            }
        }

        // System.Drawing.Bitmap을 WPF ImageSource(BitmapSource)로 변환하는 헬퍼 메서드
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);

        public static BitmapSource ToBitmapSource(Bitmap bitmap)
        {
            IntPtr hBitmap = bitmap.GetHbitmap();
            try
            {
                // [오류 3 해결] ThreadCulture 대신 CurrentCulture를 사용하거나, 
                // 간단히 CultureInfo.InvariantCulture를 사용하여 안정적인 변환 수행
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
    }
}