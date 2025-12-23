using OpenCvSharp;
using System.Collections.Generic;
using System.Linq;

namespace WpfApp1.Models
{
    public class TrackedObject
    {
        // 객체 삭제 전 최대 미감지 횟수 (예: 5프레임 이상 미감지 시 삭제)
        private const int MAX_MISSES_BEFORE_DELETION = 5;

        public int Id { get; }
        public int ClassId { get; set; }
        public Rect LastBox { get; private set; }

        // 객체의 과거 중심점 위치 및 시간 기록 (속도 계산용)
        private readonly List<(Point center, double timeMsec)> _centerHistory = new();

        public int TrackCount { get; private set; } = 0;
        public int MissedCount { get; private set; } = 0;

        public double RelativeSpeed { get; private set; } = 0;

        private static int _nextId = 1;

        public TrackedObject(Detection detection, double timeMsec)
        {
            this.Id = _nextId++;
            this.ClassId = detection.ClassId;
            Update(detection, timeMsec);
        }

        // 객체 감지 시 정보를 갱신합니다.
        public void Update(Detection detection, double timeMsec)
        {
            this.LastBox = detection.Box;
            this.ClassId = detection.ClassId;
            detection.TrackId = this.Id;

            Point currentCenter = GetCenter(detection.Box);
            _centerHistory.Add((currentCenter, timeMsec));
            TrackCount++;
            MissedCount = 0;

            // 오래된 히스토리 제거 (예: 2초 이상)
            double oldestValidTime = timeMsec - 2000;
            _centerHistory.RemoveAll(h => h.timeMsec < oldestValidTime);

            CalculateRelativeSpeed();
        }

        // 객체가 감지되지 않았을 때 호출됩니다.
        public void Missed()
        {
            MissedCount++;
        }

        // 객체 삭제 필요 여부
        public bool ShouldBeDeleted => MissedCount > MAX_MISSES_BEFORE_DELETION;


        private Point GetCenter(Rect box) => new(box.X + box.Width / 2, box.Y + box.Height / 2);

        // 객체의 상대적인 움직임 속도를 계산합니다.
        private void CalculateRelativeSpeed()
        {
            if (_centerHistory.Count < 2)
            {
                RelativeSpeed = 0;
                return;
            }

            var p2 = _centerHistory.Last();

            // 300ms 이전의 가장 가까운 히스토리 포인트 p1을 찾습니다.
            double timeThreshold = p2.timeMsec - 300;
            var p1History = _centerHistory.FirstOrDefault(h => h.timeMsec <= timeThreshold);

            if (p1History.Equals(default))
            {
                RelativeSpeed = 0;
                return;
            }

            var p1 = p1History;

            double deltaMs = p2.timeMsec - p1.timeMsec;

            if (deltaMs < 100)
            {
                RelativeSpeed = 0;
                return;
            }

            // 거리 계산
            double dx = p2.center.X - p1.center.X;
            double dy = p2.center.Y - p1.center.Y;
            double distance = System.Math.Sqrt(dx * dx + dy * dy);

            // 속도 = 거리 / 시간. 상수 100을 곱하여 값을 조정 (임의의 스케일)
            RelativeSpeed = (distance / deltaMs) * 100;
        }
    
        // ===== Speed/DB 통합용 추가 필드 =====
        public double SpeedKmh { get; set; }
        public string? Direction { get; set; }
        public int LaneNumber { get; set; }
        public bool IsViolation { get; set; }
        public string? ViolationReason { get; set; }
}
}