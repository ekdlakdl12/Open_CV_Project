using OpenCvSharp;

namespace WpfApp1.Models
{
    public record Detection(Rect Box, int ClassId, float Score)
    {
        // ✅ 추적 시스템에서 할당할 ID를 위한 속성 추가
        public int TrackId { get; set; } = -1;
    }
}