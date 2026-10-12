using System;
using System.Threading.Tasks;
using InspectionView.Core.Errors;
using InspectionView.Core.Interop;
using InspectionView.Core.Types;
using OpenCvSharp;
using ErrorCode = InspectionView.Core.Errors.ErrorCode;

namespace InspectionView.Core.Operators.Surface
{
    /// <summary>이미지 스택(같은 크기의 1채널 이미지 여러 장)을 다루는 2.5D 연산자 공통 헬퍼.</summary>
    internal static class StackOps
    {
        /// <summary>스택 검증: 최소 개수, 1채널, 같은 크기 (<paramref name="sameType"/> 이면 픽셀 타입도 동일).</summary>
        public static void Validate(ObjectArray<Image> images, int minCount, string name, bool sameType = false)
        {
            if (images.Count < minCount)
                throw new VisionException(ErrorCode.InvalidArgument, $"'{name}' needs at least {minCount} images (got {images.Count}).");
            var first = images[0];
            for (int i = 0; i < images.Count; i++)
            {
                var img = images[i];
                if (img.Channels != 1)
                    throw new VisionException(ErrorCode.TypeMismatch, $"'{name}'[{i}] must be a single-channel image (got {img.Channels} channels).");
                if (img.Width != first.Width || img.Height != first.Height)
                    throw new VisionException(ErrorCode.SizeMismatch, $"'{name}'[{i}] differs in size ({img.Width}x{img.Height} vs {first.Width}x{first.Height}).");
                if (sameType && img.PixelType != first.PixelType)
                    throw new VisionException(ErrorCode.TypeMismatch, $"'{name}'[{i}] differs in pixel type ({img.PixelType} vs {first.PixelType}).");
            }
        }

        /// <summary>
        /// Float32 1채널 사본 (컬러는 그레이로 변환). 이미 Float32 1채널이면 AddRef 한 같은 객체를 돌려준다.
        /// 호출자가 Release 한다.
        /// </summary>
        public static Image ToFloat(Image src)
        {
            if (src.PixelType == PixelType.Float32 && src.Channels == 1)
            {
                src.AddRef();
                return src;
            }
            var dst = new Image(src.Width, src.Height, PixelType.Float32, 1);
            try
            {
                using (var s = MatInterop.AsMat(src))
                using (var d = MatInterop.AsMat(dst, forWrite: true))
                {
                    if (src.Channels == 1)
                    {
                        s.ConvertTo(d, MatType.CV_32FC1);
                    }
                    else if (src.Channels == 3 || src.Channels == 4)
                    {
                        using (var g = new Mat())
                        {
                            Cv2.CvtColor(s, g, src.Channels == 3 ? ColorConversionCodes.BGR2GRAY : ColorConversionCodes.BGRA2GRAY);
                            g.ConvertTo(d, MatType.CV_32FC1);
                        }
                    }
                    else
                    {
                        throw new VisionException(ErrorCode.TypeMismatch, $"Unsupported channel count {src.Channels}.");
                    }
                }
            }
            catch
            {
                dst.Release();
                throw;
            }
            return dst;
        }

        /// <summary>스택 전체를 Float32 로. 실패 시 이미 만든 사본을 해제한다.</summary>
        public static Image[] ToFloat(ObjectArray<Image> images)
        {
            var result = new Image[images.Count];
            try
            {
                for (int i = 0; i < images.Count; i++)
                    result[i] = ToFloat(images[i]);
            }
            catch
            {
                ReleaseAll(result);
                throw;
            }
            return result;
        }

        public static void ReleaseAll(Image?[] images)
        {
            foreach (var img in images)
                img?.Release();
        }

        /// <summary>Float32 1채널 새 이미지 (0 초기화).</summary>
        public static Image NewFloat(int width, int height) => Image.Create(width, height, PixelType.Float32, 1, clear: true);

        /// <summary>Float32 이미지의 행 포인터.</summary>
        public static unsafe float* Row(Image img, int y, bool forWrite = false)
            => (float*)img.GetRowPointer(y, forWrite);

        /// <summary>행 범위를 순차 또는 병렬로 처리한다 (<paramref name="options"/> 가 null 이면 순차).</summary>
        public static void ForRows(int height, ParallelOptions? options, Action<int> body)
        {
            if (options == null)
            {
                for (int y = 0; y < height; y++) body(y);
            }
            else
            {
                Parallel.For(0, height, options, body);
            }
        }

        /// <summary>
        /// N x 3 행렬 A 의 최소제곱 의사역행렬 (AᵀA)⁻¹Aᵀ (3 x N). 특이하면 null.
        /// </summary>
        public static double[,]? PseudoInverse3(double[,] a)
        {
            int n = a.GetLength(0);
            var ata = new double[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    double s = 0;
                    for (int k = 0; k < n; k++) s += a[k, i] * a[k, j];
                    ata[i, j] = s;
                }

            double det =
                ata[0, 0] * (ata[1, 1] * ata[2, 2] - ata[1, 2] * ata[2, 1]) -
                ata[0, 1] * (ata[1, 0] * ata[2, 2] - ata[1, 2] * ata[2, 0]) +
                ata[0, 2] * (ata[1, 0] * ata[2, 1] - ata[1, 1] * ata[2, 0]);
            if (Math.Abs(det) < 1e-12)
                return null;

            var inv = new double[3, 3];
            inv[0, 0] = (ata[1, 1] * ata[2, 2] - ata[1, 2] * ata[2, 1]) / det;
            inv[0, 1] = (ata[0, 2] * ata[2, 1] - ata[0, 1] * ata[2, 2]) / det;
            inv[0, 2] = (ata[0, 1] * ata[1, 2] - ata[0, 2] * ata[1, 1]) / det;
            inv[1, 0] = (ata[1, 2] * ata[2, 0] - ata[1, 0] * ata[2, 2]) / det;
            inv[1, 1] = (ata[0, 0] * ata[2, 2] - ata[0, 2] * ata[2, 0]) / det;
            inv[1, 2] = (ata[0, 2] * ata[1, 0] - ata[0, 0] * ata[1, 2]) / det;
            inv[2, 0] = (ata[1, 0] * ata[2, 1] - ata[1, 1] * ata[2, 0]) / det;
            inv[2, 1] = (ata[0, 1] * ata[2, 0] - ata[0, 0] * ata[2, 1]) / det;
            inv[2, 2] = (ata[0, 0] * ata[1, 1] - ata[0, 1] * ata[1, 0]) / det;

            var m = new double[3, n];
            for (int i = 0; i < 3; i++)
                for (int k = 0; k < n; k++)
                {
                    double s = 0;
                    for (int j = 0; j < 3; j++) s += inv[i, j] * a[k, j];
                    m[i, k] = s;
                }
            return m;
        }
    }
}
