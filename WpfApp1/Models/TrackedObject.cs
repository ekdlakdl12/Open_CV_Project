using System;
using OpenCvSharp;

namespace WpfApp1.Models
{
    public class TrackedObject
    {
        public int Id { get; set; }
        public int LastClassId { get; set; }
        public string LastClassName { get; set; } = "Vehicle"; // 속성 추가
        public Rect LastBox { get; set; }
        public double LastTimeMsec { get; set; }
        public double SpeedInKmh { get; set; }
        public int CurrentLane { get; set; }
        public string FirstDetectedTime { get; set; }
        public int UpdateCount { get; set; }
        public int MissedCount { get; set; }
        public bool ShouldBeDeleted => MissedCount > 10;
        public string Direction { get; set; } = "F";

        private static int _nextId = 1;

        public TrackedObject(int classId, Rect box, double timeMsec, string className)
        {
            Id = _nextId++;
            LastClassId = classId;
            LastClassName = className; // 생성 시 이름 저장
            LastBox = box;
            LastTimeMsec = timeMsec;
            FirstDetectedTime = DateTime.Now.ToString("HH:mm:ss");
            UpdateCount = 1;
        }

        public void Update(int classId, Rect box, double timeMsec, int lane)
        {
            if (timeMsec > LastTimeMsec)
            {
                double dist = Math.Sqrt(Math.Pow(box.X - LastBox.X, 2) + Math.Pow(box.Y - LastBox.Y, 2));
                double dt = (timeMsec - LastTimeMsec) / 1000.0 / 3600.0;
                if (dt > 0) SpeedInKmh = (dist * 0.0005) / dt; // 픽셀-km 환산 계수 (가정)
            }
            LastClassId = classId;
            LastBox = box;
            LastTimeMsec = timeMsec;
            CurrentLane = lane;
            UpdateCount++;
            MissedCount = 0;
        }

        public void Missed() => MissedCount++;

        public string CheckViolation()
        {
            if (SpeedInKmh > 110) return "과속";
            if (LastClassId == 7 && CurrentLane == 1) return "지정차로 위반";
            return "정상";
        }
    }
}