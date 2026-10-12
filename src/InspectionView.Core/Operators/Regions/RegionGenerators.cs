using System;
using InspectionView.Core.Backends;
using InspectionView.Core.Errors;
using InspectionView.Core.Operators.Common;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;
using OpenCvSharp;

namespace InspectionView.Core.Operators.Regions
{
    [Operator("GenCircle", Category = "Regions", Description = "Creates a filled circle region (sub-pixel center, pixel-center sampling).")]
    public sealed class GenCircle : OperatorBase
    {
        [Input] public double Row { get; set; }
        [Input] public double Column { get; set; }
        [Input(Min = 0.5)] public double Radius { get; set; }
        [Output] public Region? Region { get; private set; }

        protected override void Validate()
        {
            if (Radius <= 0) throw VisionException.InvalidArgument(nameof(Radius), "must be > 0");
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx) => Region = RegionOps.Circle(Row, Column, Radius);
    }

    [Operator("GenRectangle2", Category = "Regions", Description = "Creates a rotated rectangle: center (Row, Column), orientation Phi (radians, counter-clockwise), half lengths Length1/Length2.")]
    public sealed class GenRectangle2 : OperatorBase
    {
        [Input] public double Row { get; set; }
        [Input] public double Column { get; set; }
        [Input(Description = "Orientation in radians (counter-clockwise).", Default = 0.0)] public double Phi { get; set; }
        [Input(Description = "Half length along the main axis.", Min = 0.5)] public double Length1 { get; set; }
        [Input(Description = "Half length across the main axis.", Min = 0.5)] public double Length2 { get; set; }
        [Output] public Region? Region { get; private set; }

        protected override void Validate()
        {
            if (Length1 <= 0 || Length2 <= 0) throw VisionException.InvalidArgument("Length1/Length2", "must be > 0");
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            // 이미지 좌표(y 아래)에서 반시계 방향 Phi 는 OpenCV 의 시계 방향 각도의 음수
            var rect = new RotatedRect(new Point2f((float)(Column + 0.5), (float)(Row + 0.5)),
                new Size2f((float)(2 * Length1), (float)(2 * Length2)), (float)(-Phi * 180.0 / Math.PI));
            Region = RegionOps.FillPolygon(rect.Points());
        }
    }

    public enum ShapeTransType
    {
        /// <summary>볼록 껍질</summary>
        Convex,
        /// <summary>축 정렬 바운딩 박스</summary>
        Rectangle1,
        /// <summary>최소 면적 회전 사각형</summary>
        Rectangle2,
        /// <summary>최소 외접원</summary>
        OuterCircle,
    }

    [Operator("ShapeTrans", Category = "Regions", Description = "Transforms the shape of each region: convex hull, bounding box, minimum rotated rectangle or enclosing circle.")]
    public sealed class ShapeTrans : OperatorBase
    {
        [Input] public ObjectArray<Region>? Regions { get; set; }
        [Input(Default = ShapeTransType.Convex)] public ShapeTransType Type { get; set; }
        [Output] public ObjectArray<Region>? RegionsTrans { get; private set; }

        protected override void Validate() => Require(Regions, nameof(Regions));

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var result = new ObjectArray<Region>();
            foreach (var r in Regions!)
                result.AddOwned(Transform(r));
            RegionsTrans = result;
        }

        private Region Transform(Region r)
        {
            if (r.IsEmpty)
                return Types.Region.Empty;

            if (Type == ShapeTransType.Rectangle1)
                return Types.Region.FromRectangle(r.BoundingBox);

            var points = RegionOps.OuterContourPoints(r);
            if (points.Length == 0)
                return Types.Region.Empty;

            switch (Type)
            {
                case ShapeTransType.Convex:
                {
                    var hull = Cv2.ConvexHull(points);
                    var poly = new Point2f[hull.Length];
                    for (int i = 0; i < hull.Length; i++) poly[i] = new Point2f(hull[i].X, hull[i].Y);
                    using (var filled = RegionOps.FillPolygon(poly))
                        return filled.Union(r);   // 경계 픽셀 손실 방지
                }
                case ShapeTransType.Rectangle2:
                {
                    var rect = Cv2.MinAreaRect(points);
                    using (var filled = RegionOps.FillPolygon(rect.Points()))
                        return filled.Union(r);
                }
                case ShapeTransType.OuterCircle:
                {
                    Cv2.MinEnclosingCircle(points, out Point2f center, out float radius);
                    using (var circle = RegionOps.Circle(center.Y, center.X, radius + 0.5))
                        return circle.Union(r);
                }
                default:
                    throw VisionException.InvalidArgument(nameof(Type), $"unknown type {Type}");
            }
        }
    }
}
