using InspectionView.Core.Backends;
using InspectionView.Core.Errors;
using InspectionView.Core.Interop;
using InspectionView.Core.Operators.Common;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;
using OpenCvSharp;
using ErrorCode = InspectionView.Core.Errors.ErrorCode;

namespace InspectionView.Core.Operators.Regions
{
    [Operator("PaintRegion", Category = "Regions", Description = "Copies the image and paints the region with a gray value.")]
    public sealed class PaintRegion : OperatorBase
    {
        [Input] public Image? Image { get; set; }
        [Input] public Region? Region { get; set; }
        [Input(Description = "Gray value (all channels).", Default = 255.0)] public double Grayval { get; set; }
        [Output] public Image? ImageResult { get; private set; }

        protected override void Validate() { Require(Image, nameof(Image)); Require(Region, nameof(Region)); }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var src = Image!;
            var dst = src.Clone();
            try
            {
                var mask = Region!.ToMask(src.Width, src.Height);
                using (var d = MatInterop.AsMat(dst, forWrite: true))
                    d.SetTo(Scalar.All(Grayval), mask);
            }
            catch
            {
                dst.Release();
                throw;
            }
            ImageResult = dst;
        }
    }

    [Operator("RegionToBin", Category = "Regions", Description = "Creates a Width x Height 8-bit image with ForegroundGray inside the region and BackgroundGray outside.")]
    public sealed class RegionToBin : OperatorBase
    {
        [Input] public Region? Region { get; set; }
        [Input(Default = 255, Min = 0, Max = 255)] public int ForegroundGray { get; set; }
        [Input(Default = 0, Min = 0, Max = 255)] public int BackgroundGray { get; set; }
        [Input(Min = 1)] public int Width { get; set; }
        [Input(Min = 1)] public int Height { get; set; }
        [Output] public Image? BinImage { get; private set; }

        protected override void Validate()
        {
            Require(Region, nameof(Region));
            if (Width <= 0 || Height <= 0) throw VisionException.InvalidArgument("Width/Height", "must be > 0");
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var img = Types.Image.Create(Width, Height, PixelType.UInt8);
            try
            {
                using (var m = MatInterop.AsMat(img, forWrite: true))
                {
                    m.SetTo(Scalar.All(BackgroundGray));
                    m.SetTo(Scalar.All(ForegroundGray), Region!.ToMask(Width, Height));
                }
            }
            catch
            {
                img.Release();
                throw;
            }
            BinImage = img;
        }
    }

    [Operator("Intensity", Category = "Regions", Description = "Mean and standard deviation of the gray values inside the region (or the image domain).")]
    public sealed class Intensity : OperatorBase
    {
        [Input] public Image? Image { get; set; }
        [Input(Optional = true, Description = "Region to evaluate (default: image domain).")] public Region? Region { get; set; }
        [Output] public TupleValue? Mean { get; private set; }
        [Output] public TupleValue? Deviation { get; private set; }

        protected override void Validate() => Require(Image, nameof(Image));

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var img = Image!;
            using (var m = MatInterop.AsMat(img))
            {
                var mask = ImageOps.MaskFor(img, Region);
                Scalar mean, dev;
                if (mask != null)
                    Cv2.MeanStdDev(m, out mean, out dev, mask);
                else
                    Cv2.MeanStdDev(m, out mean, out dev);
                int ch = System.Math.Min(img.Channels, 4);
                var means = new double[ch];
                var devs = new double[ch];
                for (int i = 0; i < ch; i++) { means[i] = mean[i]; devs[i] = dev[i]; }
                Mean = means;
                Deviation = devs;
            }
        }
    }

    [Operator("MinMaxGray", Category = "Regions", Description = "Minimum, maximum and range of the gray values inside the region (first channel).")]
    public sealed class MinMaxGray : OperatorBase
    {
        [Input] public Image? Image { get; set; }
        [Input(Optional = true, Description = "Region to evaluate (default: image domain).")] public Region? Region { get; set; }
        [Output] public TupleValue? Min { get; private set; }
        [Output] public TupleValue? Max { get; private set; }
        [Output] public TupleValue? Range { get; private set; }

        protected override void Validate() => Require(Image, nameof(Image));

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var img = Image!;
            using (var m = MatInterop.AsMat(img))
            {
                var mask = ImageOps.MaskFor(img, Region);
                Mat single = m;
                Mat? extracted = null;
                if (img.Channels > 1)
                {
                    extracted = new Mat();
                    Cv2.ExtractChannel(m, extracted, 0);
                    single = extracted;
                }
                try
                {
                    double min, max;
                    if (mask != null)
                        Cv2.MinMaxLoc(single, out min, out max, out _, out _, mask);
                    else
                        Cv2.MinMaxLoc(single, out min, out max);
                    Min = min;
                    Max = max;
                    Range = max - min;
                }
                finally
                {
                    extracted?.Dispose();
                }
            }
        }
    }

    public enum LightDark
    {
        Light,
        Dark,
        Equal,
        NotEqual,
    }

    [Operator("DynThreshold", Category = "Segmentation", Description = "Local threshold against a reference (e.g. mean-filtered) image: Light selects Image >= Reference + Offset, Dark selects Image <= Reference - Offset.")]
    public sealed class DynThreshold : OperatorBase
    {
        [Input] public Image? Image { get; set; }
        [Input(Description = "Reference image (same size), typically a smoothed copy.")] public Image? ImageReference { get; set; }
        [Input(Default = 5.0)] public double Offset { get; set; }
        [Input(Default = LightDark.Light)] public LightDark LightDark { get; set; }
        [Output] public Region? Region { get; private set; }

        protected override void Validate()
        {
            var a = Require(Image, nameof(Image));
            var b = Require(ImageReference, nameof(ImageReference));
            if (a.Width != b.Width || a.Height != b.Height)
                throw new VisionException(ErrorCode.SizeMismatch, "Image and ImageReference must have the same size.");
            if (a.Channels != 1 || b.Channels != 1)
                throw new VisionException(ErrorCode.TypeMismatch, "DynThreshold needs single-channel images.");
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var img = Image!;
            using (var a = MatInterop.AsMat(img))
            using (var b = MatInterop.AsMat(ImageReference!))
            using (var diff = new Mat())
            using (var mask = new Mat())
            {
                Cv2.Subtract(a, b, diff, null, MatType.CV_32F);
                const double inf = 1e30;
                switch (LightDark)
                {
                    case LightDark.Light: Cv2.InRange(diff, new Scalar(Offset), new Scalar(inf), mask); break;
                    case LightDark.Dark: Cv2.InRange(diff, new Scalar(-inf), new Scalar(-Offset), mask); break;
                    case LightDark.Equal: Cv2.InRange(diff, new Scalar(-Offset), new Scalar(Offset), mask); break;
                    default:
                        using (var inside = new Mat())
                        {
                            Cv2.InRange(diff, new Scalar(-Offset), new Scalar(Offset), inside);
                            Cv2.BitwiseNot(inside, mask);
                        }
                        break;
                }

                var region = Types.Region.FromMask(mask);
                if (img.Domain != null)
                {
                    using (region)
                        Region = region.Intersection(img.Domain);
                }
                else
                {
                    Region = region;
                }
            }
        }
    }
}
