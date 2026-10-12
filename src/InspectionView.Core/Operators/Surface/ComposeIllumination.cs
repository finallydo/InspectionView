using System;
using InspectionView.Core.Backends;
using InspectionView.Core.Errors;
using InspectionView.Core.Interop;
using InspectionView.Core.Operators.Common;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;
using OpenCvSharp;

namespace InspectionView.Core.Operators.Surface
{
    public enum FusionMethod
    {
        Max,
        Min,
        Mean,
        Median,
        /// <summary>합 (결과는 Float32).</summary>
        Sum,
        /// <summary>Max - Min.</summary>
        Range,
        /// <summary>표준편차 (결과는 Float32).</summary>
        StdDev,
    }

    /// <summary>
    /// 조명 합성: 조명 방향/노출을 바꿔 찍은 여러 장을 픽셀별 통계로 한 장에 합친다
    /// (예: 다방향 조명의 Max 로 그림자 제거, Range 로 요철 강조).
    /// </summary>
    [Operator("ComposeIllumination", Category = "Surface", Description = "Fuses a stack of differently lit (or exposed) images pixel-wise: Max, Min, Mean, Median, Sum, Range or StdDev.")]
    public sealed class ComposeIllumination : OperatorBase
    {
        [Input(Description = "Images (same size and type, single channel).")] public ObjectArray<Image>? Images { get; set; }
        [Input(Default = FusionMethod.Max)] public FusionMethod Method { get; set; }

        [Output(Description = "Fused image (input type; Float32 for Sum/StdDev).")] public Image? ImageResult { get; private set; }

        protected override void Validate()
        {
            var images = Require(Images, nameof(Images));
            StackOps.Validate(images, 1, nameof(Images), sameType: true);
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
            var method = Method;

            var planes = StackOps.ToFloat(images);
            Image? acc = null;
            try
            {
                acc = StackOps.NewFloat(w, h);
                var pBase = new IntPtr[n]; var pStride = new long[n];
                for (int k = 0; k < n; k++) { pBase[k] = planes[k].GetHostPointer(); pStride[k] = planes[k].Stride; }
                IntPtr aBase = acc.GetHostPointer(forWrite: true);
                long aStride = acc.Stride;

                StackOps.ForRows(h, options, y =>
                {
                    float* pa = (float*)((byte*)aBase + y * aStride);
                    var src = stackalloc float*[n];
                    for (int k = 0; k < n; k++) src[k] = (float*)((byte*)pBase[k] + y * pStride[k]);
                    double[]? buf = method == FusionMethod.Median ? new double[n] : null;

                    for (int x = 0; x < w; x++)
                    {
                        double r;
                        switch (method)
                        {
                            case FusionMethod.Max:
                            {
                                r = src[0][x];
                                for (int k = 1; k < n; k++) if (src[k][x] > r) r = src[k][x];
                                break;
                            }
                            case FusionMethod.Min:
                            {
                                r = src[0][x];
                                for (int k = 1; k < n; k++) if (src[k][x] < r) r = src[k][x];
                                break;
                            }
                            case FusionMethod.Range:
                            {
                                double mn = src[0][x], mx = mn;
                                for (int k = 1; k < n; k++)
                                {
                                    double v = src[k][x];
                                    if (v < mn) mn = v;
                                    if (v > mx) mx = v;
                                }
                                r = mx - mn;
                                break;
                            }
                            case FusionMethod.Median:
                            {
                                for (int k = 0; k < n; k++) buf![k] = src[k][x];
                                Array.Sort(buf!);
                                r = n % 2 == 1 ? buf![n / 2] : 0.5 * (buf![n / 2 - 1] + buf[n / 2]);
                                break;
                            }
                            case FusionMethod.StdDev:
                            {
                                double s = 0, s2 = 0;
                                for (int k = 0; k < n; k++) { double v = src[k][x]; s += v; s2 += v * v; }
                                double mean = s / n;
                                r = Math.Sqrt(Math.Max(0, s2 / n - mean * mean));
                                break;
                            }
                            default:
                            {
                                double s = 0;
                                for (int k = 0; k < n; k++) s += src[k][x];
                                r = method == FusionMethod.Sum ? s : s / n;
                                break;
                            }
                        }
                        pa[x] = (float)r;
                    }
                });

                bool keepFloat = method == FusionMethod.Sum || method == FusionMethod.StdDev || first.PixelType == PixelType.Float32;
                if (keepFloat)
                {
                    ImageResult = ImageOps.KeepDomain(acc, first);
                    acc = null;
                }
                else
                {
                    var outType = first.PixelType;
                    ImageResult = ImageOps.Filter(acc, (s, d) => s.ConvertTo(d, d.Type()), outType);
                    // acc 는 Domain 이 없으므로 Filter 결과도 전체 Domain. 첫 입력의 Domain 을 적용한다.
                    if (first.Domain != null)
                    {
                        var full = ImageResult;
                        ImageResult = full.ReduceDomain(first.Domain);
                        full.Release();
                    }
                }
            }
            finally
            {
                acc?.Release();
                StackOps.ReleaseAll(planes);
            }
        }
    }
}
