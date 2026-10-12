using System;
using System.Collections.Generic;
using InspectionView.Core.Types;
using OpenCvSharp;

namespace InspectionView.Core.Operators.Common
{
    /// <summary>XLD 윤곽 관련 헬퍼: 에지 마스크 → 체인, 다각형 면적/무게중심, 직선/원 피팅.</summary>
    internal static class XldOps
    {
        private static readonly int[] Dx8 = { 1, 0, -1, 0, 1, -1, -1, 1 };
        private static readonly int[] Dy8 = { 0, 1, 0, -1, 1, 1, -1, -1 };

        /// <summary>
        /// 8-연결 에지 마스크(0/255)를 폴리라인 체인으로 나눈다. 끝점에서 시작하는 열린 체인을 먼저 뽑고,
        /// 남은 픽셀은 닫힌 루프로 처리한다. 교차점은 먼저 도달한 체인이 가져간다.
        /// </summary>
        public static List<XldContour> TraceChains(Mat mask, int minPoints)
        {
            int w = mask.Cols, h = mask.Rows;
            var edge = new byte[w * h];
            long stride = mask.Step();
            for (int y = 0; y < h; y++)
                System.Runtime.InteropServices.Marshal.Copy(new IntPtr(mask.Data.ToInt64() + stride * y), edge, y * w, w);

            var visited = new bool[w * h];
            var result = new List<XldContour>();

            // 1. 끝점(이웃 1개)에서 시작하는 열린 체인
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (edge[y * w + x] == 0 || visited[y * w + x] || CountNeighbors(edge, w, h, x, y) != 1)
                        continue;
                    var pts = Trace(edge, visited, w, h, x, y);
                    if (pts.Count >= minPoints)
                        result.Add(new XldContour(pts, closed: false));
                }

            // 2. 남은 픽셀: 루프 (또는 교차점에서 잘린 조각)
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (edge[y * w + x] == 0 || visited[y * w + x])
                        continue;
                    var pts = Trace(edge, visited, w, h, x, y);
                    if (pts.Count < minPoints)
                        continue;
                    var first = pts[0];
                    var last = pts[pts.Count - 1];
                    bool closed = pts.Count > 2 && Math.Abs(first.X - last.X) <= 1 && Math.Abs(first.Y - last.Y) <= 1;
                    result.Add(new XldContour(pts, closed));
                }

