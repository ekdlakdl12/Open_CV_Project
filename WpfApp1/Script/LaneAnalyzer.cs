using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;

namespace WpfApp1.Scripts
{
    /// <summary>
    /// LaneAnalyzer (Lane Region Split Pipeline)
    /// - driveMask(초록, drivable) + laneProb(빨간 벽 후보)
    /// - wall = Threshold(laneProb) -> Morph -> Dilate(벽 두께)
    /// - freeRoad = drive AND NOT wall
    /// - ConnectedComponents로 freeRoad를 영역 분할 => lane regions
    /// - egoX(화면 중앙)이 속한 region을 egoRegion으로 보고,
    ///   UI EgoLane/TotalLanes 기반으로 좌우 라벨링
    /// </summary>
    public sealed class LaneAnalyzer
    {
        // ===== ViewModel에서 주입(UI) =====
        public int TotalLanes { get; set; } = 4; // 2..8
        public int EgoLane { get; set; } = 3;    // 1..TotalLanes

        // ===== ROI 튜닝 =====
        public float RoiYStartRatio { get; set; } = 0.55f;
        public float RoiXMarginRatio { get; set; } = 0.04f;

        // ===== Wall(빨간) 튜닝 =====
        public float ProbThreshold { get; set; } = 0.45f;   // laneProb threshold

        // Dilate 반복
        public int BoundaryDilateIter { get; set; } = 1;

        // ✅ (중요) 커널 크기: 가로는 얇게, 세로는 길게
        public int BoundaryDilateKx { get; set; } = 5;
        public int BoundaryDilateKy { get; set; } = 21;

        // ✅ 새 옵션: 세로 연결 중심 커널 사용(가로 퍼짐 억제)
        public bool PreferVerticalDilate { get; set; } = true;

        // ✅ 세로 라인 커널의 “가로 반폭”
        // 0 => 1px(딱 중앙만), 1 => 3px, 2 => 5px
        public int VerticalKernelHalfWidth { get; set; } = 1;

        // ===== Mask 정리(선택) =====
        public int CloseK { get; set; } = 5;
        public int OpenK { get; set; } = 3;

        // ===== Region 필터 =====
        public int MinRegionArea { get; set; } = 1200;
        public int MinRegionWidth { get; set; } = 80;
        public int BottomBandH { get; set; } = 20;

        // ===== 디버그 =====
        public bool DrawWallOverlay { get; set; } = true;
        public bool DrawRegionsOverlay { get; set; } = true;
        public bool DrawContours { get; set; } = true;
        public bool DrawLabels { get; set; } = true;

        // ===== 디버그 알파(화면 안 어둡게) =====
        public double WallAlpha { get; set; } = 0.22;
        public double RegionAlpha { get; set; } = 0.14;

        private Mat? _driveMask; // frame size, CV_8UC1 (0/255)
        public void SetDrivableMask(Mat? driveMask8u) => _driveMask = driveMask8u;

        // =========================
        // 결과 구조체
        // =========================
        public sealed class LaneRegion
        {
            public int Label;
            public int Area;
            public Rect BBox;                 // ROI 좌표
            public double SortX;              // 좌우 정렬 기준 x (프레임 좌표)
            public Point LabelPointFrame;     // 라벨 텍스트 위치(프레임)
            public Point[]? ContourFrame;     // 경계선(프레임)
            public Mat? MaskRoi;              // ROI mask (8UC1)
        }

        public sealed class LaneAnalysisResult
        {
            public Rect Roi;
            public int EgoRegionIndex = -1;
            public List<LaneRegion> Regions = new();
            public Mat? WallRoi;              // ROI wall mask (8UC1)
            public Mat? FreeRoadRoi;          // ROI freeRoad (8UC1)
            public string DebugLine = "";
        }

