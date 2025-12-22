// LaneAnalyzer.cs (FULL REPLACEMENT - API COMPATIBLE)
// - 기존 MainWindowViewModel.cs가 기대하는 API 유지:
//   * LaneAnalysisResult, Analyze(), DrawOnFrame(), TryGetLaneNumberForPoint(), SetDrivableMask()
//   * TotalLanes/EgoLane, RoiYStartRatio/RoiXMarginRatio 등 튜닝 프로퍼티 유지
// - 추가 기능:
//   * laneProb peak(빨간점)들을 세로로 연결해 "실제 검출된 라인만" 흰색 polyline으로 그리기
//   * 빨간 점이 부족하면 라인 자체를 안 그림(=요구사항)
//   * 기존 강제 boundary(1~TotalLanes+1) 흰선/라벨은 옵션으로 끌 수 있음

using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;

namespace WpfApp1.Scripts
{
    public sealed class LaneAnalyzer
    {
        // =========================
        // Public tuning params (기존과 호환)
        // =========================
        public int TotalLanes { get; set; } = 5;   // 2..8
        public int EgoLane { get; set; } = 3;      // 1..TotalLanes

        public float RoiYStartRatio { get; set; } = 0.52f;
        public float RoiXMarginRatio { get; set; } = 0.02f;

        public float LaneProbThreshold { get; set; } = 0.45f;

        public bool UseDrivableGate { get; set; } = true;
        public int GateErodeK { get; set; } = 9;

        public int LaneMaskOpenK { get; set; } = 3;
        public int LaneMaskCloseK { get; set; } = 5;

        // (호환용)
        public int CorridorBottomBandH { get; set; } = 60;

        // =========================
        // "빨간선(peak)" 파라미터
        // =========================
        public int SampleBandCount { get; set; } = 18;
        public float SampleYTopRatio { get; set; } = 0.30f;
        public float SampleYBottomRatio { get; set; } = 0.95f;

        public int PeakMinStrength { get; set; } = 6;
        public int PeakMinGapPx { get; set; } = 18;

        public int ExpectedWindowPx { get; set; } = 90;
        public int FollowWindowPx { get; set; } = 75;

        public bool PreferLaneProbForEgoBoundaries { get; set; } = true;

        // =========================
        // ✅ NEW: 화면에 "peak들을 이어 만든 흰색 라인"을 그리는 옵션
        // =========================
        public bool DebugDrawPeakPolylines { get; set; } = true;   // ✅ 기본 ON
        public bool DebugDrawBoundaries { get; set; } = false;     // 기존 강제 1~N 경계선은 기본 OFF
        public bool DebugDrawLabels { get; set; } = false;         // #1 #2 라벨도 기본 OFF
        public bool DebugDrawLanePeaksDots { get; set; } = false;  // 빨간 점(●)은 필요하면 ON

        // peak polyline clustering
        public int PeakPolylineFollowX { get; set; } = 38;         // 이전 점과 x 차 허용
        public int PeakPolylineMinLen { get; set; } = 7;           // 이 개수 이상 이어진 것만 라인으로 그림

        // =========================
        // Drivable mask(원본크기, CV_8UC1 0/255)
        // =========================
        private Mat? _drivableMaskOrig;

        public void SetDrivableMask(Mat? drivableMaskOrig)
        {
            _drivableMaskOrig?.Dispose();
            _drivableMaskOrig = drivableMaskOrig?.Clone();
        }

        // =========================
        // Result types
        // =========================
        public sealed class LaneAnalysisResult
        {
            public int Width;
            public int Height;

            public Rect Roi;

            // boundaries count = TotalLanes + 1
            // each boundary is x = a*y + b  (ROI 좌표계)
            public List<(double a, double b)> BoundaryLines = new();

            public int[] SampleYs = Array.Empty<int>();

            // optional debug (caller disposes)
            public Mat? LaneLineMaskRoi8u;
            public Mat? DriveMaskRoi8u;
        }

