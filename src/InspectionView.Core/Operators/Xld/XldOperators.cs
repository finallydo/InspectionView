using System;
using System.Collections.Generic;
using InspectionView.Core.Backends;
using InspectionView.Core.Errors;
using InspectionView.Core.Interop;
using InspectionView.Core.Operators.Common;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;
using OpenCvSharp;
using ErrorCode = InspectionView.Core.Errors.ErrorCode;

namespace InspectionView.Core.Operators.Xld
{
    [Operator("EdgesSubPix", Category = "XLD", Description = "Canny edge detection (first channel) returning edge chains as contours. Alpha is the smoothing sigma (0 = none).")]
    public sealed class EdgesSubPix : OperatorBase
    {
        [Input] public Image? Image { get; set; }
        [Input(Description = "Gaussian smoothing sigma before Canny (0 = none).", Default = 1.0, Min = 0)] public double Alpha { get; set; }
        [Input(Description = "Lower hysteresis threshold.", Default = 20.0, Min = 0)] public double Low { get; set; }
        [Input(Description = "Upper hysteresis threshold.", Default = 40.0, Min = 0)] public double High { get; set; }
        [Input(Description = "Minimum number of points per contour.", Default = 3, Min = 1)] public int MinPoints { get; set; }
        [Output] public ObjectArray<XldContour>? Edges { get; private set; }

        protected override void Validate()
        {
            Require(Image, nameof(Image));
            if (Low > High) throw VisionException.InvalidArgument(nameof(Low), "must be <= High");
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var img = Image!;
            using (var src = MatInterop.AsMat(img))
            using (var gray8 = new Mat())
            using (var smooth = new Mat())
            using (var edges = new Mat())
            {
                Mat single = src;
                Mat? extracted = null;
                if (img.Channels > 1)
                {
                    extracted = new Mat();
                    Cv2.ExtractChannel(src, extracted, 0);
                    single = extracted;
                }
                try
                {
                    if (img.PixelType == PixelType.UInt8)
                        single.CopyTo(gray8);
                    else
                    {
                        // Canny 는 8bit 입력: 전체 범위를 0..255 로 정규화
                        Cv2.Normalize(single, gray8, 0, 255, NormTypes.MinMax, MatType.CV_8U);
                    }
                }
                finally
                {
                    extracted?.Dispose();
                }

                if (Alpha > 0)
                    Cv2.GaussianBlur(gray8, smooth, new Size(0, 0), Alpha, Alpha, BorderTypes.Reflect101);
                else
                    gray8.CopyTo(smooth);

                Cv2.Canny(smooth, edges, Low, High, 3, true);

                if (img.Domain != null)
                {
                    var mask = img.Domain.ToMask(img.Width, img.Height);
                    Cv2.BitwiseAnd(edges, mask, edges);
                }

                var result = new ObjectArray<XldContour>();
                foreach (var c in XldOps.TraceChains(edges, MinPoints))
                    result.AddOwned(c);
                Edges = result;
            }
        }
    }

    public enum XldFeature
    {
        Length,
        Count,
        Area,
        Row,
        Column,
    }

    [Operator("SelectContoursXld", Category = "XLD", Description = "Selects contours whose feature (Length, Count, Area, Row, Column) lies in [Min, Max].")]
    public sealed class SelectContoursXld : OperatorBase
    {
        [Input] public ObjectArray<XldContour>? Contours { get; set; }
        [Input(Default = XldFeature.Length)] public XldFeature Feature { get; set; }
        [Input(Default = 0.0)] public double Min { get; set; }
        [Input(Default = double.MaxValue)] public double Max { get; set; }
        [Output] public ObjectArray<XldContour>? Selected { get; private set; }

        protected override void Validate() => Require(Contours, nameof(Contours));

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var result = new ObjectArray<XldContour>();
            foreach (var c in Contours!)
            {
                double v = Value(c, Feature);
                if (v >= Min && v <= Max)
                    result.Add(c);
            }
            Selected = result;
        }

