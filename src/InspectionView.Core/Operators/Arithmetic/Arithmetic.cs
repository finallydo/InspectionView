using InspectionView.Core.Operators.Common;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;
using OpenCvSharp;

namespace InspectionView.Core.Operators.Arithmetic
{
    [Operator("AddImage", Category = "Arithmetic", Description = "result = (Image1 + Image2) * Mult + Add")]
    public sealed class AddImage : TwoImageOperator
    {
        [Input(Default = 0.5, Order = 2)] public double Mult { get; set; }
        [Input(Default = 0.0, Order = 3)] public double Add { get; set; }

        protected override Image Process(Image a, Image b, OperatorContext ctx)
            => ImageOps.Binary(a, b, (x, y, d) => Cv2.AddWeighted(x, Mult, y, Mult, Add, d));
    }

    [Operator("SubImage", Category = "Arithmetic", Description = "result = (Image1 - Image2) * Mult + Add")]
    public sealed class SubImage : TwoImageOperator
    {
        [Input(Default = 1.0, Order = 2)] public double Mult { get; set; }
        [Input(Default = 0.0, Order = 3)] public double Add { get; set; }

        protected override Image Process(Image a, Image b, OperatorContext ctx)
            => ImageOps.Binary(a, b, (x, y, d) => Cv2.AddWeighted(x, Mult, y, -Mult, Add, d));
    }

    [Operator("MultImage", Category = "Arithmetic", Description = "result = Image1 * Image2 * Mult + Add")]
    public sealed class MultImage : TwoImageOperator
    {
        [Input(Default = 1.0, Order = 2)] public double Mult { get; set; }
        [Input(Default = 0.0, Order = 3)] public double Add { get; set; }

        protected override Image Process(Image a, Image b, OperatorContext ctx)
            => ImageOps.Binary(a, b, (x, y, d) =>
            {
                using (var f = new Mat())
                {
                    Cv2.Multiply(x, y, f, Mult, MatType.CV_32F);
                    f.ConvertTo(d, d.Type(), 1.0, Add);
                }
            });
    }

    [Operator("AbsDiffImage", Category = "Arithmetic", Description = "result = |Image1 - Image2|")]
    public sealed class AbsDiffImage : TwoImageOperator
    {
        protected override Image Process(Image a, Image b, OperatorContext ctx)
            => ImageOps.Binary(a, b, (x, y, d) => Cv2.Absdiff(x, y, d));
    }

    [Operator("MinImage", Category = "Arithmetic", Description = "Pixel-wise minimum.")]
    public sealed class MinImage : TwoImageOperator
    {
        protected override Image Process(Image a, Image b, OperatorContext ctx)
            => ImageOps.Binary(a, b, (x, y, d) => Cv2.Min(x, y, d));
    }

    [Operator("MaxImage", Category = "Arithmetic", Description = "Pixel-wise maximum.")]
    public sealed class MaxImage : TwoImageOperator
    {
        protected override Image Process(Image a, Image b, OperatorContext ctx)
            => ImageOps.Binary(a, b, (x, y, d) => Cv2.Max(x, y, d));
    }
}
