using InspectionView.Core.Backends;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;
using OpenCvSharp;

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

    /// <summary>
    /// InputArray/OutputArray 만 쓰는 OpenCV 호출로 구현되는 이미지→이미지 연산자. 같은 <see cref="Apply"/> 가
    /// Cpu(Mat) 와 Gpu(OpenCL UMat) 백엔드 양쪽에 쓰인다.
    /// </summary>
    public abstract class GpuFilterOperator : ImageToImageOperator
    {
        /// <summary>출력 픽셀 타입 (null = 입력과 같음).</summary>
        protected virtual PixelType? OutputType(Image source) => null;

        /// <summary>출력 채널 수 (null = 입력과 같음).</summary>
        protected virtual int? OutputChannels(Image source) => null;

        /// <summary>src → dst 연산. dst 는 이미 출력 크기/타입으로 할당되어 있다 (재할당되는 호출은 금지).</summary>
        protected abstract void Apply(InputArray src, OutputArray dst, Image source);

        protected override Image Process(Image source, OperatorContext ctx)
            => ImageOps.Filter(source, (s, d) => Apply(s, d, source), OutputType(source), OutputChannels(source));

        [Backend(BackendKind.Gpu)]
        protected void RunGpu(OperatorContext ctx)
        {
            var source = Image!;
            ImageResult = ImageOps.FilterGpu(source, (s, d) => Apply(s, d, source), OutputType(source), OutputChannels(source));
        }
    }

    /// <summary>두 입력, InputArray/OutputArray 기반 (Cpu + Gpu).</summary>
    public abstract class GpuBinaryOperator : TwoImageOperator
    {
        protected virtual PixelType? OutputType(Image a, Image b) => null;

        protected abstract void Apply(InputArray a, InputArray b, OutputArray dst);

        protected override Image Process(Image a, Image b, OperatorContext ctx)
            => ImageOps.Binary(a, b, (x, y, d) => Apply(x, y, d), OutputType(a, b));

        [Backend(BackendKind.Gpu)]
        protected void RunGpu(OperatorContext ctx)
        {
            var a = Image1!;
            var b = Image2!;
            ImageResult = ImageOps.BinaryGpu(a, b, Apply, OutputType(a, b));
        }
    }
}
