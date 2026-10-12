using System.Collections.Generic;
using InspectionView.Core.Backends;
using InspectionView.Core.Errors;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;

namespace InspectionView.Core.Operators.Regions
{
    [Operator("Union1", Category = "Regions", Description = "Union of all regions in an array.")]
    public sealed class Union1 : OperatorBase
    {
        [Input] public ObjectArray<Region>? Regions { get; set; }
        [Output] public Region? RegionUnion { get; private set; }

        protected override void Validate() => Require(Regions, nameof(Regions));

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var runs = new List<Run>();
            foreach (var r in Regions!)
                runs.AddRange(r.Runs);
            RegionUnion = new Region(runs);
        }
    }

    [Operator("Union2", Category = "Regions", Description = "Union of two regions.")]
    public sealed class Union2 : OperatorBase
    {
        [Input] public Region? Region1 { get; set; }
        [Input] public Region? Region2 { get; set; }
        [Output] public Region? RegionUnion { get; private set; }

        protected override void Validate() { Require(Region1, nameof(Region1)); Require(Region2, nameof(Region2)); }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx) => RegionUnion = Region1!.Union(Region2!);
    }

    [Operator("Intersection", Category = "Regions", Description = "Intersection of two regions.")]
    public sealed class Intersection : OperatorBase
    {
        [Input] public Region? Region1 { get; set; }
        [Input] public Region? Region2 { get; set; }
        [Output] public Region? RegionIntersection { get; private set; }

        protected override void Validate() { Require(Region1, nameof(Region1)); Require(Region2, nameof(Region2)); }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx) => RegionIntersection = Region1!.Intersection(Region2!);
    }

    [Operator("Difference", Category = "Regions", Description = "Region1 minus Region2.")]
    public sealed class Difference : OperatorBase
    {
        [Input] public Region? Region1 { get; set; }
        [Input] public Region? Region2 { get; set; }
        [Output] public Region? RegionDifference { get; private set; }

        protected override void Validate() { Require(Region1, nameof(Region1)); Require(Region2, nameof(Region2)); }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx) => RegionDifference = Region1!.Difference(Region2!);
    }

    [Operator("Complement", Category = "Regions", Description = "Complement of a region inside a Width x Height image plane.")]
    public sealed class Complement : OperatorBase
    {
        [Input] public Region? Region { get; set; }
        [Input(Description = "Plane width.", Min = 1)] public int Width { get; set; }
        [Input(Description = "Plane height.", Min = 1)] public int Height { get; set; }
        [Output] public Region? RegionComplement { get; private set; }

        protected override void Validate()
        {
            Require(Region, nameof(Region));
            if (Width <= 0 || Height <= 0)
                throw VisionException.InvalidArgument("Width/Height", "must be > 0");
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            using (var plane = Types.Region.FromRectangle(new RectI(0, 0, Width, Height)))
                RegionComplement = plane.Difference(Region!);
        }
    }

    [Operator("CountObj", Category = "Regions", Description = "Number of objects in an array.")]
    public sealed class CountObj : OperatorBase
    {
        [Input] public ObjectArray<Region>? Regions { get; set; }
        [Output] public long Number { get; private set; }

        protected override void Validate() => Require(Regions, nameof(Regions));

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx) => Number = Regions!.Count;
    }

    [Operator("SelectObj", Category = "Regions", Description = "Selects one region of an array by 0-based index.")]
    public sealed class SelectObj : OperatorBase
    {
        [Input] public ObjectArray<Region>? Regions { get; set; }
        [Input(Description = "0-based index.", Default = 0, Min = 0)] public int Index { get; set; }
        [Output] public Region? RegionSelected { get; private set; }

        protected override void Validate()
        {
            var arr = Require(Regions, nameof(Regions));
            if (Index < 0 || Index >= arr.Count)
                throw VisionException.OutOfRange(nameof(Index), Index, 0, arr.Count - 1);
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var r = Regions![Index];
            r.AddRef();                 // 출력은 호출자가 소유하는 새 참조
            RegionSelected = r;
        }
    }
}