        // =========================
        // Main entry
        // =========================
        public LaneAnalysisResult Analyze(Mat laneProbOrig32f, int frameW, int frameH)
        {
            if (laneProbOrig32f == null || laneProbOrig32f.Empty())
                throw new ArgumentException("laneProbOrig32f is empty");

            int y0 = (int)(frameH * RoiYStartRatio);
            y0 = Math.Clamp(y0, 0, frameH - 2);

            int xMargin = (int)(frameW * RoiXMarginRatio);
            xMargin = Math.Clamp(xMargin, 0, frameW / 4);

            var roi = new Rect(xMargin, y0, frameW - xMargin * 2, frameH - y0);
            if (roi.Width < 50 || roi.Height < 50)
                throw new Exception($"ROI too small: {roi}");

            using var laneProbRoi = new Mat(laneProbOrig32f, roi); // CV_32F ROI view

            // 1) lane line mask (빨간선 후보)
            using var laneMask = BuildLaneLineMaskFast(laneProbRoi); // CV_8U 0/255

            // 2) drivable ROI gate
            Mat? driveRoi8u = null;
            if (UseDrivableGate && _drivableMaskOrig != null && !_drivableMaskOrig.Empty())
            {
                using var driveRoiView = new Mat(_drivableMaskOrig, roi);
                driveRoi8u = driveRoiView.Clone();

                int k = MakeOdd(GateErodeK);
                if (k >= 3)
                {
                    using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(k, k));
                    Cv2.Erode(driveRoi8u, driveRoi8u, kernel);
                }

                // gate 적용: laneMask = laneMask & driveRoi
                Cv2.BitwiseAnd(laneMask, driveRoi8u, laneMask);
            }

            // 3) y 샘플 생성 (ROI 내부)
            var sampleYs = BuildSampleYs(roi.Height);

            // 4) 각 boundary별 (y,x) 점 수집
            var boundaryPoints = new List<List<Point2d>>();
            for (int i = 0; i < TotalLanes + 1; i++)
                boundaryPoints.Add(new List<Point2d>(sampleYs.Length));

            int egoLeftB = Math.Clamp(EgoLane - 1, 0, TotalLanes); // boundary index
            int egoRightB = Math.Clamp(EgoLane, 0, TotalLanes);

            Dictionary<int, double>? egoLeftXByY = null;
            Dictionary<int, double>? egoRightXByY = null;

            if (PreferLaneProbForEgoBoundaries)
            {
                egoLeftXByY = FollowBoundaryByPeaks(laneMask, driveRoi8u, sampleYs, egoLeftB);
                egoRightXByY = FollowBoundaryByPeaks(laneMask, driveRoi8u, sampleYs, egoRightB);
            }

            foreach (var y in sampleYs)
            {
                int leftX = 0;
                int rightX = roi.Width - 1;

                if (driveRoi8u != null && !driveRoi8u.Empty())
                {
                    GetCorridorLRAtRow(driveRoi8u, y, out leftX, out rightX);
                    if (rightX - leftX < roi.Width * 0.35)
                    {
                        leftX = 0;
                        rightX = roi.Width - 1;
                    }
                }

                var peaks = FindPeaksAtRow(laneMask, y, leftX, rightX);

                double[] expected = new double[TotalLanes + 1];
                for (int b = 0; b < expected.Length; b++)
                {
                    double t = (double)b / TotalLanes;
                    expected[b] = leftX + (rightX - leftX) * t;
                }

                for (int b = 0; b < TotalLanes + 1; b++)
                {
                    double bx;

                    if (b == 0) bx = leftX;
                    else if (b == TotalLanes) bx = rightX;
                    else if (PreferLaneProbForEgoBoundaries && (b == egoLeftB || b == egoRightB))
                    {
                        var dict = (b == egoLeftB) ? egoLeftXByY : egoRightXByY;
                        if (dict != null && dict.TryGetValue(y, out var v))
                            bx = v;
                        else
                            bx = SelectPeakNearExpected(peaks, expected[b], ExpectedWindowPx, fallback: expected[b]);
                    }
                    else
                    {
                        bx = SelectPeakNearExpected(peaks, expected[b], ExpectedWindowPx, fallback: expected[b]);
                    }

                    boundaryPoints[b].Add(new Point2d(y, bx)); // (y,x)
                }
            }

