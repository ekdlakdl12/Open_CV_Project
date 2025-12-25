using System;
using OpenCvSharp;

namespace WpfApp1.Scripts
{
    public struct YolopResult : IDisposable
    {
        public Mat DrivableMaskOrig { get; set; }
        public Mat LaneProbOrig { get; set; }

        public void Dispose()
        {
            DrivableMaskOrig?.Dispose();
            LaneProbOrig?.Dispose();
        }
    }

    public class YolopDetectService
    {
        // 실제 추론을 위한 세션 변수들이 여기에 있어야 합니다.
        // 여기서는 구조를 잡기 위해 결과 Mat을 생성하는 로직을 넣습니다.

        public YolopDetectService(string modelPath, int size, float conf, float iou) { }

        public YolopResult Infer(Mat frame)
        {
            // [중요] 여기서 실제 ONNX 추론 결과가 Mat으로 생성되어야 합니다.
            // 테스트를 위해 프레임 크기와 동일한 빈 Mat을 생성하여 반환하는 구조입니다.
            // 실제 구현부에서는 모델의 Output Tensor를 Mat으로 변환하는 코드가 작동해야 합니다.

            Mat driveMask = new Mat(frame.Size(), MatType.CV_8UC1, Scalar.All(0));
            Mat laneProb = new Mat(frame.Size(), MatType.CV_32FC1, Scalar.All(0));

            return new YolopResult
            {
                DrivableMaskOrig = driveMask,
                LaneProbOrig = laneProb
            };
        }
    }
}