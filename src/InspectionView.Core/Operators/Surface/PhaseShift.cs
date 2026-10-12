using System;
using InspectionView.Core.Backends;
using InspectionView.Core.Errors;
using InspectionView.Core.Operators.Common;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;
using ErrorCode = InspectionView.Core.Errors.ErrorCode;

namespace InspectionView.Core.Operators.Surface
{
    /// <summary>
    /// 위상 천이(phase shifting) 구조광 디코딩. I_k = Mean + Modulation · cos(Phase + Shift_k) 를 픽셀마다 최소제곱으로 푼다.
    /// Shifts 를 주지 않으면 2πk/N 등간격으로 본다.
    /// </summary>
    [Operator("PhaseShiftDecode", Category = "Surface", Description = "Decodes N phase-shifted fringe images into wrapped phase (-pi..pi), modulation and mean intensity (least squares).")]
    public sealed class PhaseShiftDecode : OperatorBase
    {
        [Input(Description = "Fringe images (3 or more, same size, single channel).")] public ObjectArray<Image>? Images { get; set; }
        [Input(Description = "Phase shift of each image in radians. Default: 2*pi*k/N.", Optional = true)] public TupleValue? Shifts { get; set; }

        [Output(Description = "Wrapped phase in radians, -pi..pi (Float32).")] public Image? Phase { get; private set; }
        [Output(Description = "Fringe modulation amplitude (Float32).")] public Image? Modulation { get; private set; }
        [Output(Description = "Mean intensity (Float32).")] public Image? Mean { get; private set; }

        private double[,]? _pinv;

        protected override void Validate()
        {
            var images = Require(Images, nameof(Images));
            StackOps.Validate(images, 3, nameof(Images));
            int n = images.Count;
            if (Shifts != null && !Shifts.IsEmpty)
            {
                if (Shifts.Length != n) throw new VisionException(ErrorCode.SizeMismatch, $"Shifts must have one entry per image ({n}).");
                if (!Shifts.IsNumeric) throw new VisionException(ErrorCode.TypeMismatch, "Shifts must be numeric.");
            }

            // I_k = A + C cos(d_k) - S sin(d_k),  C = B cos(phi), S = B sin(phi)
            var a = new double[n, 3];
            for (int k = 0; k < n; k++)
            {
                double d = Shifts != null && !Shifts.IsEmpty ? Shifts.D(k) : 2 * Math.PI * k / n;
                a[k, 0] = 1;
                a[k, 1] = Math.Cos(d);
                a[k, 2] = -Math.Sin(d);
            }
            _pinv = StackOps.PseudoInverse3(a);
            if (_pinv == null)
                throw VisionException.InvalidArgument(nameof(Shifts), "phase shifts are degenerate (need 3 distinct shifts)");
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
            var first = images[0];

            var planes = StackOps.ToFloat(images);
            Image? phase = null, mod = null, mean = null;
            try
            {
                phase = StackOps.NewFloat(w, h);
                mod = StackOps.NewFloat(w, h);
                mean = StackOps.NewFloat(w, h);

                var m = new double[3 * n];
                for (int i = 0; i < 3; i++)
                    for (int k = 0; k < n; k++)
                        m[i * n + k] = pinv[i, k];

                var pBase = new IntPtr[n]; var pStride = new long[n];
                for (int k = 0; k < n; k++) { pBase[k] = planes[k].GetHostPointer(); pStride[k] = planes[k].Stride; }
                IntPtr phBase = phase.GetHostPointer(forWrite: true), moBase = mod.GetHostPointer(forWrite: true), meBase = mean.GetHostPointer(forWrite: true);
                long phStride = phase.Stride, moStride = mod.Stride, meStride = mean.Stride;

                StackOps.ForRows(h, options, y =>
                {
                    float* pph = (float*)((byte*)phBase + y * phStride);
                    float* pmo = (float*)((byte*)moBase + y * moStride);
                    float* pme = (float*)((byte*)meBase + y * meStride);
                    var src = stackalloc float*[n];
                    for (int k = 0; k < n; k++) src[k] = (float*)((byte*)pBase[k] + y * pStride[k]);

                    for (int x = 0; x < w; x++)
                    {
                        double a = 0, c = 0, s = 0;
                        for (int k = 0; k < n; k++)
                        {
                            double v = src[k][x];
                            a += m[k] * v;
                            c += m[n + k] * v;
                            s += m[2 * n + k] * v;
                        }
                        pme[x] = (float)a;
                        pmo[x] = (float)Math.Sqrt(c * c + s * s);
                        pph[x] = (float)Math.Atan2(s, c);
                    }
                });

                Phase = ImageOps.KeepDomain(phase, first); phase = null;
                Modulation = ImageOps.KeepDomain(mod, first); mod = null;
                Mean = ImageOps.KeepDomain(mean, first); mean = null;
            }
            finally
            {
                phase?.Release(); mod?.Release(); mean?.Release();
                StackOps.ReleaseAll(planes);
            }
        }
    }
}
