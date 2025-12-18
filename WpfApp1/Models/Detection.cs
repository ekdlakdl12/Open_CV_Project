using OpenCvSharp;

namespace WpfApp1.Models
{
    public record Detection(Rect Box, int ClassId, float Score)
    {
        public int TrackId { get; set; } = -1;
        public int CustomClassId { get; set; } = -1;
    }
}