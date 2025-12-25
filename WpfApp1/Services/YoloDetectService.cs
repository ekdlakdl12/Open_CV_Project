using OpenCvSharp;

namespace WpfApp1.Scripts
{
    public class YoloDetectService
    {
        public YoloDetectService(string modelPath, int size, float conf, float iou) { }
        // ... 나머지 로직
        public IEnumerable<WpfApp1.Models.Detection> Detect(Mat frame)
        {
            return new List<WpfApp1.Models.Detection>();
        }
    }
}