using InspectionView.Core.Backends;
using InspectionView.Core.Errors;
using InspectionView.Core.Operators.Common;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;
using OpenCvSharp;

namespace InspectionView.Core.Operators.Morphology
{
    /// <summary>Region 모폴로지 공통 기반 (사각형 구조 요소).</summary>
    public abstract class RegionRectMorphology : OperatorBase
    {
        [Input(Description = "Input region.", Order = 0)]
        public Region? Region { get; set; }

        [Input(Description = "Structuring element width.", Default = 3, Min = 1, Order = 1)]
        public int Width { get; set; }

        [Input(Description = "Structuring element height.", Default = 3, Min = 1, Order = 2)]
        public int Height { get; set; }

        [Output(Description = "Result region.", Order = 100)]
        public Region? RegionResult { get; private set; }

        protected abstract MorphTypes Op { get; }

        protected override void Validate() => Require(Region, nameof(Region));

        [Backend(BackendKind.Cpu)]
        protected void RunCpu(OperatorContext ctx)
            => RegionResult = RegionOps.Morphology(Region!, Op, MorphShapes.Rect, Width, Height);
    }

    /// <summary>Region 모폴로지 공통 기반 (원 구조 요소).</summary>
    public abstract class RegionCircleMorphology : OperatorBase
    {
        [Input(Description = "Input region.", Order = 0)]
        public Region? Region { get; set; }

        [Input(Description = "Radius of the circular structuring element.", Default = 1.5, Min = 0.5, Order = 1)]
        public double Radius { get; set; }

        [Output(Description = "Result region.", Order = 100)]
        public Region? RegionResult { get; private set; }

        protected abstract MorphTypes Op { get; }

        protected override void Validate() => Require(Region, nameof(Region));

        [Backend(BackendKind.Cpu)]
        protected void RunCpu(OperatorContext ctx)
        {
            int d = 2 * (int)System.Math.Round(Radius) + 1;
            RegionResult = RegionOps.Morphology(Region!, Op, MorphShapes.Ellipse, d, d);
        }
    }

    [Operator("ErosionRectangle1", Category = "Morphology", Description = "Erodes a region with a Width x Height rectangle.")]
    public sealed class ErosionRectangle1 : RegionRectMorphology { protected override MorphTypes Op => MorphTypes.Erode; }

    [Operator("DilationRectangle1", Category = "Morphology", Description = "Dilates a region with a Width x Height rectangle.")]
    public sealed class DilationRectangle1 : RegionRectMorphology { protected override MorphTypes Op => MorphTypes.Dilate; }

    [Operator("OpeningRectangle1", Category = "Morphology", Description = "Opening (erosion then dilation) with a rectangle.")]
    public sealed class OpeningRectangle1 : RegionRectMorphology { protected override MorphTypes Op => MorphTypes.Open; }

    [Operator("ClosingRectangle1", Category = "Morphology", Description = "Closing (dilation then erosion) with a rectangle.")]
    public sealed class ClosingRectangle1 : RegionRectMorphology { protected override MorphTypes Op => MorphTypes.Close; }

    [Operator("ErosionCircle", Category = "Morphology", Description = "Erodes a region with a circle of the given radius.")]
    public sealed class ErosionCircle : RegionCircleMorphology { protected override MorphTypes Op => MorphTypes.Erode; }

    [Operator("DilationCircle", Category = "Morphology", Description = "Dilates a region with a circle of the given radius.")]
    public sealed class DilationCircle : RegionCircleMorphology { protected override MorphTypes Op => MorphTypes.Dilate; }

    [Operator("OpeningCircle", Category = "Morphology", Description = "Opening with a circle.")]
    public sealed class OpeningCircle : RegionCircleMorphology { protected override MorphTypes Op => MorphTypes.Open; }

