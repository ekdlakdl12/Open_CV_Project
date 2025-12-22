using OpenCvSharp;
using System;
using System.Collections.Generic;

namespace WpfApp1.Models
{
    public class TrackedObject
    {
        private static int _nextId = 0;
        public int Id { get; }
        public Rect LastBox { get; private set; }
        public double LastTime { get; private set; }
        public int LastClassId { get; private set; }
        public int MissedFrames { get; private set; }
        public bool ShouldBeDeleted => MissedFrames > 20; // 고속 주행 시 조금 더 유지

        public double Speed { get; private set; }
        private Point2f _lastCenter;
        private Queue<double> _speedHistory = new Queue<double>();

        public TrackedObject(Detection det, double time)
        {
            Id = _nextId++;
            LastBox = det.Box;
            LastTime = time;
            LastClassId = det.ClassId;
            _lastCenter = GetCenter(det.Box);
            MissedFrames = 0;
            Speed = 0;
        }

        public void Update(Detection det, double time)
        {
            double deltaTime = (time - LastTime) / 1000.0;
            if (deltaTime > 0)
            {
                Point2f currentCenter = GetCenter(det.Box);
                double pixelDistance = Distance(_lastCenter, currentCenter);

                // ✅ 고속도로 보정 계수 상향 (이미지 구도 반영)
                double rawSpeed = (pixelDistance / deltaTime) * 1.8;

                _speedHistory.Enqueue(rawSpeed);
                if (_speedHistory.Count > 8) _speedHistory.Dequeue();

                double avgSpeed = 0;
                foreach (var s in _speedHistory) avgSpeed += s;
                Speed = avgSpeed / _speedHistory.Count;

                _lastCenter = currentCenter;
            }

            LastBox = det.Box;
            LastTime = time;
            LastClassId = det.ClassId;
            MissedFrames = 0;
        }

        public void Missed() { MissedFrames++; Speed *= 0.9; }
        private Point2f GetCenter(Rect rect) => new Point2f(rect.X + rect.Width / 2.0f, rect.Y + rect.Height / 2.0f);
        private double Distance(Point2f p1, Point2f p2) => Math.Sqrt(Math.Pow(p1.X - p2.X, 2) + Math.Pow(p1.Y - p2.Y, 2));
    }
}