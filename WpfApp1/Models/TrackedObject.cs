using OpenCvSharp;
using System;

namespace WpfApp1.Scripts
{
    public class TrackedObject
    {
        private static int _nextId = 1;
        public int Id { get; }
        public Rect LastBox { get; set; }
        public double LastTimeMs { get; set; }
        public int LastClassId { get; set; }
        public string FirstDetectedTime { get; }
        public double SpeedInKmh { get; private set; }
        public int MissedFrames { get; private set; }
        public bool ShouldBeDeleted => MissedFrames > 15;

        public TrackedObject(int classId, Rect box, double timeMs)
        {
            Id = _nextId++;
            LastClassId = classId;
            LastBox = box;
            LastTimeMs = timeMs;
            FirstDetectedTime = DateTime.Now.ToString("HH:mm:ss");
            MissedFrames = 0;
            SpeedInKmh = 0;
        }

        public void Update(int classId, Rect newBox, double newTimeMs)
        {
            double dt = (newTimeMs - LastTimeMs) / 1000.0;
            if (dt > 0)
            {
                // 1. 픽셀 이동 거리
                double dx = newBox.X - LastBox.X;
                double dy = newBox.Y - LastBox.Y;
                double pixelDist = Math.Sqrt(dx * dx + dy * dy);

                // 2. 고속도로 현실화 보정 (가장 중요한 부분)
                // 원근법에 의해 화면 상단(Y가 작음)은 1픽셀 이동이 실제로는 매우 먼 거리임
                // 화면의 Y 좌표에 반비례하는 가중치를 적용 (상단일수록 속도 가중치 증가)
                double yRatio = (double)newBox.Y / 720.0; // 720p 해상도 기준 비율
                double perspectiveWeight = 2.5 - yRatio; // 상단(먼 곳)은 약 2배 더 빠르게 계산

                // 고속도로 속도 보정 계수 (5km 나오던 것을 100km 수준으로 올리기 위해 약 15~20배 상향)
                double highwayScale = 0.85;

                double calculatedSpeed = (pixelDist / dt) * highwayScale * perspectiveWeight;

                // 3. 속도 급변 방지 (EMA Filter)
                // 정지 상태거나 너무 작은 움직임은 속도 0 처리
                if (pixelDist < 2) calculatedSpeed = 0;

                if (SpeedInKmh == 0) SpeedInKmh = calculatedSpeed;
                else SpeedInKmh = (SpeedInKmh * 0.8) + (calculatedSpeed * 0.2); // 부드러운 수치 변화
            }

            LastClassId = classId;
            LastBox = newBox;
            LastTimeMs = newTimeMs;
            MissedFrames = 0;
        }

        public void Missed() => MissedFrames++;
    }
}