        public LaneAnalysisResult AnalyzeFromProb(Mat laneProb, int frameW, int frameH)
        {
            if (laneProb == null || laneProb.Empty())
                throw new ArgumentException("laneProb is empty");
            if (laneProb.Type() != MatType.CV_32FC1)
                throw new ArgumentException($"laneProb must be CV_32FC1, got {laneProb.Type()}");

            var roi = BuildRoi(frameW, frameH);

            // driveMask ROI
            Mat? driveRoi = null;
            if (_driveMask != null && !_driveMask.Empty())
                driveRoi = new Mat(_driveMask, roi); // parent 참조

            using var probRoi = new Mat(laneProb, roi);

            // prob -> 8U
            using var prob8u = new Mat();
            probRoi.ConvertTo(prob8u, MatType.CV_8UC1, 255.0);

            // wall(threshold)
            using var wall = new Mat();
            Cv2.Threshold(prob8u, wall, 255 * ProbThreshold, 255, ThresholdTypes.Binary);

            // wall morph (선 굵게/연결)
            using var wall2 = MorphWall(wall);

            // wall dilate (벽 두께/연결)
            using var wallThick = DilateWall(wall2);

            // freeRoad = drive AND NOT wallThick
            using var freeRoad = new Mat();
            if (driveRoi != null && !driveRoi.Empty())
            {
                using var notWall = new Mat();
                Cv2.BitwiseNot(wallThick, notWall);

                using var driveClone = driveRoi.Clone();
                Cv2.BitwiseAnd(driveClone, notWall, freeRoad);
            }
            else
            {
                using var notWall = new Mat();
                Cv2.BitwiseNot(wallThick, notWall);
                notWall.CopyTo(freeRoad);
            }

            // connected components: freeRoad
            var regions = SplitRegions(freeRoad, roi);

            // egoRegion 찾기 (하단 band 기준, 중앙 egoX와 가까운 region)
            int egoX = frameW / 2;
            int egoIdx = FindEgoRegionIndex(regions, roi, egoX);

            var res = new LaneAnalysisResult
            {
                Roi = roi,
                EgoRegionIndex = egoIdx,
                Regions = regions,
                WallRoi = wallThick.Clone(),
                FreeRoadRoi = freeRoad.Clone(),
                DebugLine =
                    $"lanes:{regions.Count} egoReg:{egoIdx} ego:{EgoLane}/{TotalLanes} | " +
                    $"driveNZ:{(driveRoi == null ? 0 : Cv2.CountNonZero(driveRoi))} " +
                    $"wallNZ:{Cv2.CountNonZero(wallThick)} freeNZ:{Cv2.CountNonZero(freeRoad)} | " +
                    $"thr:{ProbThreshold:0.00} close:{CloseK} open:{OpenK} dil:({BoundaryDilateKx},{BoundaryDilateKy}) it:{BoundaryDilateIter} minA:{MinRegionArea}"
            };

            return res;
        }

        /// <summary>
        /// ✅ 오버레이 합성은 frame 전체가 아니라 "ROI 안에서만" 수행
        /// </summary>
        public void DrawDebug(Mat frame, LaneAnalysisResult r)
        {
            if (frame == null || frame.Empty() || r == null) return;

            // ROI 박스
            Cv2.Rectangle(frame, r.Roi, Scalar.White, 2);

            using var frameRoi = new Mat(frame, r.Roi);

            // 1) Wall overlay (ROI 안에서만)
            if (DrawWallOverlay && r.WallRoi != null && !r.WallRoi.Empty())
            {
                using var overlay = frameRoi.Clone();
                overlay.SetTo(new Scalar(0, 0, 255), r.WallRoi);
                Cv2.AddWeighted(overlay, WallAlpha, frameRoi, 1.0 - WallAlpha, 0, frameRoi);
            }

            // 2) Regions overlay (ROI 안에서만)
            if (DrawRegionsOverlay && r.Regions.Count > 0)
            {
                Scalar[] palette =
                {
                    new Scalar(255, 0, 255), new Scalar(255, 255, 0), new Scalar(0, 255, 255),
                    new Scalar(255, 0, 0),   new Scalar(0, 255, 0),   new Scalar(0, 0, 255),
                    new Scalar(200, 200, 0), new Scalar(0, 200, 200)
                };

                using var layer = frameRoi.Clone();

                for (int i = 0; i < r.Regions.Count; i++)
                {
                    var reg = r.Regions[i];
                    if (reg.MaskRoi == null || reg.MaskRoi.Empty()) continue;

                    var color = palette[i % palette.Length];

                    using var colored = layer.Clone();
                    colored.SetTo(color, reg.MaskRoi);

                    Cv2.AddWeighted(colored, RegionAlpha, layer, 1.0 - RegionAlpha, 0, layer);
                }

                layer.CopyTo(frameRoi);
            }

            // 3) Contours (frame 좌표)
            if (DrawContours)
            {
                foreach (var reg in r.Regions)
                {
                    if (reg.ContourFrame != null && reg.ContourFrame.Length >= 2)
                        Cv2.Polylines(frame, new[] { reg.ContourFrame }, true, Scalar.White, 2);
                }
            }

            // 4) Labels: #laneNumber
            DrawLaneNumberLabels(frame, r);

            // 5) Debug line
            Cv2.PutText(frame, r.DebugLine,
                new Point(r.Roi.Left + 10, r.Roi.Top + 25),
                HersheyFonts.HersheySimplex, 0.6, Scalar.White, 2);
        }

