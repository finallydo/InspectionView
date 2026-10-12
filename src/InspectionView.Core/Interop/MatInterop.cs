using System;
using InspectionView.Core.Errors;
using InspectionView.Core.Types;
using OpenCvSharp;

namespace InspectionView.Core.Interop
{
    /// <summary>
    /// <see cref="Image"/> ↔ OpenCvSharp <see cref="Mat"/> 변환.
    /// <see cref="AsMat"/> 은 복사 없이 Image 의 Host 메모리를 감싸므로 Image 가 살아있는 동안만 유효하다.
    /// </summary>
    public static class MatInterop
    {
        public static MatType ToMatType(PixelType pixelType, int channels)
        {
            if (channels <= 0 || channels > CV_CN_MAX)
                throw VisionException.OutOfRange(nameof(channels), channels, 1, CV_CN_MAX);

            int depth;
            switch (pixelType)
            {
                case PixelType.UInt8: depth = MatType.CV_8U; break;
                case PixelType.Int16: depth = MatType.CV_16S; break;
                case PixelType.UInt16: depth = MatType.CV_16U; break;
                case PixelType.Int32: depth = MatType.CV_32S; break;
                case PixelType.Float32: depth = MatType.CV_32F; break;
                case PixelType.Float64: depth = MatType.CV_64F; break;
                default:
                    throw new VisionException(ErrorCode.UnsupportedFormat, $"Unsupported pixel type {pixelType}");
            }
            return MatType.MakeType(depth, channels);
        }

        private const int CV_CN_MAX = 512;

        public static PixelType FromMatDepth(int depth)
        {
            switch (depth)
            {
                case MatType.CV_8U: return PixelType.UInt8;
                case MatType.CV_16S: return PixelType.Int16;
                case MatType.CV_16U: return PixelType.UInt16;
                case MatType.CV_32S: return PixelType.Int32;
                case MatType.CV_32F: return PixelType.Float32;
                case MatType.CV_64F: return PixelType.Float64;
                default:
                    throw new VisionException(ErrorCode.UnsupportedFormat, $"Unsupported OpenCV depth {depth} (CV_8S is not supported).");
            }
        }

        /// <summary>Mat 의 내용을 새 Image 로 복사한다. 채널 순서는 OpenCV 그대로(BGR) 유지된다.</summary>
        public static unsafe Image ToImage(Mat mat)
        {
            if (mat == null) throw VisionException.NullArgument(nameof(mat));
            if (mat.Empty()) throw new VisionException(ErrorCode.EmptyObject, "Mat is empty.");
            if (mat.Dims != 2) throw new VisionException(ErrorCode.UnsupportedFormat, "Only 2-D Mat is supported.");

            var pixelType = FromMatDepth(mat.Depth());
            int channels = mat.Channels();
            var img = new Image(mat.Cols, mat.Rows, pixelType, channels);

            long rowBytes = (long)mat.Cols * img.BytesPerPixel;
            long srcStep = mat.Step();
            byte* src = (byte*)mat.Data;
            byte* dst = (byte*)img.GetHostPointer(forWrite: true);

            for (int y = 0; y < mat.Rows; y++)
            {
                Buffer.MemoryCopy(src + srcStep * y, dst + img.Stride * y, img.Stride, rowBytes);
            }
            return img;
        }

        /// <summary>
        /// Image 의 Host 메모리를 복사 없이 감싸는 Mat. 반환된 Mat 은 Image 보다 오래 살아서는 안 된다.
        /// <paramref name="forWrite"/> 가 true 면 Device 사본이 무효화된다.
        /// </summary>
        public static Mat AsMat(Image image, bool forWrite = false)
        {
            if (image == null) throw VisionException.NullArgument(nameof(image));
            var ptr = image.GetHostPointer(forWrite);
            return new Mat(image.Height, image.Width, ToMatType(image.PixelType, image.Channels), ptr, image.Stride);
        }

        /// <summary>Domain 바운딩 박스 부분만 감싸는 Mat (ROI 뷰, 복사 없음).</summary>
        public static Mat AsMat(Image image, RectI roi, bool forWrite = false)
        {
            if (image == null) throw VisionException.NullArgument(nameof(image));
            var clipped = roi.Intersect(image.Bounds);
            if (clipped.IsEmpty)
                throw new VisionException(ErrorCode.EmptyObject, "ROI does not intersect the image.");

            var basePtr = image.GetHostPointer(forWrite).ToInt64();
            var ptr = new IntPtr(basePtr + image.Stride * clipped.Y + (long)clipped.X * image.BytesPerPixel);
            return new Mat(clipped.Height, clipped.Width, ToMatType(image.PixelType, image.Channels), ptr, image.Stride);
        }

        /// <summary>Image 를 독립된 Mat 으로 복사한다.</summary>
        public static Mat ToMatCopy(Image image)
        {
            using (var view = AsMat(image))
            {
                return view.Clone();
            }
        }
    }
}
