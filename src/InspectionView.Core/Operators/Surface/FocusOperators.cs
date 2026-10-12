using System;
using InspectionView.Core.Backends;
using InspectionView.Core.Errors;
using InspectionView.Core.Interop;
using InspectionView.Core.Operators.Common;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;
using OpenCvSharp;
using ErrorCode = InspectionView.Core.Errors.ErrorCode;

namespace InspectionView.Core.Operators.Surface
{
    public enum FocusMethod
    {
        /// <summary>Sum of modified Laplacian (|Ixx| + |Iyy| 를 창 안에서 합산). 기본값.</summary>
        ModifiedLaplacian,
        /// <summary>라플라시안 에너지 (Laplacian² 합산).</summary>
        Laplacian,
        /// <summary>Tenengrad (Sobel 기울기 에너지 합산).</summary>
        Tenengrad,
        /// <summary>국소 그레이 분산.</summary>
        Variance,
    }

    /// <summary>포커스 측도. DepthFromFocus 와 AutoFocus(카메라 루프) 가 공유한다.</summary>
    internal static class FocusOps
    {
        /// <summary>픽셀별 포커스 측도 이미지 (Float32, 값이 클수록 선명). 전체 Domain.</summary>
        public static Image Measure(Image src, FocusMethod method, int size)
        {
            var f = StackOps.ToFloat(src);
            try
            {
                var dst = new Image(src.Width, src.Height, PixelType.Float32, 1);
                try
                {
                    using (var s = MatInterop.AsMat(f))
                    using (var d = MatInterop.AsMat(dst, forWrite: true))
                    using (var tmp = new Mat())
                    {
                        var ksize = new Size(size, size);
                        switch (method)
                        {
                            case FocusMethod.ModifiedLaplacian:
                                using (var kx = new Mat(1, 3, MatType.CV_32F))
                                using (var ky = new Mat(3, 1, MatType.CV_32F))
                                using (var tx = new Mat())
                                using (var ty = new Mat())
                                {
                                    kx.Set(0, 0, 1f); kx.Set(0, 1, -2f); kx.Set(0, 2, 1f);
                                    ky.Set(0, 0, 1f); ky.Set(1, 0, -2f); ky.Set(2, 0, 1f);
                                    Cv2.Filter2D(s, tx, MatType.CV_32F, kx, null, 0, BorderTypes.Reflect101);
                                    Cv2.Filter2D(s, ty, MatType.CV_32F, ky, null, 0, BorderTypes.Reflect101);
                                    using (var ax = Cv2.Abs(tx))
                                    using (var ay = Cv2.Abs(ty))
                                    using (var sum = ax + ay)
                                    using (var sumMat = sum.ToMat())
                                    {
                                        Cv2.BoxFilter(sumMat, d, MatType.CV_32F, ksize, null, false, BorderTypes.Reflect101);
                                    }
                                }
                                break;

                            case FocusMethod.Laplacian:
                                Cv2.Laplacian(s, tmp, MatType.CV_32F, 3, 1, 0, BorderTypes.Reflect101);
                                Cv2.Multiply(tmp, tmp, tmp);
                                Cv2.BoxFilter(tmp, d, MatType.CV_32F, ksize, null, false, BorderTypes.Reflect101);
                                break;

                            case FocusMethod.Tenengrad:
                                using (var dx = new Mat())
                                using (var dy = new Mat())
                                {
                                    Cv2.Sobel(s, dx, MatType.CV_32F, 1, 0, 3, 1, 0, BorderTypes.Reflect101);
                                    Cv2.Sobel(s, dy, MatType.CV_32F, 0, 1, 3, 1, 0, BorderTypes.Reflect101);
                                    Cv2.Multiply(dx, dx, dx);
                                    Cv2.Multiply(dy, dy, dy);
                                    Cv2.Add(dx, dy, tmp);
                                    Cv2.BoxFilter(tmp, d, MatType.CV_32F, ksize, null, false, BorderTypes.Reflect101);
                                }
                                break;

                            case FocusMethod.Variance:
                                using (var mean = new Mat())
                                using (var sq = new Mat())
                                {
                                    Cv2.BoxFilter(s, mean, MatType.CV_32F, ksize, null, true, BorderTypes.Reflect101);
                                    Cv2.Multiply(s, s, sq);
                                    Cv2.BoxFilter(sq, tmp, MatType.CV_32F, ksize, null, true, BorderTypes.Reflect101);
                                    Cv2.Multiply(mean, mean, mean);
                                    Cv2.Subtract(tmp, mean, d);
                                }
                                break;

                            default:
                                throw VisionException.InvalidArgument("Method", $"unknown focus method {method}");
                        }
                        if (d.Data != dst.GetHostPointer())
                            throw new VisionException(ErrorCode.BackendExecutionFailed, "OpenCV reallocated the output buffer.");
                    }
                }
                catch
                {
                    dst.Release();
                    throw;
                }
                return dst;
            }
            finally
            {
                f.Release();
            }
        }

