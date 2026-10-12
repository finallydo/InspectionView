using System;
using System.Collections.Generic;
using InspectionView.Core.Backends;
using InspectionView.Core.Errors;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;

namespace InspectionView.Core.Operators.Measure
{
    public enum EdgeTransition
    {
        All,
        /// <summary>어두움 → 밝음 (측정 방향 기준)</summary>
        Positive,
        /// <summary>밝음 → 어두움</summary>
        Negative,
    }

    public enum EdgeSelect
    {
        All,
        First,
        Last,
    }

    /// <summary>
    /// 1D 에지 측정 (Halcon measure_pos). 중심 (Row, Column), 방향 Phi(라디안, 반시계), 반길이 Length1(측정 방향),
    /// 반폭 Length2(수직 방향 평균)의 사각형 안에서 프로파일을 뽑아 1차 미분 극값을 에지로 찾는다 (서브픽셀).
    /// </summary>
    [Operator("MeasurePos", Category = "Measure", Description = "1D edge measurement along a rotated rectangle: sub-pixel edge positions, amplitudes and distances.")]
    public sealed class MeasurePos : OperatorBase
    {
        [Input] public Image? Image { get; set; }
        [Input(Description = "Center row.")] public double Row { get; set; }
        [Input(Description = "Center column.")] public double Column { get; set; }
        [Input(Description = "Orientation in radians (0 = measure along columns).", Default = 0.0)] public double Phi { get; set; }
        [Input(Description = "Half length along the measurement direction.", Min = 1)] public double Length1 { get; set; }
        [Input(Description = "Half width (profile is averaged across it).", Default = 2.0, Min = 0)] public double Length2 { get; set; }
        [Input(Description = "Gaussian smoothing sigma of the profile.", Default = 1.0, Min = 0.1)] public double Sigma { get; set; }
        [Input(Description = "Minimum edge amplitude (gray value change).", Default = 30.0, Min = 0)] public double Threshold { get; set; }
        [Input(Default = EdgeTransition.All)] public EdgeTransition Transition { get; set; }
        [Input(Default = EdgeSelect.All)] public EdgeSelect Select { get; set; }

        [Output(Description = "Edge rows (sub-pixel).")] public TupleValue? RowEdge { get; private set; }
        [Output(Description = "Edge columns (sub-pixel).")] public TupleValue? ColumnEdge { get; private set; }
        [Output(Description = "Signed edge amplitudes.")] public TupleValue? Amplitude { get; private set; }
        [Output(Description = "Distances between consecutive edges.")] public TupleValue? Distance { get; private set; }

