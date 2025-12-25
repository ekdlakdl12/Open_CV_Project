// LaneAnalyzer.cs (API-compatible with Merge_Car_DBlogic_Test)
// - Keep current project signatures:
//   Analyze(Mat laneProb, Size frameSize) -> bool
//   DrawOnFrame(Mat frame) -> void
//   TryGetLaneNumberForPoint(Point p) -> int
// - Internals: use VANISHING-POINT perspective boundaries (Merge_Carmodel_Lane_Test style)

using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;

namespace WpfApp1.Scripts
{
    public sealed class LaneAnalyzer
    {
        // =========================
        // Public tuning params (keep)
        // =========================
        public int TotalLanes { get; set; } = 5;   // 2..8
        public int EgoLane { get; set; } = 3;      // 1..TotalLanes

        // ROI
        public float RoiYStartRatio { get; set; } = 0.52f;
        public float RoiXMarginRatio { get; set; } = 0.02f;

        public float LaneProbThreshold { get; set; } = 0.45f;

        // drivable gate
        public bool UseDrivableGate { get; set; } = true;
        public int GateErodeK { get; set; } = 9;

        // lane mask morph
        public int LaneMaskOpenK { get; set; } = 3;
        public int LaneMaskCloseK { get; set; } = 5;

        // sampling / peak
        public int SampleBandCount { get; set; } = 18;
        public float SampleYTopRatio { get; set; } = 0.30f;
        public float SampleYBottomRatio { get; set; } = 0.95f;

        public int PeakMinStrength { get; set; } = 6;
        public int PeakMinGapPx { get; set; } = 18;

        public int ExpectedWindowPx { get; set; } = 90;
        public int FollowWindowPx { get; set; } = 75;

        // Debug
        public bool DebugDrawPeakDots { get; set; } = false;
        public bool DebugDrawCandidatePolylines { get; set; } = false;
        public bool DebugDrawVanishingPoint { get; set; } = false;

        // =========================
        // Exposed (keep current-style)
        // =========================
        public IReadOnlyList<Point[]> LaneBoundaries => _laneBoundaries;
        private readonly List<Point[]> _laneBoundaries = new();

        // =========================
        // Internal masks / state
        // =========================
        private Mat? _drivableMaskOrig; // expected 0/255 (same size as frame)
        private LaneAnalysisResult? _last;

        public void SetDrivableMask(Mat? drivableMaskOrig)
        {
            _drivableMaskOrig?.Dispose();
            _drivableMaskOrig = drivableMaskOrig?.Clone();
        }

        // =========================
        // API-compatible Analyze
        // =========================
        public bool Analyze(Mat laneProb, Size frameSize)
        {
            if (laneProb == null || laneProb.Empty()) return false;
            if (frameSize.Width <= 0 || frameSize.Height <= 0) return false;

            Mat lp = laneProb;
            Mat? resized = null;

            // ensure laneProb matches frame size
            if (laneProb.Width != frameSize.Width || laneProb.Height != frameSize.Height)
            {
                resized = new Mat();
                Cv2.Resize(laneProb, resized, frameSize, 0, 0, InterpolationFlags.Linear);
                lp = resized;
            }

            try
            {
                // ensure float32 1ch
                using var lp32 = new Mat();
                if (lp.Type() == MatType.CV_32FC1 || lp.Type() == MatType.CV_32F)
                    lp.CopyTo(lp32);
                else if (lp.Type() == MatType.CV_8UC1)
                    lp.ConvertTo(lp32, MatType.CV_32FC1, 1.0 / 255.0);
                else
                {
                    using var gray = new Mat();
                    Cv2.CvtColor(lp, gray, ColorConversionCodes.BGR2GRAY);
                    gray.ConvertTo(lp32, MatType.CV_32FC1, 1.0 / 255.0);
                }

                _last?.DisposeMats();
                _last = AnalyzeCore(lp32, frameSize.Width, frameSize.Height);

                // keep LaneBoundaries list (frame coords) for any other code
                _laneBoundaries.Clear();
                if (_last.BoundaryPolylinesFrame != null)
                    _laneBoundaries.AddRange(_last.BoundaryPolylinesFrame);

                return _last.Boundaries != null && _last.Boundaries.Count == Math.Clamp(TotalLanes, 2, 8) + 1;
            }
            catch
            {
                _last = null;
                _laneBoundaries.Clear();
                return false;
            }
            finally
            {
                resized?.Dispose();
            }
        }

