using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;

namespace WpfApp1.Scripts
{
    /// <summary>
    /// LaneAnalyzer (Lane Region Split Pipeline - "Near-field wall boosting")
    ///
    /// 핵심:
    /// - driveMask(초록) 내부에서만 분할
    /// - laneProb(빨강) -> wall 후보
    /// - BoostBand(하단)에서 약한 빨강도 컬럼벽으로 인정(BoostProbThreshold)
    /// - ✅ ColumnBoost 벽은 driveMask 내부에서만 세움(하늘/가드레일 침범 방지)
    /// - freeRoad = drive AND NOT wall
    /// - ConnectedComponents로 lane regions 분할
    /// - ✅ Regions가 0개여도 UI 기반 라벨(#2/#3/#4)을 "강제 표시" (Fallback)
    /// </summary>
    public sealed class LaneAnalyzer
    {
        // ===== ViewModel에서 주입(UI) =====
        public int TotalLanes { get; set; } = 4; // 2..8
        public int EgoLane { get; set; } = 3;    // 1..TotalLanes

        // ===== ROI =====
        public float RoiYStartRatio { get; set; } = 0.55f;
        public float RoiXMarginRatio { get; set; } = 0.04f;

        // ===== Wall(빨강) =====
        // 전체 ROI에서 벽으로 볼 threshold
        public float ProbThreshold { get; set; } = 0.55f;

        // ✅ BoostBand에서는 더 약한 빨강도 컬럼벽 후보로 인정
        public float BoostProbThreshold { get; set; } = 0.35f;

        // (벽 연결/정리)
        public int CloseK { get; set; } = 9;
        public int OpenK { get; set; } = 3;

        // (벽 두께)
        public int BoundaryDilateK { get; set; } = 40;
        public int BoundaryDilateIter { get; set; } = 1;

        // ✅ 핵심: "가까운 구간"만 믿고 벽을 강제 확장해서 분할 확실히 하기
        public bool EnableColumnBoost { get; set; } = true;

        // ROI 하단에서 얼마나 위까지를 "가까운 구간"으로 볼지 (0~1)
        public float BoostBandRatio { get; set; } = 0.35f;

        // BoostBand에서 한 컬럼에 벽 픽셀이 이만큼 이상이면 "벽 컬럼"
        public int BoostColumnMinCount { get; set; } = 1;

        // 컬럼벽 좌우 확장 픽셀
        public int BoostExpandX { get; set; } = 14;

        // 컬럼벽을 위로 세울지
        public bool BoostExtendToTop { get; set; } = true;

        // ===== Region 필터 =====
        // ✅ lanes:0 방지용으로 기본값을 완화
        public int MinRegionArea { get; set; } = 300;
        public int MinRegionWidth { get; set; } = 20;

        // ego/정렬 기준 band
        public int BottomBandH { get; set; } = 18;

        // ===== Debug draw =====
        public bool DrawWallOverlay { get; set; } = false; // ✅ 면 덮기 금지 기본
        public bool DrawRegionsOverlay { get; set; } = true;
        public bool DrawContours { get; set; } = true;
        public bool DrawLaneLabels { get; set; } = true;
        public bool DrawBoostBandBox { get; set; } = true;

        private Mat? _driveMask; // frame size, CV_8UC1 (0/255)

        // ✅ 안전하게 Clone해서 보관(외부 Dispose/교체 영향 방지)
        public void SetDrivableMask(Mat? driveMask8u)
        {
            _driveMask?.Dispose();
            _driveMask = null;

            if (driveMask8u == null || driveMask8u.Empty()) return;
            _driveMask = driveMask8u.Clone();
        }

        // =========================
        // 결과 구조체
        // =========================
        public sealed class LaneRegion
        {
            public int Label;             // connected component label id
            public int Area;
            public Rect BBox;             // ROI 좌표
            public double SortX;          // 정렬 기준 x (frame)
            public Point LabelPointFrame; // 텍스트 위치(frame)
            public Point[]? ContourFrame; // 경계선(frame)
            public Mat? MaskRoi;          // ROI mask (clone)
        }

        public sealed class LaneAnalysisResult
        {
            public Rect Roi;
            public int EgoRegionIndex = -1;
            public List<LaneRegion> Regions = new();
            public Mat? WallRoi;      // ROI wall mask (thick + boost)
            public Mat? FreeRoadRoi;  // ROI freeRoad
            public Rect BoostBandRoi; // ROI-local rect
            public string DebugLine = "";
        }

