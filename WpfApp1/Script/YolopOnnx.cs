using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using WpfApp1.Models;

namespace WpfApp1.Script
{
    public sealed class YolopOnnx : IDisposable
    {
        private readonly InferenceSession _session;
        private readonly string _inputName;

        private readonly int _imgSize;
        private readonly float _confThres;
        private readonly float _nmsThres;

        public sealed record YolopResult(
            List<Detection> Detections,
            Mat? DrivableMaskOrig,   // CV_8UC1 (0/255)
            Mat? LaneMaskOrig,       // CV_8UC1 (0/255)
            Mat? LaneProbOrig        // CV_32FC1 (0~1)
        );

        public YolopOnnx(string onnxPath, int imgSize = 640, float confThres = 0.35f, float nmsThres = 0.45f)
        {
            _imgSize = imgSize;
            _confThres = confThres;
            _nmsThres = nmsThres;

            _session = new InferenceSession(onnxPath);
            _inputName = _session.InputMetadata.Keys.First();
        }

        public YolopResult Infer(Mat bgr)
        {
            var (inputRgb, scale, padX, padY) = LetterboxToRgb(bgr, _imgSize);

            try
            {
                var inputTensor = RgbMatToTensorNchw(inputRgb);

                using var results = _session.Run(new[]
                {
                    NamedOnnxValue.CreateFromTensor(_inputName, inputTensor)
                });

                // outputs: det_out [1,25200,6], drive_area_seg [1,2,640,640], lane_line_seg [1,2,640,640]
                var detT = results.First(r => r.Name.Contains("det", StringComparison.OrdinalIgnoreCase)).AsTensor<float>();
                var driveT = results.First(r => r.Name.Contains("drive", StringComparison.OrdinalIgnoreCase)).AsTensor<float>();
                var laneT = results.First(r => r.Name.Contains("lane", StringComparison.OrdinalIgnoreCase)).AsTensor<float>();

                // 1) Detection (우리는 지금 차량검출에 YOLOP det를 사용 안 하지만, 안전하게 반환은 해둠)
                var dets = ParseDetOut(detT, bgr.Width, bgr.Height, scale, padX, padY, _confThres);
                dets = NmsByClass(dets, _nmsThres);

                // 2) Drive mask (argmax -> class=1)
                using var driveMask640 = SegToBinaryMask(driveT, thrProb: 0.5f); // CV_8UC1 0/255
                var driveOrig = UnLetterboxMaskToOriginal(driveMask640, bgr.Width, bgr.Height, scale, padX, padY);

                // 3) Lane prob map (softmax prob of class=1)
                using var laneProb640 = SegToProbMap(laneT); // CV_32FC1 0~1
                var laneProbOrig = UnLetterboxProbToOriginal(laneProb640, bgr.Width, bgr.Height, scale, padX, padY);

                // 4) Lane mask (prob -> threshold)
                var laneOrig = ProbToBinaryMask(laneProbOrig, thr: 0.50f); // 기본값(표시용)

                return new YolopResult(dets, driveOrig, laneOrig, laneProbOrig);
            }
            finally
            {
                inputRgb.Dispose();
            }
        }

        // =========================
        // Preprocess
        // =========================
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

        // =========================
        // Postprocess: Det
        // =========================
        private static List<Detection> ParseDetOut(Tensor<float> det, int origW, int origH, float scale, int padX, int padY, float confThres)
        {
            // det: [1,25200,6]
            int n = det.Dimensions[1];
            var list = new List<Detection>(128);

            // 값이 0~1로 나오는 모델도 있어서 자동 보정
            float maxCoord = 0f;
            for (int i = 0; i < Math.Min(n, 200); i++)
                maxCoord = Math.Max(maxCoord, Math.Max(det[0, i, 2], det[0, i, 3]));

            bool normalized = maxCoord <= 1.5f;

            for (int i = 0; i < n; i++)
            {
                float x1 = det[0, i, 0];
                float y1 = det[0, i, 1];
                float x2 = det[0, i, 2];
                float y2 = det[0, i, 3];
                float score = det[0, i, 4];
                int cls = (int)det[0, i, 5];

                if (score < confThres) continue;

                if (normalized)
                {
                    x1 *= 640f; y1 *= 640f; x2 *= 640f; y2 *= 640f;
                }

                // unletterbox: (x - pad)/scale
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

                list.Add(new Detection(new Rect(rx, ry, rw, rh), cls, score));
            }

            return list;
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

        // =========================
        // Postprocess: Segmentation
        // =========================
        private static Mat SegToProbMap(Tensor<float> seg)
        {
            // seg: [1,2,H,W] logits -> softmax prob(class=1)
            int h = seg.Dimensions[2];
            int w = seg.Dimensions[3];

            var prob = new Mat(h, w, MatType.CV_32FC1);

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float a = seg[0, 0, y, x];
                    float b = seg[0, 1, y, x];

                    float m = Math.Max(a, b);
                    float ea = (float)Math.Exp(a - m);
                    float eb = (float)Math.Exp(b - m);
                    float p1 = eb / (ea + eb);

                    prob.Set(y, x, p1);
                }
            }

            return prob;
        }

        private static Mat SegToBinaryMask(Tensor<float> seg, float thrProb = 0.5f)
        {
            // drive/lane logits -> prob -> threshold -> mask
            using var prob = SegToProbMap(seg);
            return ProbToBinaryMask(prob, thrProb);
        }

        private static Mat ProbToBinaryMask(Mat prob, float thr)
        {
            // prob: CV_32FC1 (0~1) -> CV_8UC1 0/255
            var mask = new Mat(prob.Rows, prob.Cols, MatType.CV_8UC1);
            for (int y = 0; y < prob.Rows; y++)
            {
                for (int x = 0; x < prob.Cols; x++)
                {
                    float p = prob.At<float>(y, x);
                    mask.Set(y, x, (byte)(p >= thr ? 255 : 0));
                }
            }
            return mask;
        }

        private static Mat UnLetterboxMaskToOriginal(Mat mask640, int origW, int origH, float scale, int padX, int padY)
        {
            int newW = (int)Math.Round(origW * scale);
            int newH = (int)Math.Round(origH * scale);

            var roi = new Rect(padX, padY, newW, newH);
            roi = roi.Intersect(new Rect(0, 0, mask640.Width, mask640.Height));

            using var cropped = new Mat(mask640, roi);

            var outMask = new Mat();
            Cv2.Resize(cropped, outMask, new Size(origW, origH), 0, 0, InterpolationFlags.Nearest);
            return outMask;
        }

        private static Mat UnLetterboxProbToOriginal(Mat prob640, int origW, int origH, float scale, int padX, int padY)
        {
            int newW = (int)Math.Round(origW * scale);
            int newH = (int)Math.Round(origH * scale);

            var roi = new Rect(padX, padY, newW, newH);
            roi = roi.Intersect(new Rect(0, 0, prob640.Width, prob640.Height));

            using var cropped = new Mat(prob640, roi);

            var outProb = new Mat();
            Cv2.Resize(cropped, outProb, new Size(origW, origH), 0, 0, InterpolationFlags.Linear);
            return outProb;
        }

        public void Dispose() => _session.Dispose();
    }
}
