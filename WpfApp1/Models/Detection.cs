using OpenCvSharp;

namespace WpfApp1.Models
{
    public class Detection
    {
        public Rect Box { get; set; }
        public float Confidence { get; set; }
        public float Score { get => Confidence; set => Confidence = value; }
        public int ClassId { get; set; }
        public int TrackId { get; set; } = -1;
        public int CustomClassId { get; set; } = -1;

        public Detection() { }

        public Detection(Rect box, int classId, float score)
        {
            this.Box = box;
            this.ClassId = classId;
            this.Score = score;
        }
    }
}