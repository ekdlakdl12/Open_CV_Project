using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;

namespace WpfApp1.Scripts
{
    public sealed class LaneAnalyzer
    {
        // ===== UI Inject =====
        public int TotalLanes { get; set; } = 5; // 2..8
        public int EgoLane { get; set; } = 4;    // 1..TotalLanes

        // ===== ROI =====
        public float RoiYStartRatio { get; set; } = 0.52f;
        public float RoiXMarginRatio { get; set; } = 0.02f;

        // ===== Inputs =====
        private Mat? _driveMask; // CV_8UC1 0/255 original size
        public void SetDrivableMask(Mat? driveMask8u) => _driveMask = driveMask8u;

        // ===== LaneProb -> LaneLineMask =====
        public float LaneProbThreshold { get; set; } = 0.45f;     // 차선경계(빨강) threshold
        public bool UseDrivableGate { get; set; } = true;         // lane을 drive 안으로 제한
        public int GateErodeK { get; set; } = 9;                  // drive를 살짝 깎아서(갓길/벽 제거)
        public int LaneMaskOpenK { get; set; } = 3;               // 잡음 제거
        public int LaneMaskCloseK { get; set; } = 5;              // 끊긴 선 연결(너무 키우면 굵어짐)

        // ===== Road corridor (from drivable) =====
        public int CorridorBottomBandH { get; set; } = 60;
        public int CorridorSampleStepY { get; set; } = 6;
        public int CorridorSmoothWin { get; set; } = 5; // moving average

        // ===== Boundary extraction =====
        public int BoundarySearchRadius { get; set; } = 18;       // laneLine 근처 스냅 범위(px)
        public int BoundaryMinPeakStrength { get; set; } = 25;    // bottom band에서 laneLine column count 최소치

        // ===== Debug drawing =====
        public bool DrawDebug { get; set; } = true;
        public bool DrawLaneLines { get; set; } = true;
        public bool DrawCorridor { get; set; } = true;
        public bool DrawBoundaries { get; set; } = true;
        public bool DrawLaneLabels { get; set; } = true;

        public sealed class LaneAnalysisResult
        {
            public Rect Roi;
            public int EgoLane = -1;
            public int TotalLanes = -1;

            // ROI 좌표계에서 boundary X들의 리스트(항상 Count = TotalLanes+1)
            public List<float> BoundariesX_Roi = new();

            // Debug mats (ROI)
            public Mat? LaneLineMaskRoi; // CV_8UC1
            public Mat? DriveRoi;        // CV_8UC1
            public string DebugLine = "";
        }

        public LaneAnalysisResult Analyze(Mat laneProb32f, int frameW, int frameH)
        {
            if (laneProb32f == null || laneProb32f.Empty())
                throw new ArgumentException("laneProb32f is empty");
            if (laneProb32f.Type() != MatType.CV_32FC1)
                throw new ArgumentException($"laneProb32f must be CV_32FC1, got {laneProb32f.Type()}");

            TotalLanes = Math.Clamp(TotalLanes, 2, 8);
            EgoLane = Math.Clamp(EgoLane, 1, TotalLanes);

            var roi = BuildRoi(frameW, frameH);

            // --- ROI crop
            using var probRoi = new Mat(laneProb32f, roi);
            Mat? driveRoi = null;
            if (_driveMask != null && !_driveMask.Empty())
                driveRoi = new Mat(_driveMask, roi);

            // 1) laneProb -> laneLineMask(빨강용)
            using var laneLineMask = BuildLaneLineMask(probRoi, driveRoi);

            // 2) corridor edges from drivable
            var (leftEdgeX, rightEdgeX, okCorr) = EstimateCorridorEdges(driveRoi, roi.Width, roi.Height);

            // 3) bottom band histogram peaks from laneLineMask
            var peakXs = FindLanePeaksInBottomBand(laneLineMask, roi.Width, roi.Height);

            // 4) Build boundaries: always TotalLanes+1
            var boundaries = BuildBoundariesHybrid(leftEdgeX, rightEdgeX, okCorr, peakXs, roi.Width);

            var res = new LaneAnalysisResult
            {
                Roi = roi,
                EgoLane = EgoLane,
                TotalLanes = TotalLanes,
                BoundariesX_Roi = boundaries,
                LaneLineMaskRoi = laneLineMask.Clone(),
                DriveRoi = driveRoi?.Clone(),
                DebugLine = $"bnd:{boundaries.Count - 1} lanes:{TotalLanes} egoUI:{EgoLane}/{TotalLanes} thr:{LaneProbThreshold:0.00} gate:{(UseDrivableGate ? 1 : 0)}"
            };

            driveRoi?.Dispose();
            return res;
        }

