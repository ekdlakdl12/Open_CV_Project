using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using WpfApp1.Models;   // ✅ 추가
using System;
using System.Collections.Generic;
using System.Linq;

namespace WpfApp1
{
    public sealed class YoloV8Onnx : IDisposable
    {
        private readonly InferenceSession _session;
        private readonly string _inputName;
        private readonly int _imgSize;
        private readonly float _confThres;
        private readonly float _nmsThres;

        public YoloV8Onnx(string onnxPath, int imgSize = 640, float confThres = 0.5f, float nmsThres = 0.45f)
        {
            _imgSize = imgSize;
            _confThres = confThres;
            _nmsThres = nmsThres;

            _session = new InferenceSession(onnxPath);
            _inputName = _session.InputMetadata.Keys.First();
        }

        public List<Detection> Detect(Mat bgr)
        {
            var (inputRgb, scale, padX, padY) = LetterboxToRgb(bgr, _imgSize);

            try
            {
                var inputTensor = RgbMatToTensorNchw(inputRgb);

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(_inputName, inputTensor)
                };

                using var results = _session.Run(inputs);
                var output = results.First().AsTensor<float>(); // [1,84,8400]

                var dets = ParseOutput_1x84x8400(output, bgr.Width, bgr.Height, scale, padX, padY);
                return NmsByClass(dets, _nmsThres);
            }
            finally
            {
                inputRgb.Dispose();
            }
        }

        // --- 이하 너 코드 그대로 ---
        private static (Mat rgb, float scale, int padX, int padY) LetterboxToRgb(Mat bgr, int newSize)
        {
            Mat rgb = new();
            Cv2.CvtColor(bgr, rgb, ColorConversionCodes.BGR2RGB);

            float r = Math.Min((float)newSize / rgb.Width, (float)newSize / rgb.Height);
            int newW = (int)Math.Round(rgb.Width * r);
            int newH = (int)Math.Round(rgb.Height * r);

            Mat resized = new();
            Cv2.Resize(rgb, resized, new Size(newW, newH));

            int padX = (newSize - newW) / 2;
            int padY = (newSize - newH) / 2;

            Mat outMat = new(newSize, newSize, MatType.CV_8UC3, new Scalar(114, 114, 114));
            resized.CopyTo(new Mat(outMat, new Rect(padX, padY, newW, newH)));

            rgb.Dispose();
            resized.Dispose();

            return (outMat, r, padX, padY);
        }

        private static DenseTensor<float> RgbMatToTensorNchw(Mat rgb)
        {
            int h = rgb.Rows;
            int w = rgb.Cols;

            if (rgb.Type() != MatType.CV_8UC3)
                throw new Exception($"Unexpected MatType: {rgb.Type()} (expected CV_8UC3)");

            var tensor = new DenseTensor<float>(new[] { 1, 3, h, w });
            var idx = rgb.GetGenericIndexer<Vec3b>();

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Vec3b p = idx[y, x];
                    tensor[0, 0, y, x] = p.Item0 / 255f;
                    tensor[0, 1, y, x] = p.Item1 / 255f;
                    tensor[0, 2, y, x] = p.Item2 / 255f;
                }
            }
            return tensor;
        }

        private List<Detection> ParseOutput_1x84x8400(Tensor<float> output, int origW, int origH, float scale, int padX, int padY)
        {
            var dets = new List<Detection>(256);
            int numBoxes = output.Dimensions[2];

            for (int i = 0; i < numBoxes; i++)
            {
                float cx = output[0, 0, i];
                float cy = output[0, 1, i];
                float w = output[0, 2, i];
                float h = output[0, 3, i];

                int bestCls = -1;
                float bestScore = 0f;

                for (int c = 0; c < 80; c++)
                {
                    float score = output[0, 4 + c, i];
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestCls = c;
                    }
                }

                if (bestCls != 2 && bestCls != 5 && bestCls != 7) continue;
                if (bestScore < _confThres) continue;

                float x1 = cx - w / 2f;
                float y1 = cy - h / 2f;
                float x2 = cx + w / 2f;
                float y2 = cy + h / 2f;

                x1 = (x1 - padX) / scale;
                y1 = (y1 - padY) / scale;
                x2 = (x2 - padX) / scale;
                y2 = (y2 - padY) / scale;

                x1 = Math.Clamp(x1, 0, origW - 1);
                y1 = Math.Clamp(y1, 0, origH - 1);
                x2 = Math.Clamp(x2, 0, origW - 1);
                y2 = Math.Clamp(y2, 0, origH - 1);

                int rx = (int)x1;
                int ry = (int)y1;
                int rw = (int)(x2 - x1);
                int rh = (int)(y2 - y1);

                if (rw < 10 || rh < 10) continue;

                dets.Add(new Detection(new Rect(rx, ry, rw, rh), bestCls, bestScore));
            }

            return dets;
        }

        private static float IoU(Rect a, Rect b)
        {
            int x1 = Math.Max(a.X, b.X);
            int y1 = Math.Max(a.Y, b.Y);
            int x2 = Math.Min(a.Right, b.Right);
            int y2 = Math.Min(a.Bottom, b.Bottom);

            int interW = Math.Max(0, x2 - x1);
            int interH = Math.Max(0, y2 - y1);
            int inter = interW * interH;

            int areaA = a.Width * a.Height;
            int areaB = b.Width * b.Height;

            return inter / (float)(areaA + areaB - inter + 1e-6f);
        }

        private static List<Detection> NmsByClass(List<Detection> dets, float iouThres)
        {
            var result = new List<Detection>(dets.Count);

            foreach (var grp in dets.GroupBy(d => d.ClassId))
            {
                var sorted = grp.OrderByDescending(d => d.Score).ToList();

                while (sorted.Count > 0)
                {
                    var best = sorted[0];
                    result.Add(best);
                    sorted.RemoveAt(0);

                    sorted = sorted.Where(d => IoU(best.Box, d.Box) < iouThres).ToList();
                }
            }

            return result;
        }

        public void Dispose() => _session.Dispose();
    }
}