        /// <summary>Domain(또는 전체) 안의 평균 포커스 측도.</summary>
        public static double Score(Image focus, Region? domain)
        {
            using (var m = MatInterop.AsMat(focus))
            {
                var mask = domain?.ToMask(focus.Width, focus.Height);
                return mask == null ? Cv2.Mean(m).Val0 : Cv2.Mean(m, mask).Val0;
            }
        }
    }

    [Operator("FocusMeasure", Category = "Surface", Description = "Per-pixel focus measure and its mean over the domain (shared by DepthFromFocus and auto-focus loops).")]
    public sealed class FocusMeasure : OperatorBase
    {
        [Input] public Image? Image { get; set; }
        [Input(Default = FocusMethod.ModifiedLaplacian)] public FocusMethod Method { get; set; }
        [Input(Description = "Summation window size.", Default = 5, Min = 1)] public int Size { get; set; }

        [Output(Description = "Mean focus measure inside the image domain (higher = sharper).")] public TupleValue? Score { get; private set; }
        [Output(Description = "Per-pixel focus measure (Float32).")] public Image? FocusImage { get; private set; }

        protected override void Validate()
        {
            Require(Image, nameof(Image));
            if (Size < 1) throw VisionException.InvalidArgument(nameof(Size), "must be >= 1");
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var img = Image!;
            var focus = FocusOps.Measure(img, Method, Size);
            Score = FocusOps.Score(focus, img.Domain);
            FocusImage = ImageOps.KeepDomain(focus, img);
        }
    }

    /// <summary>
    /// Depth from Focus: 초점 위치를 바꿔 찍은 스택에서 픽셀마다 가장 선명한 슬라이스 인덱스(서브슬라이스 보간)를 깊이로 구한다.
    /// </summary>
    [Operator("DepthFromFocus", Category = "Surface", Description = "Depth (best-focus slice index, sub-slice interpolated), confidence and an all-in-focus image from a focal stack.")]
    public sealed class DepthFromFocus : OperatorBase
    {
        [Input(Description = "Focal stack (same size and type), ordered by focus position.")]
        public ObjectArray<Image>? Images { get; set; }
        [Input(Default = FocusMethod.ModifiedLaplacian)] public FocusMethod Method { get; set; }
        [Input(Description = "Focus measure window size.", Default = 5, Min = 1)] public int Size { get; set; }
        [Input(Description = "Extra Gaussian smoothing of the focus measure (0 = none).", Default = 0.0, Min = 0)] public double Sigma { get; set; }
        [Input(Description = "Parabolic sub-slice interpolation of the depth.", Default = true)] public bool Interpolate { get; set; }

        [Output(Description = "Depth as slice index 0..N-1 (Float32).")] public Image? Depth { get; private set; }
        [Output(Description = "Confidence 0..1: (max - mean) / max of the focus measure over the stack (Float32).")] public Image? Confidence { get; private set; }
        [Output(Description = "All-in-focus image assembled from the sharpest slice per pixel.")] public Image? Sharp { get; private set; }

