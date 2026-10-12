using InspectionView.Core.Backends;
using InspectionView.Core.Errors;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;

namespace InspectionView.Core.Operators.Regions
{
    /// <summary>
    /// 축 정렬 사각형 Region 을 만든다 (Halcon gen_rectangle1). 좌표는 포함(inclusive).
    /// 스크립트: <c>Roi = GenRectangle1(100, 200, 299, 399)</c>
    /// </summary>
    [Operator("GenRectangle1", Category = "Regions", Description = "Creates an axis-aligned rectangle region from inclusive corner coordinates.")]
    public sealed class GenRectangle1 : OperatorBase
    {
        [Input(Description = "Top row (inclusive).")]
        public int Row1 { get; set; }

        [Input(Description = "Left column (inclusive).")]
        public int Column1 { get; set; }

        [Input(Description = "Bottom row (inclusive).")]
        public int Row2 { get; set; }

        [Input(Description = "Right column (inclusive).")]
        public int Column2 { get; set; }

        [Output(Description = "Rectangle region.")]
        public Region? Region { get; private set; }

        protected override void Validate()
        {
            if (Row2 < Row1) throw VisionException.InvalidArgument(nameof(Row2), $"must be >= Row1 ({Row2} < {Row1})");
            if (Column2 < Column1) throw VisionException.InvalidArgument(nameof(Column2), $"must be >= Column1 ({Column2} < {Column1})");
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            Region = Types.Region.FromRectangle1(Row1, Column1, Row2, Column2);
        }
    }
}
