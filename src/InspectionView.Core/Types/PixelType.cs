using InspectionView.Core.Errors;

namespace InspectionView.Core.Types
{
    /// <summary>
    /// 이미지 한 채널의 원소 타입. 채널 수는 <see cref="Image.Channels"/> 로 별도 관리한다
    /// (Color = UInt8 x 3채널 등).
    /// </summary>
    public enum PixelType
    {
        UInt8,
        Int16,
        UInt16,
        Int32,
        Float32,
        Float64,
    }

    public static class PixelTypeExtensions
    {
        /// <summary>원소 하나의 바이트 수.</summary>
        public static int BytesPerElement(this PixelType type)
        {
            switch (type)
            {
                case PixelType.UInt8: return 1;
                case PixelType.Int16:
                case PixelType.UInt16: return 2;
                case PixelType.Int32:
                case PixelType.Float32: return 4;
                case PixelType.Float64: return 8;
                default:
                    throw new VisionException(ErrorCode.UnsupportedFormat, $"Unknown pixel type {type}");
            }
        }

        public static bool IsFloatingPoint(this PixelType type)
            => type == PixelType.Float32 || type == PixelType.Float64;

        /// <summary>해당 타입이 표현 가능한 최소/최대값 (정수형). 실수형은 0~1 이 아닌 전체 범위로 간주.</summary>
        public static (double Min, double Max) ValueRange(this PixelType type)
        {
            switch (type)
            {
                case PixelType.UInt8: return (byte.MinValue, byte.MaxValue);
                case PixelType.Int16: return (short.MinValue, short.MaxValue);
                case PixelType.UInt16: return (ushort.MinValue, ushort.MaxValue);
                case PixelType.Int32: return (int.MinValue, int.MaxValue);
                case PixelType.Float32: return (float.MinValue, float.MaxValue);
                case PixelType.Float64: return (double.MinValue, double.MaxValue);
                default:
                    throw new VisionException(ErrorCode.UnsupportedFormat, $"Unknown pixel type {type}");
            }
        }

        /// <summary>스크립트/도움말에 표시되는 짧은 이름 (Halcon 의 'byte', 'uint2' 등에 대응).</summary>
        public static string ShortName(this PixelType type)
        {
            switch (type)
            {
                case PixelType.UInt8: return "byte";
                case PixelType.Int16: return "int2";
                case PixelType.UInt16: return "uint2";
                case PixelType.Int32: return "int4";
                case PixelType.Float32: return "real";
                case PixelType.Float64: return "double";
                default: return type.ToString();
            }
        }
    }
}
