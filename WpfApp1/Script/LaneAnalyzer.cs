// LaneAnalyzer.cs (VANISHING-POINT PERSPECTIVE BOUNDARIES)
// - laneProb -> laneMask -> peak polylines tracking
// - choose Ego left/right from candidates near expected at yRef
// - compute vanishing point from Ego L/R intersection (fallback if not available)
// - generate other boundaries as lines passing through VP and expected x at yRef (perspective narrowing)
// - optionally snap each boundary to nearest candidate line if close
// - drawing: cut where boundaries cross (X) to avoid overlap
// - TryGetLaneNumberForPoint: compare x against boundary lines at given y
//
// Compatible with MainWindowViewModel properties.

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

        public float RoiYStartRatio { get; set; } = 0.52f;
        public float RoiXMarginRatio { get; set; } = 0.02f;

        public float LaneProbThreshold { get; set; } = 0.45f;

        public bool UseDrivableGate { get; set; } = true;
        public int GateErodeK { get; set; } = 9;

        public int LaneMaskOpenK { get; set; } = 3;
        public int LaneMaskCloseK { get; set; } = 5;

        public int CorridorBottomBandH { get; set; } = 60;

        public int SampleBandCount { get; set; } = 18;
        public float SampleYTopRatio { get; set; } = 0.30f;
        public float SampleYBottomRatio { get; set; } = 0.95f;

        public int PeakMinStrength { get; set; } = 6;
        public int PeakMinGapPx { get; set; } = 18;

        public int ExpectedWindowPx { get; set; } = 90;
        public int FollowWindowPx { get; set; } = 75;

        public bool PreferLaneProbForEgoBoundaries { get; set; } = true;

        // Debug
        public bool DebugDrawPeakDots { get; set; } = false;
        public bool DebugDrawCandidatePolylines { get; set; } = false;
        public bool DebugDrawVanishingPoint { get; set; } = false;

        // =========================
        // Drivable mask
        // =========================
        private Mat? _drivableMaskOrig;

        public void SetDrivableMask(Mat? drivableMaskOrig)
        {
            _drivableMaskOrig?.Dispose();
            _drivableMaskOrig = drivableMaskOrig?.Clone();
        }

        // =========================
        // Result
        // =========================
        public sealed class LaneAnalysisResult
        {
            public int Width;
            public int Height;
            public Rect Roi;
            public int[] SampleYs = Array.Empty<int>();

            // count = TotalLanes + 1, left->right
            // boundary line in ROI coords: x = m*y + b
            public List<(double m, double b)> Boundaries = new();

            // for drawing (frame coords), clipped for crossing
            public List<Point[]> BoundaryPolylinesFrame = new();

            // debug
            public List<Point[]> CandidatePolylinesFrame = new();
            public Point2d VanishingPointRoi;

            public Mat? LaneMaskRoi8u;
            public Mat? DriveMaskRoi8u;
        }

        // =========================
        // Analyze
        // =========================
        public LaneAnalysisResult Analyze(Mat laneProbOrig32f, int frameW, int frameH)
        {
            if (laneProbOrig32f == null || laneProbOrig32f.Empty())
                throw new ArgumentException("laneProbOrig32f is empty");

            int total = Math.Clamp(TotalLanes, 2, 8);
            int ego = Math.Clamp(EgoLane, 1, total);

            int y0 = (int)(frameH * RoiYStartRatio);
            y0 = Math.Clamp(y0, 0, frameH - 2);

            int xMargin = (int)(frameW * RoiXMarginRatio);
            xMargin = Math.Clamp(xMargin, 0, frameW / 4);

            var roi = new Rect(xMargin, y0, frameW - xMargin * 2, frameH - y0);
            if (roi.Width < 80 || roi.Height < 80)
                throw new Exception($"ROI too small: {roi}");

            using var laneProbRoi = new Mat(laneProbOrig32f, roi);
            using var laneMask = BuildLaneLineMaskFast(laneProbRoi);

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

                Cv2.BitwiseAnd(laneMask, driveRoi8u, laneMask);
            }

            var sampleYs = BuildSampleYs(roi.Height);

            // 후보 폴리라인(ROI)
            var candidates = BuildCandidatePolylinesRoi(laneMask, driveRoi8u, sampleYs);

            // 후보 라인(ROI): x = m*y + b
            var candLines = candidates
                .Select(pl => (poly: pl, line: FitLineFromPolyline(pl)))
                .Where(t => !double.IsNaN(t.line.m))
                .ToList();

            // yRef: 아래쪽 기준 줄(차선 위치 결정)
            int yRef = (int)(roi.Height * 0.92);
            yRef = Math.Clamp(yRef, 0, roi.Height - 1);

            // corridor로 laneW 추정
            GetCorridorLRAtRow(driveRoi8u, yRef, roi.Width, out int corL, out int corR);
            if (corR - corL < roi.Width * 0.35)
            {
                corL = 0;
                corR = roi.Width - 1;
            }

            double laneW = (corR - corL) / (double)total;
            laneW = Math.Max(laneW, 40.0);

            // Ego가 화면 중심이라고 가정(ROI 기준)
            double centerX = roi.Width * 0.5;

            // expected boundary x at yRef (Ego center anchored)
            int egoLeftIdx = ego - 1;
            int egoRightIdx = ego;

            double egoCenterBoundaryOffset = (ego - 0.5);
            double[] expectedX = new double[total + 1];
            for (int j = 0; j <= total; j++)
            {
                expectedX[j] = centerX + (j - egoCenterBoundaryOffset) * laneW;
                expectedX[j] = Math.Clamp(expectedX[j], 0, roi.Width - 1);
            }

            // 스냅 도우미
            int snapWin = Math.Max(40, ExpectedWindowPx);
            var used = new HashSet<int>();

            int PickNearestCandidate(double ex)
            {
                int best = -1;
                double bestDx = double.MaxValue;

                for (int i = 0; i < candLines.Count; i++)
                {
                    if (used.Contains(i)) continue;
                    double x = candLines[i].line.m * yRef + candLines[i].line.b;
                    double dx = Math.Abs(x - ex);
                    if (dx < bestDx)
                    {
                        bestDx = dx;
                        best = i;
                    }
                }

                if (best >= 0)
                {
                    double xBest = candLines[best].line.m * yRef + candLines[best].line.b;
                    if (Math.Abs(xBest - ex) <= snapWin) return best;
                }
                return -1;
            }

            // 1) Ego 좌/우 라인 선택(가능하면 후보에 스냅)
            (double m, double b)? egoL = null;
            (double m, double b)? egoR = null;

            int cL = PickNearestCandidate(expectedX[egoLeftIdx]);
            if (cL >= 0) { used.Add(cL); egoL = candLines[cL].line; }

            int cR = PickNearestCandidate(expectedX[egoRightIdx]);
            if (cR >= 0) { used.Add(cR); egoR = candLines[cR].line; }

            // 2) 소실점 계산(ROI 좌표)
            //    - Ego L/R 둘다 있으면 교점
            //    - 없으면 화면 상단(ROI 위쪽) 중앙 근처로 fallback
            Point2d vp = new Point2d(centerX, -roi.Height * 0.35);

            if (egoL.HasValue && egoR.HasValue)
            {
                if (TryIntersect(egoL.Value, egoR.Value, out var inter))
                {
                    // 너무 아래에서 만나면(비정상) 상단으로 보정
                    if (inter.Y > roi.Height * 0.6) inter.Y = roi.Height * 0.1;
                    vp = inter;
                }
            }
            else
            {
                // 후보 라인들이 있으면 "대략적" 소실점을 잡기 위해 평균 교점 시도(가벼운 방식)
                if (candLines.Count >= 2)
                {
                    var picks = candLines.Take(Math.Min(6, candLines.Count)).Select(x => x.line).ToList();
                    var inters = new List<Point2d>();
                    for (int i = 0; i < picks.Count; i++)
                        for (int j = i + 1; j < picks.Count; j++)
                        {
                            if (TryIntersect(picks[i], picks[j], out var p))
                            {
                                // 상단 쪽 교점만
                                if (p.Y < roi.Height * 0.4 && p.Y > -roi.Height * 2 && p.X > -roi.Width && p.X < roi.Width * 2)
                                    inters.Add(p);
                            }
                        }
                    if (inters.Count > 0)
                    {
                        vp = new Point2d(inters.Average(p => p.X), inters.Average(p => p.Y));
                    }
                }
            }

            // 3) 모든 경계를 “소실점 통과” 직선으로 생성 + 후보 스냅
            //    line through VP and (x=expectedX[j], y=yRef)
            var chosen = new (double m, double b)[total + 1];

            // ego boundaries: 후보가 있으면 우선 사용(원근을 더 잘 타는 경우가 많음)
            // (단, ego 후보가 너무 이상하면 VP line이 더 안정적일 수 있는데, 일단 우선 사용)
            for (int j = 0; j <= total; j++)
            {
                bool isEgoSide = (j == egoLeftIdx && egoL.HasValue) || (j == egoRightIdx && egoR.HasValue);
                if (isEgoSide)
                {
                    chosen[j] = (j == egoLeftIdx) ? egoL!.Value : egoR!.Value;
                    continue;
                }

                // 다른 경계는 candidate 스냅을 한번 더 시도(남은 후보)
                int ci = PickNearestCandidate(expectedX[j]);
                if (ci >= 0)
                {
                    used.Add(ci);
                    chosen[j] = candLines[ci].line;
                }
                else
                {
                    // VP 기반 생성
                    chosen[j] = LineThrough(vp, new Point2d(expectedX[j], yRef));
                }
            }

            // 4) left->right 정렬 보정(yRef에서 monotonic)
            double prev = double.NegativeInfinity;
            for (int j = 0; j <= total; j++)
            {
                var ln = chosen[j];
                double x = ln.m * yRef + ln.b;

                if (x <= prev + 5)
                {
                    // b만 조정해서 yRef에서 순서 유지
                    double delta = (prev + 5) - x;
                    ln.b += delta;
                    chosen[j] = ln;
                    x = ln.m * yRef + ln.b;
                }
                prev = x;
            }

            // 5) 폴리라인(그림) 생성 + 교차 시작하면 컷
            var boundaryLines = chosen.ToList();
            var boundaryPolysFrame = BuildBoundaryPolylinesClipped(roi, sampleYs, boundaryLines);

            // Debug candidates (frame coords)
            var candPolysFrame = new List<Point[]>();
            foreach (var pl in candidates)
                candPolysFrame.Add(pl.Select(p => new Point(roi.X + p.X, roi.Y + p.Y)).ToArray());

            var result = new LaneAnalysisResult
            {
                Width = frameW,
                Height = frameH,
                Roi = roi,
                SampleYs = sampleYs,
                Boundaries = boundaryLines,
                BoundaryPolylinesFrame = boundaryPolysFrame,
                CandidatePolylinesFrame = candPolysFrame,
                VanishingPointRoi = vp,
                LaneMaskRoi8u = laneMask.Clone(),
                DriveMaskRoi8u = driveRoi8u?.Clone()
            };

            driveRoi8u?.Dispose();
            return result;
        }

        // =========================
        // Lane mapping
        // =========================
        public bool TryGetLaneNumberForPoint(LaneAnalysisResult r, Point pFrame, out int laneNum)
        {
            laneNum = -1;
            if (r == null) return false;

            int total = Math.Clamp(TotalLanes, 2, 8);
            if (r.Boundaries == null || r.Boundaries.Count != total + 1) return false;
            if (!r.Roi.Contains(pFrame)) return false;

            int xR = pFrame.X - r.Roi.X;
            int yR = pFrame.Y - r.Roi.Y;

            var xs = new double[total + 1];
            for (int i = 0; i <= total; i++)
            {
                var ln = r.Boundaries[i];
                xs[i] = ln.m * yR + ln.b;
            }

            for (int i = 1; i < xs.Length; i++)
                if (xs[i] < xs[i - 1] + 2) xs[i] = xs[i - 1] + 2;

            int idx = 0;
            while (idx < xs.Length && xR >= xs[idx]) idx++;

            laneNum = Math.Clamp(idx, 1, total);
            return true;
        }

        // =========================
        // Draw
        // =========================
        public void DrawOnFrame(Mat frame, LaneAnalysisResult r)
        {
            if (frame == null || frame.Empty() || r == null) return;

            Cv2.Rectangle(frame, r.Roi, Scalar.White, 1);

            if (DebugDrawPeakDots)
                DrawPeakDots(frame, r);

            if (DebugDrawCandidatePolylines && r.CandidatePolylinesFrame != null)
            {
                foreach (var pl in r.CandidatePolylinesFrame)
                    if (pl.Length >= 2)
                        Cv2.Polylines(frame, new[] { pl }, false, new Scalar(0, 0, 255), 1, LineTypes.AntiAlias);
            }

            if (DebugDrawVanishingPoint)
            {
                var vpF = new Point((int)Math.Round(r.Roi.X + r.VanishingPointRoi.X),
                                    (int)Math.Round(r.Roi.Y + r.VanishingPointRoi.Y));
                Cv2.Circle(frame, vpF, 5, new Scalar(255, 0, 255), -1, LineTypes.AntiAlias);
                Cv2.PutText(frame, "VP", new Point(vpF.X + 6, vpF.Y - 6),
                    HersheyFonts.HersheySimplex, 0.6, new Scalar(255, 0, 255), 2);
            }

            // final boundaries (clipped)
            if (r.BoundaryPolylinesFrame != null)
            {
                foreach (var pl in r.BoundaryPolylinesFrame)
                {
                    if (pl == null || pl.Length < 2) continue;
                    Cv2.Polylines(frame, new[] { pl }, false, Scalar.White, 2, LineTypes.AntiAlias);
                }
            }

            // lane labels (#1..#N)
            int total = Math.Clamp(TotalLanes, 2, 8);
            int yLabelR = (int)(r.Roi.Height * 0.92);
            yLabelR = Math.Clamp(yLabelR, 0, r.Roi.Height - 1);

            if (r.Boundaries != null && r.Boundaries.Count == total + 1)
            {
                for (int lane = 1; lane <= total; lane++)
                {
                    var L = r.Boundaries[lane - 1];
                    var R = r.Boundaries[lane];

                    double xL = L.m * yLabelR + L.b;
                    double xR = R.m * yLabelR + R.b;
                    int cx = (int)Math.Round((xL + xR) * 0.5);
                    cx = Math.Clamp(cx, 0, r.Roi.Width - 1);

                    var pt = new Point(r.Roi.X + cx, r.Roi.Y + yLabelR);
                    var color = (lane == EgoLane) ? Scalar.Yellow : Scalar.White;

                    Cv2.PutText(frame, $"#{lane}", pt, HersheyFonts.HersheySimplex, 0.9, color, 2, LineTypes.AntiAlias);
                }
            }
        }

        // =========================
        // Build lane mask
        // =========================
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
                double tt = Math.Pow(t, 1.30);
                ys[i] = (int)Math.Round(yBot - (yBot - yTop) * tt);
            }

            ys = ys.Distinct().ToArray();
            Array.Sort(ys);
            return ys;
        }

        // =========================
        // Candidate polylines
        // =========================
        private List<List<Point>> BuildCandidatePolylinesRoi(Mat laneMask8u, Mat? driveRoi8u, int[] sampleYs)
        {
            var ysDesc = sampleYs.ToArray();
            Array.Sort(ysDesc);
            Array.Reverse(ysDesc);

            var clusters = new List<List<(int y, int x)>>();

            foreach (var y in ysDesc)
            {
                GetCorridorLRAtRow(driveRoi8u, y, laneMask8u.Width, out int leftX, out int rightX);
                if (rightX - leftX < laneMask8u.Width * 0.35)
                {
                    leftX = 0;
                    rightX = laneMask8u.Width - 1;
                }

                var peaks = FindPeaksAtRow(laneMask8u, y, leftX, rightX);

                foreach (var px in peaks)
                {
                    int bestC = -1;
                    int bestDx = int.MaxValue;

                    for (int ci = 0; ci < clusters.Count; ci++)
                    {
                        var last = clusters[ci][^1];
                        int dx = Math.Abs(last.x - px);
                        if (dx <= Math.Max(20, FollowWindowPx) && dx < bestDx)
                        {
                            bestDx = dx;
                            bestC = ci;
                        }
                    }

                    if (bestC >= 0) clusters[bestC].Add((y, px));
                    else clusters.Add(new List<(int y, int x)> { (y, px) });
                }
            }

            var polylines = new List<List<Point>>();
            foreach (var c in clusters)
            {
                if (c.Count < 6) continue;
                var pts = c.OrderBy(p => p.y).Select(p => new Point(p.x, p.y)).ToList();
                polylines.Add(pts);
            }

            return polylines;
        }

        private (double m, double b) FitLineFromPolyline(List<Point> polyRoi)
        {
            if (polyRoi == null || polyRoi.Count < 2) return (double.NaN, double.NaN);

            double sumY = 0, sumX = 0, sumYY = 0, sumYX = 0;
            int n = polyRoi.Count;

            for (int i = 0; i < n; i++)
            {
                double y = polyRoi[i].Y;
                double x = polyRoi[i].X;

                sumY += y;
                sumX += x;
                sumYY += y * y;
                sumYX += y * x;
            }

            double denom = (n * sumYY - sumY * sumY);
            if (Math.Abs(denom) < 1e-9)
            {
                double avgX = sumX / n;
                return (0.0, avgX);
            }

            double m = (n * sumYX - sumY * sumX) / denom;
            double b = (sumX - m * sumY) / n;

            m = Math.Clamp(m, -6.0, 6.0);
            return (m, b);
        }

        // =========================
        // Perspective line through VP and a point
        // =========================
        private (double m, double b) LineThrough(Point2d vp, Point2d p)
        {
            // x = m*y + b
            double dy = (p.Y - vp.Y);
            if (Math.Abs(dy) < 1e-6)
            {
                // 거의 수평이면 큰 기울기 방지: 약간 아래로 보정
                dy = (dy >= 0) ? 1.0 : -1.0;
            }

            double m = (p.X - vp.X) / dy;
            double b = vp.X - m * vp.Y;

            m = Math.Clamp(m, -6.0, 6.0);
            return (m, b);
        }

        private bool TryIntersect((double m, double b) a, (double m, double b) c, out Point2d p)
        {
            // a: x = m1*y + b1
            // c: x = m2*y + b2
            // => (m1-m2)*y = (b2-b1)
            double dm = a.m - c.m;
            if (Math.Abs(dm) < 1e-6)
            {
                p = default;
                return false;
            }

            double y = (c.b - a.b) / dm;
            double x = a.m * y + a.b;
            p = new Point2d(x, y);
            return true;
        }

        // =========================
        // Build boundary polylines with crossing cut
        // =========================
        private List<Point[]> BuildBoundaryPolylinesClipped(Rect roi, int[] sampleYs, List<(double m, double b)> lines)
        {
            int count = lines.Count;
            var outPolys = new List<Point[]>(count);

            var ys = sampleYs.ToArray();
            Array.Sort(ys);
            Array.Reverse(ys); // bottom->top in ROI coords

            double[,] xAt = new double[count, ys.Length];
            for (int b = 0; b < count; b++)
            {
                for (int k = 0; k < ys.Length; k++)
                {
                    double y = ys[k];
                    double x = lines[b].m * y + lines[b].b;
                    xAt[b, k] = x;
                }
            }

            int[] validUntil = Enumerable.Repeat(ys.Length - 1, count).ToArray();

            for (int k = 0; k < ys.Length; k++)
            {
                for (int b = 1; b < count; b++)
                {
                    if (xAt[b, k] <= xAt[b - 1, k] + 2)
                    {
                        for (int bb = 0; bb < count; bb++)
                            validUntil[bb] = Math.Min(validUntil[bb], Math.Max(0, k - 1));
                        break;
                    }
                }
            }

            for (int b = 0; b < count; b++)
            {
                int end = validUntil[b];
                if (end < 1)
                {
                    outPolys.Add(Array.Empty<Point>());
                    continue;
                }

                var pts = new List<Point>(end + 1);
                for (int k = 0; k <= end; k++)
                {
                    int yR = ys[k];
                    int xR = (int)Math.Round(xAt[b, k]);

                    if (xR < 0 || xR >= roi.Width) continue;
                    pts.Add(new Point(roi.X + xR, roi.Y + yR));
                }

                outPolys.Add(pts.ToArray());
            }

            return outPolys;
        }

        // =========================
        // Debug peak dots
        // =========================
        private void DrawPeakDots(Mat frame, LaneAnalysisResult r)
        {
            var laneMask8u = r.LaneMaskRoi8u;
            if (laneMask8u == null || laneMask8u.Empty()) return;

            var drive = r.DriveMaskRoi8u;

            foreach (var y in r.SampleYs)
            {
                GetCorridorLRAtRow(drive, y, laneMask8u.Width, out int leftX, out int rightX);
                if (rightX - leftX < laneMask8u.Width * 0.35)
                {
                    leftX = 0;
                    rightX = laneMask8u.Width - 1;
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
        // Corridor LR
        // =========================
        private void GetCorridorLRAtRow(Mat? driveRoi8u, int y, int width, out int leftX, out int rightX)
        {
            leftX = 0;
            rightX = width - 1;

            if (driveRoi8u == null || driveRoi8u.Empty()) return;

            y = Math.Clamp(y, 0, driveRoi8u.Height - 1);

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

        // =========================
        // Peaks at row
        // =========================
        private List<int> FindPeaksAtRow(Mat laneMask8u, int y, int leftX, int rightX)
        {
            var idx = laneMask8u.GetGenericIndexer<byte>();

            leftX = Math.Clamp(leftX, 0, laneMask8u.Width - 1);
            rightX = Math.Clamp(rightX, 0, laneMask8u.Width - 1);
            if (rightX <= leftX) return new List<int>();

            var peaks = new List<int>(8);
            int runStart = -1;
            int runCount = 0;

            y = Math.Clamp(y, 0, laneMask8u.Height - 1);

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

            var merged = new List<int>(peaks.Count);
            foreach (var p in peaks)
            {
                if (merged.Count == 0) merged.Add(p);
                else
                {
                    int last = merged[^1];
                    if (p - last < PeakMinGapPx) merged[^1] = (last + p) / 2;
                    else merged.Add(p);
                }
            }

            return merged;
        }

        private static int MakeOdd(int k) => (k <= 0) ? 0 : (k % 2 == 1 ? k : k + 1);
    }
}