            // 5) boundary line fit (x = a*y + b)
            var lines = new List<(double a, double b)>(TotalLanes + 1);
            for (int b = 0; b < TotalLanes + 1; b++)
            {
                var pts = boundaryPoints[b];
                var filtered = FilterOutliersByMedianX(pts, 70);

                var (a, bb) = FitLineXofY(filtered.Count >= 6 ? filtered : pts);

                a = Math.Clamp(a, -2.0, 2.0);
                lines.Add((a, bb));
            }

            var result = new LaneAnalysisResult
            {
                Width = frameW,
                Height = frameH,
                Roi = roi,
                BoundaryLines = lines,
                SampleYs = sampleYs,

                LaneLineMaskRoi8u = laneMask.Clone(),
                DriveMaskRoi8u = driveRoi8u?.Clone()
            };

            driveRoi8u?.Dispose();
            return result;
        }

        // =========================
        // Ego boundary "Follow" (bottom -> top)
        // =========================
        private Dictionary<int, double> FollowBoundaryByPeaks(
            Mat laneMask8u,
            Mat? driveRoi8u,
            int[] sampleYsAscending,
            int boundaryIndex)
        {
            var ysDesc = sampleYsAscending.ToArray();
            Array.Sort(ysDesc);
            Array.Reverse(ysDesc);

            var map = new Dictionary<int, double>(ysDesc.Length);
            double prevX = double.NaN;

            foreach (var y in ysDesc)
            {
                int leftX = 0;
                int rightX = laneMask8u.Width - 1;

                if (driveRoi8u != null && !driveRoi8u.Empty())
                {
                    GetCorridorLRAtRow(driveRoi8u, y, out leftX, out rightX);
                    if (rightX - leftX < laneMask8u.Width * 0.35)
                    {
                        leftX = 0;
                        rightX = laneMask8u.Width - 1;
                    }
                }

                var peaks = FindPeaksAtRow(laneMask8u, y, leftX, rightX);

                double t = (TotalLanes == 0) ? 0 : (double)boundaryIndex / TotalLanes;
                double expected = leftX + (rightX - leftX) * t;

                double chosen;

                if (double.IsNaN(prevX))
                {
                    chosen = SelectPeakNearExpected(peaks, expected, ExpectedWindowPx, fallback: expected);
                }
                else
                {
                    chosen = SelectPeakNearExpected(peaks, prevX, FollowWindowPx, fallback: double.NaN);
                    if (double.IsNaN(chosen))
                        chosen = SelectPeakNearExpected(peaks, expected, ExpectedWindowPx, fallback: expected);
                }

                chosen = Math.Clamp(chosen, leftX, rightX);

                map[y] = chosen;
                prevX = chosen;
            }

            return map;
        }

        // =========================
        // Lane number for point
        // =========================
        public bool TryGetLaneNumberForPoint(LaneAnalysisResult r, Point pFrame, out int laneNum)
        {
            laneNum = -1;
            if (r == null || r.BoundaryLines == null || r.BoundaryLines.Count < 3)
                return false;

            if (!r.Roi.Contains(pFrame))
                return false;

            int xRoi = pFrame.X - r.Roi.X;
            int yRoi = pFrame.Y - r.Roi.Y;

            var xs = new double[r.BoundaryLines.Count];
            for (int i = 0; i < r.BoundaryLines.Count; i++)
            {
                var (a, b) = r.BoundaryLines[i];
                xs[i] = a * yRoi + b;
            }

            // monotonic 보정
            for (int i = 1; i < xs.Length; i++)
                if (xs[i] < xs[i - 1] + 2) xs[i] = xs[i - 1] + 2;

            int idx = 0;
            while (idx < xs.Length && xRoi >= xs[idx]) idx++;

            int lane = Math.Clamp(idx, 1, TotalLanes);
            laneNum = lane;
            return true;
        }

