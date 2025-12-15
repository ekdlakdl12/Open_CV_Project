using OpenCvSharp;
using WpfApp1.Models;
using System;
using System.Collections.Generic;

namespace WpfApp1.Services
{
    public class YoloDetectService : IDisposable
    {
        private readonly YoloV8Onnx _yolo;

        public YoloDetectService(string onnxPath, int imgSize = 640, float conf = 0.5f, float nms = 0.45f)
        {
            _yolo = new YoloV8Onnx(onnxPath, imgSize, conf, nms);
        }

        public List<Detection> Detect(Mat frame) => _yolo.Detect(frame);

        public void Dispose() => _yolo.Dispose();
    }
}
