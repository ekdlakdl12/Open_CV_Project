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
        private int _lastLane = -1; // 이전 차선 저장용
        private bool _isLaneChanged = false; // 차선 변경 여부 플래그
        public string Direction { get; set; } = "Unknown";
        public string ClassName { get; set; }

        private Point2f _lastPos;
        private int _missedFrames = 0;
        public bool ShouldBeDeleted => _missedFrames > 10;

        public TrackedObject(int classId, Rect box, double time, string className)
        {
            Id = new Random().Next(1000, 9999);
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

                // 픽셀 거리 기반 속도 추정
                double speed = (dist / dt) * 0.2;
                SpeedInKmh = (SpeedInKmh * 0.8) + (speed * 0.2);

                _lastPos = currPos;
            }

            // 차선 변경 감지 로직
            if (lane != -1 && _lastLane != -1 && _lastLane != lane)
            {
                _isLaneChanged = true;
            }

            if (lane != -1) _lastLane = lane; // 유효한 차선일 때만 갱신

            LastClassId = classId;
            LastBox = box;
            LastTime = time;
            CurrentLane = lane;
            UpdateCount++;
            _missedFrames = 0;
        }

        public void Missed() => _missedFrames++;

        public string CheckViolation()
        {
            string type = GetTypeName(LastClassId);

            // 1. 트럭, 버스, 화물차 차선 변경 위반 (요청 사항)
            if ((type == "TRUCK" || type == "BUS" || type == "Vehicle") && _isLaneChanged)
            {
                return "대형차 차선변경 위반";
            }

            // 2. 대형차 1차선 진입 금지 (기존 지정차선 위반)
            if ((type == "TRUCK" || type == "BUS") && CurrentLane == 1)
            {
                return "지정차선 위반";
            }

            // 3. 과속 위반 (예시)
            if (SpeedInKmh > 110) return "과속 위반";

            return "정상";
        }

        private string GetTypeName(int id) => id switch { 2 => "CAR", 5 => "BUS", 7 => "TRUCK", _ => "Vehicle" };
    }
}