using System;
using InspectionView.Core.Errors;

namespace InspectionView.Core.Types
{
    /// <summary>SelectShape / 형상 분석에서 쓰는 Region 특징 (Halcon select_shape 의 Features 에 대응).</summary>
    public enum ShapeFeature
    {
        /// <summary>픽셀 수.</summary>
        Area,
        /// <summary>바운딩 박스 폭.</summary>
        Width,
        /// <summary>바운딩 박스 높이.</summary>
        Height,
        /// <summary>무게중심 행.</summary>
        Row,
        /// <summary>무게중심 열.</summary>
        Column,
        /// <summary>바운딩 박스 위쪽 행 (포함).</summary>
        Row1,
        /// <summary>바운딩 박스 왼쪽 열 (포함).</summary>
        Column1,
        /// <summary>바운딩 박스 아래쪽 행 (포함).</summary>
        Row2,
        /// <summary>바운딩 박스 오른쪽 열 (포함).</summary>
        Column2,
        /// <summary>원형도: Area / (pi * maxDist^2), 0~1.</summary>
        Circularity,
        /// <summary>바운딩 박스 대비 채움 비율: Area / (Width * Height), 0~1.</summary>
        Rectangularity,
    }

    /// <summary>Region 특징 계산 (확장 메서드).</summary>
    public static class RegionFeatures
    {
        /// <summary>무게중심 (x = column, y = row). 빈 영역이면 (NaN, NaN).</summary>
        public static PointD Centroid(this Region region)
        {
            if (region == null) throw VisionException.NullArgument(nameof(region));
            if (region.IsEmpty)
                return new PointD(double.NaN, double.NaN);

            double sumX = 0, sumY = 0;
            long count = 0;
            foreach (var r in region.Runs)
            {
                long len = r.Length;
                sumX += (r.ColBegin + r.ColEnd) * 0.5 * len;
                sumY += (double)r.Row * len;
                count += len;
            }
            return new PointD(sumX / count, sumY / count);
        }

        /// <summary>
        /// 원형도 (Halcon circularity): Area / (pi * max^2), max 는 무게중심에서 가장 먼 영역 픽셀까지의 거리.
        /// 가장 먼 픽셀은 항상 run 의 양 끝이므로 run 끝점만 검사한다.
        /// </summary>
        public static double Circularity(this Region region)
        {
            if (region == null) throw VisionException.NullArgument(nameof(region));
            if (region.IsEmpty)
                return 0;

            var c = region.Centroid();
            double maxSq = 0;
            foreach (var r in region.Runs)
            {
                double dy = r.Row - c.Y;
                double d1 = r.ColBegin - c.X, d2 = r.ColEnd - c.X;
                double s1 = d1 * d1 + dy * dy, s2 = d2 * d2 + dy * dy;
                if (s1 > maxSq) maxSq = s1;
                if (s2 > maxSq) maxSq = s2;
            }

            if (maxSq <= 0)
                return 1.0;

            double v = region.Area / (Math.PI * maxSq);
            return v > 1.0 ? 1.0 : v;
        }

        /// <summary>바운딩 박스 대비 채움 비율.</summary>
        public static double Rectangularity(this Region region)
        {
            if (region == null) throw VisionException.NullArgument(nameof(region));
            var bb = region.BoundingBox;
            return bb.IsEmpty ? 0 : (double)region.Area / bb.Area;
        }

        /// <summary>특징 값 하나를 계산한다.</summary>
        public static double Feature(this Region region, ShapeFeature feature)
        {
            if (region == null) throw VisionException.NullArgument(nameof(region));

            switch (feature)
            {
                case ShapeFeature.Area: return region.Area;
                case ShapeFeature.Width: return region.BoundingBox.Width;
                case ShapeFeature.Height: return region.BoundingBox.Height;
                case ShapeFeature.Row: return region.Centroid().Y;
                case ShapeFeature.Column: return region.Centroid().X;
                case ShapeFeature.Row1: return region.IsEmpty ? double.NaN : region.BoundingBox.Y;
                case ShapeFeature.Column1: return region.IsEmpty ? double.NaN : region.BoundingBox.X;
                case ShapeFeature.Row2: return region.IsEmpty ? double.NaN : region.BoundingBox.Bottom - 1;
                case ShapeFeature.Column2: return region.IsEmpty ? double.NaN : region.BoundingBox.Right - 1;
                case ShapeFeature.Circularity: return region.Circularity();
                case ShapeFeature.Rectangularity: return region.Rectangularity();
                default:
                    throw VisionException.InvalidArgument(nameof(feature), $"unknown feature {feature}");
            }
        }
    }
}
