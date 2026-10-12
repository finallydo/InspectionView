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
    /// <summary>
    /// Photometric Stereo (Halcon photometric_stereo). 같은 시점에서 조명 방향만 바꿔 찍은 3장 이상의 이미지로
    /// 표면 기울기(∂z/∂col, ∂z/∂row), 알베도, 높이맵을 복원한다 (Lambertian 가정, 최소제곱).
    /// <para>
    /// 조명 방향: Slant = 광축(카메라 방향)과 이루는 각, Tilt = 이미지 평면에서 열(+column) 축으로부터
    /// 반시계(화면 기준, 위쪽 = 행 감소 방향) 로 잰 각. 둘 다 도(degree). 높이 z 는 카메라 쪽이 +.
    /// </para>
    /// </summary>
    [Operator("PhotometricStereo", Category = "Surface", Description = "Recovers surface gradients, albedo and a height field from 3+ images lit from known directions (Lambertian least squares, Frankot-Chellappa integration).")]
    public sealed class PhotometricStereo : OperatorBase
    {
        [Input(Description = "Images (same size, single channel), one per light direction.")]
        public ObjectArray<Image>? Images { get; set; }

        [Input(Description = "Slant angle of each light in degrees (angle to the optical axis).")]
        public TupleValue? Slants { get; set; }

        [Input(Description = "Tilt angle of each light in degrees (in the image plane, counter-clockwise from the column axis).")]
        public TupleValue? Tilts { get; set; }

        [Input(Description = "Integrate the gradients into a height field (FFT, assumes a periodic surface).", Default = true)]
        public bool ComputeHeight { get; set; }

        [Output(Description = "Height field (Float32), up to an additive constant. Empty-size zero image when ComputeHeight is false.")]
        public Image? HeightField { get; private set; }

        [Output(Description = "Surface gradient along columns (dz/dcol, Float32).")]
        public Image? GradientX { get; private set; }

        [Output(Description = "Surface gradient along rows (dz/drow, Float32).")]
        public Image? GradientY { get; private set; }

        [Output(Description = "Albedo (Float32, gray-value scale).")]
        public Image? Albedo { get; private set; }

        private double[,]? _pinv;

        protected override void Validate()
        {
            var images = Require(Images, nameof(Images));
            var slants = Require(Slants, nameof(Slants));
            var tilts = Require(Tilts, nameof(Tilts));
            StackOps.Validate(images, 3, nameof(Images));
            if (slants.Length != images.Count || tilts.Length != images.Count)
                throw new VisionException(ErrorCode.SizeMismatch, $"Slants/Tilts must have one entry per image ({images.Count}).");
            if (!slants.IsNumeric || !tilts.IsNumeric)
                throw new VisionException(ErrorCode.TypeMismatch, "Slants and Tilts must be numeric.");

            int n = images.Count;
            var l = new double[n, 3];
            for (int i = 0; i < n; i++)
            {
                double s = slants.D(i) * Math.PI / 180.0;
                double t = tilts.D(i) * Math.PI / 180.0;
                l[i, 0] = Math.Sin(s) * Math.Cos(t);
                l[i, 1] = -Math.Sin(s) * Math.Sin(t);
                l[i, 2] = Math.Cos(s);
            }
            _pinv = StackOps.PseudoInverse3(l);
            if (_pinv == null)
                throw VisionException.InvalidArgument(nameof(Slants), "light directions are degenerate (not spanning 3-D space)");
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx) => Run(null);

        [Backend(BackendKind.Parallel)]
        private void RunParallel(OperatorContext ctx) => Run(ctx.CreateParallelOptions());

        private unsafe void Run(System.Threading.Tasks.ParallelOptions? options)
        {
            var images = Images!;
            var pinv = _pinv!;
            int n = images.Count;
            int w = images[0].Width, h = images[0].Height;

            var planes = StackOps.ToFloat(images);
            Image? gx = null, gy = null, albedo = null, height = null;
            try
            {
                gx = StackOps.NewFloat(w, h);
                gy = StackOps.NewFloat(w, h);
                albedo = StackOps.NewFloat(w, h);

                var m = new double[3 * n];
                for (int i = 0; i < 3; i++)
                    for (int k = 0; k < n; k++)
                        m[i * n + k] = pinv[i, k];

                var rowPtrs = new IntPtr[n];
                for (int k = 0; k < n; k++) rowPtrs[k] = planes[k].GetHostPointer();
                long[] strides = new long[n];
                for (int k = 0; k < n; k++) strides[k] = planes[k].Stride;

                IntPtr gxBase = gx.GetHostPointer(forWrite: true), gyBase = gy.GetHostPointer(forWrite: true), alBase = albedo.GetHostPointer(forWrite: true);
                long gxStride = gx.Stride, gyStride = gy.Stride, alStride = albedo.Stride;

                StackOps.ForRows(h, options, y =>
                {
                    float* px = (float*)((byte*)gxBase + y * gxStride);
                    float* py = (float*)((byte*)gyBase + y * gyStride);
                    float* pa = (float*)((byte*)alBase + y * alStride);
                    var src = stackalloc float*[n];
                    for (int k = 0; k < n; k++) src[k] = (float*)((byte*)rowPtrs[k] + y * strides[k]);

                    for (int x = 0; x < w; x++)
                    {
                        double g0 = 0, g1 = 0, g2 = 0;
                        for (int k = 0; k < n; k++)
                        {
                            double v = src[k][x];
                            g0 += m[k] * v;
                            g1 += m[n + k] * v;
                            g2 += m[2 * n + k] * v;
                        }
                        double rho = Math.Sqrt(g0 * g0 + g1 * g1 + g2 * g2);
                        pa[x] = (float)rho;
                        if (rho < 1e-9)
                        {
                            px[x] = 0; py[x] = 0;
                            continue;
                        }
                        double nz = g2 / rho;
                        if (nz < 1e-3) nz = 1e-3;              // 수평에 가까운 법선: 기울기 폭주 방지
                        double p = -(g0 / rho) / nz, q = -(g1 / rho) / nz;
                        px[x] = (float)Math.Max(-1e4, Math.Min(1e4, p));
                        py[x] = (float)Math.Max(-1e4, Math.Min(1e4, q));
                    }
                });

                height = ComputeHeight ? IntegrateFrankotChellappa(gx, gy) : StackOps.NewFloat(1, 1);

                var first = images[0];
                GradientX = ImageOps.KeepDomain(gx, first); gx = null;
                GradientY = ImageOps.KeepDomain(gy, first); gy = null;
                Albedo = ImageOps.KeepDomain(albedo, first); albedo = null;
                HeightField = ComputeHeight ? ImageOps.KeepDomain(height, first) : height; height = null;
            }
            finally
            {
                gx?.Release(); gy?.Release(); albedo?.Release(); height?.Release();
                StackOps.ReleaseAll(planes);
            }
        }

        /// <summary>
        /// Frankot-Chellappa: 기울기장 (p, q) 에 가장 가까운(최소제곱) 적분 가능한 표면을 주파수 영역에서 구한다.
        /// Z(u,v) = (-j·wx·P - j·wy·Q) / (wx² + wy²), Z(0,0) = 0.
        /// </summary>
        internal static unsafe Image IntegrateFrankotChellappa(Image gx, Image gy)
        {
            int w = gx.Width, h = gx.Height;
            using (var p = new Mat())
            using (var q = new Mat())
            using (var z = new Mat(h, w, MatType.CV_32FC2))
            using (var outMat = new Mat())
            {
                using (var mx = MatInterop.AsMat(gx)) Cv2.Dft(mx, p, DftFlags.ComplexOutput);
                using (var my = MatInterop.AsMat(gy)) Cv2.Dft(my, q, DftFlags.ComplexOutput);

                long ps = p.Step(), qs = q.Step(), zs = z.Step();
                byte* pb = (byte*)p.Data, qb = (byte*)q.Data, zb = (byte*)z.Data;
                for (int v = 0; v < h; v++)
                {
                    double wy = 2 * Math.PI * (v < (h + 1) / 2 ? v : v - h) / h;
                    float* pr = (float*)(pb + v * ps);
                    float* qr = (float*)(qb + v * qs);
                    float* zr = (float*)(zb + v * zs);
                    for (int u = 0; u < w; u++)
                    {
                        double wx = 2 * Math.PI * (u < (w + 1) / 2 ? u : u - w) / w;
                        double denom = wx * wx + wy * wy;
                        if (denom < 1e-12)
                        {
                            zr[2 * u] = 0; zr[2 * u + 1] = 0;
                            continue;
                        }
                        double pre = pr[2 * u], pim = pr[2 * u + 1];
                        double qre = qr[2 * u], qim = qr[2 * u + 1];
                        // -j·(a + jb) = b - ja
                        zr[2 * u] = (float)((wx * pim + wy * qim) / denom);
                        zr[2 * u + 1] = (float)((-wx * pre - wy * qre) / denom);
                    }
                }

                Cv2.Dft(z, outMat, DftFlags.Inverse | DftFlags.RealOutput | DftFlags.Scale);
                return MatInterop.ToImage(outMat);
            }
        }
    }
}