        public LaneAnalysisResult AnalyzeFromProb(Mat laneProb, int frameW, int frameH)
        {
            if (laneProb == null || laneProb.Empty())
                throw new ArgumentException("laneProb is empty");
            if (laneProb.Type() != MatType.CV_32FC1)
                throw new ArgumentException($"laneProb must be CV_32FC1, got {laneProb.Type()}");

            TotalLanes = Math.Clamp(TotalLanes, 2, 8);
            EgoLane = Math.Clamp(EgoLane, 1, TotalLanes);

            var roi = BuildRoi(frameW, frameH);

            using var probRoi = new Mat(laneProb, roi);

            // drive ROI
            using var driveRoi = (_driveMask != null && !_driveMask.Empty())
                ? new Mat(_driveMask, roi)
                : null;

            // prob -> 8U
            using var prob8u = new Mat();
            probRoi.ConvertTo(prob8u, MatType.CV_8UC1, 255.0);

            // wall(threshold)
            using var wall = new Mat();
            Cv2.Threshold(prob8u, wall, 255 * ProbThreshold, 255, ThresholdTypes.Binary);

            // wall morph (연결/정리)
            using var wall2 = MorphWall(wall);

            // wall dilate (벽 두께)
            using var wallThick = DilateWall(wall2);

            // ✅ near-field column boost (drive 내부에서만 세우기 + boostThr 적용)
            Rect boostBand = BuildBoostBandRect(roi.Width, roi.Height);
            if (EnableColumnBoost)
                ApplyColumnBoost(wallThick, driveRoi, prob8u, boostBand);

            // freeRoad = drive AND NOT wallThick
            Mat freeRoad;
            if (driveRoi != null && !driveRoi.Empty())
            {
                using var notWall = new Mat();
                Cv2.BitwiseNot(wallThick, notWall);

                freeRoad = new Mat();
                Cv2.BitwiseAnd(driveRoi, notWall, freeRoad);
            }
            else
            {
                // driveMask 없으면 디버그용
                using var notWall = new Mat();
                Cv2.BitwiseNot(wallThick, notWall);
                freeRoad = notWall.Clone();
            }

            // connected components -> regions
            var regions = SplitRegions(freeRoad, roi);

            // ego region
            int egoX = frameW / 2;
            int egoIdx = FindEgoRegionIndex(regions, roi, egoX);

            var res = new LaneAnalysisResult
            {
                Roi = roi,
                EgoRegionIndex = egoIdx,
                Regions = regions,
                WallRoi = wallThick.Clone(),
                FreeRoadRoi = freeRoad.Clone(),
                BoostBandRoi = boostBand,
                DebugLine =
                    $"lanes:{regions.Count} egoReg:{egoIdx} ego:{EgoLane}/{TotalLanes} | " +
                    $"driveNZ:{(driveRoi == null ? 0 : Cv2.CountNonZero(driveRoi))} " +
                    $"wallNZ:{Cv2.CountNonZero(wallThick)} freeNZ:{Cv2.CountNonZero(freeRoad)} | " +
                    $"thr:{ProbThreshold:0.00} boostThr:{BoostProbThreshold:0.00} close:{CloseK} open:{OpenK} " +
                    $"dil:{BoundaryDilateK} it:{BoundaryDilateIter} boost:{EnableColumnBoost} " +
                    $"band:{BoostBandRatio:0.00} colMin:{BoostColumnMinCount} expX:{BoostExpandX} " +
                    $"minA:{MinRegionArea} minW:{MinRegionWidth}"
            };

            freeRoad.Dispose();
            return res;
        }