    [Operator("ClosingCircle", Category = "Morphology", Description = "Closing with a circle.")]
    public sealed class ClosingCircle : RegionCircleMorphology { protected override MorphTypes Op => MorphTypes.Close; }

    [Operator("FillUp", Category = "Morphology", Description = "Fills all holes of a region.")]
    public sealed class FillUp : OperatorBase
    {
        [Input] public Region? Region { get; set; }
        [Output] public Region? RegionResult { get; private set; }

        protected override void Validate() => Require(Region, nameof(Region));

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx) => RegionResult = RegionOps.FillUp(Region!);
    }

    public enum BoundaryType
    {
        /// <summary>영역 안쪽 1픽셀 테두리</summary>
        Inner,
        /// <summary>영역 바깥쪽 1픽셀 테두리</summary>
        Outer,
    }

    [Operator("Boundary", Category = "Morphology", Description = "One-pixel inner or outer boundary of a region.")]
    public sealed class Boundary : OperatorBase
    {
        [Input] public Region? Region { get; set; }
        [Input(Default = BoundaryType.Inner)] public BoundaryType Type { get; set; }
        [Output] public Region? RegionResult { get; private set; }

        protected override void Validate() => Require(Region, nameof(Region));

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var r = Region!;
            if (Type == BoundaryType.Inner)
            {
                using (var eroded = RegionOps.Morphology(r, MorphTypes.Erode, MorphShapes.Rect, 3, 3))
                    RegionResult = r.Difference(eroded);
            }
            else
            {
                using (var dilated = RegionOps.Morphology(r, MorphTypes.Dilate, MorphShapes.Rect, 3, 3))
                    RegionResult = dilated.Difference(r);
            }
        }
    }

    /// <summary>그레이 모폴로지 (이미지, 사각형 구조 요소).</summary>
    public abstract class GrayRectMorphology : ImageToImageOperator
    {
        [Input(Description = "Mask width.", Default = 3, Min = 1, Order = 1)]
        public int Width { get; set; }

        [Input(Description = "Mask height.", Default = 3, Min = 1, Order = 2)]
        public int Height { get; set; }

        protected abstract MorphTypes Op { get; }

        protected override Image Process(Image source, OperatorContext ctx)
        {
            using (var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(Width, Height)))
            {
                var k = kernel;
                return ImageOps.Filter(source, (s, d) => Cv2.MorphologyEx(s, d, Op, k, null, 1, BorderTypes.Replicate));
            }
        }
    }

    [Operator("GrayErosionRect", Category = "Morphology", Description = "Gray value erosion (minimum) with a rectangular mask.")]
    public sealed class GrayErosionRect : GrayRectMorphology { protected override MorphTypes Op => MorphTypes.Erode; }

    [Operator("GrayDilationRect", Category = "Morphology", Description = "Gray value dilation (maximum) with a rectangular mask.")]
    public sealed class GrayDilationRect : GrayRectMorphology { protected override MorphTypes Op => MorphTypes.Dilate; }

    [Operator("GrayOpeningRect", Category = "Morphology", Description = "Gray value opening with a rectangular mask.")]
    public sealed class GrayOpeningRect : GrayRectMorphology { protected override MorphTypes Op => MorphTypes.Open; }

    [Operator("GrayClosingRect", Category = "Morphology", Description = "Gray value closing with a rectangular mask.")]
    public sealed class GrayClosingRect : GrayRectMorphology { protected override MorphTypes Op => MorphTypes.Close; }

    [Operator("GrayTopHat", Category = "Morphology", Description = "Image minus gray opening (bright details smaller than the mask).")]
    public sealed class GrayTopHat : GrayRectMorphology { protected override MorphTypes Op => MorphTypes.TopHat; }

    [Operator("GrayBlackHat", Category = "Morphology", Description = "Gray closing minus image (dark details smaller than the mask).")]
    public sealed class GrayBlackHat : GrayRectMorphology { protected override MorphTypes Op => MorphTypes.BlackHat; }
}
