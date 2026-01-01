using System;
using OpenCvSharp;
using WpfApp1.Script;

namespace WpfApp1.Services
{
    public sealed class YolopDetectService : IDisposable
    {
        private readonly YolopOnnx _yolop;

        public YolopDetectService(string onnxPath, int imgSize = 640, float conf = 0.35f, float nms = 0.45f)
        {
            _yolop = new YolopOnnx(onnxPath, imgSize, conf, nms);
        }

        public YolopOnnx.YolopResult Infer(Mat frame) => _yolop.Infer(frame);

        public void Dispose() => _yolop.Dispose();
    }
}