        public void DrawDebug(Mat frame, LaneAnalysisResult r)
        {
            if (frame == null || frame.Empty() || r == null) return;

            // ROI
            Cv2.Rectangle(frame, r.Roi, Scalar.White, 2);

            // Boost band 표시(ROI-local -> frame)
            if (DrawBoostBandBox)
            {
                var bb = r.BoostBandRoi;
                var bbFrame = new Rect(r.Roi.X + bb.X, r.Roi.Y + bb.Y, bb.Width, bb.Height);
                Cv2.Rectangle(frame, bbFrame, new Scalar(255, 255, 255), 1);
                Cv2.PutText(frame, "BoostBand", new Point(bbFrame.Left + 6, bbFrame.Top + 18),
                    HersheyFonts.HersheySimplex, 0.6, new Scalar(255, 255, 255), 2);
            }

            // (선택) wall overlay (빨강 면덮기)
            if (DrawWallOverlay && r.WallRoi != null && !r.WallRoi.Empty())
            {
                using var wallBgr = new Mat(frame.Size(), MatType.CV_8UC3, Scalar.Black);
                using (var roiMat = new Mat(wallBgr, r.Roi))
                {
                    roiMat.SetTo(new Scalar(0, 0, 255), r.WallRoi);
                }
                Cv2.AddWeighted(wallBgr, 0.18, frame, 0.82, 0, frame);
            }

            // regions overlay
            if (DrawRegionsOverlay && r.Regions.Count > 0)
            {
                Scalar[] palette =
                {
                    new Scalar(255, 0, 255), new Scalar(255, 255, 0), new Scalar(0, 255, 255),
                    new Scalar(255, 0, 0), new Scalar(0, 255, 0), new Scalar(0, 0, 255),
                    new Scalar(200, 200, 0), new Scalar(0, 200, 200)
                };

                using var layer = frame.Clone();

                for (int i = 0; i < r.Regions.Count; i++)
                {
                    var reg = r.Regions[i];
                    if (reg.MaskRoi == null || reg.MaskRoi.Empty()) continue;

                    var color = palette[i % palette.Length];

                    using var colored = new Mat(frame.Size(), MatType.CV_8UC3, Scalar.Black);
                    using (var roiMat = new Mat(colored, r.Roi))
                    {
                        roiMat.SetTo(color, reg.MaskRoi);
                    }
                    Cv2.AddWeighted(colored, 0.16, layer, 0.84, 0, layer);
                }

                layer.CopyTo(frame);
            }

            // contours
            if (DrawContours)
            {
                foreach (var reg in r.Regions)
                {
                    if (reg.ContourFrame != null && reg.ContourFrame.Length >= 2)
                        Cv2.Polylines(frame, new[] { reg.ContourFrame }, true, Scalar.White, 2);
                }
            }

            // Debug line
            Cv2.PutText(frame, r.DebugLine,
                new Point(r.Roi.Left + 10, r.Roi.Top + 25),
                HersheyFonts.HersheySimplex, 0.6, Scalar.White, 2);

            // Lane labels (#n)
            if (DrawLaneLabels)
                DrawLaneNumberLabels(frame, r);
        }

        // =========================
        // ROI / Band
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

        private Rect BuildBoostBandRect(int roiW, int roiH)
        {
            float r = Math.Clamp(BoostBandRatio, 0.10f, 0.80f);
            int bandH = Math.Clamp((int)Math.Round(roiH * r), 8, roiH);
            int y0 = roiH - bandH;
            if (y0 < 0) y0 = 0;
            return new Rect(0, y0, roiW, bandH); // ROI-local
        }

        // =========================
        // Wall
        // =========================
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

        private Mat DilateWall(Mat wall)
        {
            if (BoundaryDilateK <= 1) return wall.Clone();

            int k = BoundaryDilateK;
            if (k % 2 == 0) k += 1;

            // 벽은 좌우로 퍼뜨리고 세로는 얇게
            int ky = Math.Max(3, k / 6);
            if (ky % 2 == 0) ky += 1;

            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(k, ky));
            var dst = new Mat();
            Cv2.Dilate(wall, dst, kernel, iterations: Math.Max(1, BoundaryDilateIter));
            return dst;
        }

