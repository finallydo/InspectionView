using System.Threading.Tasks;
using InspectionView.Core.Backends;
using InspectionView.Core.Errors;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;

namespace InspectionView.Core.Operators.Regions
{
    /// <summary>
    /// 형상 특징이 [Min, Max] 인 Region 만 고른다 (Halcon select_shape). 입력 순서를 유지한다.
    /// 스크립트: <c>Selected = SelectShape(Regions, 'Area', 100, 5000)</c>
    /// </summary>
    [Operator("SelectShape", Category = "Regions", Description = "Selects regions whose shape feature lies in [Min, Max].")]
    public sealed class SelectShape : OperatorBase
    {
        [Input(Description = "Input regions.")]
        public ObjectArray<Region>? Regions { get; set; }

        [Input(Description = "Shape feature to test.", Default = ShapeFeature.Area)]
        public ShapeFeature Feature { get; set; }

        [Input(Description = "Lower bound (inclusive).", Default = 0.0)]
        public double Min { get; set; }

        [Input(Description = "Upper bound (inclusive).", Default = double.MaxValue)]
        public double Max { get; set; }

        [Output(Description = "Selected regions.")]
        public ObjectArray<Region>? Selected { get; private set; }

        protected override void Validate()
        {
            Require(Regions, nameof(Regions));
            if (Min > Max)
                throw VisionException.InvalidArgument(nameof(Min), $"must be <= Max ({Min} > {Max})");
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var input = Regions!;
            var values = new double[input.Count];
            for (int i = 0; i < input.Count; i++)
                values[i] = input[i].Feature(Feature);
            Selected = Collect(input, values);
        }

        [Backend(BackendKind.Parallel)]
        private void RunParallel(OperatorContext ctx)
        {
            var input = Regions!;
            var values = new double[input.Count];
            var feature = Feature;
            Parallel.For(0, input.Count, ctx.CreateParallelOptions(), i => values[i] = input[i].Feature(feature));
            Selected = Collect(input, values);
        }

        private ObjectArray<Region> Collect(ObjectArray<Region> input, double[] values)
        {
            var result = new ObjectArray<Region>();
            for (int i = 0; i < input.Count; i++)
            {
                double v = values[i];
                if (v >= Min && v <= Max)
                    result.Add(input[i]);
            }
            return result;
        }
    }
}
