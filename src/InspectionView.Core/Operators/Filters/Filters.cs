using System;
using InspectionView.Core.Errors;
using InspectionView.Core.Interop;
using InspectionView.Core.Operators.Common;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;
using OpenCvSharp;
using ErrorCode = InspectionView.Core.Errors.ErrorCode;

namespace InspectionView.Core.Operators.Filters
{
    [Operator("GaussFilter", Category = "Filters", Description = "Gaussian smoothing with the given sigma (kernel size derived from sigma).")]
    public sealed class GaussFilter : ImageToImageOperator
    {
        [Input(Description = "Standard deviation in pixels.", Default = 1.0, Min = 0.1, Order = 1)]
        public double Sigma { get; set; }

        protected override Image Process(Image source, OperatorContext ctx)
            => ImageOps.Filter(source, (s, d) => Cv2.GaussianBlur(s, d, new Size(0, 0), Sigma, Sigma, BorderTypes.Reflect101));
    }

    [Operator("MeanFilter", Category = "Filters", Description = "Box (mean) filter with a Width x Height window.")]
    public sealed class MeanFilter : ImageToImageOperator
    {
        [Input(Description = "Window width.", Default = 3, Min = 1, Order = 1)]
        public int Width { get; set; }

        [Input(Description = "Window height.", Default = 3, Min = 1, Order = 2)]
        public int Height { get; set; }

        protected override Image Process(Image source, OperatorContext ctx)
            => ImageOps.Filter(source, (s, d) => Cv2.Blur(s, d, new Size(Width, Height), null, BorderTypes.Reflect101));
    }

    [Operator("MedianFilter", Category = "Filters", Description = "Median filter with a square window of odd size (8-bit: any size, others: 3 or 5).")]
    public sealed class MedianFilter : ImageToImageOperator
    {
        [Input(Description = "Window size (odd).", Default = 3, Min = 1, Order = 1)]
        public int Size { get; set; }

        protected override void ValidateParameters()
        {
            ImageOps.RequireOddSize(Size, nameof(Size));
            if (Image!.PixelType != PixelType.UInt8 && Size > 5)
                throw VisionException.InvalidArgument(nameof(Size), "must be 3 or 5 for non 8-bit images");
            if (Image.PixelType == PixelType.Int32 || Image.PixelType == PixelType.Float64 || Image.PixelType == PixelType.Int16)
                throw new VisionException(ErrorCode.UnsupportedFormat, $"MedianFilter does not support {Image.PixelType}.");
        }

        protected override Image Process(Image source, OperatorContext ctx)
            => ImageOps.Filter(source, (s, d) => Cv2.MedianBlur(s, d, Size));
    }

    public enum SobelMode
    {
        /// <summary>|dx| + |dy|</summary>
        SumAbs,
        /// <summary>sqrt(dx² + dy²)</summary>
        Sqrt,
        /// <summary>dx 만</summary>
        X,
        /// <summary>dy 만</summary>
        Y,
    }

    [Operator("SobelAmp", Category = "Filters", Description = "Sobel edge amplitude. Result has the input type (saturated) unless AsReal is true.")]
    public sealed class SobelAmp : ImageToImageOperator
    {
        [Input(Description = "Amplitude mode.", Default = SobelMode.SumAbs, Order = 1)]
        public SobelMode Mode { get; set; }

        [Input(Description = "Sobel kernel size (1, 3, 5, 7).", Default = 3, Order = 2)]
        public int Size { get; set; }

        [Input(Description = "Return a float image instead of the input type.", Default = false, Order = 3)]
        public bool AsReal { get; set; }

        protected override void ValidateParameters()
        {
            if (Size != 1 && Size != 3 && Size != 5 && Size != 7)
                throw VisionException.InvalidArgument(nameof(Size), "must be 1, 3, 5 or 7");
        }