        // =========================
        // ✅ NEW: peak들을 이어서 polyline 만들기 (점이 충분히 이어진 것만)
        // =========================
        private List<Point[]> BuildPeakPolylines(LaneAnalysisResult r)
        {
            var mask = r.LaneLineMaskRoi8u;
            if (mask == null || mask.Empty()) return new List<Point[]>();

            var drive = r.DriveMaskRoi8u;
            var ys = r.SampleYs;
            if (ys == null || ys.Length < 2) return new List<Point[]>();

            // y desc로 내려오면서 클러스터 트래킹
            var ysDesc = ys.ToArray();
            Array.Sort(ysDesc);
            Array.Reverse(ysDesc);

            var clusters = new List<List<(int y, int x)>>();

            foreach (var y in ysDesc)
            {
                int leftX = 0;
                int rightX = mask.Width - 1;

                if (drive != null && !drive.Empty())
                {
                    GetCorridorLRAtRow(drive, y, out leftX, out rightX);
                    if (rightX - leftX < mask.Width * 0.35)
                    {
                        leftX = 0;
                        rightX = mask.Width - 1;
                    }
                }

                var peaks = FindPeaksAtRow(mask, y, leftX, rightX);

                foreach (var px in peaks)
                {
                    // 가장 가까운 클러스터(마지막 점 x 기준)로 붙이기
                    int bestC = -1;
                    int bestDx = int.MaxValue;

                    for (int ci = 0; ci < clusters.Count; ci++)
                    {
                        var last = clusters[ci][^1];
                        int dx = Math.Abs(last.x - px);
                        if (dx <= PeakPolylineFollowX && dx < bestDx)
                        {
                            bestDx = dx;
                            bestC = ci;
                        }
                    }

                    if (bestC >= 0)
                        clusters[bestC].Add((y, px));
                    else
                        clusters.Add(new List<(int y, int x)> { (y, px) });
                }
            }

            // 길이(연속성) 부족한 건 버리고 polyline 변환
            var lines = new List<Point[]>();

            foreach (var c in clusters)
            {
                if (c.Count < PeakPolylineMinLen) continue;

                // y 오름차순으로 정렬해서 선 그리기
                var pts = c.OrderBy(p => p.y)
                           .Select(p => new Point(r.Roi.X + p.x, r.Roi.Y + p.y))
                           .ToArray();

                // 너무 짧은 경우 제외(안전)
                if (pts.Length >= 2)
                    lines.Add(pts);
            }

            return lines;
        }

        // =========================
        // Draw
        // =========================
        public void DrawOnFrame(Mat frame, LaneAnalysisResult r)
        {
            if (frame == null || frame.Empty() || r == null) return;

            Cv2.Rectangle(frame, r.Roi, Scalar.White, 1);

            // ✅ 1) "빨간점 기반 흰색 polyline"만 그리고 싶다 (요구사항)
            if (DebugDrawPeakPolylines)
            {
                var polylines = BuildPeakPolylines(r);
                foreach (var pl in polylines)
                    Cv2.Polylines(frame, new[] { pl }, false, Scalar.White, 2, LineTypes.AntiAlias);
            }

            // ✅ 2) 필요하면 peak 점(빨간점)도 같이 표시
            if (DebugDrawLanePeaksDots)
            {
                DrawLanePeaksDots(frame, r);
            }

            // (옵션) 기존 강제 boundary 라인
            if (!DebugDrawBoundaries) return;

            int[] ys = r.SampleYs;
            if (ys == null || ys.Length < 2) return;

            for (int bi = 0; bi < r.BoundaryLines.Count; bi++)
            {
                var (a, b) = r.BoundaryLines[bi];

                var pts = new List<Point>(ys.Length);
                foreach (var y in ys)
                {
                    double x = a * y + b;
                    int xI = (int)Math.Round(x);
                    xI = Math.Clamp(xI, 0, r.Roi.Width - 1);
                    pts.Add(new Point(r.Roi.X + xI, r.Roi.Y + y));
                }

                Cv2.Polylines(frame, new[] { pts.ToArray() }, false, Scalar.White, 2);
            }

            if (!DebugDrawLabels) return;

            int yLabelRoi = (int)(r.Roi.Height * 0.92);
            yLabelRoi = Math.Clamp(yLabelRoi, 0, r.Roi.Height - 1);

            for (int lane = 1; lane <= TotalLanes; lane++)
            {
                double xL = EvalX(r.BoundaryLines[lane - 1], yLabelRoi);
                double xR = EvalX(r.BoundaryLines[lane], yLabelRoi);
                int cx = (int)Math.Round((xL + xR) * 0.5);
                cx = Math.Clamp(cx, 0, r.Roi.Width - 1);

                var pt = new Point(r.Roi.X + cx, r.Roi.Y + yLabelRoi);
                var color = (lane == EgoLane) ? Scalar.Yellow : Scalar.White;
                Cv2.PutText(frame, $"#{lane}", pt, HersheyFonts.HersheySimplex, 0.9, color, 2);
            }
        }