        internal static double Value(XldContour c, XldFeature f)
        {
            switch (f)
            {
                case XldFeature.Length: return c.Length();
                case XldFeature.Count: return c.Count;
                default:
                    XldOps.AreaCenter(c, out double area, out double row, out double col);
                    return f == XldFeature.Area ? area : f == XldFeature.Row ? row : col;
            }
        }
    }

    [Operator("LengthXld", Category = "XLD", Description = "Length of each contour (polyline length).")]
    public sealed class LengthXld : OperatorBase
    {
        [Input] public ObjectArray<XldContour>? Contours { get; set; }
        [Output] public TupleValue? Length { get; private set; }

        protected override void Validate() => Require(Contours, nameof(Contours));

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var v = new double[Contours!.Count];
            for (int i = 0; i < v.Length; i++) v[i] = Contours[i].Length();
            Length = v;
        }
    }

    [Operator("AreaCenterXld", Category = "XLD", Description = "Polygon area and centroid of each contour (treated as closed).")]
    public sealed class AreaCenterXld : OperatorBase
    {
        [Input] public ObjectArray<XldContour>? Contours { get; set; }
        [Output] public TupleValue? Area { get; private set; }
        [Output] public TupleValue? Row { get; private set; }
        [Output] public TupleValue? Column { get; private set; }

        protected override void Validate() => Require(Contours, nameof(Contours));

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            int n = Contours!.Count;
            var a = new double[n]; var r = new double[n]; var c = new double[n];
            for (int i = 0; i < n; i++)
                XldOps.AreaCenter(Contours[i], out a[i], out r[i], out c[i]);
            Area = a; Row = r; Column = c;
        }
    }

    [Operator("FitLineContourXld", Category = "XLD", Description = "Least-squares line fit of a contour: projected end points, normal (Nr, Nc) and Dist with Nr*Row + Nc*Column + Dist = 0.")]
    public sealed class FitLineContourXld : OperatorBase
    {
        [Input] public XldContour? Contour { get; set; }
        [Output] public double RowBegin { get; private set; }
        [Output] public double ColBegin { get; private set; }
        [Output] public double RowEnd { get; private set; }
        [Output] public double ColEnd { get; private set; }
        [Output] public double Nr { get; private set; }
        [Output] public double Nc { get; private set; }
        [Output] public double Dist { get; private set; }

        protected override void Validate() => Require(Contour, nameof(Contour));

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            XldOps.FitLine(Contour!, out double rb, out double cb, out double re, out double ce, out double nr, out double nc, out double d);
            RowBegin = rb; ColBegin = cb; RowEnd = re; ColEnd = ce; Nr = nr; Nc = nc; Dist = d;
        }
    }

    [Operator("FitCircleContourXld", Category = "XLD", Description = "Least-squares circle fit of a contour.")]
    public sealed class FitCircleContourXld : OperatorBase
    {
        [Input] public XldContour? Contour { get; set; }
        [Output] public double Row { get; private set; }
        [Output] public double Column { get; private set; }
        [Output] public double Radius { get; private set; }

        protected override void Validate() => Require(Contour, nameof(Contour));

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            XldOps.FitCircle(Contour!, out double r, out double c, out double rad);
            Row = r; Column = c; Radius = rad;
        }
    }

    [Operator("SmoothContoursXld", Category = "XLD", Description = "Moving-average smoothing of contour points.")]
    public sealed class SmoothContoursXld : OperatorBase
    {
        [Input] public ObjectArray<XldContour>? Contours { get; set; }
        [Input(Default = 5, Min = 2)] public int NumPoints { get; set; }
        [Output] public ObjectArray<XldContour>? Smoothed { get; private set; }

        protected override void Validate() => Require(Contours, nameof(Contours));

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var result = new ObjectArray<XldContour>();
            foreach (var c in Contours!)
                result.AddOwned(XldOps.Smooth(c, NumPoints));
            Smoothed = result;
        }
    }

    [Operator("GenContourRegionXld", Category = "XLD", Description = "Outer boundary of each region as a closed contour.")]
    public sealed class GenContourRegionXld : OperatorBase
    {
        [Input] public ObjectArray<Region>? Regions { get; set; }
        [Output] public ObjectArray<XldContour>? Contours { get; private set; }

        protected override void Validate() => Require(Regions, nameof(Regions));

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var result = new ObjectArray<XldContour>();
            foreach (var r in Regions!)
            {
                if (r.IsEmpty) continue;
                using (var mask = RegionOps.LocalMask(r, 1, out var origin))
                {
                    Cv2.FindContours(mask, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxNone);
                    foreach (var c in contours)
                    {
                        var pts = new List<PointD>(c.Length);
                        foreach (var p in c)
                            pts.Add(new PointD(p.X + origin.X, p.Y + origin.Y));
                        result.AddOwned(new XldContour(pts, closed: true));
                    }
                }
            }
            Contours = result;
        }
    }

    public enum ContourRegionMode
    {
        /// <summary>닫힌 다각형 내부</summary>
        Filled,
        /// <summary>선 자체 (1픽셀)</summary>
        Margin,
    }

    [Operator("GenRegionContourXld", Category = "XLD", Description = "Converts each contour to a region: Filled (polygon interior) or Margin (the line pixels).")]
    public sealed class GenRegionContourXld : OperatorBase
    {
        [Input] public ObjectArray<XldContour>? Contours { get; set; }
        [Input(Default = ContourRegionMode.Filled)] public ContourRegionMode Mode { get; set; }
        [Output] public ObjectArray<Region>? Regions { get; private set; }

        protected override void Validate() => Require(Contours, nameof(Contours));

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var result = new ObjectArray<Region>();
            foreach (var c in Contours!)
            {
                if (c.Count == 0)
                {
                    result.AddOwned(Region.Empty);
                    continue;
                }
                var poly = new Point2f[c.Count];
                for (int i = 0; i < c.Count; i++) poly[i] = new Point2f((float)c.X[i], (float)c.Y[i]);

                if (Mode == ContourRegionMode.Filled)
                {
                    result.AddOwned(RegionOps.FillPolygon(poly));
                }
                else
                {
                    var pts = new List<Point>(c.Count);
                    foreach (var p in poly) pts.Add(new Point((int)Math.Round(p.X), (int)Math.Round(p.Y)));
                    result.AddOwned(LineRegion(pts, c.IsClosed));
                }
            }
            Regions = result;
        }

        private static Region LineRegion(List<Point> pts, bool closed)
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (var p in pts)
            {
                if (p.X < minX) minX = p.X; if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X; if (p.Y > maxY) maxY = p.Y;
            }
            var origin = new PointI(minX - 1, minY - 1);
            using (var mask = new Mat(maxY - minY + 3, maxX - minX + 3, MatType.CV_8UC1, Scalar.All(0)))
            {
                var local = new Point[pts.Count];
                for (int i = 0; i < pts.Count; i++) local[i] = new Point(pts[i].X - origin.X, pts[i].Y - origin.Y);
                Cv2.Polylines(mask, new[] { local }, closed, Scalar.All(255), 1, LineTypes.Link8);
                return Region.FromMask(mask, origin.X, origin.Y);
            }
        }
    }
}
