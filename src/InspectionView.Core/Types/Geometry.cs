using System;

namespace InspectionView.Core.Types
{
    /// <summary>정수 좌표 점 (x = column, y = row).</summary>
    public readonly struct PointI : IEquatable<PointI>
    {
        public int X { get; }
        public int Y { get; }

        public PointI(int x, int y)
        {
            X = x;
            Y = y;
        }

        public bool Equals(PointI other) => X == other.X && Y == other.Y;
        public override bool Equals(object? obj) => obj is PointI p && Equals(p);
        public override int GetHashCode() => unchecked(X * 397 ^ Y);
        public override string ToString() => $"({X}, {Y})";

        public static bool operator ==(PointI a, PointI b) => a.Equals(b);
        public static bool operator !=(PointI a, PointI b) => !a.Equals(b);
    }

    /// <summary>실수 좌표 점 (서브픽셀).</summary>
    public readonly struct PointD : IEquatable<PointD>
    {
        public double X { get; }
        public double Y { get; }

        public PointD(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double DistanceTo(PointD other)
        {
            double dx = X - other.X, dy = Y - other.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public bool Equals(PointD other) => X.Equals(other.X) && Y.Equals(other.Y);
        public override bool Equals(object? obj) => obj is PointD p && Equals(p);
        public override int GetHashCode() => unchecked(X.GetHashCode() * 397 ^ Y.GetHashCode());
        public override string ToString() => $"({X:F3}, {Y:F3})";

        public static bool operator ==(PointD a, PointD b) => a.Equals(b);
        public static bool operator !=(PointD a, PointD b) => !a.Equals(b);
    }

    /// <summary>
    /// 정수 사각형. <see cref="Right"/>, <see cref="Bottom"/> 은 배타(exclusive) 경계이다.
    /// 즉 유효 픽셀은 [X, Right), [Y, Bottom).
    /// </summary>
    public readonly struct RectI : IEquatable<RectI>
    {
        public int X { get; }
        public int Y { get; }
        public int Width { get; }
        public int Height { get; }

        public int Left => X;
        public int Top => Y;
        public int Right => X + Width;
        public int Bottom => Y + Height;
        public bool IsEmpty => Width <= 0 || Height <= 0;
        public long Area => IsEmpty ? 0 : (long)Width * Height;

        public static RectI Empty => default;

        public RectI(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        /// <summary>포함(inclusive) 좌표로부터 생성: (x1,y1) ~ (x2,y2) 모두 포함.</summary>
        public static RectI FromInclusive(int x1, int y1, int x2, int y2)
        {
            if (x2 < x1 || y2 < y1)
                return Empty;
            return new RectI(x1, y1, x2 - x1 + 1, y2 - y1 + 1);
        }

        public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;

        public RectI Intersect(RectI other)
        {
            int l = Math.Max(X, other.X);
            int t = Math.Max(Y, other.Y);
            int r = Math.Min(Right, other.Right);
            int b = Math.Min(Bottom, other.Bottom);
            if (r <= l || b <= t)
                return Empty;
            return new RectI(l, t, r - l, b - t);
        }

        public RectI Union(RectI other)
        {
            if (IsEmpty) return other;
            if (other.IsEmpty) return this;
            int l = Math.Min(X, other.X);
            int t = Math.Min(Y, other.Y);
            int r = Math.Max(Right, other.Right);
            int b = Math.Max(Bottom, other.Bottom);
            return new RectI(l, t, r - l, b - t);
        }

        public RectI Offset(int dx, int dy) => new RectI(X + dx, Y + dy, Width, Height);

        public RectI Inflate(int dx, int dy) => new RectI(X - dx, Y - dy, Width + 2 * dx, Height + 2 * dy);

        public bool Equals(RectI other)
            => X == other.X && Y == other.Y && Width == other.Width && Height == other.Height;

        public override bool Equals(object? obj) => obj is RectI r && Equals(r);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = X;
                h = h * 397 ^ Y;
                h = h * 397 ^ Width;
                h = h * 397 ^ Height;
                return h;
            }
        }

        public override string ToString() => $"[{X}, {Y}, {Width} x {Height}]";

        public static bool operator ==(RectI a, RectI b) => a.Equals(b);
        public static bool operator !=(RectI a, RectI b) => !a.Equals(b);
    }

    /// <summary>실수 사각형 (ROI 그리기, 서브픽셀 결과용).</summary>
    public readonly struct RectD
    {
        public double X { get; }
        public double Y { get; }
        public double Width { get; }
        public double Height { get; }

        public RectD(double x, double y, double width, double height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public double Right => X + Width;
        public double Bottom => Y + Height;
        public bool IsEmpty => Width <= 0 || Height <= 0;

        public override string ToString() => $"[{X:F2}, {Y:F2}, {Width:F2} x {Height:F2}]";
    }
}
