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

        private const string OUT_DET = "det_out";
        private const string OUT_DRIVE = "drive_area_seg";
        private const string OUT_LANE = "lane_line_seg";

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
                var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, inputTensor) };

                using var results = _session.Run(inputs);

                var det = results.First(r => r.Name == OUT_DET).AsTensor<float>();      // [1,25200,6]
                var drive = results.First(r => r.Name == OUT_DRIVE).AsTensor<float>(); // [1,2,640,640]
                var lane = results.First(r => r.Name == OUT_LANE).AsTensor<float>();   // [1,2,640,640]

                // seg mask 640
                using var driveMask640 = SegProbToMask(drive, thr: 0.48f, roiTopCut: 0.35);  // 도로는 넓게
                using var laneMask640Raw = SegProbToMask(lane, thr: 0.60f, roiTopCut: 0.35); // 차선은 조금 빡세게

                // 차선은 도로 내부로 제한
                using var driveDilated = DilateMask(driveMask640, ksize: 17);
                using var laneMask640 = new Mat();
                Cv2.BitwiseAnd(laneMask640Raw, driveDilated, laneMask640);

                PostProcessLaneInPlace(laneMask640);

                // ✅ 언레터박스 → 원본 크기로 복원
                var driveOrig = UnLetterboxMaskToOriginal(driveMask640, bgr.Width, bgr.Height, scale, padX, padY);
                var laneOrig = UnLetterboxMaskToOriginal(laneMask640, bgr.Width, bgr.Height, scale, padX, padY);

                // det
                var dets = ParseDetections_1xNx6(det, bgr.Width, bgr.Height, scale, padX, padY);
                dets = NmsByClass(dets, _nmsThres);

                return new YolopResult(dets, driveOrig, laneOrig);
            }
            finally
            {
                inputRgb.Dispose();
            }
        }

        public sealed record YolopResult(
            List<Detection> Detections,
            Mat? DrivableMaskOrig, // 원본 크기 CV_8UC1
            Mat? LaneMaskOrig      // 원본 크기 CV_8UC1
        );

        // ---------- preprocess ----------
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

            var tensor = new DenseTensor<float>(new[] { 1, 3, h, w });
            var idx = rgb.GetGenericIndexer<Vec3b>();

            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    Vec3b p = idx[y, x];
                    tensor[0, 0, y, x] = p.Item0 / 255f;
                    tensor[0, 1, y, x] = p.Item1 / 255f;
                    tensor[0, 2, y, x] = p.Item2 / 255f;
                }
            return tensor;
        }

        // ---------- seg post ----------
        private static Mat SegProbToMask(Tensor<float> seg, float thr, double roiTopCut)
        {
            // seg: [1,2,H,W]
            int h = seg.Dimensions[2];
            int w = seg.Dimensions[3];

            Mat mask = new Mat(h, w, MatType.CV_8UC1, Scalar.Black);

            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float a = seg[0, 0, y, x];
                    float b = seg[0, 1, y, x];

                    float m = Math.Max(a, b);
                    float ea = (float)Math.Exp(a - m);
                    float eb = (float)Math.Exp(b - m);
                    float p1 = eb / (ea + eb);

                    mask.Set(y, x, (byte)(p1 >= thr ? 255 : 0));
                }

            int cutY = (int)(h * roiTopCut);
            using (var top = new Mat(mask, new Rect(0, 0, w, cutY)))
                top.SetTo(0);

            return mask;
        }

        private static Mat DilateMask(Mat m, int ksize)
        {
            var outm = new Mat();
            using var k = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(ksize, ksize));
            Cv2.Dilate(m, outm, k);
            return outm;
        }

        private static void PostProcessLaneInPlace(Mat laneMask)
        {
            using (var k1 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5)))
                Cv2.MorphologyEx(laneMask, laneMask, MorphTypes.Open, k1);

            using (var k2 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(11, 11)))
                Cv2.MorphologyEx(laneMask, laneMask, MorphTypes.Close, k2);

            using var labels = new Mat();
            using var stats = new Mat();
            using var centroids = new Mat();

            Cv2.ConnectedComponentsWithStats(laneMask, labels, stats, centroids);

            int nLabels = stats.Rows;
            for (int i = 1; i < nLabels; i++)
            {
                int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
                if (area < 500)
                {
                    using var compMask = new Mat();
                    Cv2.Compare(labels, i, compMask, CmpType.EQ);
                    laneMask.SetTo(Scalar.All(0), compMask);
                }
            }
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

        // ---------- det post ----------
        private List<Detection> ParseDetections_1xNx6(Tensor<float> det, int origW, int origH, float scale, int padX, int padY)
        {
            var dets = new List<Detection>(256);
            int n = det.Dimensions[1];

            for (int i = 0; i < n; i++)
            {
                float x1 = det[0, i, 0];
                float y1 = det[0, i, 1];
                float x2 = det[0, i, 2];
                float y2 = det[0, i, 3];
                float conf = det[0, i, 4];
                int cls = (int)det[0, i, 5];

                if (conf < _confThres) continue;

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

                // 팀원 Detection 생성자(Rect,int,float) 맞춤
                dets.Add(new Detection(new Rect(rx, ry, rw, rh), cls, conf));
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
