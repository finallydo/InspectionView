using System;
using System.Collections.Generic;
using InspectionView.Core.Memory;

namespace InspectionView.Core.Types
{
    /// <summary>Shape 모델 피라미드 한 단: 기준점 기준 에지 점 위치(그 단의 픽셀 인덱스 좌표)와 단위 그라디언트 방향.</summary>
    public sealed class ShapeModelLevel
    {
        public ShapeModelLevel(float[] x, float[] y, float[] dx, float[] dy)
        {
            X = x;
            Y = y;
            Dx = dx;
            Dy = dy;
            double r = 1;
            for (int i = 0; i < x.Length; i++)
                r = Math.Max(r, Math.Sqrt((double)x[i] * x[i] + (double)y[i] * y[i]));
            Radius = r;
        }

        public float[] X { get; }
        public float[] Y { get; }
        public float[] Dx { get; }
        public float[] Dy { get; }
        public int Count => X.Length;
        /// <summary>기준점에서 가장 먼 에지 점까지의 거리 (각도/스케일 탐색 간격 계산용).</summary>
        public double Radius { get; }
    }

    /// <summary>
    /// 에지 기반 Shape 모델 (Halcon create_shape_model 에 해당). 템플릿의 에지 점과 그라디언트 방향을 피라미드 단마다 가진다.
    /// 기준점(원점)은 템플릿 영역 바운딩 박스의 중심이다 (NCC 모델과 같은 규약).
    /// </summary>
    public sealed class ShapeModel : IconicObject
    {
        public ShapeModel(IReadOnlyList<ShapeModelLevel> levels, IReadOnlyList<PointD[]> contours, int width, int height,
            double angleStart, double angleExtent, double angleStep, double scaleMin, double scaleMax, double scaleStep,
            string metric, double minContrast)
        {
            if (levels == null || levels.Count == 0) throw new ArgumentException("A shape model needs at least one level.", nameof(levels));
            Levels = levels;
            Contours = contours;
            Width = width;
            Height = height;
            AngleStart = angleStart;
            AngleExtent = Math.Max(0, angleExtent);
            AngleStep = angleStep;
            ScaleMin = scaleMin;
            ScaleMax = Math.Max(scaleMin, scaleMax);
            ScaleStep = scaleStep;
            Metric = metric;
            MinContrast = minContrast;
        }

        public override string TypeName => "ShapeModel";

        /// <summary>피라미드 단 (0 = 원본 해상도).</summary>
        public IReadOnlyList<ShapeModelLevel> Levels { get; }
        public int NumLevels => Levels.Count;

        /// <summary>표시용 모델 윤곽 (원본 해상도, 기준점 기준 픽셀 인덱스 좌표: x = 열, y = 행).</summary>
        public IReadOnlyList<PointD[]> Contours { get; }

        /// <summary>템플릿 영역 바운딩 박스 크기.</summary>
        public int Width { get; }
        public int Height { get; }

        /// <summary>탐색 각도 범위 (라디안, 반시계 +). 간격은 원본 해상도 기준.</summary>
        public double AngleStart { get; }
        public double AngleExtent { get; }
        public double AngleStep { get; }

        public double ScaleMin { get; }
        public double ScaleMax { get; }
        public double ScaleStep { get; }

        /// <summary>use_polarity / ignore_global_polarity / ignore_local_polarity.</summary>
        public string Metric { get; }

        /// <summary>탐색 이미지에서 에지로 보는 최소 대비 (그레이 값 차이).</summary>
        public double MinContrast { get; }

        public bool HasRotation => AngleExtent > 1e-9;
        public bool HasScale => ScaleMax - ScaleMin > 1e-9;

        protected override void ReleaseResources(bool disposing)
        {
            // 관리 메모리만 가진다
        }

        public override string ToString()
            => $"ShapeModel {Width}x{Height}, {Levels[0].Count} points, {NumLevels} levels"
               + (HasRotation ? $", angle {AngleStart * 180 / Math.PI:F0}..{(AngleStart + AngleExtent) * 180 / Math.PI:F0} deg" : "")
               + (HasScale ? $", scale {ScaleMin:F2}..{ScaleMax:F2}" : "");
    }
}
