using OpenCvSharp;
using System;
using System.Collections.Generic;

namespace WpfApp1.Scripts
{
    public class TrackedObject
    {
        private static int _nextId = 1;
        public int Id { get; }
        public Rect LastBox { get; set; }
        public double LastTimeMs { get; set; }
        public int LastClassId { get; set; }
        public string? ClassName { get; set; }
        public string FirstDetectedTime { get; }
        public double SpeedInKmh { get; private set; }
        public int MissedFrames { get; private set; }
        public int UpdateCount { get; private set; } = 0;

        public bool HasExceededSpeed { get; private set; } = false;
        public int PreviousLane { get; private set; } = -1;
        public int CurrentLane { get; private set; } = -1;
        public string Direction { get; set; } = "F";
        public int FramesInFirstLane { get; private set; } = 0;

        public bool ShouldBeDeleted => MissedFrames > 15;

        public TrackedObject(int classId, Rect box, double timeMs, string? className = null)
        {
            Id = _nextId++;
            LastClassId = classId;
            LastBox = box;
            LastTimeMs = timeMs;
            ClassName = className;
            FirstDetectedTime = DateTime.Now.ToString("HH:mm:ss");
            MissedFrames = 0;
            SpeedInKmh = 0;
        }

        public void Update(int classId, Rect newBox, double newTimeMs, int newLane)
        {
            double dt = (newTimeMs - LastTimeMs) / 1000.0;
            if (dt > 0.01 && dt < 0.5)
            {
                double dx = newBox.X - LastBox.X;
                double dy = newBox.Y - LastBox.Y;
                double pixelDist = Math.Sqrt(dx * dx + dy * dy);

                double yRatio = (double)(newBox.Y + newBox.Height) / 720.0;
                double perspectiveWeight = 0.8 + (yRatio * 0.7);
                double highwayScale = 0.65;

                double calculatedSpeed = (pixelDist / dt) * highwayScale * perspectiveWeight;

                if (calculatedSpeed > 220) calculatedSpeed = SpeedInKmh > 0 ? SpeedInKmh : 100;

                // EMA 필터: 속도 안정화
                if (UpdateCount < 10) SpeedInKmh = calculatedSpeed;
                else SpeedInKmh = (SpeedInKmh * 0.9) + (calculatedSpeed * 0.1);

                // 차종 안정화 (진입 시 오인식 방지)
                if (UpdateCount < 30) LastClassId = classId;

                // 과속 확정 플래그 (안정화 후 120km/h 초과 시)
                if (UpdateCount > 25 && SpeedInKmh >= 120) HasExceededSpeed = true;

                UpdateCount++;
            }

            if (newLane != -1)
            {
                if (CurrentLane != -1 && CurrentLane != newLane)
                {
                    PreviousLane = CurrentLane;
                    Direction = (newLane < CurrentLane) ? "L" : "R";
                }
                CurrentLane = newLane;
                if (CurrentLane == 1) FramesInFirstLane++;
                else FramesInFirstLane = 0;
            }

            LastBox = newBox;
            LastTimeMs = newTimeMs;
            MissedFrames = 0;
        }

        public string CheckViolation()
        {
            // [중요] 속도가 100 미만이면 과거 기록 상관없이 무조건 "정상" (17.7km/h 과속 방지)
            if (UpdateCount < 25 || SpeedInKmh < 100) return "정상";

            List<string> violations = new List<string>();

            // 1. 과속 판정
            if (HasExceededSpeed || SpeedInKmh >= 120) violations.Add("과속");

            // 2. 1차로 정속주행 (1차로 알박기)
            if (CurrentLane == 1 && FramesInFirstLane > 300 && SpeedInKmh < 100)
                violations.Add("1차로 정속주행");

            // 3. 지정차로 위반 (트럭/버스 하위차로 미준수)
            if ((LastClassId == 5 || LastClassId == 7) && CurrentLane == 1)
                violations.Add("지정차로 위반(상위차로 진입)");

            return violations.Count > 0 ? string.Join(", ", violations) : "정상";
        }

        public void Missed() => MissedFrames++;
    }
}