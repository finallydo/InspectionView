using System;
using InspectionView.Core.Errors;
using InspectionView.Core.Interop;
using InspectionView.Core.Types;
using OpenCvSharp;
using ErrorCode = InspectionView.Core.Errors.ErrorCode;

namespace InspectionView.Core.Cameras
{
    /// <summary>벤더 플러그인용 프레임 변환 헬퍼: SDK 버퍼 → <see cref="Image"/>.</summary>
    public static class CameraFrames
    {
        public enum BayerPattern { RG, GR, BG, GB }

        /// <summary>버퍼를 복사해 이미지로 만든다 (stride 0 이면 촘촘한 배치).</summary>
        public static Image FromBuffer(IntPtr data, int width, int height, long stride, PixelType type, int channels = 1)
            => Image.FromBuffer(data, width, height, stride, type, channels);

        /// <summary>RGB(8bit, 3채널) 버퍼 → BGR 이미지 (InspectionView 컬러 순서는 OpenCV 와 같은 BGR).</summary>
        public static Image FromRgb8(IntPtr data, int width, int height, long stride)
        {
            if (stride <= 0) stride = (long)width * 3;
            using (var src = Mat.FromPixelData(height, width, MatType.CV_8UC3, data, stride))
            using (var bgr = new Mat())
            {
                Cv2.CvtColor(src, bgr, ColorConversionCodes.RGB2BGR);
                return MatInterop.ToImage(bgr);
            }
        }

        /// <summary>8/16bit Bayer 버퍼 → BGR 이미지.</summary>
        public static Image FromBayer(IntPtr data, int width, int height, long stride, BayerPattern pattern, bool sixteenBit = false)
        {
            var type = sixteenBit ? MatType.CV_16UC1 : MatType.CV_8UC1;
            if (stride <= 0) stride = (long)width * (sixteenBit ? 2 : 1);
            ColorConversionCodes code;
            switch (pattern)
            {
                // OpenCV 의 Bayer 코드 이름은 두 번째 행/열 기준이라 벤더 표기와 반대다
                case BayerPattern.RG: code = ColorConversionCodes.BayerBG2BGR; break;
                case BayerPattern.GR: code = ColorConversionCodes.BayerGB2BGR; break;
                case BayerPattern.BG: code = ColorConversionCodes.BayerRG2BGR; break;
                default: code = ColorConversionCodes.BayerGR2BGR; break;
            }
            using (var src = Mat.FromPixelData(height, width, type, data, stride))
            using (var bgr = new Mat())
            {
                Cv2.CvtColor(src, bgr, code);
                return MatInterop.ToImage(bgr);
            }
        }

        /// <summary>
        /// 일반 GenICam 픽셀 포맷 이름(Mono8, Mono10, Mono12, Mono16, RGB8, BGR8, BayerRG8, BayerGB12 ...)으로 변환한다.
        /// 지원하지 않는 포맷은 <see cref="ErrorCode.UnsupportedFormat"/>.
        /// </summary>
        public static Image FromPixelFormat(string pixelFormat, IntPtr data, int width, int height, long stride)
        {
            string f = pixelFormat ?? "";
            if (f.StartsWith("Mono", StringComparison.OrdinalIgnoreCase))
            {
                if (f.EndsWith("8")) return FromBuffer(data, width, height, stride, PixelType.UInt8);
                if (f.EndsWith("10") || f.EndsWith("12") || f.EndsWith("14") || f.EndsWith("16"))
                    return FromBuffer(data, width, height, stride, PixelType.UInt16);
            }
            if (f.Equals("BGR8", StringComparison.OrdinalIgnoreCase) || f.Equals("BGR8Packed", StringComparison.OrdinalIgnoreCase))
                return FromBuffer(data, width, height, stride, PixelType.UInt8, 3);
            if (f.Equals("RGB8", StringComparison.OrdinalIgnoreCase) || f.Equals("RGB8Packed", StringComparison.OrdinalIgnoreCase))
                return FromRgb8(data, width, height, stride);
            if (f.StartsWith("Bayer", StringComparison.OrdinalIgnoreCase) && f.Length >= 8)
            {
                string pat = f.Substring(5, 2).ToUpperInvariant();
                string depth = f.Substring(7);
                BayerPattern bp;
                switch (pat)
                {
                    case "RG": bp = BayerPattern.RG; break;
                    case "GR": bp = BayerPattern.GR; break;
                    case "BG": bp = BayerPattern.BG; break;
                    case "GB": bp = BayerPattern.GB; break;
                    default: throw new VisionException(ErrorCode.UnsupportedFormat, $"Unsupported pixel format '{pixelFormat}'.");
                }
                if (depth == "8") return FromBayer(data, width, height, stride, bp, false);
                if (depth == "10" || depth == "12" || depth == "16") return FromBayer(data, width, height, stride, bp, true);
            }
            throw new VisionException(ErrorCode.UnsupportedFormat, $"Unsupported pixel format '{pixelFormat}' (set the camera to Mono8/Mono16/BGR8/RGB8/Bayer*8 or unpacked Bayer*12).");
        }
    }
}
