using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;

namespace WpfApp1.Scripts
{
    public sealed class LaneAnalyzer
    {
        // =========================
        // Public tuning params
        // =========================
        public int TotalLanes { get; set; } = 5;   // 2..8
        public int EgoLane { get; set; } = 3;      // 1..TotalLanes

        public float RoiYStartRatio { get; set; } = 0.55f;
        public float BottomAnchorRatio { get; set; } = 0.75f;
        public float VanishYRatio { get; set; } = 0.20f;
        public float EgoLaneWidthScale { get; set; } = 1.5f;

        public float LaneProbThreshold { get; set; } = 0.35f;
        public int MinCorridorWidthPx { get; set; } = 80;
        public int SmoothHistory { get; set; } = 5;

        // Results
        public IReadOnlyList<Point[]> LaneBoundaries => _laneBoundaries;

        private Mat? _drivableMask; // 0/255
        private readonly List<Point[]> _laneBoundaries = new();
        private readonly Queue<int[]> _historyX = new();

        public void SetDrivableMask(Mat? drivableMask)
        {
            _drivableMask?.Dispose();
            _drivableMask = drivableMask?.Clone();
        }

        public bool Analyze(Mat laneProb, Size frameSize)
        {
            if (laneProb == null || laneProb.Empty()) return false;

            Mat lp = laneProb;
            Mat? resized = null;

            if (laneProb.Width != frameSize.Width || laneProb.Height != frameSize.Height)
            {
                resized = new Mat();
                Cv2.Resize(laneProb, resized, frameSize, 0, 0, InterpolationFlags.Linear);
                lp = resized;
            }

            try
            {
                int yStart = (int)(frameSize.Height * RoiYStartRatio);
                yStart = Math.Clamp(yStart, 0, frameSize.Height - 2);

                var roiRect = new Rect(0, yStart, frameSize.Width, frameSize.Height - yStart);
                using var lpRoi = new Mat(lp, roiRect);

                // laneProb -> 8U
                using var lp8u = new Mat();
                if (lpRoi.Type() == MatType.CV_32FC1 || lpRoi.Type() == MatType.CV_32F)
                    lpRoi.ConvertTo(lp8u, MatType.CV_8UC1, 255.0);
                else if (lpRoi.Type() == MatType.CV_8UC1)
                    lpRoi.CopyTo(lp8u);
                else
                    Cv2.CvtColor(lpRoi, lp8u, ColorConversionCodes.BGR2GRAY);

                // threshold to binary
                using var bin = new Mat();
                Cv2.Threshold(lp8u, bin, LaneProbThreshold * 255.0, 255, ThresholdTypes.Binary);

                // optionally mask by drivable
                if (_drivableMask != null && !_drivableMask.Empty()
                    && _drivableMask.Width == frameSize.Width && _drivableMask.Height == frameSize.Height)
                {
                    using var driveRoi = new Mat(_drivableMask, roiRect);
                    Cv2.BitwiseAnd(bin, driveRoi, bin);
                }

                int w = bin.Width;
                int h = bin.Height;

                // histogram by column: Reduce SUM -> 1xW (CV_32S)
                int[] hist = ReduceColumnSumToInt(bin);

                // corridor from drivable mask (fallback full width)
                int leftCorr = 0, rightCorr = w - 1;
                if (_drivableMask != null && !_drivableMask.Empty()
                    && _drivableMask.Width == frameSize.Width && _drivableMask.Height == frameSize.Height)
                {
                    using var driveRoi = new Mat(_drivableMask, roiRect);
                    int[] cols = ReduceColumnSumToInt(driveRoi);

                    int thr = Math.Max(5, h / 50);
                    leftCorr = 0;
                    while (leftCorr < w && cols[leftCorr] < thr) leftCorr++;
                    rightCorr = w - 1;
                    while (rightCorr >= 0 && cols[rightCorr] < thr) rightCorr--;

                    if (rightCorr - leftCorr < MinCorridorWidthPx)
                    {
                        leftCorr = 0;
                        rightCorr = w - 1;
                    }
                }

                // peaks
                int[] peaks = FindPeaks(hist, leftCorr, rightCorr, maxPeaks: 6, minDistance: 40);

                int boundaryCount = TotalLanes + 1;
                int[] xs = new int[boundaryCount];

                // default: evenly split corridor
                for (int i = 0; i < boundaryCount; i++)
                {
                    float t = (float)i / (boundaryCount - 1);
                    xs[i] = (int)(leftCorr + t * (rightCorr - leftCorr));
                }

                int egoL = Math.Clamp(EgoLane - 1, 0, boundaryCount - 2);
                int egoR = egoL + 1;

                if (peaks.Length > 0)
                {
                    int expectL = xs[egoL];
                    int expectR = xs[egoR];

                    int bestL = NearestPeak(peaks, expectL);
                    int bestR = NearestPeak(peaks, expectR);

                    if (bestL == bestR && peaks.Length >= 2)
                        bestR = peaks.OrderBy(p => Math.Abs(p - expectR)).Skip(1).First();

                    int mid = (bestL + bestR) / 2;
                    int half = Math.Max(20, (int)((bestR - bestL) * 0.5f * EgoLaneWidthScale));

                    xs[egoL] = Math.Clamp(mid - half, leftCorr, rightCorr);
                    xs[egoR] = Math.Clamp(mid + half, leftCorr, rightCorr);

                    // left side resplit
                    int leftBoundaries = egoL;
                    for (int i = 0; i < leftBoundaries; i++)
                    {
                        float t = (float)i / Math.Max(1, leftBoundaries);
                        xs[i] = (int)(leftCorr + t * (xs[egoL] - leftCorr));
                    }
                    // right side resplit
                    int rightBoundaries = boundaryCount - 1 - egoR;
                    for (int k = 1; k <= rightBoundaries; k++)
                    {
                        float t = (float)k / Math.Max(1, rightBoundaries);
                        xs[egoR + k] = (int)(xs[egoR] + t * (rightCorr - xs[egoR]));
                    }
                }

                // smoothing
                PushHistory(xs);
                xs = AverageHistory();

                // build polylines
                _laneBoundaries.Clear();

                int yBottom = frameSize.Height - 1;
                int yVanish = yStart + (int)((frameSize.Height - yStart) * VanishYRatio);
                yVanish = Math.Clamp(yVanish, yStart, frameSize.Height - 2);

                int yAnchor = yStart + (int)((frameSize.Height - yStart) * BottomAnchorRatio);
                yAnchor = Math.Clamp(yAnchor, yStart, frameSize.Height - 2);

                for (int i = 0; i < boundaryCount; i++)
                {
                    int xBottom = xs[i];
                    int xTop = (int)Lerp(xs[i], frameSize.Width / 2f, 0.35f);

                    var pts = new[]
                    {
                        new Point(xBottom, yBottom),
                        new Point((xBottom + xTop) / 2, yAnchor),
                        new Point(xTop, yVanish)
                    };
                    _laneBoundaries.Add(pts);
                }

                return true;
            }
            finally
            {
                resized?.Dispose();
            }
        }

