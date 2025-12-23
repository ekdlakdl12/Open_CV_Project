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

        private Mat? _drivableMaskOrig;

        public void SetDrivableMask(Mat? drivableMaskOrig)
        {
            _drivableMaskOrig?.Dispose();
            _drivableMaskOrig = drivableMaskOrig?.Clone();
        }

        public sealed class LaneAnalysisResult
        {
            public int Width;
            public int Height;
            public Rect Roi;
            public int[] SampleYs = Array.Empty<int>();
            public List<(double m, double b)> Boundaries = new();
            public List<Point[]> BoundaryPolylinesFrame = new();
            public List<Point[]> CandidatePolylinesFrame = new();
            public Point2d VanishingPointRoi;
            public Mat? LaneMaskRoi8u;
            public Mat? DriveMaskRoi8u;
        }

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
            var candidates = BuildCandidatePolylinesRoi(laneMask, driveRoi8u, sampleYs);
            var candLines = candidates
                .Select(pl => (poly: pl, line: FitLineFromPolyline(pl)))
                .Where(t => !double.IsNaN(t.line.m)).ToList();

            int yRef = (int)(roi.Height * 0.92);
            double centerX = roi.Width * 0.5;
            double laneW = Math.Max(40.0, roi.Width * 0.8 / total);

            double egoCenterBoundaryOffset = (ego - 0.5);
            double[] expectedX = new double[total + 1];
            for (int j = 0; j <= total; j++)
                expectedX[j] = Math.Clamp(centerX + (j - egoCenterBoundaryOffset) * laneW, 0, roi.Width - 1);

            var chosen = new (double m, double b)[total + 1];
            Point2d vp = new Point2d(centerX, -roi.Height * 0.35);

            for (int j = 0; j <= total; j++)
                chosen[j] = LineThrough(vp, new Point2d(expectedX[j], yRef));

            var result = new LaneAnalysisResult
            {
                Width = frameW,
                Height = frameH,
                Roi = roi,
                SampleYs = sampleYs,
                Boundaries = chosen.ToList(),
                BoundaryPolylinesFrame = BuildBoundaryPolylinesClipped(roi, sampleYs, chosen.ToList()),
                VanishingPointRoi = vp,
                LaneMaskRoi8u = laneMask.Clone(),
                DriveMaskRoi8u = driveRoi8u?.Clone()
            };
            driveRoi8u?.Dispose();
            return result;
        }

        // ✅ 추가된 핵심 메서드: 차량의 X 좌표를 기반으로 차선 번호 반환
        public int GetLaneNumber(float centerX, LaneAnalysisResult r)
        {
            if (r == null || r.Boundaries == null) return 0;
            // 차량 하단 지점을 기준으로 판별 (ROI 내부 좌표로 변환)
            Point testPoint = new Point((int)centerX, r.Roi.Y + (int)(r.Roi.Height * 0.9));
            if (TryGetLaneNumberForPoint(r, testPoint, out int laneNum)) return laneNum;
            return 0;
        }

        public bool TryGetLaneNumberForPoint(LaneAnalysisResult r, Point pFrame, out int laneNum)
        {
            laneNum = -1;
            if (r == null || r.Boundaries.Count == 0) return false;
            int xR = pFrame.X - r.Roi.X;
            int yR = pFrame.Y - r.Roi.Y;
            var xs = r.Boundaries.Select(ln => ln.m * yR + ln.b).ToArray();
            int idx = 0;
            while (idx < xs.Length && xR >= xs[idx]) idx++;
            laneNum = Math.Clamp(idx, 1, TotalLanes);
            return true;
        }

        public void DrawOnFrame(Mat frame, LaneAnalysisResult r)
        {
            if (frame == null || r == null) return;
            foreach (var pl in r.BoundaryPolylinesFrame)
                if (pl.Length >= 2) Cv2.Polylines(frame, new[] { pl }, false, Scalar.White, 2);

            for (int lane = 1; lane <= TotalLanes; lane++)
            {
                double xL = r.Boundaries[lane - 1].m * (r.Roi.Height * 0.9) + r.Boundaries[lane - 1].b;
                double xR = r.Boundaries[lane].m * (r.Roi.Height * 0.9) + r.Boundaries[lane].b;
                var pt = new Point(r.Roi.X + (xL + xR) / 2, r.Roi.Y + r.Roi.Height * 0.9);
                Cv2.PutText(frame, $"#{lane}", pt, HersheyFonts.HersheySimplex, 0.8, (lane == EgoLane) ? Scalar.Yellow : Scalar.White, 2);
            }
        }

        private Mat BuildLaneLineMaskFast(Mat prob)
        {
            using var prob8u = new Mat(); prob.ConvertTo(prob8u, MatType.CV_8UC1, 255.0);
            var bin = new Mat(); Cv2.Threshold(prob8u, bin, 255 * LaneProbThreshold, 255, ThresholdTypes.Binary);
            return bin;
        }

        private int[] BuildSampleYs(int h) => Enumerable.Range(0, SampleBandCount).Select(i => (int)(h * (0.3 + 0.6 * i / (SampleBandCount - 1)))).ToArray();

        private List<List<Point>> BuildCandidatePolylinesRoi(Mat mask, Mat? drive, int[] ys) => new List<List<Point>>();

        private (double m, double b) FitLineFromPolyline(List<Point> p) => (0, 0);

        private (double m, double b) LineThrough(Point2d vp, Point2d p)
        {
            double m = (p.X - vp.X) / (p.Y - vp.Y);
            return (m, vp.X - m * vp.Y);
        }

        private List<Point[]> BuildBoundaryPolylinesClipped(Rect roi, int[] ys, List<(double m, double b)> lines)
        {
            return lines.Select(ln => ys.Select(y => new Point(roi.X + (int)(ln.m * y + ln.b), roi.Y + y)).ToArray()).ToList();
        }

        private static int MakeOdd(int k) => k % 2 == 1 ? k : k + 1;
    }
}