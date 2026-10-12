using System;
using System.Threading.Tasks;
using InspectionView.Core.Backends;
using InspectionView.Core.Errors;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;

namespace InspectionView.Core.Operators.Images
{
    /// <summary>
    /// 이미지의 사각형 부분을 잘라 새 이미지를 만든다 (Halcon crop_part). 결과의 Domain 은 전체이다.
    /// 스크립트: <c>Part = CropPart(Image, 100, 200, 640, 480)</c>
    /// </summary>
    [Operator("CropPart", Category = "Images", Description = "Copies a rectangular part (Row, Column, Width, Height) into a new image.")]
    public sealed unsafe class CropPart : OperatorBase
    {
        private const int ParallelThresholdBytes = 1 << 20;

        [Input(Description = "Input image.")]
        public Image? Image { get; set; }

        [Input(Description = "Top row of the part.", Default = 0)]
        public int Row { get; set; }

        [Input(Description = "Left column of the part.", Default = 0)]
        public int Column { get; set; }

        [Input(Description = "Width of the part.", Min = 1)]
        public int Width { get; set; }

        [Input(Description = "Height of the part.", Min = 1)]
        public int Height { get; set; }

        [Output(Description = "Cropped image.")]
        public Image? ImagePart { get; private set; }

        protected override void Validate()
        {
            var img = Require(Image, nameof(Image));
            if (Width <= 0) throw VisionException.InvalidArgument(nameof(Width), "must be > 0");
            if (Height <= 0) throw VisionException.InvalidArgument(nameof(Height), "must be > 0");
            if (Row < 0 || Row + Height > img.Height)
                throw VisionException.OutOfRange("Row+Height", Row + Height, Row < 0 ? 0 : Height, img.Height);
            if (Column < 0 || Column + Width > img.Width)
                throw VisionException.OutOfRange("Column+Width", Column + Width, Column < 0 ? 0 : Width, img.Width);
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var src = Image!;
            var dst = new Image(Width, Height, src.PixelType, src.Channels);
            byte* s = (byte*)src.GetHostPointer(forWrite: false) + src.Stride * Row + (long)Column * src.BytesPerPixel;
            byte* d = (byte*)dst.GetHostPointer(forWrite: true);
            long rowBytes = (long)Width * src.BytesPerPixel;

            for (int y = 0; y < Height; y++)
                Buffer.MemoryCopy(s + src.Stride * y, d + dst.Stride * y, dst.Stride, rowBytes);

            ImagePart = dst;
        }

        [Backend(BackendKind.Parallel)]
        private void RunParallel(OperatorContext ctx)
        {
            var src = Image!;
            long rowBytes = (long)Width * src.BytesPerPixel;
            if (rowBytes * Height < ParallelThresholdBytes)
            {
                RunCpu(ctx);
                return;
            }

            var dst = new Image(Width, Height, src.PixelType, src.Channels);
            IntPtr s = (IntPtr)((byte*)src.GetHostPointer(forWrite: false) + src.Stride * Row + (long)Column * src.BytesPerPixel);
            IntPtr d = dst.GetHostPointer(forWrite: true);
            long srcStride = src.Stride, dstStride = dst.Stride;

            Parallel.For(0, Height, ctx.CreateParallelOptions(), y =>
                Buffer.MemoryCopy((byte*)s + srcStride * y, (byte*)d + dstStride * y, dstStride, rowBytes));

            ImagePart = dst;
        }
    }
}
