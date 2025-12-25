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
            if (dt > 0)
            {
                double dx = newBox.X - LastBox.X;
                double dy = newBox.Y - LastBox.Y;
                double pixelDist = Math.Sqrt(dx * dx + dy * dy);

                double yRatio = (double)newBox.Y / 720.0;
                double perspectiveWeight = 2.5 - yRatio;
                double highwayScale = 0.85;

                double calculatedSpeed = (pixelDist / dt) * highwayScale * perspectiveWeight;
                if (pixelDist < 2) calculatedSpeed = 0;

                if (SpeedInKmh == 0) SpeedInKmh = calculatedSpeed;
                else SpeedInKmh = (SpeedInKmh * 0.8) + (calculatedSpeed * 0.2);
            }

            // [차선 보정 로직] -1이 들어오면 업데이트하지 않고 기존 차선을 유지함
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

            LastClassId = classId;
            LastBox = newBox;
            LastTimeMs = newTimeMs;
            MissedFrames = 0;
        }

        public string CheckViolation()
        {
            bool isTarget = (LastClassId == 2 || LastClassId == 5 || LastClassId == 7);
            if (!isTarget) return "정상";

            List<string> violations = new List<string>();
            if (this.SpeedInKmh > 120) violations.Add("과속");
            if (PreviousLane >= 4 && CurrentLane < PreviousLane && PreviousLane != -1)
                violations.Add("지정차로 위반(추월)");
            if (FramesInFirstLane > 150)
                violations.Add("1차로 정속주행");

            return violations.Count > 0 ? string.Join(", ", violations) : "정상";
        }

        public void Missed() => MissedFrames++;
    }
}