            return result;
        }

        private static bool IsEdge(byte[] edge, int w, int h, int x, int y)
            => x >= 0 && y >= 0 && x < w && y < h && edge[y * w + x] != 0;

        private static int CountNeighbors(byte[] edge, int w, int h, int x, int y)
        {
            int n = 0;
            for (int k = 0; k < 8; k++)
                if (IsEdge(edge, w, h, x + Dx8[k], y + Dy8[k])) n++;
            return n;
        }

        private static List<PointD> Trace(byte[] edge, bool[] visited, int w, int h, int sx, int sy)
        {
            var pts = new List<PointD>();
            int x = sx, y = sy;
            int lastDir = -1;
            while (true)
            {
                visited[y * w + x] = true;
                pts.Add(new PointD(x, y));

                int next = -1;
                // 같은 방향 → 4-이웃 → 대각 순으로 선호
                if (lastDir >= 0)
                {
                    int nx = x + Dx8[lastDir], ny = y + Dy8[lastDir];
                    if (IsEdge(edge, w, h, nx, ny) && !visited[ny * w + nx])
                        next = lastDir;
                }
                for (int k = 0; k < 8 && next < 0; k++)
                {
                    int nx = x + Dx8[k], ny = y + Dy8[k];
                    if (IsEdge(edge, w, h, nx, ny) && !visited[ny * w + nx])
                        next = k;
                }
                if (next < 0)
                    break;
                x += Dx8[next];
                y += Dy8[next];
                lastDir = next;
            }
            return pts;
        }

        /// <summary>다각형 면적(절대값)과 무게중심 (닫힌 것으로 간주).</summary>
        public static void AreaCenter(XldContour c, out double area, out double row, out double column)
        {
            int n = c.Count;
            if (n < 3)
            {
                area = 0;
                row = n > 0 ? Mean(c.Y) : double.NaN;
                column = n > 0 ? Mean(c.X) : double.NaN;
                return;
            }

            double a = 0, cx = 0, cy = 0;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                double cross = c.X[i] * c.Y[j] - c.X[j] * c.Y[i];
                a += cross;
                cx += (c.X[i] + c.X[j]) * cross;
                cy += (c.Y[i] + c.Y[j]) * cross;
            }
            a *= 0.5;
            if (Math.Abs(a) < 1e-12)
            {
                area = 0;
                row = Mean(c.Y);
                column = Mean(c.X);
                return;
            }
            area = Math.Abs(a);
            column = cx / (6 * a);
            row = cy / (6 * a);
        }

        private static double Mean(IReadOnlyList<double> v)
        {
            double s = 0;
            for (int i = 0; i < v.Count; i++) s += v[i];
            return v.Count > 0 ? s / v.Count : double.NaN;
        }

        /// <summary>최소제곱 직선 피팅. 결과: 시작/끝점(투영), 법선 (Nr, Nc), Dist (Nr*row + Nc*col + Dist = 0).</summary>
        public static void FitLine(XldContour c, out double rowBegin, out double colBegin, out double rowEnd, out double colEnd,
            out double nr, out double nc, out double dist)
        {
            int n = c.Count;
            if (n < 2)
                throw new Errors.VisionException(Errors.ErrorCode.InvalidArgument, "FitLine needs at least 2 points.");

            var pts = new Point2f[n];
            for (int i = 0; i < n; i++) pts[i] = new Point2f((float)c.X[i], (float)c.Y[i]);
            var line = Cv2.FitLine(pts, DistanceTypes.L2, 0, 0.01, 0.01);
            double vx = line.Vx, vy = line.Vy, x0 = line.X1, y0 = line.Y1;

            double tMin = double.MaxValue, tMax = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                double t = (c.X[i] - x0) * vx + (c.Y[i] - y0) * vy;
                if (t < tMin) tMin = t;
                if (t > tMax) tMax = t;
            }
            colBegin = x0 + tMin * vx; rowBegin = y0 + tMin * vy;
            colEnd = x0 + tMax * vx; rowEnd = y0 + tMax * vy;

            // 방향 (dr, dc) = (vy, vx) → 법선 (nr, nc) = (vx, -vy)
            nr = vx; nc = -vy;
            dist = -(nr * y0 + nc * x0);
        }

        /// <summary>대수적(Kasa) 최소제곱 원 피팅.</summary>
        public static void FitCircle(XldContour c, out double row, out double column, out double radius)
        {
            int n = c.Count;
            if (n < 3)
                throw new Errors.VisionException(Errors.ErrorCode.InvalidArgument, "FitCircle needs at least 3 points.");

            // x² + y² + a x + b y + c = 0 → 정규방정식 3x3
            double sxx = 0, sxy = 0, syy = 0, sx = 0, sy = 0, sxz = 0, syz = 0, sz = 0;
            for (int i = 0; i < n; i++)
            {
                double x = c.X[i], y = c.Y[i], z = x * x + y * y;
                sxx += x * x; sxy += x * y; syy += y * y; sx += x; sy += y;
                sxz += x * z; syz += y * z; sz += z;
            }
            var m = new double[3, 4]
            {
                { sxx, sxy, sx, -sxz },
                { sxy, syy, sy, -syz },
                { sx, sy, n, -sz },
            };
            Solve3(m, out double a, out double b, out double cc);
            column = -a / 2;
            row = -b / 2;
            double r2 = column * column + row * row - cc;
            radius = r2 > 0 ? Math.Sqrt(r2) : 0;
        }

        private static void Solve3(double[,] m, out double x, out double y, out double z)
        {
            for (int col = 0; col < 3; col++)
            {
                int pivot = col;
                for (int r = col + 1; r < 3; r++)
                    if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col])) pivot = r;
                if (Math.Abs(m[pivot, col]) < 1e-12)
                    throw new Errors.VisionException(Errors.ErrorCode.InvalidArgument, "Points are degenerate for a circle fit.");
                if (pivot != col)
                    for (int k = 0; k < 4; k++) { var t = m[col, k]; m[col, k] = m[pivot, k]; m[pivot, k] = t; }
                for (int r = 0; r < 3; r++)
                {
                    if (r == col) continue;
                    double f = m[r, col] / m[col, col];
                    for (int k = col; k < 4; k++) m[r, k] -= f * m[col, k];
                }
            }
            x = m[0, 3] / m[0, 0];
            y = m[1, 3] / m[1, 1];
            z = m[2, 3] / m[2, 2];
        }

        /// <summary>이동 평균 스무딩 (양 끝은 가능한 범위만).</summary>
        public static XldContour Smooth(XldContour c, int numPoints)
        {
            int n = c.Count, half = Math.Max(1, numPoints / 2);
            var x = new double[n];
            var y = new double[n];
            for (int i = 0; i < n; i++)
            {
                double sx = 0, sy = 0;
                int cnt = 0;
                for (int k = -half; k <= half; k++)
                {
                    int j = c.IsClosed ? ((i + k) % n + n) % n : i + k;
                    if (j < 0 || j >= n) continue;
                    sx += c.X[j]; sy += c.Y[j]; cnt++;
                }
                x[i] = sx / cnt;
                y[i] = sy / cnt;
            }
            return new XldContour(x, y, c.IsClosed);
        }
    }
}