        // 차량 바닥점(framePoint)이 몇 차선인지(1..TotalLanes)
        public bool TryGetLaneNumberForPoint(LaneAnalysisResult r, Point framePoint, out int laneNum)
        {
            laneNum = -1;
            if (r == null) return false;
            if (r.BoundariesX_Roi == null || r.BoundariesX_Roi.Count < 3) return false;

            // ROI check
            if (framePoint.X < r.Roi.Left || framePoint.X >= r.Roi.Right ||
                framePoint.Y < r.Roi.Top || framePoint.Y >= r.Roi.Bottom)
                return false;

            float xRoi = framePoint.X - r.Roi.Left;

            // boundaries: x0 < x1 < ... < xN
            // lane index = i where xi <= x < x(i+1)
            int nLanes = r.TotalLanes;
            for (int i = 0; i < nLanes; i++)
            {
                float a = r.BoundariesX_Roi[i];
                float b = r.BoundariesX_Roi[i + 1];
                if (xRoi >= a && xRoi < b)
                {
                    laneNum = i + 1; // 절대차선 1..TotalLanes
                    return true;
                }
            }

            return false;
        }

        public void DrawOnFrame(Mat frame, LaneAnalysisResult r)
        {
            if (!DrawDebug || frame == null || frame.Empty() || r == null) return;

            Cv2.Rectangle(frame, r.Roi, Scalar.White, 2);
            using var frameRoi = new Mat(frame, r.Roi);

            // Lane line overlay
            if (DrawLaneLines && r.LaneLineMaskRoi != null && !r.LaneLineMaskRoi.Empty())
            {
                using var ov = frameRoi.Clone();
                ov.SetTo(new Scalar(0, 0, 255), r.LaneLineMaskRoi);
                Cv2.AddWeighted(ov, 0.14, frameRoi, 0.86, 0, frameRoi);
            }

            // Drive overlay (optional)
            if (DrawCorridor && r.DriveRoi != null && !r.DriveRoi.Empty())
            {
                using var ov = frameRoi.Clone();
                ov.SetTo(new Scalar(0, 255, 0), r.DriveRoi);
                Cv2.AddWeighted(ov, 0.10, frameRoi, 0.90, 0, frameRoi);
            }

            // Boundaries
            if (DrawBoundaries && r.BoundariesX_Roi != null && r.BoundariesX_Roi.Count == r.TotalLanes + 1)
            {
                for (int i = 0; i < r.BoundariesX_Roi.Count; i++)
                {
                    int x = (int)Math.Round(r.BoundariesX_Roi[i]);
                    Cv2.Line(frameRoi, new Point(x, 0), new Point(x, frameRoi.Rows - 1), Scalar.White, 2);
                }
            }

            // Labels
            if (DrawLaneLabels && r.BoundariesX_Roi != null && r.BoundariesX_Roi.Count == r.TotalLanes + 1)
            {
                for (int i = 0; i < r.TotalLanes; i++)
                {
                    float mid = 0.5f * (r.BoundariesX_Roi[i] + r.BoundariesX_Roi[i + 1]);
                    int laneNum = i + 1;
                    var color = (laneNum == r.EgoLane) ? new Scalar(0, 255, 255) : Scalar.White;
                    Cv2.PutText(frameRoi, $"#{laneNum}", new Point((int)mid - 10, frameRoi.Rows - 20),
                        HersheyFonts.HersheySimplex, 0.9, color, 2);
                }
            }

            Cv2.PutText(frame, r.DebugLine, new Point(r.Roi.Left + 10, r.Roi.Top + 25),
                HersheyFonts.HersheySimplex, 0.6, Scalar.White, 2);
        }

