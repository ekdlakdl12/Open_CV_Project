using System;
using System.Collections.Generic;
using System.Linq;
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
        public string Direction { get; set; } = "Unknown";
        public string ClassName { get; set; }

        private List<int> _classHistory = new List<int>();
        private const int HistoryLimit = 15;

        public bool HasViolationHistory { get; private set; } = false;
        public string ConfirmedViolationReason { get; private set; } = "정상";

        public bool IsSpeeding => (LastClassId == 2 && SpeedInKmh > 120);

        private Point2f _lastPos;
        private int _missedFrames = 0;
        public bool ShouldBeDeleted => _missedFrames > 15;

        public TrackedObject(int classId, Rect box, double time, string className)
        {
            Id = Guid.NewGuid().GetHashCode() & 0x7FFFFFFF;
            LastBox = box;
            LastTime = time;
            ClassName = className;
            LastClassId = classId;
            FirstDetectedTime = DateTime.Now.ToString("HH:mm:ss");
            _lastPos = new Point2f(box.X + box.Width / 2, box.Y + box.Height / 2);
            UpdateClassLogic(classId);
        }

        // CarModelData.Names 리스트에서 이름을 안전하게 가져오는 메서드
        public string GetModelName()
        {
            if (LastClassId >= 0 && LastClassId < CarModelData.Names.Length)
            {
                return CarModelData.Names[LastClassId];
            }
            return ClassName ?? "Vehicle";
        }

        private void UpdateClassLogic(int id)
        {
            _classHistory.Add(id);
            if (_classHistory.Count > HistoryLimit) _classHistory.RemoveAt(0);
            if (_classHistory.Count >= 5)
            {
                var groups = _classHistory.GroupBy(x => x).OrderByDescending(g => g.Count());
                LastClassId = groups.First().Key;
            }
            else LastClassId = id;
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
            UpdateClassLogic(classId);
            LastBox = box;
            LastTime = time;
            if (lane != -1) CurrentLane = lane;
            UpdateCount++;
            _missedFrames = 0;
        }

        public void Missed() => _missedFrames++;

        public string CheckViolation(int totalLanes)
        {
            if (LastClassId == 2)
            {
                if (IsSpeeding)
                {
                    ConfirmedViolationReason = "속도 위반(120km/h 초과)";
                    return ConfirmedViolationReason;
                }
                return "정상";
            }

            if (HasViolationHistory) return ConfirmedViolationReason;

            if (LastClassId == 7 || LastClassId == 5)
            {
                if (CurrentLane != -1 && CurrentLane < totalLanes)
                {
                    HasViolationHistory = true;
                    ConfirmedViolationReason = $"지정차선 위반({totalLanes}차선 미준수)";
                }
                else
                {
                    ConfirmedViolationReason = "정상";
                }
            }
            return ConfirmedViolationReason;
        }
    }
}