        // =========================
        // API-compatible Draw
        // =========================
        public void DrawOnFrame(Mat frame)
        {
            if (frame == null || frame.Empty()) return;
            if (_last == null) return;

            var r = _last;

            Cv2.Rectangle(frame, r.Roi, Scalar.White, 1);

            if (DebugDrawCandidatePolylines && r.CandidatePolylinesFrame != null)
            {
                foreach (var pl in r.CandidatePolylinesFrame)
                    if (pl != null && pl.Length >= 2)
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

            if (r.BoundaryPolylinesFrame != null)
            {
                foreach (var pl in r.BoundaryPolylinesFrame)
                {
                    if (pl == null || pl.Length < 2) continue;
                    Cv2.Polylines(frame, new[] { pl }, false, Scalar.White, 2, LineTypes.AntiAlias);
                }
            }

            // lane labels
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
        // API-compatible point->lane
        // =========================
        public int TryGetLaneNumberForPoint(Point pFrame)
        {
            if (_last == null) return -1;

            var r = _last;
            int total = Math.Clamp(TotalLanes, 2, 8);
            if (r.Boundaries == null || r.Boundaries.Count != total + 1) return -1;
            if (!r.Roi.Contains(pFrame)) return -1;

            int xR = pFrame.X - r.Roi.X;
            int yR = pFrame.Y - r.Roi.Y;

            var xs = new double[total + 1];
            for (int i = 0; i <= total; i++)
            {
                var ln = r.Boundaries[i];
                xs[i] = ln.m * yR + ln.b;
            }

            // monotonic safeguard
            for (int i = 1; i < xs.Length; i++)
                if (xs[i] < xs[i - 1] + 2) xs[i] = xs[i - 1] + 2;

            int idx = 0;
            while (idx < xs.Length && xR >= xs[idx]) idx++;

            return Math.Clamp(idx, 1, total);
        }

        // =========================
        // Core result (internal)
        // =========================
        private sealed class LaneAnalysisResult
        {
            public Rect Roi;
            public int[] SampleYs = Array.Empty<int>();

            // boundary lines in ROI coords: x = m*y + b  (count=TotalLanes+1)
            public List<(double m, double b)> Boundaries = new();

            // drawing
            public List<Point[]> BoundaryPolylinesFrame = new();

            // debug
            public List<Point[]> CandidatePolylinesFrame = new();
            public Point2d VanishingPointRoi;

            public Mat? LaneMaskRoi8u;
            public Mat? DriveMaskRoi8u;

            public void DisposeMats()
            {
                LaneMaskRoi8u?.Dispose(); LaneMaskRoi8u = null;
                DriveMaskRoi8u?.Dispose(); DriveMaskRoi8u = null;
            }
        }

        // =========================
        // AnalyzeCore: Merge_Carmodel_Lane_Test style
        // =========================
        private LaneAnalysisResult AnalyzeCore(Mat laneProbOrig32f, int frameW, int frameH)
        {
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
            if (UseDrivableGate && _drivableMaskOrig != null && !_drivableMaskOrig.Empty()
                && _drivableMaskOrig.Width == frameW && _drivableMaskOrig.Height == frameH)
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

            // candidates polylines (ROI)
            var candidates = BuildCandidatePolylinesRoi(laneMask, driveRoi8u, sampleYs);

            // candidate lines x = m*y + b
            var candLines = candidates
                .Select(pl => (poly: pl, line: FitLineFromPolyline(pl)))
                .Where(t => !double.IsNaN(t.line.m))
                .ToList();

            // yRef: near bottom
            int yRef = (int)(roi.Height * 0.92);
            yRef = Math.Clamp(yRef, 0, roi.Height - 1);

            // corridor (drivable) for laneW
            GetCorridorLRAtRow(driveRoi8u, yRef, roi.Width, out int corL, out int corR);
            if (corR - corL < roi.Width * 0.35)
            {
                corL = 0;
                corR = roi.Width - 1;
            }

            double laneW = (corR - corL) / (double)total;
            laneW = Math.Max(laneW, 40.0);

            // Ego assumed centered in ROI
            double centerX = roi.Width * 0.5;

            int egoLeftIdx = ego - 1;
            int egoRightIdx = ego;

            double egoCenterBoundaryOffset = (ego - 0.5);
            double[] expectedX = new double[total + 1];
            for (int j = 0; j <= total; j++)
            {
                expectedX[j] = centerX + (j - egoCenterBoundaryOffset) * laneW;
                expectedX[j] = Math.Clamp(expectedX[j], 0, roi.Width - 1);
            }

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
                    if (dx < bestDx) { bestDx = dx; best = i; }
                }

                if (best >= 0)
                {
                    double xBest = candLines[best].line.m * yRef + candLines[best].line.b;
                    if (Math.Abs(xBest - ex) <= snapWin) return best;
                }
                return -1;
            }

            (double m, double b)? egoL = null;
            (double m, double b)? egoR = null;

            int cL = PickNearestCandidate(expectedX[egoLeftIdx]);
            if (cL >= 0) { used.Add(cL); egoL = candLines[cL].line; }

            int cR = PickNearestCandidate(expectedX[egoRightIdx]);
            if (cR >= 0) { used.Add(cR); egoR = candLines[cR].line; }

            // vanishing point (ROI coords)
            Point2d vp = new Point2d(centerX, -roi.Height * 0.35);

            if (egoL.HasValue && egoR.HasValue)
            {
                if (TryIntersect(egoL.Value, egoR.Value, out var inter))
                {
                    // keep VP above ROI and not too crazy
                    if (inter.Y < roi.Height * 0.2 && Math.Abs(inter.X - centerX) < roi.Width * 0.7)
                        vp = inter;
                }
            }

            // build ideal boundary lines through VP and expected x at yRef
            var boundaries = new List<(double m, double b)>(total + 1);

            for (int j = 0; j <= total; j++)
            {
                double xRef = expectedX[j];

                // line passing through (xRef, yRef) and VP
                // x = m*y + b
                double dy = (yRef - vp.Y);
                if (Math.Abs(dy) < 1e-6) dy = 1e-6;

                double m = (xRef - vp.X) / dy;
                double b = xRef - m * yRef;

                boundaries.Add((m, b));
            }

            // optional snap each boundary to closest candidate if near at yRef
            int followWin = Math.Max(25, FollowWindowPx);
            for (int j = 0; j < boundaries.Count; j++)
            {
                // already picked ego boundaries? prioritize keeping them
                bool isEgoB = (j == egoLeftIdx && egoL.HasValue) || (j == egoRightIdx && egoR.HasValue);
                if (isEgoB) continue;

                double ex = boundaries[j].m * yRef + boundaries[j].b;

                int best = -1;
                double bestDx = double.MaxValue;
                for (int i = 0; i < candLines.Count; i++)
                {
                    double x = candLines[i].line.m * yRef + candLines[i].line.b;
                    double dx = Math.Abs(x - ex);
                    if (dx < bestDx) { bestDx = dx; best = i; }
                }

                if (best >= 0 && bestDx <= followWin)
                {
                    boundaries[j] = candLines[best].line;
                }
            }

            // inject ego boundaries if found
            if (egoL.HasValue) boundaries[egoLeftIdx] = egoL.Value;
            if (egoR.HasValue) boundaries[egoRightIdx] = egoR.Value;

            // enforce monotonic at yRef
            var xsRef = boundaries.Select(b => b.m * yRef + b.b).ToArray();
            for (int i = 1; i < xsRef.Length; i++)
            {
                if (xsRef[i] < xsRef[i - 1] + 6)
                {
                    double delta = (xsRef[i - 1] + 6) - xsRef[i];
                    // shift by adjusting intercept b
                    boundaries[i] = (boundaries[i].m, boundaries[i].b + delta);
                    xsRef[i] += delta;
                }
            }

            // build draw polylines (frame coords) with clipping at crossings
            var polylinesFrame = new List<Point[]>(total + 1);
            int yTop = (int)(roi.Height * Math.Clamp(SampleYTopRatio, 0.05f, 0.8f));
            int yBot = (int)(roi.Height * Math.Clamp(SampleYBottomRatio, 0.6f, 0.99f));
            yTop = Math.Clamp(yTop, 0, roi.Height - 1);
            yBot = Math.Clamp(yBot, 0, roi.Height - 1);
            if (yBot <= yTop) { yTop = 0; yBot = roi.Height - 1; }

            for (int j = 0; j <= total; j++)
            {
                var ln = boundaries[j];
                var pts = new List<Point>(SampleBandCount);

                for (int k = 0; k < SampleBandCount; k++)
                {
                    double t = (double)k / Math.Max(1, SampleBandCount - 1);
                    int y = (int)Math.Round(yBot + (yTop - yBot) * t);
                    y = Math.Clamp(y, 0, roi.Height - 1);

                    double x = ln.m * y + ln.b;
                    x = Math.Clamp(x, 0, roi.Width - 1);

                    pts.Add(new Point(roi.X + (int)Math.Round(x), roi.Y + y));
                }

                polylinesFrame.Add(pts.ToArray());
            }

            // pack result
            var res = new LaneAnalysisResult
            {
                Roi = roi,
                SampleYs = sampleYs,
                Boundaries = boundaries,
                BoundaryPolylinesFrame = polylinesFrame,
                CandidatePolylinesFrame = DebugDrawCandidatePolylines ? candidates.Select(pl => pl.Select(p => new Point(p.X + roi.X, p.Y + roi.Y)).ToArray()).ToList() : new List<Point[]>(),
                VanishingPointRoi = vp,
                LaneMaskRoi8u = laneMask.Clone(),
                DriveMaskRoi8u = driveRoi8u
            };

            return res;
        }

