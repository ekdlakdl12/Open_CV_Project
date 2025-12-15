using OpenCvSharp;
using System;

namespace WpfApp1.Services
{
    public class VideoPlayerService : IDisposable
    {
        private VideoCapture? _cap;

        public double Fps => _cap?.Fps > 0 ? _cap.Fps : 30;

        public void Open(string path)
        {
            Close();
            _cap = new VideoCapture(path);
            if (!_cap.IsOpened())
                throw new Exception($"영상 파일 열기 실패: {path}");
        }

        public bool Read(Mat frame)
        {
            if (_cap == null) return false;
            return _cap.Read(frame) && !frame.Empty();
        }

        // ✅ 처리 지연 시 프레임을 n개 “Grab”해서 스킵 (Decode/Render 안 함)
        public void GrabFrames(int n)
        {
            if (_cap == null || n <= 0) return;
            for (int i = 0; i < n; i++)
            {
                if (!_cap.Grab()) break;
            }
        }

        public int PosFrame => _cap == null ? 0 : (int)_cap.Get(VideoCaptureProperties.PosFrames);
        public double PosMsec => _cap == null ? 0 : _cap.Get(VideoCaptureProperties.PosMsec);

        public void Close()
        {
            _cap?.Release();
            _cap?.Dispose();
            _cap = null;
        }

        public void Dispose() => Close();
    }
}
