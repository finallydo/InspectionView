using System;
using System.Collections.Generic;
using InspectionView.Core.Backends;
using InspectionView.Core.Errors;
using InspectionView.Core.Operators.Common;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;
using ErrorCode = InspectionView.Core.Errors.ErrorCode;

namespace InspectionView.Core.Operators.Surface
{
    public enum LineDirection
    {
        /// <summary>레이저 라인이 가로로 놓임: 열마다 라인의 행 위치를 찾는다 (프로파일 = 열의 함수).</summary>
        Column,
        /// <summary>레이저 라인이 세로로 놓임: 행마다 라인의 열 위치를 찾는다.</summary>
        Row,
    }

    public enum LinePeakMethod
    {
        /// <summary>최대값 픽셀 (정수).</summary>
        Peak,
        /// <summary>최대값 주변 (최대 Width 픽셀) 의 MinGray 를 넘는 연속 구간의 (MinGray 를 뺀) 무게중심.</summary>
        CenterOfGravity,
        /// <summary>최대값과 양옆 값의 포물선 보간.</summary>
        Parabolic,
    }

    /// <summary>레이저 라인(sheet of light) 추출 헬퍼.</summary>
    internal static class LaserOps
    {
        /// <summary>
        /// 프로파일 길이 (Direction=Column 이면 Width, Row 면 Height).
        /// </summary>
        public static int ProfileLength(Image img, LineDirection dir) => dir == LineDirection.Column ? img.Width : img.Height;

        /// <summary>
        /// 라인 위치를 추출한다. positions[i] 는 서브픽셀 위치(픽셀 인덱스 좌표), 라인이 없으면 -1. amplitudes[i] 는 최대 그레이.
        /// </summary>
        public static unsafe void Extract(Image src, LineDirection dir, double minGray, LinePeakMethod method, int width, double[] positions, double[] amplitudes)
        {
            var f = StackOps.ToFloat(src);
            try
            {
                int w = f.Width, h = f.Height;
                byte* basePtr = (byte*)f.GetHostPointer();
                long stride = f.Stride;
                int len = ProfileLength(f, dir);
                int depth = dir == LineDirection.Column ? h : w;
                int half = Math.Max(0, width / 2);

                for (int i = 0; i < len; i++)
                {
                    int best = -1;
                    double gmax = double.NegativeInfinity;
                    for (int j = 0; j < depth; j++)
                    {
                        double g = Sample(basePtr, stride, dir, i, j);
                        if (g > gmax) { gmax = g; best = j; }
                    }
                    if (best < 0 || gmax < minGray)
                    {
                        positions[i] = -1;
                        amplitudes[i] = best < 0 ? 0 : gmax;
                        continue;
                    }
                    amplitudes[i] = gmax;

                    double pos = best;
                    switch (method)
                    {
                        case LinePeakMethod.Parabolic:
                            if (best > 0 && best < depth - 1)
                            {
                                double l = Sample(basePtr, stride, dir, i, best - 1), r = Sample(basePtr, stride, dir, i, best + 1);
                                double denom = l - 2 * gmax + r;
                                if (denom < -1e-12)
                                    pos += Math.Max(-0.5, Math.Min(0.5, 0.5 * (l - r) / denom));
                            }
                            break;

                        case LinePeakMethod.CenterOfGravity:
                        {
                            // 최대값에서 양쪽으로 MinGray 를 넘는 연속 구간(최대 ±half)만 사용 → 창이 라인에 대해 대칭이 된다
                            double sw = gmax - minGray, swj = (gmax - minGray) * best;
                            for (int j = best - 1; j >= Math.Max(0, best - half); j--)
                            {
                                double g = Sample(basePtr, stride, dir, i, j) - minGray;
                                if (g <= 0) break;
                                sw += g; swj += g * j;
                            }
                            for (int j = best + 1; j <= Math.Min(depth - 1, best + half); j++)
                            {
                                double g = Sample(basePtr, stride, dir, i, j) - minGray;
                                if (g <= 0) break;
                                sw += g; swj += g * j;
                            }
                            if (sw > 0) pos = swj / sw;
                            break;
                        }
                    }
                    positions[i] = pos;
                }
            }
            finally
            {
                f.Release();
            }
        }

        private static unsafe double Sample(byte* basePtr, long stride, LineDirection dir, int i, int j)
        {
            // Column: i = 열, j = 행 / Row: i = 행, j = 열
            int x = dir == LineDirection.Column ? i : j;
            int y = dir == LineDirection.Column ? j : i;
            return ((float*)(basePtr + y * stride))[x];
        }
    }

    [Operator("ExtractLaserLine", Category = "Surface", Description = "Sub-pixel laser line position per column (or row): positions (-1 where no line), peak amplitudes and the line as an XLD contour.")]
    public sealed class ExtractLaserLine : OperatorBase
    {
        [Input] public Image? Image { get; set; }
        [Input(Description = "Column: the line runs horizontally and a row position is found per column. Row: vice versa.", Default = LineDirection.Column)]
        public LineDirection Direction { get; set; }
        [Input(Description = "Minimum peak gray value.", Default = 30.0)] public double MinGray { get; set; }
        [Input(Default = LinePeakMethod.CenterOfGravity)] public LinePeakMethod Method { get; set; }
        [Input(Description = "Maximum window size for the center of gravity (should cover the line width).", Default = 7, Min = 1)] public int Width { get; set; }

