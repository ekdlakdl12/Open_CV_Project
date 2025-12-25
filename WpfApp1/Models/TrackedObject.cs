using System;
using OpenCvSharp;

namespace WpfApp1.Models
{
    public class TrackedObject
    {
        public int Id { get; set; }
        public int LastClassId { get; set; }
        public Rect LastBox { get; set; }
        public double LastTime { get; set; }
        public string FirstDetectedTime { get; set; }
        public int UpdateCount { get; set; }
        public double SpeedInKmh { get; private set; }
        public int CurrentLane { get; set; } = -1;
        private int _lastLane = -1;
        public string Direction { get; set; } = "Unknown";
        public string ClassName { get; set; }

        private Point2f _lastPos;
        private int _missedFrames = 0;
        public bool ShouldBeDeleted => _missedFrames > 10;

        public TrackedObject(int classId, Rect box, double time, string className)
        {
            Id = new Random().Next(10000, 99999);
            LastClassId = classId;
            LastBox = box;
            LastTime = time;
            ClassName = className;
            FirstDetectedTime = DateTime.Now.ToString("HH:mm:ss");
            _lastPos = new Point2f(box.X + box.Width / 2, box.Y + box.Height / 2);
        }

        public void Update(int classId, Rect box, double time, int lane)
        {
            double dt = (time - LastTime) / 1000.0;
            if (dt > 0)
            {
                Point2f currPos = new Point2f(box.X + box.Width / 2, box.Y + box.Height / 2);
                double dist = Math.Sqrt(Math.Pow(currPos.X - _lastPos.X, 2) + Math.Pow(currPos.Y - _lastPos.Y, 2));
                double speed = (dist / dt) * 0.2;
                SpeedInKmh = (SpeedInKmh * 0.8) + (speed * 0.2);
                _lastPos = currPos;
            }

            if (lane != -1) _lastLane = lane;
            LastClassId = classId;
            LastBox = box;
            LastTime = time;
            CurrentLane = lane;
            UpdateCount++;
            _missedFrames = 0;
        }

        public void Missed() => _missedFrames++;

        public string CheckViolation(int totalLanes)
        {
            string type = GetTypeName(LastClassId);
            bool isLargeVehicle = (type == "TRUCK" || type == "BUS" || LastClassId == 7 || LastClassId == 5);

            if (isLargeVehicle)
            {
                // 오인식 상관없이 마지막 차선이 아니면 무조건 위반
                if (CurrentLane != -1 && CurrentLane != totalLanes)
                {
                    return $"지정차선 위반({totalLanes}차선 미준수)";
                }
            }

            if (SpeedInKmh > 100.0) return "과속 위반";
            return "정상";
        }

        private string GetTypeName(int id) => id switch { 2 => "CAR", 5 => "BUS", 7 => "TRUCK", _ => "Vehicle" };
    }
}