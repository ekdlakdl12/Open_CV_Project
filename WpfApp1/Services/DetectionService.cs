using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using WpfApp1.Models;
using WpfApp1.Scripts;

namespace WpfApp1.Services
{
    public class DetectionService : IDisposable
    {
        private readonly YoloDetectService _detector;
        private readonly YolopDetectService _yolop;
        private readonly InferenceSession? _classSession;
        private readonly string[] _carModelNames = CarModelData.Names;
        private readonly object _sessionLock = new object();

        public DetectionService(string baseDir)
        {
            _detector = new YoloDetectService(Path.Combine(baseDir, "Scripts/yolov8n.onnx"), 640, 0.35f, 0.45f);
            _yolop = new YolopDetectService(Path.Combine(baseDir, "Scripts/yolop-640-640.onnx"), 640, 0.35f, 0.45f);

            string clsPath = Path.Combine(baseDir, "Scripts/best.onnx");
            if (File.Exists(clsPath)) _classSession = new InferenceSession(clsPath);
        }

        public List<Detection> DetectObjects(Mat frame)
        {
            try { return _detector.Detect(frame).ToList(); }
            catch { return new List<Detection>(); }
        }

        public YolopResult InferLanes(Mat frame) => _yolop.Infer(frame);

        public string GetCarModel(Mat cropImg)
        {
            if (_classSession == null) return "Vehicle";
            try
            {
                using var rgb = new Mat(); Cv2.CvtColor(cropImg, rgb, ColorConversionCodes.BGR2RGB);
                using var res = new Mat(); Cv2.Resize(rgb, res, new Size(160, 160));
                var input = new DenseTensor<float>(new[] { 1, 3, 160, 160 });
                for (int y = 0; y < 160; y++) for (int x = 0; x < 160; x++)
                    {
                        var p = res.At<Vec3b>(y, x);
                        input[0, 0, y, x] = p.Item0 / 255f;
                        input[0, 1, y, x] = p.Item1 / 255f;
                        input[0, 2, y, x] = p.Item2 / 255f;
                    }
                lock (_sessionLock)
                {
                    using var r = _classSession.Run(new[] { NamedOnnxValue.CreateFromTensor(_classSession.InputMetadata.Keys.First(), input) });
                    var o = r.First().AsTensor<float>();
                    float[] scores = new float[206];
                    for (int i = 0; i < o.Dimensions[2]; i++)
                        for (int c = 0; c < 206; c++) scores[c] += o[0, 4 + c, i];
                    return _carModelNames[Array.IndexOf(scores, scores.Max())];
                }
            }
            catch { return "Vehicle"; }
        }

        public void Dispose() => _classSession?.Dispose();
    }
}