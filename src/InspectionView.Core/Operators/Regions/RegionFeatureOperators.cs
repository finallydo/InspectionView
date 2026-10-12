using InspectionView.Core.Backends;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;

namespace InspectionView.Core.Operators.Regions
{
    [Operator("AreaCenter", Category = "Regions", Description = "Area and center of gravity (Row, Column) of each region.")]
    public sealed class AreaCenter : OperatorBase
    {
        [Input] public ObjectArray<Region>? Regions { get; set; }
        [Output] public TupleValue? Area { get; private set; }
        [Output] public TupleValue? Row { get; private set; }
        [Output] public TupleValue? Column { get; private set; }

        protected override void Validate() => Require(Regions, nameof(Regions));

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            int n = Regions!.Count;
            var area = new long[n];
            var row = new double[n];
            var col = new double[n];
            for (int i = 0; i < n; i++)
            {
                var r = Regions[i];
                area[i] = r.Area;
                var c = r.Centroid();
                row[i] = c.Y;
                col[i] = c.X;
            }
            Area = area;
            Row = row;
            Column = col;
        }
    }

    [Operator("SmallestRectangle1", Category = "Regions", Description = "Axis-aligned bounding box of each region (inclusive coordinates).")]
    public sealed class SmallestRectangle1 : OperatorBase
    {
        [Input] public ObjectArray<Region>? Regions { get; set; }
        [Output] public TupleValue? Row1 { get; private set; }
        [Output] public TupleValue? Column1 { get; private set; }
        [Output] public TupleValue? Row2 { get; private set; }
        [Output] public TupleValue? Column2 { get; private set; }

        protected override void Validate() => Require(Regions, nameof(Regions));

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            int n = Regions!.Count;
            var r1 = new long[n]; var c1 = new long[n]; var r2 = new long[n]; var c2 = new long[n];
            for (int i = 0; i < n; i++)
            {
                var bb = Regions[i].BoundingBox;
                r1[i] = bb.Y; c1[i] = bb.X; r2[i] = bb.Bottom - 1; c2[i] = bb.Right - 1;
            }
            Row1 = r1; Column1 = c1; Row2 = r2; Column2 = c2;
        }
    }

    [Operator("RegionFeatures", Category = "Regions", Description = "Computes one shape feature for each region (see SelectShape for the feature names).")]
    public sealed class RegionFeatures : OperatorBase
    {
        [Input] public ObjectArray<Region>? Regions { get; set; }
        [Input(Default = ShapeFeature.Area)] public ShapeFeature Feature { get; set; }
        [Output] public TupleValue? Value { get; private set; }

        protected override void Validate() => Require(Regions, nameof(Regions));

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            int n = Regions!.Count;
            var v = new double[n];
            for (int i = 0; i < n; i++)
                v[i] = Regions[i].Feature(Feature);
            Value = v;
        }
    }
}
