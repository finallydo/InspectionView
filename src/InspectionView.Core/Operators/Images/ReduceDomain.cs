using InspectionView.Core.Backends;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;

namespace InspectionView.Core.Operators.Images
{
    /// <summary>
    /// 이미지의 Domain 을 Region 으로 줄인다 (Halcon reduce_domain). 픽셀은 복사하지 않고 저장소를 공유한다.
    /// 스크립트: <c>ImageReduced = ReduceDomain(Image, Region)</c>
    /// </summary>
    [Operator("ReduceDomain", Category = "Images", Description = "Restricts the image domain to a region without copying pixels.")]
    public sealed class ReduceDomain : OperatorBase
    {
        [Input(Description = "Input image.")]
        public Image? Image { get; set; }

        [Input(Description = "New domain.")]
        public Region? Region { get; set; }

        [Output(Description = "Image with reduced domain.")]
        public Image? ImageReduced { get; private set; }

        protected override void Validate()
        {
            Require(Image, nameof(Image));
            Require(Region, nameof(Region));
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            // 기존 Domain 이 있으면 교집합
            var img = Image!;
            if (img.Domain == null)
            {
                ImageReduced = img.ReduceDomain(Region!);
                return;
            }

            using (var inter = img.Domain.Intersection(Region!))
            {
                ImageReduced = img.ReduceDomain(inter);
            }
        }
    }
}
