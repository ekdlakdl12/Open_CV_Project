using OpenCvSharp;

namespace WpfApp1.Models
{
    public record Detection(Rect Box, int ClassId, float Score);
}
