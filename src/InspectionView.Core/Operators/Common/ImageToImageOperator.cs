using InspectionView.Core.Backends;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;

namespace InspectionView.Core.Operators.Common
{
    /// <summary>입력 이미지 하나 → 출력 이미지 하나인 연산자의 공통 기반. 파생 클래스의 입력은 Order 1 이상으로 지정할 것.</summary>
    public abstract class ImageToImageOperator : OperatorBase
    {
        [Input(Description = "Input image.", Order = 0)]
        public Image? Image { get; set; }

        [Output(Description = "Result image.", Order = 100)]
        public Image? ImageResult { get; protected set; }

        protected override void Validate()
        {
            Require(Image, nameof(Image));
            ValidateParameters();
        }

        protected virtual void ValidateParameters()
        {
        }

        protected abstract Image Process(Image source, OperatorContext ctx);

        [Backend(BackendKind.Cpu)]
        protected void RunCpu(OperatorContext ctx)
        {
            ImageResult = Process(Image!, ctx);
        }
    }

    /// <summary>입력 이미지 두 개 → 출력 하나.</summary>
    public abstract class TwoImageOperator : OperatorBase
    {
        [Input(Description = "First image.", Order = 0)]
        public Image? Image1 { get; set; }

        [Input(Description = "Second image.", Order = 1)]
        public Image? Image2 { get; set; }

        [Output(Description = "Result image.", Order = 100)]
        public Image? ImageResult { get; protected set; }

        protected override void Validate()
        {
            Require(Image1, nameof(Image1));
            Require(Image2, nameof(Image2));
            ValidateParameters();
        }

        protected virtual void ValidateParameters()
        {
        }

        protected abstract Image Process(Image a, Image b, OperatorContext ctx);

        [Backend(BackendKind.Cpu)]
        protected void RunCpu(OperatorContext ctx)
        {
            ImageResult = Process(Image1!, Image2!, ctx);
        }
    }
}
