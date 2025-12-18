using OpenCvSharp;

namespace WpfApp1.Models
{
    public class Detection
    {
        public Rect Box { get; set; }
        public float Confidence { get; set; }
        // YoloV8Onnx.cs 183번 줄에서 Score라는 이름을 사용하므로 추가
        public float Score { get => Confidence; set => Confidence = value; }
        public int ClassId { get; set; }
        public int TrackId { get; set; } = -1;
        public int CustomClassId { get; set; } = -1;

        public Detection() { }

        // [오류 해결 핵심] image_924ed4.png의 호출 순서(Rect, int, float)와 완벽히 일치시킴
        public Detection(Rect box, int classId, float score)
        {
            this.Box = box;
            this.ClassId = classId;
            this.Score = score;
        }
    }
}