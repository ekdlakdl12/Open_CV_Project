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

        // ROI start (상단으로 올릴수록 "앞쪽(원근)" 위주)
        public float RoiYStartRatio { get; set; } = 0.55f;

        // 선을 더 "눕히는" 느낌(원근 상단 y)
        public float VanishYRatio { get; set; } = 0.20f;

        // Ego 폭 스케일
        public float EgoLaneWidthScale { get; set; } = 1.5f;

        public float LaneProbThreshold { get; set; } = 0.35f;
        public int MinCorridorWidthPx { get; set; } = 80;

        // 추적 세팅 (핵심)
        public int TraceStepPx { get; set; } = 18;          // 위로 올라가며 샘플링 간격
        public int TraceWindowPx { get; set; } = 48;        // x 탐색 윈도우(+/-)
        public int TraceSmoothing { get; set; } = 5;        // ego trace smoothing

        // boundary smoothing (전체 xs 안정화)
        public int SmoothHistory { get; set; } = 5;

        // Results
        public IReadOnlyList<Point[]> LaneBoundaries => _laneBoundaries;

        private Mat? _drivableMask; // 0/255
        private readonly List<Point[]> _laneBoundaries = new();
        private readonly Queue<int[]> _historyX = new();

        // ego boundary x (bottom 기준) 추적 안정화
        private int _prevEgoLeftX = -1;
        private int _prevEgoRightX = -1;

        public void SetDrivableMask(Mat? drivableMask)
        {
            _drivableMask?.Dispose();
            _drivableMask = drivableMask?.Clone();
        }

        public bool Analyze(Mat laneProb, Size frameSize)
        {
            if (laneProb == null || laneProb.Empty()) return false;
            if (TotalLanes < 1) return false;

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

                // laneProb -> 8U (0..255)
                using var lp8u = new Mat();
                if (lpRoi.Type() == MatType.CV_32FC1 || lpRoi.Type() == MatType.CV_32F)
                    lpRoi.ConvertTo(lp8u, MatType.CV_8UC1, 255.0);
                else if (lpRoi.Type() == MatType.CV_8UC1)
                    lpRoi.CopyTo(lp8u);
                else
                    Cv2.CvtColor(lpRoi, lp8u, ColorConversionCodes.BGR2GRAY);

                // threshold -> binary
                using var bin = new Mat();
                Cv2.Threshold(lp8u, bin, LaneProbThreshold * 255.0, 255, ThresholdTypes.Binary);

                // drivable gate
                if (_drivableMask != null && !_drivableMask.Empty()
                    && _drivableMask.Width == frameSize.Width && _drivableMask.Height == frameSize.Height)
                {
                    using var driveRoi = new Mat(_drivableMask, roiRect);
                    Cv2.BitwiseAnd(bin, driveRoi, bin);
                }

                int w = bin.Width;
                int h = bin.Height;

                // corridor from drivable (fallback full)
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

                // initial xs: evenly split corridor
                int boundaryCount = TotalLanes + 1;
                int[] xs = new int[boundaryCount];
                for (int i = 0; i < boundaryCount; i++)
                {
                    float t = (float)i / (boundaryCount - 1);
                    xs[i] = (int)(leftCorr + t * (rightCorr - leftCorr));
                }

                int egoL = Math.Clamp(EgoLane - 1, 0, boundaryCount - 2);
                int egoR = egoL + 1;

                // ---- Ego boundary seed: bottom band histogram ----
                // bin의 "아래쪽 일부"만 사용해서 seed 잡기 (근거리 노이즈 완화)
                int seedBandH = Math.Clamp(h / 5, 40, 140);
                using var bottomBand = new Mat(bin, new Rect(0, h - seedBandH, w, seedBandH));
                int[] hist = ReduceColumnSumToInt(bottomBand);

                // peaks
                int[] peaks = FindPeaks(hist, leftCorr, rightCorr, maxPeaks: 8, minDistance: 35);

                // expected from previous (follow)
                int expectL = (_prevEgoLeftX > 0) ? _prevEgoLeftX : xs[egoL];
                int expectR = (_prevEgoRightX > 0) ? _prevEgoRightX : xs[egoR];

                int bestL = (peaks.Length > 0) ? NearestPeakWithin(peaks, expectL, TraceWindowPx) : -1;
                int bestR = (peaks.Length > 0) ? NearestPeakWithin(peaks, expectR, TraceWindowPx) : -1;

                if (bestL < 0 && peaks.Length > 0) bestL = NearestPeak(peaks, expectL);
                if (bestR < 0 && peaks.Length > 0) bestR = NearestPeak(peaks, expectR);

                // 못 찾으면 corridor 등분값으로
                if (bestL <= 0) bestL = xs[egoL];
                if (bestR <= 0) bestR = xs[egoR];

                // 폭 스케일 보정
                int mid = (bestL + bestR) / 2;
                int half = Math.Max(20, (int)(Math.Abs(bestR - bestL) * 0.5f * EgoLaneWidthScale));
                int egoLeftSeed = Math.Clamp(mid - half, leftCorr, rightCorr);
                int egoRightSeed = Math.Clamp(mid + half, leftCorr, rightCorr);

                // ---- Ego boundary trace: bottom->top follow peaks ----
                var egoLeftTrace = TraceBoundaryX(bin, egoLeftSeed, TraceWindowPx, TraceStepPx);
                var egoRightTrace = TraceBoundaryX(bin, egoRightSeed, TraceWindowPx, TraceStepPx);

                // smoothing trace
                SmoothTraceInPlace(egoLeftTrace, TraceSmoothing);
                SmoothTraceInPlace(egoRightTrace, TraceSmoothing);

                // bottom x from trace (y=h-1)
                int egoLeftBottom = egoLeftTrace.Count > 0 ? egoLeftTrace[0].x : egoLeftSeed;
                int egoRightBottom = egoRightTrace.Count > 0 ? egoRightTrace[0].x : egoRightSeed;

                xs[egoL] = Math.Clamp(egoLeftBottom, leftCorr, rightCorr);
                xs[egoR] = Math.Clamp(egoRightBottom, leftCorr, rightCorr);

                // corridor 재분배 (ego를 기준으로 좌/우 등분)
                int leftBoundaries = egoL;
                for (int i = 0; i < leftBoundaries; i++)
                {
                    float t = (float)i / Math.Max(1, leftBoundaries);
                    xs[i] = (int)(leftCorr + t * (xs[egoL] - leftCorr));
                }
                int rightBoundaries = boundaryCount - 1 - egoR;
                for (int k = 1; k <= rightBoundaries; k++)
                {
                    float t = (float)k / Math.Max(1, rightBoundaries);
                    xs[egoR + k] = (int)(xs[egoR] + t * (rightCorr - xs[egoR]));
                }

                // boundary smoothing (bottom 기준 안정화)
                PushHistory(xs);
                xs = AverageHistory();

                // boundary monotonic fix (교차/역전 방지)
                for (int i = 1; i < xs.Length; i++)
                {
                    if (xs[i] <= xs[i - 1] + 6) xs[i] = xs[i - 1] + 6;
                }
                for (int i = 0; i < xs.Length; i++)
                {
                    xs[i] = Math.Clamp(xs[i], leftCorr, rightCorr);
                }

                // ego 최소폭 보장 (너무 좁아지면 mid 기준으로 다시 벌림)
                int minEgoWidth = 60;
                if (xs[egoR] - xs[egoL] < minEgoWidth)
                {
                    int m2 = (xs[egoL] + xs[egoR]) / 2;
                    xs[egoL] = Math.Clamp(m2 - minEgoWidth / 2, leftCorr, rightCorr);
                    xs[egoR] = Math.Clamp(m2 + minEgoWidth / 2, leftCorr, rightCorr);
                }


                // prev update
                _prevEgoLeftX = xs[egoL];
                _prevEgoRightX = xs[egoR];

                // ---- Build final boundary polylines in full-frame coordinates ----
                // ---- Build final boundary polylines in full-frame coordinates ----
                _laneBoundaries.Clear();

                int yBottomFull = frameSize.Height - 1;
                int yVanishFull = yStart + (int)((frameSize.Height - yStart) * VanishYRatio);
                yVanishFull = Math.Clamp(yVanishFull, yStart, frameSize.Height - 2);

                // 모든 boundary를 "직선 3-point"로 통일 (ego도 직선화)
                for (int b = 0; b < boundaryCount; b++)
                {
                    int xBottom = xs[b];

                    // 위쪽으로 갈수록 화면 중앙(소실점)쪽으로 모이게
                    // (Merge_Carmodel_Lane_Test처럼 곧은 선 느낌)
                    int xTop = (int)Lerp(xs[b], frameSize.Width / 2f, 0.35f);

                    _laneBoundaries.Add(new[]
                    {
                        new Point(xBottom, yBottomFull),
                        new Point((xBottom + xTop) / 2, (yBottomFull + yVanishFull) / 2),
                        new Point(xTop, yVanishFull)
                    });
                }

                return _laneBoundaries.Count >= 2;

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
                Cv2.PutText(frame, $"#{lane}", new Point(cx - 12, cy),
                    HersheyFonts.HersheySimplex, 0.9, color, 2);
            }
        }

        public int TryGetLaneNumberForPoint(Point p)
        {
            if (_laneBoundaries.Count < 2) return -1;

            // bottom point 기준으로 boundary x를 비교
            var bx = _laneBoundaries.Select(b => b[0].X).ToArray();

            for (int lane = 1; lane <= TotalLanes; lane++)
            {
                int left = bx[lane - 1];
                int right = bx[lane];
                int lo = Math.Min(left, right);
                int hi = Math.Max(left, right);
                if (p.X >= lo && p.X < hi) return lane;
            }
            return -1;
        }

        // ============== helpers ==============
        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        private static int[] ReduceColumnSumToInt(Mat bin8u)
        {
            using var colSum = new Mat();
            Cv2.Reduce(bin8u, colSum, 0, ReduceTypes.Sum, MatType.CV_32S); // 1 x W

            int w = bin8u.Width;
            int[] outArr = new int[w];
            for (int x = 0; x < w; x++)
            {
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

        private static int NearestPeakWithin(int[] peaks, int x, int win)
        {
            int best = -1;
            int bestDist = int.MaxValue;
            foreach (var p in peaks)
            {
                int d = Math.Abs(p - x);
                if (d <= win && d < bestDist)
                {
                    best = p;
                    bestDist = d;
                }
            }
            return best;
        }

        // bin: ROI binary (h x w), y=0 상단, y=h-1 하단
        // return: list of (x,y) from bottom->top (y desc)
        private static List<(int x, int y)> TraceBoundaryX(Mat bin, int seedX, int win, int step)
        {
            int w = bin.Width;
            int h = bin.Height;

            var trace = new List<(int x, int y)>(Math.Max(8, h / Math.Max(1, step)));

            int xCur = Math.Clamp(seedX, 0, w - 1);

            for (int y = h - 1; y >= 0; y -= Math.Max(1, step))
            {
                int x0 = Math.Max(0, xCur - win);
                int x1 = Math.Min(w - 1, xCur + win);
                if (x1 <= x0) break;

                // window에서 가장 "많이 켜진" column 찾기 (row 한 줄만 보면 약해서, 주변 3줄 합산)
                int y0 = Math.Max(0, y - 1);
                int y1 = Math.Min(h - 1, y + 1);

                int bestX = xCur;
                int bestScore = -1;

                for (int x = x0; x <= x1; x++)
                {
                    int s = 0;
                    for (int yy = y0; yy <= y1; yy++)
                        s += bin.At<byte>(yy, x); // 0 or 255

                    if (s > bestScore)
                    {
                        bestScore = s;
                        bestX = x;
                    }
                }

                // score가 너무 낮으면(차선이 끊김) xCur 유지
                if (bestScore <= 0) bestX = xCur;

                xCur = bestX;
                trace.Add((xCur, y));
            }

            return trace;
        }

        private static void SmoothTraceInPlace(List<(int x, int y)> trace, int k)
        {
            if (trace == null || trace.Count < 3) return;
            if (k <= 1) return;

            int n = trace.Count;
            var xs = trace.Select(t => t.x).ToArray();

            int half = k / 2;
            var sm = new int[n];

            for (int i = 0; i < n; i++)
            {
                int a = Math.Max(0, i - half);
                int b = Math.Min(n - 1, i + half);
                int sum = 0;
                int cnt = 0;
                for (int j = a; j <= b; j++)
                {
                    sum += xs[j];
                    cnt++;
                }
                sm[i] = sum / Math.Max(1, cnt);
            }

            for (int i = 0; i < n; i++)
                trace[i] = (sm[i], trace[i].y);
        }

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
