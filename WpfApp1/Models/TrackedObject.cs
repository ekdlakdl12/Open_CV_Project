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
        public int MissedFrames { get; private set; }
        public bool ShouldBeDeleted => MissedFrames > 15; // 넉넉하게 15프레임 대기

        public double Speed { get; private set; }
        private Point2f _lastCenter;

        // 속도 튐 방지를 위한 버퍼
        private Queue<double> _speedHistory = new Queue<double>();

        public TrackedObject(Detection det, double time)
        {
            Id = _nextId++;
            LastBox = det.Box;
            LastTime = time;
            _lastCenter = GetCenter(det.Box);
            MissedFrames = 0;
            Speed = 0;
        }

        public void Update(Detection det, double time)
        {
            double deltaTime = (time - LastTime) / 1000.0; // 밀리초를 초(s) 단위로 변환

            if (deltaTime > 0)
            {
                Point2f currentCenter = GetCenter(det.Box);
                double pixelDistance = Distance(_lastCenter, currentCenter);

                // [팩트체크] 고속도로 영상(1920x1080) 기준, 픽셀 거리당 보정 계수 상향
                // 기존 0.1에서 1.5~2.5 정도로 상향해야 실제 속도(60~100km/h)와 비슷해집니다.
                double rawSpeed = (pixelDistance / deltaTime) * 0.25;

                // 갑작스러운 속도 튐 방지 (이동 평균 필터)
                _speedHistory.Enqueue(rawSpeed);
                if (_speedHistory.Count > 5) _speedHistory.Dequeue();

                double avgSpeed = 0;
                foreach (var s in _speedHistory) avgSpeed += s;
                Speed = avgSpeed / _speedHistory.Count;

                _lastCenter = currentCenter;
            }

            LastBox = det.Box;
            LastTime = time;
            MissedFrames = 0;
        }

        public void Missed()
        {
            MissedFrames++;
            Speed *= 0.8; // 놓치면 속도 감속
        }

        private Point2f GetCenter(Rect rect)
        {
            return new Point2f(rect.X + rect.Width / 2.0f, rect.Y + rect.Height / 2.0f);
        }

        private double Distance(Point2f p1, Point2f p2)
        {
            return Math.Sqrt(Math.Pow(p1.X - p2.X, 2) + Math.Pow(p1.Y - p2.Y, 2));
        }
    }
}