        // =========================
        // Internals
        // =========================
        private Rect BuildRoi(int w, int h)
        {
            int y0 = (int)(h * RoiYStartRatio);
            int xMargin = (int)(w * RoiXMarginRatio);

            y0 = Math.Clamp(y0, 0, h - 1);
            xMargin = Math.Clamp(xMargin, 0, w / 3);

            int x0 = xMargin;
            int width = Math.Max(1, w - xMargin * 2);
            int height = Math.Max(1, h - y0);

            return new Rect(x0, y0, width, height);
        }

        private Mat BuildLaneLineMask(Mat laneProbRoi32f, Mat? driveRoi8u)
        {
            // threshold
            using var mask = new Mat(laneProbRoi32f.Rows, laneProbRoi32f.Cols, MatType.CV_8UC1);
            for (int y = 0; y < laneProbRoi32f.Rows; y++)
            {
                for (int x = 0; x < laneProbRoi32f.Cols; x++)
                {
                    float p = laneProbRoi32f.At<float>(y, x);
                    mask.Set(y, x, (byte)(p >= LaneProbThreshold ? 255 : 0));
                }
            }

            Mat outMask = mask.Clone();

            // drivable gate
            if (UseDrivableGate && driveRoi8u != null && !driveRoi8u.Empty())
            {
                using var gate = driveRoi8u.Clone();
                int k = Math.Max(1, GateErodeK);
                if (k % 2 == 0) k++;
                using var ke = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(k, k));
                Cv2.Erode(gate, gate, ke); // 갓길/벽쪽 제거

                Cv2.BitwiseAnd(outMask, gate, outMask);
            }