        protected override void Validate()
        {
            Require(Image, nameof(Image));
            if (Length1 < 1) throw VisionException.InvalidArgument(nameof(Length1), "must be >= 1");
            if (Length2 < 0) throw VisionException.InvalidArgument(nameof(Length2), "must be >= 0");
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var img = Image!;
            double dx = Math.Cos(Phi), dy = -Math.Sin(Phi);     // 측정 방향 (이미지 좌표, y 아래)
            double px = -dy, py = dx;                            // 수직 방향

            int n = (int)Math.Ceiling(2 * Length1) + 1;
            int m = (int)Math.Ceiling(2 * Length2) + 1;
            double step = n > 1 ? 2 * Length1 / (n - 1) : 0;
            double wstep = m > 1 ? 2 * Length2 / (m - 1) : 0;

            // 프로파일: 수직 방향 평균 (이미지 밖은 제외)
            var profile = new double[n];
            for (int i = 0; i < n; i++)
            {
                double t = -Length1 + i * step;
                double sum = 0;
                int cnt = 0;
                for (int j = 0; j < m; j++)
                {
                    double s = -Length2 + j * wstep;
                    double x = Column + 0.5 + t * dx + s * px;
                    double y = Row + 0.5 + t * dy + s * py;
                    if (TrySample(img, x, y, out double v))
                    {
                        sum += v;
                        cnt++;
                    }
                }
                profile[i] = cnt > 0 ? sum / cnt : double.NaN;
            }

            // NaN(이미지 밖)은 인접값으로 채움
            for (int i = 0; i < n; i++)
                if (double.IsNaN(profile[i]))
                    profile[i] = NearestValid(profile, i);

            var smooth = GaussianSmooth(profile, Sigma);

            // 1차 미분 (픽셀당 변화량)
            var deriv = new double[n];
            for (int i = 1; i < n - 1; i++)
                deriv[i] = (smooth[i + 1] - smooth[i - 1]) / (2 * step);

            var edges = new List<(double t, double amp)>();
            for (int i = 1; i < n - 1; i++)
            {
                double d = deriv[i];
                if (Math.Abs(d) < Threshold) continue;
                if (Transition == EdgeTransition.Positive && d < 0) continue;
                if (Transition == EdgeTransition.Negative && d > 0) continue;

                double a = Math.Abs(d);
                if (a < Math.Abs(deriv[i - 1]) || a <= Math.Abs(deriv[i + 1]))
                    continue;   // 극값만

                // 포물선 보간으로 서브픽셀 위치
                double l = Math.Abs(deriv[i - 1]), r = Math.Abs(deriv[i + 1]);
                double denom = l - 2 * a + r;
                double offset = Math.Abs(denom) > 1e-12 ? 0.5 * (l - r) / denom : 0;
                double t = -Length1 + (i + offset) * step;
                edges.Add((t, d));
            }

            if (Select == EdgeSelect.First && edges.Count > 1) edges = new List<(double, double)> { edges[0] };
            if (Select == EdgeSelect.Last && edges.Count > 1) edges = new List<(double, double)> { edges[edges.Count - 1] };

            var rows = new double[edges.Count];
            var cols = new double[edges.Count];
            var amps = new double[edges.Count];
            for (int k = 0; k < edges.Count; k++)
            {
                rows[k] = Row + edges[k].t * dy;
                cols[k] = Column + edges[k].t * dx;
                amps[k] = edges[k].amp;
            }
            var dist = new double[Math.Max(0, edges.Count - 1)];
            for (int k = 0; k + 1 < edges.Count; k++)
                dist[k] = edges[k + 1].t - edges[k].t;

            RowEdge = rows;
            ColumnEdge = cols;
            Amplitude = amps;
            Distance = dist;
        }

        private static double NearestValid(double[] p, int i)
        {
            for (int d = 1; d < p.Length; d++)
            {
                if (i - d >= 0 && !double.IsNaN(p[i - d])) return p[i - d];
                if (i + d < p.Length && !double.IsNaN(p[i + d])) return p[i + d];
            }
            return 0;
        }

        /// <summary>양선형 보간 샘플링 (픽셀 중심 좌표계: 픽셀 (x,y) 의 중심은 (x+0.5, y+0.5)).</summary>
        private static bool TrySample(Image img, double x, double y, out double value)
        {
            double fx = x - 0.5, fy = y - 0.5;
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            double ax = fx - x0, ay = fy - y0;
            int x1 = x0 + 1, y1 = y0 + 1;
            if (x0 < 0 || y0 < 0 || x1 >= img.Width || y1 >= img.Height)
            {
                // 가장자리: 가장 가까운 픽셀
                int cx = (int)Math.Round(fx), cy = (int)Math.Round(fy);
                if (cx < 0 || cy < 0 || cx >= img.Width || cy >= img.Height)
                {
                    value = 0;
                    return false;
                }
                value = img.GetPixel(cx, cy);
                return true;
            }
            double v00 = img.GetPixel(x0, y0), v10 = img.GetPixel(x1, y0);
            double v01 = img.GetPixel(x0, y1), v11 = img.GetPixel(x1, y1);
            value = (1 - ay) * ((1 - ax) * v00 + ax * v10) + ay * ((1 - ax) * v01 + ax * v11);
            return true;
        }

        private static double[] GaussianSmooth(double[] p, double sigma)
        {
            int half = Math.Max(1, (int)Math.Ceiling(3 * sigma));
            var k = new double[2 * half + 1];
            double sum = 0;
            for (int i = -half; i <= half; i++)
            {
                k[i + half] = Math.Exp(-(i * i) / (2 * sigma * sigma));
                sum += k[i + half];
            }
            for (int i = 0; i < k.Length; i++) k[i] /= sum;

            var r = new double[p.Length];
            for (int i = 0; i < p.Length; i++)
            {
                double acc = 0, wsum = 0;
                for (int j = -half; j <= half; j++)
                {
                    int idx = i + j;
                    if (idx < 0 || idx >= p.Length) continue;
                    acc += p[idx] * k[j + half];
                    wsum += k[j + half];
                }
                r[i] = wsum > 0 ? acc / wsum : p[i];
            }
            return r;
        }
    }
}
