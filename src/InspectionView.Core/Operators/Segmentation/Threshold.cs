using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using InspectionView.Core.Backends;
using InspectionView.Core.Errors;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;

namespace InspectionView.Core.Operators.Segmentation
{
    /// <summary>
    /// 그레이 값이 [MinGray, MaxGray] 인 픽셀을 선택해 Region 으로 만든다 (Halcon threshold).
    /// 첫 번째 채널만 사용하며, 입력 이미지의 Domain 안에서만 검사한다.
    /// 스크립트: <c>Region = Threshold(Image, 128, 255)</c>
    /// </summary>
    [Operator("Threshold", Category = "Segmentation", Description = "Selects pixels whose gray value lies in [MinGray, MaxGray] inside the image domain.")]
    public sealed unsafe class Threshold : OperatorBase
    {
        private const int RowsPerChunk = 64;

        [Input(Description = "Input image (first channel is used).")]
        public Image? Image { get; set; }

        [Input(Description = "Lower gray value (inclusive).", Default = 128.0)]
        public double MinGray { get; set; }

        [Input(Description = "Upper gray value (inclusive).", Default = 255.0)]
        public double MaxGray { get; set; }

        [Output(Description = "Selected pixels.")]
        public Region? Region { get; private set; }

        protected override void Validate()
        {
            Require(Image, nameof(Image));
            if (double.IsNaN(MinGray) || double.IsNaN(MaxGray))
                throw VisionException.InvalidArgument("MinGray/MaxGray", "must be a number");
            if (MinGray > MaxGray)
                throw VisionException.InvalidArgument("MinGray", $"must be <= MaxGray ({MinGray} > {MaxGray})");
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var img = Image!;
            var runs = new List<Run>();
            byte* basePtr = (byte*)img.GetHostPointer(forWrite: false);
            ScanRows(img, basePtr, 0, img.Height, runs);
            Region = Types.Region.FromNormalizedRuns(runs.ToArray());
        }

        [Backend(BackendKind.Parallel)]
        private void RunParallel(OperatorContext ctx)
        {
            var img = Image!;
            byte* basePtr = (byte*)img.GetHostPointer(forWrite: false);
            IntPtr basePtrValue = (IntPtr)basePtr;

            int chunks = (img.Height + RowsPerChunk - 1) / RowsPerChunk;
            var lists = new List<Run>[chunks];

            Parallel.For(0, chunks, ctx.CreateParallelOptions(), i =>
            {
                var list = new List<Run>();
                int rowBegin = i * RowsPerChunk;
                int rowEnd = Math.Min(img.Height, rowBegin + RowsPerChunk);
                ScanRows(img, (byte*)basePtrValue, rowBegin, rowEnd, list);
                lists[i] = list;
            });

            int total = 0;
            foreach (var l in lists) total += l.Count;
            var runs = new Run[total];
            int pos = 0;
            foreach (var l in lists)
            {
                l.CopyTo(runs, pos);
                pos += l.Count;
            }
            Region = Types.Region.FromNormalizedRuns(runs);
        }

        /// <summary>[rowBegin, rowEnd) 행을 검사해 run 을 추가한다. Domain 이 있으면 그 run 안에서만 검사한다.</summary>
        private void ScanRows(Image img, byte* basePtr, int rowBegin, int rowEnd, List<Run> output)
        {
            var domain = img.Domain;
            if (domain == null)
            {
                for (int y = rowBegin; y < rowEnd; y++)
                    ScanSegment(img, basePtr + img.Stride * y, y, 0, img.Width - 1, output);
                return;
            }

            var runs = domain.RunsArray;
            for (int i = domain.LowerBoundRow(rowBegin); i < runs.Length && runs[i].Row < rowEnd; i++)
            {
                var r = runs[i];
                ScanSegment(img, basePtr + img.Stride * r.Row, r.Row, r.ColBegin, r.ColEnd, output);
            }
        }

        private void ScanSegment(Image img, byte* row, int y, int x0, int x1, List<Run> output)
        {
            int ch = img.Channels;
            switch (img.PixelType)
            {
                case PixelType.UInt8:
                    if (ClampRange(byte.MinValue, byte.MaxValue, out var lo8, out var hi8))
                        Scan(row, ch, y, x0, x1, (byte)lo8, (byte)hi8, output);
                    break;
                case PixelType.Int16:
                    if (ClampRange(short.MinValue, short.MaxValue, out var lo16s, out var hi16s))
                        Scan((short*)row, ch, y, x0, x1, (short)lo16s, (short)hi16s, output);
                    break;
                case PixelType.UInt16:
                    if (ClampRange(ushort.MinValue, ushort.MaxValue, out var lo16, out var hi16))
                        Scan((ushort*)row, ch, y, x0, x1, (ushort)lo16, (ushort)hi16, output);
                    break;
                case PixelType.Int32:
                    if (ClampRange(int.MinValue, int.MaxValue, out var lo32, out var hi32))
                        Scan((int*)row, ch, y, x0, x1, (int)lo32, (int)hi32, output);
                    break;
                case PixelType.Float32:
                    Scan((float*)row, ch, y, x0, x1, (float)MinGray, (float)MaxGray, output);
                    break;
                case PixelType.Float64:
                    Scan((double*)row, ch, y, x0, x1, MinGray, MaxGray, output);
                    break;
                default:
                    throw new VisionException(ErrorCode.UnsupportedFormat, $"Threshold does not support {img.PixelType}.");
            }
        }

        /// <summary>정수 타입 범위로 [MinGray, MaxGray] 를 잘라낸다. 교집합이 없으면 false.</summary>
        private bool ClampRange(double typeMin, double typeMax, out double lo, out double hi)
        {
            lo = Math.Max(Math.Ceiling(MinGray), typeMin);
            hi = Math.Min(Math.Floor(MaxGray), typeMax);
            return lo <= hi;
        }

        private static void Scan<T>(T* row, int channels, int y, int x0, int x1, T lo, T hi, List<Run> output)
            where T : unmanaged, IComparable<T>
        {
            int start = -1;
            for (int x = x0; x <= x1; x++)
            {
                T v = row[(long)x * channels];
                bool inside = v.CompareTo(lo) >= 0 && v.CompareTo(hi) <= 0;
                if (inside)
                {
                    if (start < 0) start = x;
                }
                else if (start >= 0)
                {
                    output.Add(new Run(y, start, x - 1));
                    start = -1;
                }
            }
            if (start >= 0)
                output.Add(new Run(y, start, x1));
        }
    }
}