        [Output(Description = "Line position per profile sample (row for Direction=Column), -1 where none.")] public TupleValue? Position { get; private set; }
        [Output(Description = "Peak gray value per profile sample.")] public TupleValue? Amplitude { get; private set; }
        [Output(Description = "Valid line points as an XLD contour.")] public XldContour? Line { get; private set; }

        protected override void Validate()
        {
            Require(Image, nameof(Image));
            if (Width < 1) throw VisionException.InvalidArgument(nameof(Width), "must be >= 1");
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var img = Image!;
            int len = LaserOps.ProfileLength(img, Direction);
            var pos = new double[len];
            var amp = new double[len];
            LaserOps.Extract(img, Direction, MinGray, Method, Width, pos, amp);

            var xs = new List<double>();
            var ys = new List<double>();
            for (int i = 0; i < len; i++)
            {
                if (pos[i] < 0) continue;
                if (Direction == LineDirection.Column) { xs.Add(i); ys.Add(pos[i]); }
                else { xs.Add(pos[i]); ys.Add(i); }
            }
            Position = pos;
            Amplitude = amp;
            Line = new XldContour(xs.ToArray(), ys.ToArray(), closed: false);
        }
    }

    [Operator("MeasureProfileSheetOfLight", Category = "Surface", Description = "Extracts the laser line of every image of a stack into a disparity image (one row per image, Float32, -1 where no line).")]
    public sealed class MeasureProfileSheetOfLight : OperatorBase
    {
        [Input(Description = "Profile images in scan order.")] public ObjectArray<Image>? Images { get; set; }
        [Input(Default = LineDirection.Column)] public LineDirection Direction { get; set; }
        [Input(Default = 30.0)] public double MinGray { get; set; }
        [Input(Default = LinePeakMethod.CenterOfGravity)] public LinePeakMethod Method { get; set; }
        [Input(Default = 7, Min = 1)] public int Width { get; set; }

        [Output(Description = "Disparity image: row k = line positions of image k (Float32, -1 = no line).")] public Image? Disparity { get; private set; }

        protected override void Validate()
        {
            var images = Require(Images, nameof(Images));
            StackOps.Validate(images, 1, nameof(Images));
            if (Width < 1) throw VisionException.InvalidArgument(nameof(Width), "must be >= 1");
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx) => Run(null);

        [Backend(BackendKind.Parallel)]
        private void RunParallel(OperatorContext ctx) => Run(ctx.CreateParallelOptions());

        private void Run(System.Threading.Tasks.ParallelOptions? options)
        {
            var images = Images!;
            int len = LaserOps.ProfileLength(images[0], Direction);
            var result = StackOps.NewFloat(len, images.Count);
            try
            {
                var dir = Direction; double minGray = MinGray; var method = Method; int width = Width;
                StackOps.ForRows(images.Count, options, k =>
                {
                    var pos = new double[len];
                    var amp = new double[len];
                    LaserOps.Extract(images[k], dir, minGray, method, width, pos, amp);
                    var row = result.GetRow<float>(k);
                    for (int i = 0; i < len; i++) row[i] = (float)pos[i];
                });
            }
            catch
            {
                result.Release();
                throw;
            }
            Disparity = result;
        }
    }

    [Operator("DisparityToHeight", Category = "Surface", Description = "Linear sheet-of-light calibration: Height = (Disparity - OffsetZ) * ScaleZ; negative (invalid) disparities become InvalidValue.")]
    public sealed class DisparityToHeight : OperatorBase
    {
        [Input(Description = "Disparity image (Float32, -1 = invalid).")] public Image? Disparity { get; set; }
        [Input(Description = "Height units per disparity pixel.", Default = 1.0)] public double ScaleZ { get; set; }
        [Input(Description = "Disparity of the reference (zero height) plane.", Default = 0.0)] public double OffsetZ { get; set; }
        [Input(Description = "Value written where the disparity is invalid.", Default = 0.0)] public double InvalidValue { get; set; }

        [Output(Description = "Height image (Float32).")] public Image? Height { get; private set; }

        protected override void Validate()
        {
            var d = Require(Disparity, nameof(Disparity));
            if (d.Channels != 1)
                throw new VisionException(ErrorCode.TypeMismatch, "Disparity must be a single-channel image.");
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var src = Disparity!;
            var dst = StackOps.NewFloat(src.Width, src.Height);
            try
            {
                for (int y = 0; y < src.Height; y++)
                {
                    var row = dst.GetRow<float>(y);
                    for (int x = 0; x < src.Width; x++)
                    {
                        double d = src.GetPixel(x, y);
                        row[x] = d < 0 ? (float)InvalidValue : (float)((d - OffsetZ) * ScaleZ);
                    }
                }
            }
            catch
            {
                dst.Release();
                throw;
            }
            Height = ImageOps.KeepDomain(dst, src);
        }
    }
}
