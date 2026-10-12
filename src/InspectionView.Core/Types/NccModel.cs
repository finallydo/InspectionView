using System;
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

        /// <summary>탐색 각도 범위 (라디안, 반시계 +). Extent 0 이면 회전 없음.</summary>
        public double AngleStart { get; }
        public double AngleExtent { get; }
        public double AngleStep { get; }

        /// <summary>탐색 스케일 범위. Min == Max 면 스케일 없음.</summary>
        public double ScaleMin { get; }
        public double ScaleMax { get; }
        public double ScaleStep { get; }

        public bool HasRotation => AngleExtent > 1e-9;
        public bool HasScale => ScaleMax - ScaleMin > 1e-9;

        public NccModel(Image template, double originRow, double originColumn)
            : this(template, originRow, originColumn, 0, 0, 0, 1, 1, 0)
        {
        }

        public NccModel(Image template, double originRow, double originColumn,
            double angleStart, double angleExtent, double angleStep, double scaleMin, double scaleMax, double scaleStep)
        {
            template.AddRef();
            _template = template;
            OriginRow = originRow;
            OriginColumn = originColumn;
            AngleStart = angleStart;
            AngleExtent = Math.Max(0, angleExtent);
            AngleStep = angleStep;
            ScaleMin = scaleMin;
            ScaleMax = Math.Max(scaleMin, scaleMax);
            ScaleStep = scaleStep;
        }

        /// <summary>탐색할 각도 목록.</summary>
        public double[] Angles()
        {
            if (!HasRotation) return new[] { AngleStart };
            double step = AngleStep > 1e-9 ? AngleStep : Math.Max(0.5 * Math.PI / 180, Math.Atan2(2.0, Math.Max(Width, Height)));
            int n = (int)Math.Round(AngleExtent / step) + 1;
            var list = new double[n];
            for (int i = 0; i < n; i++) list[i] = AngleStart + Math.Min(AngleExtent, i * step);
            return list;
        }

        /// <summary>탐색할 스케일 목록.</summary>
        public double[] Scales()
        {
            if (!HasScale) return new[] { ScaleMin };
            double step = ScaleStep > 1e-9 ? ScaleStep : 0.05;
            int n = (int)Math.Round((ScaleMax - ScaleMin) / step) + 1;
            var list = new double[n];
            for (int i = 0; i < n; i++) list[i] = Math.Min(ScaleMax, ScaleMin + i * step);
            return list;
        }

        protected override void ReleaseResources(bool disposing)
        {
            if (!disposing)
                return;
            _template?.Release();
            _template = null;
        }

        public override string ToString() => $"NccModel {Width}x{Height}" + (HasRotation ? $" angle {AngleStart * 180 / Math.PI:F0}..{(AngleStart + AngleExtent) * 180 / Math.PI:F0} deg" : "") + (HasScale ? $" scale {ScaleMin:F2}..{ScaleMax:F2}" : "");
    }
}