        // =========================
        // 내부 구현
        // =========================
        private Rect BuildRoi(int w, int h)
        {
            int y0 = (int)(h * RoiYStartRatio);
            int xMargin = (int)(w * RoiXMarginRatio);

            y0 = Math.Clamp(y0, 0, h - 1);
            xMargin = Math.Clamp(xMargin, 0, w / 3);

            int x0 = xMargin;
            int width = w - xMargin * 2;
            width = Math.Clamp(width, 1, w);

            int height = h - y0;
            height = Math.Clamp(height, 1, h);

            return new Rect(x0, y0, width, height);
        }

        private Mat MorphWall(Mat wall)
        {
            Mat outMask = wall.Clone();

            if (CloseK > 1)
            {
                using var kClose = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(CloseK, CloseK));
                Cv2.MorphologyEx(outMask, outMask, MorphTypes.Close, kClose);
            }

            if (OpenK > 1)
            {
                using var kOpen = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(OpenK, OpenK));
                Cv2.MorphologyEx(outMask, outMask, MorphTypes.Open, kOpen);
            }

            return outMask;
        }

        private Mat BuildVerticalLineKernel(int kx, int ky, int halfWidth)
        {
            // ky x kx 커널 생성: 중앙 열 기준으로 세로줄을 1로 채움
            // halfWidth=0 => 1px, 1 => 3px, 2 => 5px
            kx = Math.Max(1, kx);
            ky = Math.Max(1, ky);
            if (kx % 2 == 0) kx++;
            if (ky % 2 == 0) ky++;

            int cx = kx / 2;
            int hw = Math.Clamp(halfWidth, 0, (kx - 1) / 2);

            var kernel = new Mat(ky, kx, MatType.CV_8UC1, Scalar.All(0));
            for (int y = 0; y < ky; y++)
            {
                for (int dx = -hw; dx <= hw; dx++)
                {
                    int x = cx + dx;
                    if (x >= 0 && x < kx) kernel.Set(y, x, (byte)1);
                }
            }
            return kernel;
        }

        private Mat DilateWall(Mat wall)
        {
            int kx = Math.Max(1, BoundaryDilateKx);
            int ky = Math.Max(1, BoundaryDilateKy);
            if (kx % 2 == 0) kx++;
            if (ky % 2 == 0) ky++;

            using Mat kernel = PreferVerticalDilate
                ? BuildVerticalLineKernel(kx, ky, VerticalKernelHalfWidth)   // ✅ 세로 연결 커널
                : Cv2.GetStructuringElement(MorphShapes.Rect, new Size(kx, ky)); // 일반 Rect

            var dst = new Mat();
            Cv2.Dilate(wall, dst, kernel, iterations: Math.Max(1, BoundaryDilateIter));
            return dst;
        }

        private List<LaneRegion> SplitRegions(Mat freeRoadRoi, Rect roiInFrame)
        {
            using var bin = new Mat();
            Cv2.Threshold(freeRoadRoi, bin, 127, 255, ThresholdTypes.Binary);

            using var labels = new Mat();
            using var stats = new Mat();
            using var centroids = new Mat();

            Cv2.ConnectedComponentsWithStats(bin, labels, stats, centroids);

            int n = stats.Rows; // 0 = background
            var list = new List<LaneRegion>();

            int bandH = Math.Clamp(BottomBandH, 4, roiInFrame.Height);
            int bandY0 = Math.Max(0, roiInFrame.Height - bandH);
            var bandRect = new Rect(0, bandY0, roiInFrame.Width, roiInFrame.Height - bandY0);

            for (int i = 1; i < n; i++)
            {
                int area = stats.At<int>(i, 4);
                if (area < MinRegionArea) continue;

                int x = stats.At<int>(i, 0);
                int y = stats.At<int>(i, 1);
                int w = stats.At<int>(i, 2);
                int h = stats.At<int>(i, 3);

                if (w < MinRegionWidth) continue;

                using var compMask = new Mat();
                Cv2.InRange(labels, new Scalar(i), new Scalar(i), compMask);

                using var band = new Mat(compMask, bandRect);
                double sortX = ComputeBandSortX(band, roiInFrame);

                // contour -> frame
                Point[]? contourFrame = null;
                Cv2.FindContours(compMask, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
                var best = contours.OrderByDescending(c => c.Length).FirstOrDefault();
                if (best != null && best.Length >= 10)
                    contourFrame = best.Select(p => new Point(p.X + roiInFrame.X, p.Y + roiInFrame.Y)).ToArray();

                var labelPoint = new Point((int)Math.Round(sortX), roiInFrame.Bottom - 20);

                list.Add(new LaneRegion
                {
                    Label = i,
                    Area = area,
                    BBox = new Rect(x, y, w, h),
                    SortX = sortX,
                    LabelPointFrame = labelPoint,
                    ContourFrame = contourFrame,
                    MaskRoi = compMask.Clone()
                });
            }

            return list.OrderBy(r => r.SortX).ToList();
        }

        private double ComputeBandSortX(Mat bandMask, Rect roiInFrame)
        {
            int w = bandMask.Cols;
            int h = bandMask.Rows;

            long sum = 0;
            long cnt = 0;

            for (int x = 0; x < w; x++)
            {
                int colCnt = 0;
                for (int y = 0; y < h; y++)
                {
                    if (bandMask.At<byte>(y, x) != 0) colCnt++;
                }
                if (colCnt > 0)
                {
                    sum += (long)x * colCnt;
                    cnt += colCnt;
                }
            }

            double xLocal = (cnt > 0) ? (double)sum / cnt : w * 0.5;
            return roiInFrame.X + xLocal;
        }

        private int FindEgoRegionIndex(List<LaneRegion> regions, Rect roiInFrame, int egoXFrame)
        {
            if (regions.Count == 0) return -1;

            int egoX = Math.Clamp(egoXFrame, roiInFrame.Left, roiInFrame.Right);

            int bestIdx = 0;
            double best = double.MaxValue;

            for (int i = 0; i < regions.Count; i++)
            {
                double d = Math.Abs(regions[i].SortX - egoX);
                if (d < best) { best = d; bestIdx = i; }
            }
            return bestIdx;
        }

        private void DrawLaneNumberLabels(Mat frame, LaneAnalysisResult r)
        {
            if (!DrawLabels) return;
            if (r.Regions.Count == 0) return;

            TotalLanes = Math.Clamp(TotalLanes, 2, 8);
            EgoLane = Math.Clamp(EgoLane, 1, TotalLanes);

            int egoIdx = r.EgoRegionIndex;
            if (egoIdx < 0 || egoIdx >= r.Regions.Count) egoIdx = r.Regions.Count / 2;

            for (int i = 0; i < r.Regions.Count; i++)
            {
                int laneNum = EgoLane + (i - egoIdx);
                if (laneNum < 1 || laneNum > TotalLanes) continue;

                var p = r.Regions[i].LabelPointFrame;
                var color = (i == egoIdx) ? new Scalar(0, 255, 255) : Scalar.White; // ego 노랑
                Cv2.PutText(frame, $"#{laneNum}", p,
                    HersheyFonts.HersheySimplex, 0.9, color, 2);
            }
        }
    }
}