        protected override Image Process(Image source, OperatorContext ctx)
        {
            var outType = AsReal ? PixelType.Float32 : source.PixelType;
            return ImageOps.Filter(source, (s, d) =>
            {
                using (var dx = new Mat())
                using (var dy = new Mat())
                using (var amp = new Mat())
                {
                    if (Mode != SobelMode.Y) Cv2.Sobel(s, dx, MatType.CV_32F, 1, 0, Size);
                    if (Mode != SobelMode.X) Cv2.Sobel(s, dy, MatType.CV_32F, 0, 1, Size);
                    switch (Mode)
                    {
                        case SobelMode.X: Cv2.Absdiff(dx, Scalar.All(0), amp); break;
                        case SobelMode.Y: Cv2.Absdiff(dy, Scalar.All(0), amp); break;
                        case SobelMode.Sqrt: Cv2.Magnitude(dx, dy, amp); break;
                        default:
                            using (var ax = new Mat())
                            using (var ay = new Mat())
                            {
                                Cv2.Absdiff(dx, Scalar.All(0), ax);
                                Cv2.Absdiff(dy, Scalar.All(0), ay);
                                Cv2.Add(ax, ay, amp);
                            }
                            break;
                    }
                    amp.ConvertTo(d, d.Type());
                }
            }, outType);
        }
    }

    [Operator("Emphasize", Category = "Filters", Description = "Sharpens: result = (image - mean) * Factor + image, using a Width x Height mean window.")]
    public sealed class Emphasize : ImageToImageOperator
    {
        [Input(Description = "Window width.", Default = 7, Min = 1, Order = 1)]
        public int Width { get; set; }

        [Input(Description = "Window height.", Default = 7, Min = 1, Order = 2)]
        public int Height { get; set; }

        [Input(Description = "Sharpening factor.", Default = 1.0, Order = 3)]
        public double Factor { get; set; }

        protected override Image Process(Image source, OperatorContext ctx)
            => ImageOps.Filter(source, (s, d) =>
            {
                using (var f = new Mat())
                using (var mean = new Mat())
                using (var res = new Mat())
                {
                    s.ConvertTo(f, MatType.CV_32F);
                    Cv2.Blur(f, mean, new Size(Width, Height), null, BorderTypes.Reflect101);
                    // (f - mean) * Factor + f = f * (1 + Factor) - mean * Factor
                    Cv2.AddWeighted(f, 1.0 + Factor, mean, -Factor, 0, res);
                    res.ConvertTo(d, d.Type());
                }
            });
    }

    [Operator("ScaleImage", Category = "Filters", Description = "Linear gray value transform: result = image * Mult + Add (saturated to the image type).")]
    public sealed class ScaleImage : ImageToImageOperator
    {
        [Input(Description = "Multiplier.", Default = 1.0, Order = 1)]
        public double Mult { get; set; }

        [Input(Description = "Offset.", Default = 0.0, Order = 2)]
        public double Add { get; set; }

        protected override Image Process(Image source, OperatorContext ctx)
            => ImageOps.Filter(source, (s, d) => s.ConvertTo(d, d.Type(), Mult, Add));
    }

    [Operator("InvertImage", Category = "Filters", Description = "Inverts gray values (max - value for integer types, -value for float).")]
    public sealed class InvertImage : ImageToImageOperator
    {
        protected override Image Process(Image source, OperatorContext ctx)
        {
            double max = source.PixelType.IsFloatingPoint() ? 0 : source.PixelType.ValueRange().Max;
            return ImageOps.Filter(source, (s, d) => s.ConvertTo(d, d.Type(), -1.0, max));
        }
    }

    [Operator("ConvertImageType", Category = "Filters", Description = "Converts the pixel type (values are saturated for integer targets).")]
    public sealed class ConvertImageType : ImageToImageOperator
    {
        [Input(Description = "Target pixel type.", Default = PixelType.UInt8, Order = 1)]
        public PixelType Type { get; set; }

