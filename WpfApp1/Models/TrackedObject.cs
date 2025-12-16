using OpenCvSharp;
using System.Collections.Generic;
using System.Linq;

namespace WpfApp1.Models
{
    public class TrackedObject
    {
        public int Id { get; }
        public int ClassId { get; set; }
        public Rect LastBox { get; private set; }

        private readonly List<(Point center, double timeMsec)> _centerHistory = new();

        public int TrackCount { get; private set; } = 0;

        public double RelativeSpeed { get; private set; } = 0;

        private static int _nextId = 1;

        public TrackedObject(Detection detection, double timeMsec)
        {
            this.Id = _nextId++;
            this.ClassId = detection.ClassId;
            Update(detection, timeMsec);
        }

        public void Update(Detection detection, double timeMsec)
        {
            this.LastBox = detection.Box;
            this.ClassId = detection.ClassId;
            detection.TrackId = this.Id;

            Point currentCenter = GetCenter(detection.Box);
            _centerHistory.Add((currentCenter, timeMsec));
            TrackCount++;

            double oldestValidTime = timeMsec - 2000;
            _centerHistory.RemoveAll(h => h.timeMsec < oldestValidTime);

            CalculateRelativeSpeed();
        }

        public void Missed()
        {
            // 추적 실패 시 처리
        }

        private Point GetCenter(Rect box) => new(box.X + box.Width / 2, box.Y + box.Height / 2);

        private void CalculateRelativeSpeed()
        {
            if (_centerHistory.Count < 2)
            {
                RelativeSpeed = 0;
                return;
            }

            var p2 = _centerHistory.Last();

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

            double dx = p2.center.X - p1.center.X;
            double dy = p2.center.Y - p1.center.Y;
            double distance = System.Math.Sqrt(dx * dx + dy * dy);

            RelativeSpeed = (distance / deltaMs) * 100;
        }
    }
}