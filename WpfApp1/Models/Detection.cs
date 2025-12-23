using OpenCvSharp;

namespace WpfApp1.Models
{
    public sealed class Detection
    {
        public Rect Box { get; set; }
        public float Confidence { get; set; }

        // 일부 코드에서 Score 이름을 쓰는 경우 호환
        public float Score
        {
            get => Confidence;
            set => Confidence = value;
        }

        public int ClassId { get; set; }

        // 트래킹 매칭 후 채움
        public int TrackId { get; set; } = -1;

        // (옵션) 사람이 읽기 쉬운 클래스명
        public string? ClassName { get; set; }

        public Detection() { }

        public Detection(Rect box, int classId, float score, string? className = null)
        {
            Box = box;
            ClassId = classId;
            Score = score;
            ClassName = className;
        }
    }
}
