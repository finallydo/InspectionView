using InspectionView.Core.Memory;

namespace InspectionView.Core.Types
{
    /// <summary>NCC(정규화 상관) 템플릿 모델. 템플릿 이미지(8bit 또는 float, 1채널)와 기준점을 가진다.</summary>
    public sealed class NccModel : IconicObject
    {
        private Image? _template;

        public override string TypeName => "NccModel";

        /// <summary>템플릿 이미지 (모델이 소유).</summary>
        public Image Template => _template ?? throw Errors.VisionException.Disposed(TypeName);

        /// <summary>기준점(원점)의 템플릿 내 좌표. 기본은 중심.</summary>
        public double OriginRow { get; }
        public double OriginColumn { get; }

        public int Width => Template.Width;
        public int Height => Template.Height;

        public NccModel(Image template, double originRow, double originColumn)
        {
            template.AddRef();
            _template = template;
            OriginRow = originRow;
            OriginColumn = originColumn;
        }

        protected override void ReleaseResources(bool disposing)
        {
            if (!disposing)
                return;
            _template?.Release();
            _template = null;
        }

        public override string ToString() => $"NccModel {Width}x{Height}";
    }
}
