using System;
using System.Threading.Tasks;
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
    /// <summary>
    /// 벡터장 (보통 PhotometricStereo 의 기울기 GradientX = dz/dcol, GradientY = dz/drow) 의 미분량 (Halcon derivate_vector_field).
    /// 미분은 가우시안 미분 (표준편차 Sigma) 으로 한다.
    /// <list type="bullet">
    /// <item><c>divergence</c>: ∂Gx/∂col + ∂Gy/∂row. 기울기장이면 높이의 라플라시안 (오목 +, 볼록 −) — 찍힘/돌기/스크래치 검출용</item>
    /// <item><c>curl</c>: ∂Gy/∂col − ∂Gx/∂row (적분 가능한 기울기장이면 0, 노이즈/그림자 지표)</item>
    /// <item><c>mean_curvature</c>: 높이 z 의 평균 곡률 ((1+q²)z_xx − 2pq z_xy + (1+p²)z_yy) / (2(1+p²+q²)^1.5)</item>
    /// </list>
    /// 큰 이미지는 행 띠 단위로 처리하므로 추가 메모리는 띠 크기만큼만 쓴다 (띠 경계는 실제 이웃 픽셀을 사용).
    /// </summary>
    [Operator("DerivateVectorField", Category = "Surface", Description = "Divergence, curl or mean curvature of a vector field given as two images (e.g. PhotometricStereo GradientX/GradientY), using Gaussian derivatives with the given Sigma.")]
    public sealed class DerivateVectorField : OperatorBase
    {
        [Input(Description = "Column component of the field (e.g. PhotometricStereo GradientX).")] public Image? GradientX { get; set; }
        [Input(Description = "Row component of the field (e.g. PhotometricStereo GradientY).")] public Image? GradientY { get; set; }
        [Input(Description = "Standard deviation of the Gaussian derivative.", Default = 1.0, Min = 0.3, Max = 50.0)] public double Sigma { get; set; } = 1.0;
        [Input(Description = "divergence, curl or mean_curvature.", Default = "divergence")] public string Component { get; set; } = "divergence";

        [Output(Description = "Result (Float32).")] public Image? ImageResult { get; private set; }

        private enum Kind { Divergence, Curl, MeanCurvature }

        private const int BandRows = 256;

        private Kind _kind;

        protected override void Validate()
        {
            var gx = Require(GradientX, nameof(GradientX));
            var gy = Require(GradientY, nameof(GradientY));
            if (gx.Width != gy.Width || gx.Height != gy.Height)
                throw new VisionException(ErrorCode.SizeMismatch, $"GradientX ({gx.Width}x{gx.Height}) and GradientY ({gy.Width}x{gy.Height}) must have the same size.");
            if (gx.Channels != 1 || gy.Channels != 1)
                throw new VisionException(ErrorCode.TypeMismatch, "GradientX and GradientY must be single-channel images.");
            if (!(Sigma >= 0.3 && Sigma <= 50))
                throw VisionException.OutOfRange(nameof(Sigma), Sigma, 0.3, 50.0);
            _kind = ParseComponent(Component);
        }

        private static Kind ParseComponent(string? component)
        {
            switch ((component ?? "").Trim().ToLowerInvariant())
            {
                case "divergence":
                case "div":
                    return Kind.Divergence;
                case "curl":
                    return Kind.Curl;
                case "mean_curvature":
                    return Kind.MeanCurvature;
                default:
                    throw VisionException.InvalidArgument(nameof(Component), "must be divergence, curl or mean_curvature");
            }
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx) => Run(null);

        [Backend(BackendKind.Parallel)]
        private void RunParallel(OperatorContext ctx) => Run(ctx.CreateParallelOptions());

        private void Run(ParallelOptions? options)
        {
            var srcX = GradientX!;
            int w = srcX.Width, h = srcX.Height;
            var kind = _kind;
            Gaussian(Sigma, out var smooth, out var deriv);

            Image? fx = null, fy = null, result = null;
            try
            {
                fx = StackOps.ToFloat(srcX);
                fy = StackOps.ToFloat(GradientY!);
                result = StackOps.NewFloat(w, h);

                using (var kSmooth = new Mat(1, smooth.Length, MatType.CV_32FC1))
                using (var kDeriv = new Mat(1, deriv.Length, MatType.CV_32FC1))
                using (var mx = MatInterop.AsMat(fx))
                using (var my = MatInterop.AsMat(fy))
                {
                    kSmooth.SetArray(smooth);
                    kDeriv.SetArray(deriv);
                    var res = result;
                    int bands = (h + BandRows - 1) / BandRows;
                    Action<int> band = b =>
                    {
                        int y0 = b * BandRows, rows = Math.Min(BandRows, h - y0);
                        // 부분 Mat 에 필터를 걸면 OpenCV 가 띠 밖의 실제 픽셀을 이웃으로 쓴다 (이미지 끝에서만 반사)
                        using (var bx = new Mat(mx, new Rect(0, y0, w, rows)))
                        using (var by = new Mat(my, new Rect(0, y0, w, rows)))
                        {
                            ComputeBand(bx, by, kSmooth, kDeriv, kind, res, y0, w, rows);
                        }
                    };
                    if (options != null) Parallel.For(0, bands, options, band);
                    else for (int b = 0; b < bands; b++) band(b);
                }

                ImageResult = ImageOps.KeepDomain(result, srcX);
                result = null;
            }
            finally
            {
                fx?.Release();
                fy?.Release();
                result?.Release();
            }
        }

        private static unsafe void ComputeBand(Mat bx, Mat by, Mat kSmooth, Mat kDeriv, Kind kind, Image result, int y0, int w, int rows)
        {
            // OpenCV sepFilter2D 는 상관(correlation): ∂/∂col 은 kernelX = 미분, kernelY = 평활
            using (var px = Filter(bx, kDeriv, kSmooth))   // ∂Gx/∂col
            using (var py = Filter(bx, kSmooth, kDeriv))   // ∂Gx/∂row
            using (var qx = Filter(by, kDeriv, kSmooth))   // ∂Gy/∂col
            using (var qy = Filter(by, kSmooth, kDeriv))   // ∂Gy/∂row
            using (var ps = kind == Kind.MeanCurvature ? Filter(bx, kSmooth, kSmooth) : null)
            using (var qs = kind == Kind.MeanCurvature ? Filter(by, kSmooth, kSmooth) : null)
            {
                for (int r = 0; r < rows; r++)
                {
                    float* dst = StackOps.Row(result, y0 + r, forWrite: true);
                    float* aPx = (float*)px.Ptr(r), aPy = (float*)py.Ptr(r), aQx = (float*)qx.Ptr(r), aQy = (float*)qy.Ptr(r);
                    switch (kind)
                    {
                        case Kind.Divergence:
                            for (int c = 0; c < w; c++) dst[c] = aPx[c] + aQy[c];
                            break;
                        case Kind.Curl:
                            for (int c = 0; c < w; c++) dst[c] = aQx[c] - aPy[c];
                            break;
                        default:
                        {
                            float* sp = (float*)ps!.Ptr(r), sq = (float*)qs!.Ptr(r);
                            for (int c = 0; c < w; c++)
                            {
                                double p = sp[c], q = sq[c];
                                double zxy = 0.5 * (aPy[c] + aQx[c]);
                                double g = 1 + p * p + q * q;
                                dst[c] = (float)(((1 + q * q) * aPx[c] - 2 * p * q * zxy + (1 + p * p) * aQy[c]) / (2 * g * Math.Sqrt(g)));
                            }
                            break;
                        }
                    }
                }
            }
        }

        private static Mat Filter(Mat src, Mat kernelX, Mat kernelY)
        {
            var dst = new Mat();
            Cv2.SepFilter2D(src, dst, MatType.CV_32F, kernelX, kernelY, null, 0, BorderTypes.Reflect101);
            return dst;
        }

        /// <summary>
        /// 가우시안 평활 커널 (합 1) 과 미분 커널 (상관용: 직선 f = t 에 적용하면 정확히 1). 반지름 = ceil(4σ).
        /// </summary>
        internal static void Gaussian(double sigma, out float[] smooth, out float[] deriv)
        {
            int r = Math.Max(1, (int)Math.Ceiling(4 * sigma));
            var g = new double[2 * r + 1];
            double sum = 0, moment = 0;
            for (int t = -r; t <= r; t++)
            {
                double v = Math.Exp(-0.5 * t * t / (sigma * sigma));
                g[t + r] = v;
                sum += v;
                moment += t * t * v;
            }
            smooth = new float[g.Length];
            deriv = new float[g.Length];
            for (int t = -r; t <= r; t++)
            {
                smooth[t + r] = (float)(g[t + r] / sum);
                deriv[t + r] = (float)(t * g[t + r] / moment);
            }
        }
    }
}
