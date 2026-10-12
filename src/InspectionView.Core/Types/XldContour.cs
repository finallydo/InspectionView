using System;
using System.Collections.Generic;
using InspectionView.Core.Errors;
using InspectionView.Core.Memory;

namespace InspectionView.Core.Types
{
    /// <summary>
    /// 서브픽셀 윤곽선 (Halcon XLD contour 에 대응). 점 좌표와 점별 속성(예: 'angle', 'response')을 가진다.
    /// </summary>
    public sealed class XldContour : IconicObject
    {
        private readonly double[] _x;
        private readonly double[] _y;
        private readonly Dictionary<string, double[]> _attributes = new Dictionary<string, double[]>(StringComparer.Ordinal);

        public override string TypeName => "XLD";

        public int Count => _x.Length;
        public bool IsClosed { get; }
        public IReadOnlyList<double> X => _x;
        public IReadOnlyList<double> Y => _y;

        /// <summary>점별 속성 이름 목록.</summary>
        public IEnumerable<string> AttributeNames => _attributes.Keys;

        public XldContour(double[] x, double[] y, bool closed = false)
        {
            if (x == null) throw VisionException.NullArgument(nameof(x));
            if (y == null) throw VisionException.NullArgument(nameof(y));
            if (x.Length != y.Length)
                throw new VisionException(ErrorCode.SizeMismatch, "X and Y must have the same length.");

            _x = x;
            _y = y;
            IsClosed = closed;
        }

        public XldContour(IReadOnlyList<PointD> points, bool closed = false)
        {
            if (points == null) throw VisionException.NullArgument(nameof(points));
            _x = new double[points.Count];
            _y = new double[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                _x[i] = points[i].X;
                _y[i] = points[i].Y;
            }
            IsClosed = closed;
        }

        public PointD this[int index] => new PointD(_x[index], _y[index]);

        /// <summary>점별 속성 설정 (길이는 점 수와 같아야 함).</summary>
        public void SetAttribute(string name, double[] values)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(name)) throw VisionException.InvalidArgument(nameof(name), "must not be empty");
            if (values == null) throw VisionException.NullArgument(nameof(values));
            if (values.Length != Count)
                throw new VisionException(ErrorCode.SizeMismatch, $"Attribute '{name}' length {values.Length} != point count {Count}.");
            _attributes[name] = values;
        }

        public bool TryGetAttribute(string name, out double[] values)
        {
            ThrowIfDisposed();
            return _attributes.TryGetValue(name, out values!);
        }

        /// <summary>윤곽선 길이 (폴리라인 기준).</summary>
        public double Length()
        {
            double len = 0;
            for (int i = 1; i < Count; i++)
            {
                double dx = _x[i] - _x[i - 1], dy = _y[i] - _y[i - 1];
                len += Math.Sqrt(dx * dx + dy * dy);
            }
            if (IsClosed && Count > 1)
            {
                double dx = _x[0] - _x[Count - 1], dy = _y[0] - _y[Count - 1];
                len += Math.Sqrt(dx * dx + dy * dy);
            }
            return len;
        }

        public RectD BoundingBox()
        {
            if (Count == 0)
                return default;

            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            for (int i = 0; i < Count; i++)
            {
                if (_x[i] < minX) minX = _x[i];
                if (_x[i] > maxX) maxX = _x[i];
                if (_y[i] < minY) minY = _y[i];
                if (_y[i] > maxY) maxY = _y[i];
            }
            return new RectD(minX, minY, maxX - minX, maxY - minY);
        }

        protected override void ReleaseResources(bool disposing)
        {
            if (disposing)
                _attributes.Clear();
        }

        public override string ToString() => $"XLD points={Count}" + (IsClosed ? " (closed)" : "");
    }
}
