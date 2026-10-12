using System;
using InspectionView.Core.Errors;
using InspectionView.Core.Interop;
using InspectionView.Core.Types;
using OpenCvSharp;
using ErrorCode = InspectionView.Core.Errors.ErrorCode;

namespace InspectionView.Core.Operators.Common
{
    /// <summary>OpenCV 기반 이미지 연산자용 공통 헬퍼. 결과 이미지는 입력의 Domain 을 그대로 물려받는다.</summary>
    internal static class ImageOps
    {
        /// <summary>입력과 같은 크기(타입/채널은 선택)로 새 이미지.</summary>
        public static Image NewLike(Image src, PixelType? type = null, int? channels = null)
            => new Image(src.Width, src.Height, type ?? src.PixelType, channels ?? src.Channels);

        /// <summary>결과에 입력의 Domain 을 적용한다. <paramref name="result"/> 의 소유권을 가져가고 새 참조를 돌려준다.</summary>
        public static Image KeepDomain(Image result, Image src)
        {
            if (src.Domain == null)
                return result;
            var reduced = result.ReduceDomain(src.Domain);
            result.Release();
            return reduced;
        }

        /// <summary>
        /// src → dst 로 OpenCV 연산을 적용한 새 이미지. dst Mat 은 결과 이미지 메모리를 감싸므로
        /// 연산이 dst 를 재할당하지 않도록 크기/타입이 맞는 연산만 사용할 것.
        /// </summary>
        public static Image Filter(Image src, Action<Mat, Mat> op, PixelType? outType = null, int? outChannels = null)
        {
            var dst = NewLike(src, outType, outChannels);
            try
            {
                using (var s = MatInterop.AsMat(src))
                using (var d = MatInterop.AsMat(dst, forWrite: true))
                {
                    op(s, d);
                    if (d.Data != dst.GetHostPointer())
                        throw new VisionException(ErrorCode.BackendExecutionFailed, "OpenCV reallocated the output buffer (size/type mismatch).");
                }
            }
            catch
            {
                dst.Release();
                throw;
            }
            return KeepDomain(dst, src);
        }

        /// <summary>두 입력(같은 크기)으로 연산. 결과 Domain 은 첫 입력의 것.</summary>
        public static Image Binary(Image a, Image b, Action<Mat, Mat, Mat> op, PixelType? outType = null, int? outChannels = null)
        {
            if (a.Width != b.Width || a.Height != b.Height)
                throw new VisionException(ErrorCode.SizeMismatch, $"Images differ in size ({a.Width}x{a.Height} vs {b.Width}x{b.Height}).");
            if (a.Channels != b.Channels)
                throw new VisionException(ErrorCode.SizeMismatch, $"Images differ in channel count ({a.Channels} vs {b.Channels}).");

            var dst = NewLike(a, outType, outChannels);
            try
            {
                using (var ma = MatInterop.AsMat(a))
                using (var mb = MatInterop.AsMat(b))
                using (var d = MatInterop.AsMat(dst, forWrite: true))
                {
                    op(ma, mb, d);
                    if (d.Data != dst.GetHostPointer())
                        throw new VisionException(ErrorCode.BackendExecutionFailed, "OpenCV reallocated the output buffer (size/type mismatch).");
                }
            }
            catch
            {
                dst.Release();
                throw;
            }
            return KeepDomain(dst, a);
        }

        /// <summary>
        /// GPU(OpenCL T-API) 변형: 입력을 UMat 으로 올려 연산하고 결과를 내려받는다. OpenCL 디바이스가 없으면 OpenCV 가 CPU 로 수행한다.
        /// <paramref name="op"/> 는 InputArray/OutputArray 만 쓰는 OpenCV 호출이어야 한다.
        /// </summary>
        public static Image FilterGpu(Image src, Action<InputArray, OutputArray> op, PixelType? outType = null, int? outChannels = null)
        {
            var dst = NewLike(src, outType, outChannels);
            try
            {
                using (var s = MatInterop.AsMat(src))
                using (var us = new UMat())
                using (var ud = new UMat(src.Height, src.Width, MatInterop.ToMatType(dst.PixelType, dst.Channels)))
                using (var d = MatInterop.AsMat(dst, forWrite: true))
                {
                    // 복사해서 올린다: Image 의 패딩 stride 를 그대로 감싼 UMat 은 더 큰 버퍼의 ROI 로 취급되어
                    // 필터가 경계 밖(패딩) 픽셀을 읽는다.
                    s.CopyTo(us);
                    op(InputArray.Create(us), OutputArray.Create(ud));
                    ud.CopyTo(d);
                    if (d.Data != dst.GetHostPointer())
                        throw new VisionException(ErrorCode.BackendExecutionFailed, "OpenCV reallocated the output buffer (size/type mismatch).");
                }
            }
            catch
            {
                dst.Release();
                throw;
            }
            return KeepDomain(dst, src);
        }

        /// <summary>두 입력의 GPU(OpenCL T-API) 변형.</summary>
        public static Image BinaryGpu(Image a, Image b, Action<InputArray, InputArray, OutputArray> op, PixelType? outType = null, int? outChannels = null)
        {
            if (a.Width != b.Width || a.Height != b.Height)
                throw new VisionException(ErrorCode.SizeMismatch, $"Images differ in size ({a.Width}x{a.Height} vs {b.Width}x{b.Height}).");
            if (a.Channels != b.Channels)
                throw new VisionException(ErrorCode.SizeMismatch, $"Images differ in channel count ({a.Channels} vs {b.Channels}).");

            var dst = NewLike(a, outType, outChannels);
            try
            {
                using (var ma = MatInterop.AsMat(a))
                using (var mb = MatInterop.AsMat(b))
                using (var ua = new UMat())
                using (var ub = new UMat())
                using (var ud = new UMat(a.Height, a.Width, MatInterop.ToMatType(dst.PixelType, dst.Channels)))
                using (var d = MatInterop.AsMat(dst, forWrite: true))
                {
                    ma.CopyTo(ua);
                    mb.CopyTo(ub);
                    op(InputArray.Create(ua), InputArray.Create(ub), OutputArray.Create(ud));
                    ud.CopyTo(d);
                    if (d.Data != dst.GetHostPointer())
                        throw new VisionException(ErrorCode.BackendExecutionFailed, "OpenCV reallocated the output buffer (size/type mismatch).");
                }
            }
            catch
            {
                dst.Release();
                throw;
            }
            return KeepDomain(dst, a);
        }

        /// <summary>이미지 좌표계의 마스크(CV_8UC1). region 이 null 이면 이미지 Domain, 둘 다 없으면 null(전체).</summary>
        public static Mat? MaskFor(Image image, Region? region)
        {
            var r = region ?? image.Domain;
            if (r == null)
                return null;
            return r.ToMask(image.Width, image.Height);
        }

        /// <summary>홀수 커널 크기 검사.</summary>
        public static void RequireOddSize(int value, string name)
        {
            if (value < 1 || value % 2 == 0)
                throw VisionException.InvalidArgument(name, $"must be an odd number >= 1 (got {value})");
        }
    }
}
