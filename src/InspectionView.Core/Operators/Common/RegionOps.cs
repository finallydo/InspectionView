using System;
using System.Collections.Generic;
using InspectionView.Core.Errors;
using InspectionView.Core.Types;
using OpenCvSharp;

namespace InspectionView.Core.Operators.Common
{
    /// <summary>Region ↔ 마스크 변환을 거치는 OpenCV 기반 영역 연산 헬퍼.</summary>
    internal static class RegionOps
    {
        /// <summary>
        /// 바운딩 박스를 <paramref name="pad"/> 만큼 넓힌 로컬 마스크를 만든다. 반환 Mat 은 호출자가 Dispose.
        /// origin 은 마스크 (0,0) 의 이미지 좌표.
        /// </summary>
        public static Mat LocalMask(Region region, int pad, out PointI origin)
        {
            var bb = region.BoundingBox;
            origin = new PointI(bb.X - pad, bb.Y - pad);
            int w = Math.Max(1, bb.Width + 2 * pad), h = Math.Max(1, bb.Height + 2 * pad);
            var mask = new Mat(h, w, MatType.CV_8UC1, Scalar.All(0));
            if (region.IsEmpty)
                return mask;

            using (var local = region.Translate(-origin.X, -origin.Y))
            {
                var cached = local.ToMask(w, h);
                cached.CopyTo(mask);
            }
            return mask;
        }

        /// <summary>로컬 마스크 → Region (origin 만큼 되돌림).</summary>
        public static Region FromLocalMask(Mat mask, PointI origin)
            => Region.FromMask(mask, origin.X, origin.Y);

        /// <summary>모폴로지 연산 (구조 요소: 사각형 w x h 또는 반지름 r 원).</summary>
        public static Region Morphology(Region region, MorphTypes op, MorphShapes shape, int width, int height)
        {
            if (region.IsEmpty)
                return Region.Empty;

            int pad = Math.Max(width, height) + 1;
            using (var kernel = Cv2.GetStructuringElement(shape, new Size(width, height)))
            using (var src = LocalMask(region, pad, out var origin))
            using (var dst = new Mat())
            {
                Cv2.MorphologyEx(src, dst, op, kernel, null, 1, BorderTypes.Constant, Scalar.All(0));
                return FromLocalMask(dst, origin);
            }
        }

        public static Region FillUp(Region region)
        {
            if (region.IsEmpty)
                return Region.Empty;

            using (var mask = LocalMask(region, 1, out var origin))
            using (var inv = new Mat())
            {
                // 배경+구멍 = 255 → 바깥 배경을 0 으로 채우면 구멍만 남는다
                Cv2.BitwiseNot(mask, inv);
                Cv2.FloodFill(inv, new Point(0, 0), Scalar.All(0));
                Cv2.BitwiseOr(mask, inv, mask);
                return FromLocalMask(mask, origin);
            }
        }

        /// <summary>외곽 윤곽점 (이미지 좌표).</summary>
        public static Point[] OuterContourPoints(Region region)
        {
            if (region.IsEmpty)
                return Array.Empty<Point>();

            using (var mask = LocalMask(region, 1, out var origin))
            {
                Cv2.FindContours(mask, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxNone);
                var all = new List<Point>();
                foreach (var c in contours)
                    foreach (var p in c)
                        all.Add(new Point(p.X + origin.X, p.Y + origin.Y));
                return all.ToArray();
            }
        }

        /// <summary>다각형(이미지 좌표, 실수) 내부를 채운 Region.</summary>
        public static Region FillPolygon(Point2f[] polygon)
        {
            if (polygon.Length == 0)
                return Region.Empty;

            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var p in polygon)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
            var origin = new PointI((int)Math.Floor(minX) - 1, (int)Math.Floor(minY) - 1);
            int w = (int)Math.Ceiling(maxX) - origin.X + 2, h = (int)Math.Ceiling(maxY) - origin.Y + 2;

            using (var mask = new Mat(h, w, MatType.CV_8UC1, Scalar.All(0)))
            {
                var pts = new Point[polygon.Length];
                for (int i = 0; i < pts.Length; i++)
                    pts[i] = new Point((int)Math.Round(polygon[i].X) - origin.X, (int)Math.Round(polygon[i].Y) - origin.Y);
                Cv2.FillPoly(mask, new[] { pts }, Scalar.All(255));
                return FromLocalMask(mask, origin);
            }
        }

        /// <summary>원 Region (중심 실수 좌표, 반지름).</summary>
        public static Region Circle(double row, double column, double radius)
        {
            if (radius <= 0)
                throw VisionException.InvalidArgument("Radius", "must be > 0");

            int r = (int)Math.Ceiling(radius) + 2;
            var origin = new PointI((int)Math.Floor(column) - r, (int)Math.Floor(row) - r);
            int size = 2 * r + 2;
            using (var mask = new Mat(size, size, MatType.CV_8UC1, Scalar.All(0)))
            {
                // 픽셀 중심 기준: (x + 0.5 - column)^2 + (y + 0.5 - row)^2 <= radius^2
                double r2 = radius * radius;
                for (int y = 0; y < size; y++)
                {
                    double dy = y + origin.Y + 0.5 - row;
                    for (int x = 0; x < size; x++)
                    {
                        double dx = x + origin.X + 0.5 - column;
                        if (dx * dx + dy * dy <= r2)
                            mask.Set(y, x, (byte)255);
                    }
                }
                return FromLocalMask(mask, origin);
            }
        }

        /// <summary>배열 또는 단일 Region 을 리스트로.</summary>
        public static IReadOnlyList<Region> AsList(ObjectArray<Region> regions) => regions.ToArray();
    }
}