        private void DrawLanePeaksDots(Mat frame, LaneAnalysisResult r)
        {
            var laneMask8u = r.LaneLineMaskRoi8u;
            if (laneMask8u == null || laneMask8u.Empty()) return;

            Mat? drive = r.DriveMaskRoi8u;

            foreach (var y in r.SampleYs)
            {
                int leftX = 0;
                int rightX = laneMask8u.Width - 1;

                if (drive != null && !drive.Empty())
                {
                    GetCorridorLRAtRow(drive, y, out leftX, out rightX);
                    if (rightX - leftX < laneMask8u.Width * 0.35)
                    {
                        leftX = 0;
                        rightX = laneMask8u.Width - 1;
                    }
                }

                var peaks = FindPeaksAtRow(laneMask8u, y, leftX, rightX);
                foreach (var px in peaks)
                {
                    var pt = new Point(r.Roi.X + px, r.Roi.Y + y);
                    Cv2.Circle(frame, pt, 3, new Scalar(0, 0, 255), -1, LineTypes.AntiAlias);
                }
            }
        }

        // =========================
        // Helpers
        // =========================
        private static int MakeOdd(int k) => (k <= 0) ? 0 : (k % 2 == 1 ? k : k + 1);

        private Mat BuildLaneLineMaskFast(Mat laneProbRoi32f)
        {
            using var prob8u = new Mat();
            laneProbRoi32f.ConvertTo(prob8u, MatType.CV_8UC1, 255.0);

            using var bin = new Mat();
            double thr = 255.0 * Math.Clamp(LaneProbThreshold, 0.05f, 0.95f);
            Cv2.Threshold(prob8u, bin, thr, 255, ThresholdTypes.Binary);

            int ok = MakeOdd(LaneMaskOpenK);
            if (ok >= 3)
            {
                using var kOpen = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(ok, ok));
                Cv2.MorphologyEx(bin, bin, MorphTypes.Open, kOpen);
            }

