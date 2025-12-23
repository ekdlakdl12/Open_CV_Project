using OpenCvSharp;
using System;

namespace WpfApp1.Models
{
    public class TrackedObject
    {
        public int Id { get; }
        public Rect LastBox { get; set; }
        public int LastClassId { get; set; }  // ✅ 누락 에러 해결
        public double SpeedInKmh { get; set; } // ✅ 누락 에러 해결
        public double LastTimeMs { get; set; }
        private int _missedFrames = 0;
        public bool ShouldBeDeleted => _missedFrames > 15;

        private static int _nextId = 1;

        public TrackedObject(int classId, Rect box, double timeMs)
        {
            Id = _nextId++;
            LastClassId = classId;
            LastBox = box;
            LastTimeMs = timeMs;
        }

        public void Update(int classId, Rect box, double timeMs)
        {
            if (LastTimeMs > 0)
            {
                double dist = Math.Sqrt(Math.Pow(box.X - LastBox.X, 2) + Math.Pow(box.Y - LastBox.Y, 2));
                double timeSec = (timeMs - LastTimeMs) / 1000.0;
                if (timeSec > 0) SpeedInKmh = (dist / timeSec) * 0.4;
            }
            LastClassId = classId;
            LastBox = box;
            LastTimeMs = timeMs;
            _missedFrames = 0;
        }
        public void Missed() => _missedFrames++;
    }
}