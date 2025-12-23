using OpenCvSharp;
using System;

namespace WpfApp1.Scripts
{
    public class TrackedObject
    {
        private static int _nextId = 1;
        public int Id { get; }
        public Rect LastBox { get; set; }
        public double LastTimeMs { get; set; }
        public int LastClassId { get; set; }
        public string FirstDetectedTime { get; } // 최초 감지 시간 고정
        public double SpeedInKmh { get; private set; }
        public int MissedFrames { get; private set; }
        public bool ShouldBeDeleted => MissedFrames > 15;

        // ViewModel의 new TrackedObject(d.ClassId, d.Box, time) 호출과 일치
        public TrackedObject(int classId, Rect box, double timeMs)
        {
            Id = _nextId++;
            LastClassId = classId;
            LastBox = box;
            LastTimeMs = timeMs;
            // 처음 객체가 생성될 때의 시스템 시간을 기록 (이후 변경 안 됨)
            FirstDetectedTime = DateTime.Now.ToString("HH:mm:ss");
            MissedFrames = 0;
            SpeedInKmh = 0;
        }

        // ViewModel의 track.Update(dets[best].ClassId, dets[best].Box, time) 호출과 일치
        public void Update(int classId, Rect newBox, double newTimeMs)
        {
            double dt = (newTimeMs - LastTimeMs) / 1000.0;
            if (dt > 0)
            {
                // 단순 픽셀 이동거리 기반 속도 계산 (보정치 0.05 적용)
                double dist = Math.Sqrt(Math.Pow(newBox.X - LastBox.X, 2) + Math.Pow(newBox.Y - LastBox.Y, 2));
                SpeedInKmh = (dist / dt) * 0.05;
            }

            LastClassId = classId;
            LastBox = newBox;
            LastTimeMs = newTimeMs;
            MissedFrames = 0;
        }

        public void Missed() => MissedFrames++;
    }
}