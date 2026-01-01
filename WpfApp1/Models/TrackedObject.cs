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
        public double SpeedInKmh { get; private set; } // 이제 절대 속도(내 차 속도 포함)를 저장
        public int CurrentLane { get; set; } = -1;
        public string ClassName { get; set; }

        private List<int> _classHistory = new List<int>();
        private const int HistoryLimit = 20;

        public bool HasViolationHistory { get; private set; } = false;
        public string ConfirmedViolationReason { get; private set; } = "정상";

        // 과속 판단 기준 (절대 속도 120km/h 초과)
        public bool IsSpeeding => SpeedInKmh > 120;

        private Point2f _lastPos;
        private int _missedFrames = 0;
        public bool ShouldBeDeleted => _missedFrames > 60;

        // 내 차량의 가상 주행 속도 (95km/h)
        private const double EgoSpeed = 95.0;

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

        public string GetModelName()
        {
            string baseTag = LastClassId switch { 2 => "CAR", 5 => "BUS", 7 => "TRUCK", _ => "VEHICLE" };
            if (LastClassId >= 0 && LastClassId < Scripts.CarModelData.Names.Length)
            {
                return $"{baseTag} | {Scripts.CarModelData.Names[LastClassId]}";
            }
            return $"{baseTag} | {ClassName ?? "Unknown"}";
        }

        private void UpdateClassLogic(int id)
        {
            _classHistory.Add(id);
            if (_classHistory.Count > HistoryLimit) _classHistory.RemoveAt(0);
            if (_classHistory.Count >= 3)
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

                // 1. 상대 속도 계산 (중심점 이동 기반)
                double relativeSpeed = (dist / dt) * 0.25;

                // 2. 절대 속도 = 내 차 속도(95) + 상대 속도
                double absoluteSpeed = EgoSpeed + relativeSpeed;

                // 3. 지수 이동 평균 적용 (보정)
                SpeedInKmh = (SpeedInKmh == 0) ? absoluteSpeed : (SpeedInKmh * 0.8) + (absoluteSpeed * 0.2);

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
            List<string> violations = new List<string>();

            // 1. 모든 차종 공통: 과속 체크 (120km/h 초과)
            if (IsSpeeding && UpdateCount > 5)
            {
                violations.Add($"과속({SpeedInKmh:F1}km/h)");
            }

            // 2. 대형차(트럭, 버스) 전용: 지정차선 위반 체크
            if ((LastClassId == 7 || LastClassId == 5) && UpdateCount > 3)
            {
                if (CurrentLane > 0 && CurrentLane < totalLanes)
                {
                    violations.Add($"지정차선 위반({CurrentLane}/{totalLanes}차로)");
                }
            }

            // 3. 위반 결과 취합
            if (violations.Count > 0)
            {
                HasViolationHistory = true;
                ConfirmedViolationReason = string.Join(" + ", violations);
            }
            else
            {
                HasViolationHistory = false;
                ConfirmedViolationReason = "정상";
            }

            return ConfirmedViolationReason;
        }
    }
}