            int ck = MakeOdd(LaneMaskCloseK);
            if (ck >= 3)
            {
                using var kClose = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(ck, ck));
                Cv2.MorphologyEx(bin, bin, MorphTypes.Close, kClose);
            }

            return bin.Clone();
        }

        private int[] BuildSampleYs(int roiH)
        {
            int n = Math.Clamp(SampleBandCount, 10, 30);

            int yTop = (int)(roiH * SampleYTopRatio);
            int yBot = (int)(roiH * SampleYBottomRatio);

            yTop = Math.Clamp(yTop, 0, roiH - 1);
            yBot = Math.Clamp(yBot, yTop + 5, roiH - 1);

            var ys = new int[n];
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / (n - 1);
                double tt = Math.Pow(t, 1.30); // 아래쪽 촘촘
                ys[i] = (int)Math.Round(yBot - (yBot - yTop) * tt);
            }

            ys = ys.Distinct().ToArray();
            Array.Sort(ys);
            return ys;
        }

        private void GetCorridorLRAtRow(Mat driveRoi8u, int y, out int leftX, out int rightX)
        {
            leftX = 0;
            rightX = driveRoi8u.Width - 1;

            var idx = driveRoi8u.GetGenericIndexer<byte>();
            int w = driveRoi8u.Width;

            int l = -1, r = -1;
            for (int x = 0; x < w; x++)
            {
                if (idx[y, x] != 0) { l = x; break; }
            }
            for (int x = w - 1; x >= 0; x--)
            {
                if (idx[y, x] != 0) { r = x; break; }
            }

            if (l >= 0 && r >= 0 && r > l)
            {
                leftX = l;
                rightX = r;
            }
        }

        private List<int> FindPeaksAtRow(Mat laneMask8u, int y, int leftX, int rightX)
        {
            var idx = laneMask8u.GetGenericIndexer<byte>();

            leftX = Math.Clamp(leftX, 0, laneMask8u.Width - 1);
            rightX = Math.Clamp(rightX, 0, laneMask8u.Width - 1);
            if (rightX <= leftX) return new List<int>();

            var peaks = new List<int>(8);

            int runStart = -1;
            int runCount = 0;

            for (int x = leftX; x <= rightX; x++)
            {
                bool on = idx[y, x] != 0;

                if (on)
                {
                    if (runStart < 0) { runStart = x; runCount = 1; }
                    else runCount++;
                }
                else
                {
                    if (runStart >= 0)
                    {
                        if (runCount >= PeakMinStrength)
                        {
                            int center = (runStart + (x - 1)) / 2;
                            peaks.Add(center);
                        }
                        runStart = -1;
                        runCount = 0;
                    }
                }
            }

            if (runStart >= 0 && runCount >= PeakMinStrength)
            {
                int center = (runStart + rightX) / 2;
                peaks.Add(center);
            }

            peaks.Sort();

            // merge near peaks
            var merged = new List<int>(peaks.Count);
            foreach (var p in peaks)
            {
                if (merged.Count == 0) merged.Add(p);
                else
                {
                    int last = merged[merged.Count - 1];
                    if (p - last < PeakMinGapPx)
                        merged[merged.Count - 1] = (last + p) / 2;
                    else
                        merged.Add(p);
                }
            }

            return merged;
        }

        private static double SelectPeakNearExpected(List<int> peaks, double expected, int windowPx, double fallback)
        {
            if (peaks == null || peaks.Count == 0) return fallback;

            double best = double.NaN;
            double bestDist = double.MaxValue;

            foreach (var p in peaks)
            {
                double d = Math.Abs(p - expected);
                if (d <= windowPx && d < bestDist)
                {
                    bestDist = d;
                    best = p;
                }
            }

            return double.IsNaN(best) ? fallback : best;
        }

        private static (double a, double b) FitLineXofY(List<Point2d> ptsYx)
        {
            if (ptsYx == null || ptsYx.Count < 2)
                return (0, ptsYx.Count == 1 ? ptsYx[0].Y : 0);

            double sumY = 0, sumX = 0, sumYY = 0, sumYX = 0;
            int n = ptsYx.Count;

            for (int i = 0; i < n; i++)
            {
                double y = ptsYx[i].X;
                double x = ptsYx[i].Y;
                sumY += y;
                sumX += x;
                sumYY += y * y;
                sumYX += y * x;
            }

            double denom = (n * sumYY - sumY * sumY);
            if (Math.Abs(denom) < 1e-6)
            {
                double avgX = sumX / n;
                return (0, avgX);
            }

            double a = (n * sumYX - sumY * sumX) / denom;
            double b = (sumX - a * sumY) / n;
            return (a, b);
        }

        private static List<Point2d> FilterOutliersByMedianX(List<Point2d> ptsYx, double tolPx)
        {
            if (ptsYx == null || ptsYx.Count < 6) return ptsYx;

            var xs = ptsYx.Select(p => p.Y).OrderBy(v => v).ToArray();
            double med = xs[xs.Length / 2];

            return ptsYx.Where(p => Math.Abs(p.Y - med) <= tolPx).ToList();
        }

        private static double EvalX((double a, double b) line, int y)
            => line.a * y + line.b;
    }
}