        // =========================
        // Build lane mask (fast)
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
                using var k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(ok, ok));
                Cv2.MorphologyEx(bin, bin, MorphTypes.Open, k);
            }

            int ck = MakeOdd(LaneMaskCloseK);
            if (ck >= 3)
            {
                using var k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(ck, ck));
                Cv2.MorphologyEx(bin, bin, MorphTypes.Close, k);
            }

            return bin.Clone();
        }

        private static int MakeOdd(int k)
        {
            if (k <= 0) return 0;
            return (k % 2 == 1) ? k : (k + 1);
        }

        // =========================
        // Sample Ys
        // =========================
        private int[] BuildSampleYs(int roiH)
        {
            int n = Math.Clamp(SampleBandCount, 8, 40);
            int yTop = (int)(roiH * Math.Clamp(SampleYTopRatio, 0.05f, 0.8f));
            int yBot = (int)(roiH * Math.Clamp(SampleYBottomRatio, 0.6f, 0.99f));
            yTop = Math.Clamp(yTop, 0, roiH - 1);
            yBot = Math.Clamp(yBot, 0, roiH - 1);
            if (yBot <= yTop) { yTop = 0; yBot = roiH - 1; }

            var ys = new int[n];
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / Math.Max(1, n - 1);
                ys[i] = (int)Math.Round(yBot + (yTop - yBot) * t);
                ys[i] = Math.Clamp(ys[i], 0, roiH - 1);
            }
            return ys;
        }

        // =========================
        // Candidate polylines
        // =========================
        private List<List<Point>> BuildCandidatePolylinesRoi(Mat laneMask8u, Mat? driveMask8u, int[] sampleYs)
        {
            int w = laneMask8u.Width;
            int h = laneMask8u.Height;

            var polylines = new List<List<Point>>();

            // For each y, find peaks in column sums in a band around y
            foreach (int y in sampleYs)
            {
                int bandH = Math.Clamp(h / 18, 10, 28);
                int y0 = Math.Clamp(y - bandH / 2, 0, h - 1);
                int y1 = Math.Clamp(y0 + bandH, 0, h);
                if (y1 <= y0 + 1) continue;

                using var band = new Mat(laneMask8u, new Rect(0, y0, w, y1 - y0));

                int[] hist = ReduceColumnSumToInt(band);

                // optional corridor from drive
                int left = 0, right = w - 1;
                if (driveMask8u != null && !driveMask8u.Empty())
                {
                    using var driveBand = new Mat(driveMask8u, new Rect(0, y0, w, y1 - y0));
                    int[] d = ReduceColumnSumToInt(driveBand);
                    GetCorridorFromHist(d, y1 - y0, out left, out right);
                }

                var peaks = FindPeaks(hist, left, right, maxPeaks: 10, minDistance: Math.Max(10, PeakMinGapPx));

                // store as single-point "polyline candidates"; later we stitch by x proximity
                foreach (int px in peaks)
                {
                    polylines.Add(new List<Point> { new Point(px, y) });
                }
            }

            // Stitch: group points by rough slope continuity (simple nearest by y order)
            // We do a lightweight stitch: sort by y descending (bottom->top), then chain by nearest x
            var groups = new List<List<Point>>();
            var pts = polylines.SelectMany(g => g).ToList();
            pts.Sort((a, b) => b.Y.CompareTo(a.Y)); // bottom first

            foreach (var p in pts)
            {
                List<Point>? best = null;
                int bestDx = int.MaxValue;

                foreach (var g in groups)
                {
                    var last = g[g.Count - 1];
                    // allow only if going upward
                    if (p.Y >= last.Y) continue;

                    int dx = Math.Abs(p.X - last.X);
                    if (dx < bestDx && dx <= Math.Max(25, FollowWindowPx))
                    {
                        bestDx = dx;
                        best = g;
                    }
                }

                if (best == null) groups.Add(new List<Point> { p });
                else best.Add(p);
            }

            // filter short
            groups = groups.Where(g => g.Count >= Math.Max(6, SampleBandCount / 3)).ToList();
            // ensure each is bottom->top order
            foreach (var g in groups) g.Sort((a, b) => b.Y.CompareTo(a.Y));

            return groups;
        }

        private static int[] ReduceColumnSumToInt(Mat m8u)
        {
            int w = m8u.Width;
            int h = m8u.Height;
            var sum = new int[w];

            unsafe
            {
                for (int y = 0; y < h; y++)
                {
                    byte* row = (byte*)m8u.Ptr(y);
                    for (int x = 0; x < w; x++)
                        sum[x] += (row[x] > 0) ? 1 : 0;
                }
            }
            return sum;
        }

        private static void GetCorridorFromHist(int[] hist, int bandH, out int left, out int right)
        {
            int w = hist.Length;
            int thr = Math.Max(3, bandH / 20);

            left = 0;
            while (left < w && hist[left] < thr) left++;

            right = w - 1;
            while (right >= 0 && hist[right] < thr) right--;

            if (right - left < w * 0.25) { left = 0; right = w - 1; }
        }

        private static void GetCorridorLRAtRow(Mat? driveMaskRoi8u, int yRef, int w, out int left, out int right)
        {
            left = 0; right = w - 1;
            if (driveMaskRoi8u == null || driveMaskRoi8u.Empty()) return;

            yRef = Math.Clamp(yRef, 0, driveMaskRoi8u.Height - 1);
            unsafe
            {
                byte* row = (byte*)driveMaskRoi8u.Ptr(yRef);
                int l = 0;
                while (l < w && row[l] == 0) l++;
                int r = w - 1;
                while (r >= 0 && row[r] == 0) r--;

                if (r - l >= w * 0.25) { left = l; right = r; }
            }
        }

        private static int[] FindPeaks(int[] hist, int left, int right, int maxPeaks, int minDistance)
        {
            left = Math.Clamp(left, 0, hist.Length - 1);
            right = Math.Clamp(right, 0, hist.Length - 1);
            if (right <= left) return Array.Empty<int>();

            var peaks = new List<(int x, int v)>();

            for (int x = left + 1; x < right - 1; x++)
            {
                int v = hist[x];
                if (v <= 0) continue;
                if (v >= hist[x - 1] && v >= hist[x + 1])
                    peaks.Add((x, v));
            }

            // strong first
            peaks.Sort((a, b) => b.v.CompareTo(a.v));

            var picked = new List<int>();
            foreach (var p in peaks)
            {
                if (p.v < 1) continue;
                bool ok = true;
                foreach (var q in picked)
                {
                    if (Math.Abs(q - p.x) < minDistance) { ok = false; break; }
                }
                if (!ok) continue;
                picked.Add(p.x);
                if (picked.Count >= maxPeaks) break;
            }

            picked.Sort();
            return picked.ToArray();
        }

        private static (double m, double b) FitLineFromPolyline(List<Point> poly)
        {
            if (poly == null || poly.Count < 2) return (double.NaN, double.NaN);

            // linear regression x = m*y + b
            double sumY = 0, sumX = 0, sumYY = 0, sumYX = 0;
            int n = poly.Count;

            for (int i = 0; i < n; i++)
            {
                double y = poly[i].Y;
                double x = poly[i].X;
                sumY += y;
                sumX += x;
                sumYY += y * y;
                sumYX += y * x;
            }

            double denom = (n * sumYY - sumY * sumY);
            if (Math.Abs(denom) < 1e-6) return (double.NaN, double.NaN);

            double m = (n * sumYX - sumY * sumX) / denom;
            double b = (sumX - m * sumY) / n;
            return (m, b);
        }

        private static bool TryIntersect((double m, double b) L, (double m, double b) R, out Point2d inter)
        {
            // x = m*y + b
            // m1*y + b1 = m2*y + b2 => y = (b2-b1)/(m1-m2)
            inter = default;
            double den = (L.m - R.m);
            if (Math.Abs(den) < 1e-6) return false;

            double y = (R.b - L.b) / den;
            double x = L.m * y + L.b;
            inter = new Point2d(x, y);
            return true;
        }
    }
}