        protected override Image Process(Image source, OperatorContext ctx)
            => ImageOps.Filter(source, (s, d) => s.ConvertTo(d, d.Type()), Type);
    }

    [Operator("Rgb1ToGray", Category = "Filters", Description = "Converts a 3- or 4-channel (BGR) image to gray with the standard luminance weights.")]
    public sealed class Rgb1ToGray : ImageToImageOperator
    {
        protected override void ValidateParameters()
        {
            if (Image!.Channels != 3 && Image.Channels != 4)
                throw new VisionException(ErrorCode.TypeMismatch, $"Rgb1ToGray needs a 3- or 4-channel image (got {Image.Channels}).");
        }

        protected override Image Process(Image source, OperatorContext ctx)
        {
            var code = source.Channels == 3 ? ColorConversionCodes.BGR2GRAY : ColorConversionCodes.BGRA2GRAY;
            return ImageOps.Filter(source, (s, d) => Cv2.CvtColor(s, d, code), null, 1);
        }
    }

    [Operator("Decompose3", Category = "Filters", Description = "Splits a 3-channel image into its channels (Image1 = B, Image2 = G, Image3 = R for files read by ReadImage).")]
    public sealed class Decompose3 : OperatorBase
    {
        [Input(Description = "3-channel image.")]
        public Image? Image { get; set; }

        [Output] public Image? Image1 { get; private set; }
        [Output] public Image? Image2 { get; private set; }
        [Output] public Image? Image3 { get; private set; }

        protected override void Validate()
        {
            var img = Require(Image, nameof(Image));
            if (img.Channels < 3)
                throw new VisionException(ErrorCode.TypeMismatch, $"Decompose3 needs at least 3 channels (got {img.Channels}).");
        }

        [Backend(Backends.BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var src = Image!;
            using (var s = MatInterop.AsMat(src))
            {
                var planes = Cv2.Split(s);
                try
                {
                    Image1 = FromPlane(planes[0], src);
                    Image2 = FromPlane(planes[1], src);
                    Image3 = FromPlane(planes[2], src);
                }
                finally
                {
                    foreach (var p in planes) p.Dispose();
                }
            }
        }

        private static Image FromPlane(Mat plane, Image src)
        {
            var img = MatInterop.ToImage(plane);
            return ImageOps.KeepDomain(img, src);
        }
    }

    [Operator("Compose3", Category = "Filters", Description = "Combines three single-channel images into one 3-channel image.")]
    public sealed class Compose3 : OperatorBase
    {
        [Input] public Image? Image1 { get; set; }
        [Input] public Image? Image2 { get; set; }
        [Input] public Image? Image3 { get; set; }

        [Output(Description = "3-channel image.")]
        public Image? ImageResult { get; private set; }

        protected override void Validate()
        {
            var a = Require(Image1, nameof(Image1));
            var b = Require(Image2, nameof(Image2));
            var c = Require(Image3, nameof(Image3));
            foreach (var img in new[] { a, b, c })
            {
                if (img.Channels != 1)
                    throw new VisionException(ErrorCode.TypeMismatch, "Compose3 needs single-channel images.");
                if (img.Width != a.Width || img.Height != a.Height || img.PixelType != a.PixelType)
                    throw new VisionException(ErrorCode.SizeMismatch, "Compose3 inputs must have the same size and type.");
            }
        }

        [Backend(Backends.BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var a = Image1!;
            var dst = ImageOps.NewLike(a, null, 3);
            try
            {
                using (var ma = MatInterop.AsMat(a))
                using (var mb = MatInterop.AsMat(Image2!))
                using (var mc = MatInterop.AsMat(Image3!))
                using (var d = MatInterop.AsMat(dst, forWrite: true))
                {
                    Cv2.Merge(new[] { ma, mb, mc }, d);
                }
            }
            catch
            {
                dst.Release();
                throw;
            }
            ImageResult = ImageOps.KeepDomain(dst, a);
        }
    }
}
