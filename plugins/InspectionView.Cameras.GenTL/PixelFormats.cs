using System;
using System.Collections.Generic;
using InspectionView.Core.Cameras;
using InspectionView.Core.Errors;
using InspectionView.Core.Interop;
using InspectionView.Core.Types;
using OpenCvSharp;
using ErrorCode = InspectionView.Core.Errors.ErrorCode;

namespace InspectionView.Cameras.GenTL
{
    /// <summary>
    /// PFNC(GenICam Pixel Format Naming Convention) 코드 → 이미지 변환. 코드 구조: 비트 31-24 = 단색(0x01)/컬러(0x02),
    /// 23-16 = 픽셀당 비트 수, 15-0 = 번호. GigE Vision(GEV) 이름공간의 공통 포맷은 같은 값을 쓴다.
    /// </summary>
    public static class PixelFormats
    {
        private static readonly Dictionary<long, string> Names = new Dictionary<long, string>
        {
            [0x01080001] = "Mono8",
            [0x01100003] = "Mono10",
            [0x010C0004] = "Mono10Packed",
            [0x01100005] = "Mono12",
            [0x010C0006] = "Mono12Packed",
            [0x01100025] = "Mono14",
            [0x01100007] = "Mono16",
            [0x010A0046] = "Mono10p",
            [0x010C0047] = "Mono12p",
            [0x01080008] = "BayerGR8",
            [0x01080009] = "BayerRG8",
            [0x0108000A] = "BayerGB8",
            [0x0108000B] = "BayerBG8",
            [0x0110000C] = "BayerGR10",
            [0x0110000D] = "BayerRG10",
            [0x0110000E] = "BayerGB10",
            [0x0110000F] = "BayerBG10",
            [0x01100010] = "BayerGR12",
            [0x01100011] = "BayerRG12",
            [0x01100012] = "BayerGB12",
            [0x01100013] = "BayerBG12",
            [0x0110002E] = "BayerGR16",
            [0x0110002F] = "BayerRG16",
            [0x01100030] = "BayerGB16",
            [0x01100031] = "BayerBG16",
            [0x02180014] = "RGB8",
            [0x02180015] = "BGR8",
            [0x02200016] = "RGBa8",
            [0x02200017] = "BGRa8",
            [0x0210001F] = "YUV422_8_UYVY",
            [0x02100032] = "YUV422_8",
            [0x0210003B] = "YCbCr422_8",
        };

        public static string? NameOf(long code) => Names.TryGetValue(code, out var n) ? n : null;

        public static long? CodeOf(string name)
        {
            foreach (var kv in Names)
                if (string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase)) return kv.Key;
            return null;
        }

        /// <summary>픽셀당 비트 수 (PFNC 코드의 비트 23-16).</summary>
        public static int BitsPerPixel(long code) => (int)((code >> 16) & 0xFF);

        /// <summary>한 줄의 바이트 수 (패딩 제외).</summary>
        public static long LineBytes(long code, int width) => ((long)width * BitsPerPixel(code) + 7) / 8;

        /// <summary>
        /// 버퍼를 복사해 이미지를 만든다. 10/12/14bit 는 UInt16 (값 범위는 원래 비트 수), Bayer/RGB/YUV 는 BGR 컬러.
        /// <paramref name="xPadding"/> 은 각 줄 끝의 추가 바이트.
        /// </summary>
        public static unsafe Image ToImage(long code, IntPtr data, int width, int height, long xPadding)
        {
            string name = NameOf(code) ?? throw new VisionException(ErrorCode.UnsupportedFormat, $"Unsupported pixel format 0x{code:X8}.");
            long stride = LineBytes(code, width) + Math.Max(0, xPadding);
            switch (name)
            {
                case "Mono10Packed":
                case "Mono12Packed":
                    return UnpackGev(data, width, height, stride, name == "Mono12Packed" ? 12 : 10);
                case "Mono10p":
                    return UnpackLsb(data, width, height, stride, 10);
                case "Mono12p":
                    return UnpackLsb(data, width, height, stride, 12);
                case "RGBa8":
                case "BGRa8":
                    return Convert(data, width, height, stride, MatType.CV_8UC4, name == "RGBa8" ? ColorConversionCodes.RGBA2BGR : ColorConversionCodes.BGRA2BGR);
                case "YUV422_8_UYVY":
                    return Convert(data, width, height, stride, MatType.CV_8UC2, ColorConversionCodes.YUV2BGR_UYVY);
                case "YUV422_8":
                case "YCbCr422_8":
                    return Convert(data, width, height, stride, MatType.CV_8UC2, ColorConversionCodes.YUV2BGR_YUY2);
                default:
                    return CameraFrames.FromPixelFormat(name, data, width, height, stride);
            }
        }

        private static Image Convert(IntPtr data, int width, int height, long stride, MatType type, ColorConversionCodes code)
        {
            using (var src = Mat.FromPixelData(height, width, type, data, stride))
            using (var bgr = new Mat())
            {
                Cv2.CvtColor(src, bgr, code);
                return MatInterop.ToImage(bgr);
            }
        }

        /// <summary>
        /// GigE Vision Mono10Packed/Mono12Packed: 2픽셀 = 3바이트. b0 = p0 상위 비트, b1 하위 니블 = p0 하위, b1 상위 니블 = p1 하위, b2 = p1 상위 비트.
        /// </summary>
        public static unsafe Image UnpackGev(IntPtr data, int width, int height, long stride, int bits)
        {
            var img = Image.Create(width, height, PixelType.UInt16);
            int low = bits - 8;            // 하위 비트 수 (12bit → 4, 10bit → 2)
            int lowMask = (1 << low) - 1;
            for (int y = 0; y < height; y++)
            {
                byte* src = (byte*)data + stride * y;
                var row = img.GetRow<ushort>(y);
                for (int x = 0; x + 1 < width; x += 2, src += 3)
                {
                    row[x] = (ushort)((src[0] << low) | (src[1] & lowMask));
                    row[x + 1] = (ushort)((src[2] << low) | ((src[1] >> 4) & lowMask));
                }
                if ((width & 1) != 0)
                    row[width - 1] = (ushort)((src[0] << low) | (src[1] & lowMask));
            }
            return img;
        }

        /// <summary>PFNC Mono10p/Mono12p: 비트 스트림을 LSB 부터 채운다 (픽셀 i = 비트 i*bits ...).</summary>
        public static unsafe Image UnpackLsb(IntPtr data, int width, int height, long stride, int bits)
        {
            var img = Image.Create(width, height, PixelType.UInt16);
            int mask = (1 << bits) - 1;
            for (int y = 0; y < height; y++)
            {
                byte* src = (byte*)data + stride * y;
                var row = img.GetRow<ushort>(y);
                for (int x = 0; x < width; x++)
                {
                    long bit = (long)x * bits;
                    long b = bit >> 3;
                    int shift = (int)(bit & 7);
                    // 최대 12bit + 7 shift = 19bit → 3바이트면 충분
                    int v = src[b] | (src[b + 1] << 8) | ((shift + bits > 16 ? src[b + 2] : 0) << 16);
                    row[x] = (ushort)((v >> shift) & mask);
                }
            }
            return img;
        }
    }
}