        protected override void Validate()
        {
            var images = Require(Images, nameof(Images));
            StackOps.Validate(images, 2, nameof(Images), sameType: true);
            if (Size < 1) throw VisionException.InvalidArgument(nameof(Size), "must be >= 1");
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx) => Run(null);

        [Backend(BackendKind.Parallel)]
        private void RunParallel(OperatorContext ctx) => Run(ctx.CreateParallelOptions());

        private unsafe void Run(System.Threading.Tasks.ParallelOptions? options)
        {
            var images = Images!;
            int n = images.Count;
            int w = images[0].Width, h = images[0].Height;
            var first = images[0];

            var focus = new Image?[n];
            Image? depth = null, conf = null, sharp = null;
            try
            {
                for (int k = 0; k < n; k++)
                {
                    var f = FocusOps.Measure(images[k], Method, Size);
                    if (Sigma > 0)
                    {
                        double sigma = Sigma;
                        var g = ImageOps.Filter(f, (s, d) => Cv2.GaussianBlur(s, d, new Size(0, 0), sigma, sigma, BorderTypes.Reflect101));
                        f.Release();
                        f = g;
                    }
                    focus[k] = f;
                }

                depth = StackOps.NewFloat(w, h);
                conf = StackOps.NewFloat(w, h);
                sharp = new Image(w, h, first.PixelType, first.Channels);

                var fBase = new IntPtr[n]; var fStride = new long[n];
                var sBase = new IntPtr[n]; var sStride = new long[n];
                for (int k = 0; k < n; k++)
                {
                    fBase[k] = focus[k]!.GetHostPointer(); fStride[k] = focus[k]!.Stride;
                    sBase[k] = images[k].GetHostPointer(); sStride[k] = images[k].Stride;
                }
                IntPtr dBase = depth.GetHostPointer(forWrite: true), cBase = conf.GetHostPointer(forWrite: true), shBase = sharp.GetHostPointer(forWrite: true);
                long dStride = depth.Stride, cStride = conf.Stride, shStride = sharp.Stride;
                int bpp = first.BytesPerPixel;
                bool interpolate = Interpolate;

                StackOps.ForRows(h, options, y =>
                {
                    float* pd = (float*)((byte*)dBase + y * dStride);
                    float* pc = (float*)((byte*)cBase + y * cStride);
                    byte* psh = (byte*)shBase + y * shStride;
                    var fr = stackalloc float*[n];
                    for (int k = 0; k < n; k++) fr[k] = (float*)((byte*)fBase[k] + y * fStride[k]);

                    for (int x = 0; x < w; x++)
                    {
                        int best = 0;
                        double fmax = fr[0][x], sum = fmax;
                        for (int k = 1; k < n; k++)
                        {
                            double v = fr[k][x];
                            sum += v;
                            if (v > fmax) { fmax = v; best = k; }
                        }
                        double mean = sum / n;
                        pc[x] = fmax > 1e-12 ? (float)Math.Max(0, Math.Min(1, (fmax - mean) / fmax)) : 0f;

                        double z = best;
                        if (interpolate && best > 0 && best < n - 1)
                        {
                            double l = fr[best - 1][x], r = fr[best + 1][x];
                            double denom = l - 2 * fmax + r;
                            if (denom < -1e-12)
                            {
                                double off = 0.5 * (l - r) / denom;
                                z += Math.Max(-0.5, Math.Min(0.5, off));
                            }
                        }
                        pd[x] = (float)z;

                        byte* srcPix = (byte*)sBase[best] + y * sStride[best] + (long)x * bpp;
                        byte* dstPix = psh + (long)x * bpp;
                        for (int b = 0; b < bpp; b++) dstPix[b] = srcPix[b];
                    }
                });

                Depth = ImageOps.KeepDomain(depth, first); depth = null;
                Confidence = ImageOps.KeepDomain(conf, first); conf = null;
                Sharp = ImageOps.KeepDomain(sharp, first); sharp = null;
            }
            finally
            {
                depth?.Release(); conf?.Release(); sharp?.Release();
                StackOps.ReleaseAll(focus);
            }
        }
    }
}