            // morphology (너무 세게 하면 굵어짐)
            if (LaneMaskOpenK > 1)
            {
                int k = LaneMaskOpenK; if (k % 2 == 0) k++;
                using var ko = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(k, k));
                Cv2.MorphologyEx(outMask, outMask, MorphTypes.Open, ko);
            }
            if (LaneMaskCloseK > 1)
            {
                int k = LaneMaskCloseK; if (k % 2 == 0) k++;
                using var kc = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(k, k));
                Cv2.MorphologyEx(outMask, outMask, MorphTypes.Close, kc);
            }

            return outMask;
        }

        private (float[] leftEdgeX, float[] rightEdgeX, bool ok) EstimateCorridorEdges(Mat? driveRoi8u, int roiW, int roiH)
        {
            // drive가 없으면 ROI 전체를 corridor로 가정
            if (driveRoi8u == null || driveRoi8u.Empty())
            {
                float[] l0 = Enumerable.Repeat(0f, roiH).ToArray();
                float[] r0 = Enumerable.Repeat((float)(roiW - 1), roiH).ToArray();
                return (l0, r0, false);
            }

            float[] left = new float[roiH];
            float[] right = new float[roiH];

            // 기본값
            for (int y = 0; y < roiH; y++) { left[y] = 0; right[y] = roiW - 1; }

            int bandH = Math.Clamp(CorridorBottomBandH, 20, roiH);
            int yStart = Math.Max(0, roiH - bandH);

            int okCount = 0;
            for (int y = yStart; y < roiH; y += Math.Max(1, CorridorSampleStepY))
            {
                int lx = -1, rx = -1;

                // leftmost
                for (int x = 0; x < roiW; x++)
                {
                    if (driveRoi8u.At<byte>(y, x) != 0) { lx = x; break; }
                }
                // rightmost
                for (int x = roiW - 1; x >= 0; x--)
                {
                    if (driveRoi8u.At<byte>(y, x) != 0) { rx = x; break; }
                }

                if (lx >= 0 && rx >= 0 && rx - lx > roiW * 0.35)
                {
                    left[y] = lx;
                    right[y] = rx;
                    okCount++;
                }
            }

            // 간단한 smoothing (moving avg)
            left = Smooth1D(left, CorridorSmoothWin);
            right = Smooth1D(right, CorridorSmoothWin);

            bool ok = okCount >= 3;
            return (left, right, ok);
        }

        private static float[] Smooth1D(float[] a, int win)
        {
            win = Math.Max(1, win);
            if (win % 2 == 0) win++;
            int r = win / 2;

            float[] o = new float[a.Length];
            for (int i = 0; i < a.Length; i++)
            {
                float sum = 0;
                int cnt = 0;
                for (int j = i - r; j <= i + r; j++)
                {
                    if (j < 0 || j >= a.Length) continue;
                    sum += a[j];
                    cnt++;
                }
                o[i] = (cnt > 0) ? sum / cnt : a[i];
            }
            return o;
        }

        private List<int> FindLanePeaksInBottomBand(Mat laneLineMaskRoi8u, int roiW, int roiH)
        {
            int bandH = Math.Clamp(CorridorBottomBandH, 20, roiH);
            int y0 = Math.Max(0, roiH - bandH);

            // column histogram
            int[] hist = new int[roiW];
            for (int x = 0; x < roiW; x++)
            {
                int c = 0;
                for (int y = y0; y < roiH; y++)
                    if (laneLineMaskRoi8u.At<byte>(y, x) != 0) c++;
                hist[x] = c;
            }

            // peak picking
            var peaks = new List<(int x, int v)>();
            for (int x = 2; x < roiW - 2; x++)
            {
                int v = hist[x];
                if (v < BoundaryMinPeakStrength) continue;
                if (v >= hist[x - 1] && v >= hist[x + 1] && v >= hist[x - 2] && v >= hist[x + 2])
                    peaks.Add((x, v));
            }

            // 너무 가까운 peak는 합치기
            peaks = peaks.OrderByDescending(p => p.v).ToList();
            var selected = new List<int>();
            int minDist = Math.Max(12, roiW / 80);
            foreach (var p in peaks)
            {
                if (selected.Any(s => Math.Abs(s - p.x) < minDist)) continue;
                selected.Add(p.x);
            }

            selected.Sort();
            return selected;
        }

        private List<float> BuildBoundariesHybrid(float[] leftEdgeX, float[] rightEdgeX, bool okCorr, List<int> peakXs, int roiW)
        {
            // base corridor edges (bottom 기준으로 잡음)
            int yBottom = leftEdgeX.Length - 1;
            float L = okCorr ? leftEdgeX[yBottom] : 0;
            float R = okCorr ? rightEdgeX[yBottom] : (roiW - 1);
            if (R - L < roiW * 0.4f) { L = 0; R = roiW - 1; }

            int need = TotalLanes + 1;

            // 우선 내부 경계 후보 = peak 중 corridor 안쪽 것만
            var insidePeaks = peakXs.Where(x => x > L + 5 && x < R - 5).ToList();

            // 목표: [L, (내부...), R] 형태로 need개 만들기
            var boundaries = new List<float> { L };

            // 내부가 충분히 있으면 “대략 등분 위치”에 가까운 peak를 채택
            if (insidePeaks.Count > 0)
            {
                for (int k = 1; k < TotalLanes; k++)
                {
                    float target = L + (R - L) * (k / (float)TotalLanes);
                    int best = insidePeaks.OrderBy(x => Math.Abs(x - target)).First();
                    // 너무 중복되면 skip
                    if (boundaries.Any(b => Math.Abs(b - best) < 10)) continue;
                    boundaries.Add(best);
                }
            }

            boundaries.Add(R);
            boundaries = boundaries.Distinct().OrderBy(x => x).ToList();

            // 부족하면 등분으로 채우기
            if (boundaries.Count != need)
            {
                boundaries.Clear();
                for (int k = 0; k <= TotalLanes; k++)
                    boundaries.Add(L + (R - L) * (k / (float)TotalLanes));
            }

            // 안전 클램프
            for (int i = 0; i < boundaries.Count; i++)
                boundaries[i] = Math.Clamp(boundaries[i], 0, roiW - 1);

            // 단조 증가 보정
            for (int i = 1; i < boundaries.Count; i++)
                if (boundaries[i] <= boundaries[i - 1] + 1) boundaries[i] = boundaries[i - 1] + 2;

            boundaries[0] = Math.Max(0, boundaries[0]);
            boundaries[^1] = Math.Min(roiW - 1, boundaries[^1]);

            return boundaries;
        }
    }
}