        public void DrawOnFrame(Mat frame)
        {
            if (_laneBoundaries.Count == 0) return;

            for (int i = 0; i < _laneBoundaries.Count; i++)
                Cv2.Polylines(frame, new[] { _laneBoundaries[i] }, false, Scalar.White, 2, LineTypes.AntiAlias);

            for (int lane = 1; lane <= TotalLanes; lane++)
            {
                int leftIdx = lane - 1;
                int rightIdx = lane;
                if (rightIdx >= _laneBoundaries.Count) break;

                var pL = _laneBoundaries[leftIdx][0];
                var pR = _laneBoundaries[rightIdx][0];
                int cx = (pL.X + pR.X) / 2;
                int cy = Math.Min(frame.Height - 15, frame.Height - 40);

                var color = (lane == EgoLane) ? Scalar.Yellow : Scalar.White;
                Cv2.PutText(frame, $"#{lane}", new Point(cx - 12, cy), HersheyFonts.HersheySimplex, 0.9, color, 2);
            }
        }

        public int TryGetLaneNumberForPoint(Point p)
        {
            if (_laneBoundaries.Count < 2) return -1;

            var bx = _laneBoundaries.Select(b => b[0].X).ToArray();

            for (int lane = 1; lane <= TotalLanes; lane++)
            {
                int left = bx[lane - 1];
                int right = bx[lane];
                int lo = Math.Min(left, right);
                int hi = Math.Max(left, right);

                if (p.X >= lo && p.X < hi)
                    return lane;
            }
            return -1;
        }

        // ---------- helpers ----------
        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        private static int[] ReduceColumnSumToInt(Mat bin8u)
        {
            using var colSum = new Mat();
            Cv2.Reduce(bin8u, colSum, 0, ReduceTypes.Sum, MatType.CV_32S); // 1 x W

            int w = bin8u.Width;
            int[] outArr = new int[w];
            for (int x = 0; x < w; x++)
            {
                // 0/255 이미지면 sum도 255단위로 커짐 → 정규화
                int v = colSum.At<int>(0, x);
                outArr[x] = v / 255;
            }
            return outArr;
        }

        private static int[] FindPeaks(int[] hist, int left, int right, int maxPeaks, int minDistance)
        {
            var peaks = new List<(int x, int v)>();
            for (int x = left + 1; x < right - 1; x++)
            {
                int v = hist[x];
                if (v > hist[x - 1] && v > hist[x + 1])
                    peaks.Add((x, v));
            }

            peaks.Sort((a, b) => b.v.CompareTo(a.v));

            var picked = new List<int>();
            foreach (var (x, _) in peaks)
            {
                if (picked.All(px => Math.Abs(px - x) >= minDistance))
                {
                    picked.Add(x);
                    if (picked.Count >= maxPeaks) break;
                }
            }
            picked.Sort();
            return picked.ToArray();
        }

        private static int NearestPeak(int[] peaks, int x)
            => peaks.OrderBy(p => Math.Abs(p - x)).FirstOrDefault();

        private void PushHistory(int[] xs)
        {
            if (SmoothHistory <= 1) return;
            _historyX.Enqueue(xs.ToArray());
            while (_historyX.Count > SmoothHistory) _historyX.Dequeue();
        }

        private int[] AverageHistory()
        {
            if (_historyX.Count == 0) return Array.Empty<int>();

            int n = _historyX.Peek().Length;
            var sum = new int[n];

            foreach (var arr in _historyX)
                for (int i = 0; i < n; i++) sum[i] += arr[i];

            for (int i = 0; i < n; i++) sum[i] /= _historyX.Count;
            return sum;
        }
    }
}