        /// <summary>
        /// BoostBand에서 낮은 threshold로 컬럼벽 후보를 잡고,
        /// wallThick에 세로벽을 세우되 driveRoi 내부에서만 적용
        /// </summary>
        private void ApplyColumnBoost(Mat wallThick, Mat? driveRoi, Mat prob8uRoi, Rect boostBandRoiLocal)
        {
            using var bandProb = new Mat(prob8uRoi, boostBandRoiLocal);

            int w = bandProb.Cols;
            int h = bandProb.Rows;

            using var colMask = new Mat(1, w, MatType.CV_8UC1, Scalar.Black);

            byte thr = (byte)Math.Clamp((int)Math.Round(255 * BoostProbThreshold), 0, 255);

            for (int x = 0; x < w; x++)
            {
                int cnt = 0;
                for (int y = 0; y < h; y++)
                {
                    if (bandProb.At<byte>(y, x) >= thr)
                    {
                        cnt++;
                        if (cnt >= BoostColumnMinCount)
                        {
                            colMask.Set<byte>(0, x, 255);
                            break;
                        }
                    }
                }
            }

            // 좌우 확장
            if (BoostExpandX > 0)
            {
                int kx = BoostExpandX;
                if (kx % 2 == 0) kx += 1;
                using var k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(kx, 1));
                Cv2.Dilate(colMask, colMask, k, iterations: 1);
            }

            if (BoostExtendToTop)
            {
                for (int x = 0; x < w; x++)
                {
                    if (colMask.At<byte>(0, x) == 0) continue;

                    for (int y = 0; y < wallThick.Rows; y++)
                    {
                        if (driveRoi != null)
                        {
                            if (driveRoi.At<byte>(y, x) == 0) continue; // drive 밖은 벽 금지
                        }
                        wallThick.Set<byte>(y, x, 255);
                    }
                }
            }
            else
            {
                for (int x = 0; x < w; x++)
                {
                    if (colMask.At<byte>(0, x) == 0) continue;

                    for (int y = boostBandRoiLocal.Y; y < boostBandRoiLocal.Bottom; y++)
                    {
                        if (driveRoi != null)
                        {
                            if (driveRoi.At<byte>(y, x) == 0) continue;
                        }
                        wallThick.Set<byte>(y, x, 255);
                    }
                }
            }
        }

        // =========================
        // Regions split
        // =========================
        private List<LaneRegion> SplitRegions(Mat freeRoadRoi, Rect roiInFrame)
        {
            using var bin = new Mat();
            Cv2.Threshold(freeRoadRoi, bin, 127, 255, ThresholdTypes.Binary);

            using var labels = new Mat();
            using var stats = new Mat();
            using var centroids = new Mat();

            Cv2.ConnectedComponentsWithStats(bin, labels, stats, centroids);

            int n = stats.Rows;
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

            // 좌->우 정렬
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

        // =========================
        // Labels (✅ Fallback 포함)
        // =========================
        private void DrawLaneNumberLabels(Mat frame, LaneAnalysisResult r)
        {
            // ✅ Fallback: regions가 0이면 UI 기준으로라도 라벨 강제 표시
            if (r.Regions == null || r.Regions.Count == 0)
            {
                int y = r.Roi.Bottom - 35;

                int xL = r.Roi.Left + (int)(r.Roi.Width * 0.22);
                int xC = r.Roi.Left + (int)(r.Roi.Width * 0.50);
                int xR = r.Roi.Left + (int)(r.Roi.Width * 0.78);

                DrawLaneLabel(frame, xL, y, EgoLane - 1, isEgo: false);
                DrawLaneLabel(frame, xC, y, EgoLane, isEgo: true);
                DrawLaneLabel(frame, xR, y, EgoLane + 1, isEgo: false);
                return;
            }

            int egoIdx = r.EgoRegionIndex;
            if (egoIdx < 0 || egoIdx >= r.Regions.Count)
                egoIdx = r.Regions.Count / 2;

            for (int i = 0; i < r.Regions.Count; i++)
            {
                int laneNum = EgoLane + (i - egoIdx);
                if (laneNum < 1 || laneNum > TotalLanes) continue;

                var p = r.Regions[i].LabelPointFrame;
                var color = (i == egoIdx) ? new Scalar(0, 255, 255) : Scalar.White;
                Cv2.PutText(frame, $"#{laneNum}", p,
                    HersheyFonts.HersheySimplex, 0.9, color, 2);
            }
        }

        private void DrawLaneLabel(Mat frame, int x, int y, int laneNum, bool isEgo)
        {
            if (laneNum < 1 || laneNum > TotalLanes) return;

            var color = isEgo ? new Scalar(0, 255, 255) : Scalar.White;
            Cv2.PutText(frame, $"#{laneNum}", new Point(x, y),
                HersheyFonts.HersheySimplex, 1.0, color, 2);
        }
    }
}
