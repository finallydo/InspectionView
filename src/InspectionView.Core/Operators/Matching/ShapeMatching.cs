using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using InspectionView.Core.Backends;
using InspectionView.Core.Errors;
using InspectionView.Core.Interop;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;
using OpenCvSharp;
using ErrorCode = InspectionView.Core.Errors.ErrorCode;

namespace InspectionView.Core.Operators.Matching
{
    [Operator("CreateShapeModel", Category = "Matching", Description = "Creates an edge-based shape model from the template (inside the region, first channel): edge points with gradient directions on an image pyramid, searchable over a rotation (radians) and scale range. The model origin is the center of the region's bounding box.")]
    public sealed class CreateShapeModel : OperatorBase
    {
        [Input(Description = "Template image.")] public Image? Template { get; set; }
        [Input(Optional = true, Description = "Region of the template (default: whole image / domain). Only edges inside it are used.")] public Region? Region { get; set; }
        [Input(Description = "Number of pyramid levels (0 = automatic).", Default = 0, Min = 0, Max = 8)] public int NumLevels { get; set; }
        [Input(Description = "Start of the rotation range in radians (counter-clockwise positive).", Default = 0.0)] public double AngleStart { get; set; }
        [Input(Description = "Extent of the rotation range in radians (0 = no rotation).", Default = 0.0, Min = 0)] public double AngleExtent { get; set; }
        [Input(Description = "Rotation step in radians at full resolution (0 = automatic from the model size).", Default = 0.0, Min = 0)] public double AngleStep { get; set; }
        [Input(Description = "Minimum scale.", Default = 1.0, Min = 0.1)] public double ScaleMin { get; set; }
        [Input(Description = "Maximum scale.", Default = 1.0, Min = 0.1)] public double ScaleMax { get; set; }
        [Input(Description = "Scale step at full resolution (0 = automatic from the model size).", Default = 0.0, Min = 0)] public double ScaleStep { get; set; }
        [Input(Description = "use_polarity: edges must have the same dark/bright direction; ignore_global_polarity: all edges may be inverted together; ignore_local_polarity: each edge may be inverted.",
            Default = "use_polarity", Choices = new[] { "use_polarity", "ignore_global_polarity", "ignore_local_polarity" })] public string Metric { get; set; } = "use_polarity";
        [Input(Description = "Minimum gray-value step for a template edge.", Default = 30.0, Min = 1)] public double Contrast { get; set; } = 30;
        [Input(Description = "Minimum gray-value step for an edge in the search image (noise level).", Default = 10.0, Min = 0)] public double MinContrast { get; set; } = 10;
        [Output] public ShapeModel? Model { get; private set; }

        protected override void Validate()
        {
            Require(Template, nameof(Template));
            if (ScaleMax < ScaleMin) throw VisionException.InvalidArgument(nameof(ScaleMax), "must be >= ScaleMin");
            ShapeOps.ParseMetric(Metric);
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var img = Template!;
            var box = Region != null ? Region.BoundingBox.Intersect(img.Bounds) : img.DomainBounds;
            if (box.IsEmpty || box.Width < 3 || box.Height < 3)
                throw new VisionException(ErrorCode.EmptyObject, "Template region is empty or too small.");

            using (var view = MatInterop.AsMat(img, box))
            using (var level0 = ShapeOps.ToFloat(view))
            using (var mask0 = Region != null ? Region.ToMask(box).Clone() : null)
            {
                // 기준점: 박스 중심 (원본 해상도 픽셀 인덱스 좌표)
                double ox = (box.Width - 1) / 2.0, oy = (box.Height - 1) / 2.0;
                var levels = new List<ShapeModelLevel>();
                var contours = new List<PointD[]>();
                int maxLevels = NumLevels > 0 ? NumLevels : 6;

                Mat cur = level0;
                Mat? curMask = mask0;
                var owned = new List<Mat>();
                try
                {
                    for (int l = 0; l < maxLevels; l++)
                    {
                        if (l > 0)
                        {
                            if (Math.Min(cur.Cols, cur.Rows) < 16) break;
                            var next = new Mat();
                            Cv2.PyrDown(cur, next);
                            owned.Add(next);
                            cur = next;
                            if (curMask != null)
                            {
                                var m = new Mat();
                                Cv2.Resize(curMask, m, cur.Size(), 0, 0, InterpolationFlags.Nearest);
                                owned.Add(m);
                                curMask = m;
                            }
                        }

                        double contrast = Math.Max(MinContrast, Contrast * Math.Pow(ShapeOps.ContrastFalloff, l));
                        var pts = ShapeOps.EdgePoints(cur, curMask, contrast, out var edgeMap);
                        using (edgeMap)
                        {
                            if (l == 0)
                            {
                                if (pts.Count < 8)
                                    throw new VisionException(ErrorCode.EmptyObject, $"The template has too few edge points ({pts.Count}); lower Contrast or check the region.");
                                contours.AddRange(ShapeOps.Contours(edgeMap, ox, oy));
                            }
                            else if (pts.Count < 12)
                            {
                                break;   // 이 단은 점이 너무 적어 쓸모없다
                            }
                        }

                        double scale = 1.0 / (1 << l);
                        levels.Add(ShapeOps.MakeLevel(pts, ox * scale, oy * scale, ShapeOps.MaxPointsPerLevel));
                    }
                }
                finally
                {
                    foreach (var m in owned) m.Dispose();
                }

                double r0 = levels[0].Radius;
                double angleStep = AngleExtent > 1e-9 ? (AngleStep > 1e-9 ? AngleStep : Math.Max(0.1 * Math.PI / 180, Math.Atan(1.0 / r0))) : 0;
                double scaleStep = ScaleMax - ScaleMin > 1e-9 ? (ScaleStep > 1e-9 ? ScaleStep : Math.Max(0.005, 1.0 / r0)) : 0;
                Model = new ShapeModel(levels, contours, box.Width, box.Height, AngleStart, AngleExtent, angleStep,
                    ScaleMin, ScaleMax, scaleStep, Metric, MinContrast);
            }
        }
    }

    [Operator("FindShapeModel", Category = "Matching", Description = "Finds a shape model in the image (edge directions, robust to lighting changes, clutter and partial occlusion): positions of the model origin, angles (radians), scales and scores (0..1, fraction of matching edge directions).")]
    public sealed class FindShapeModel : OperatorBase
    {
        [Input] public Image? Image { get; set; }
        [Input] public ShapeModel? Model { get; set; }
        [Input(Description = "Minimum score (0..1). Parts of the model may be hidden: 0.7 finds instances with about 30 % missing edges.", Default = 0.5, Min = 0, Max = 1)] public double MinScore { get; set; } = 0.5;
        [Input(Description = "Maximum number of matches (0 = all).", Default = 1, Min = 0)] public int NumMatches { get; set; } = 1;
        [Input(Description = "Maximum allowed overlap of the matches' bounding boxes (0..1).", Default = 0.5, Min = 0, Max = 1)] public double MaxOverlap { get; set; } = 0.5;
        [Input(Description = "Search speed vs. safety: 0 = never miss a match, 1 = fastest (may miss weak matches).", Default = 0.9, Min = 0, Max = 1)] public double Greediness { get; set; } = 0.9;

        [Output] public TupleValue? Row { get; private set; }
        [Output] public TupleValue? Column { get; private set; }
        [Output(Description = "Rotation of each match in radians (counter-clockwise positive).")] public TupleValue? Angle { get; private set; }
        [Output(Description = "Scale of each match.")] public TupleValue? Scale { get; private set; }
        [Output] public TupleValue? Score { get; private set; }

        protected override void Validate()
        {
            Require(Image, nameof(Image));
            Require(Model, nameof(Model));
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx) => Run(ctx, null);

        [Backend(BackendKind.Parallel)]
        private void RunParallel(OperatorContext ctx) => Run(ctx, ctx.CreateParallelOptions());

        private void Run(OperatorContext ctx, ParallelOptions? options)
        {
            var img = Image!;
            var box = img.DomainBounds;
            List<ShapeMatch> matches;
            using (var view = MatInterop.AsMat(img, box))
            using (var level0 = ShapeOps.ToFloat(view))
            {
                matches = ShapeOps.Find(level0, Model!, MinScore, NumMatches, MaxOverlap, Greediness, options, ctx);
            }

            Row = matches.Select(m => box.Y + m.Y + 0.5).ToArray();
            Column = matches.Select(m => box.X + m.X + 0.5).ToArray();
            Angle = matches.Select(m => m.Angle).ToArray();
            Scale = matches.Select(m => m.Scale).ToArray();
            Score = matches.Select(m => m.Score).ToArray();
        }
    }

    [Operator("GetShapeMatchContours", Category = "Matching", Description = "Model contours placed at found matches (row, column, angle, scale from FindShapeModel) for display.")]
    public sealed class GetShapeMatchContours : OperatorBase
    {
        [Input] public ShapeModel? Model { get; set; }
        [Input] public TupleValue? Row { get; set; }
        [Input] public TupleValue? Column { get; set; }
        [Input(Optional = true, Description = "Angles in radians (default 0).")] public TupleValue? Angle { get; set; }
        [Input(Optional = true, Description = "Scales (default 1).")] public TupleValue? Scale { get; set; }
        [Output] public ObjectArray<XldContour>? Contours { get; private set; }

        protected override void Validate()
        {
            Require(Model, nameof(Model));
            var r = Require(Row, nameof(Row));
            var c = Require(Column, nameof(Column));
            if (r.Count != c.Count) throw new VisionException(ErrorCode.SizeMismatch, "Row and Column must have the same length.");
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var rows = Row!.ToDoubleArray();
            var cols = Column!.ToDoubleArray();
            var angles = Angle?.ToDoubleArray() ?? Array.Empty<double>();
            var scales = Scale?.ToDoubleArray() ?? Array.Empty<double>();
            var result = new ObjectArray<XldContour>();
            try
            {
                for (int i = 0; i < rows.Length; i++)
                {
                    double a = i < angles.Length ? angles[i] : angles.Length == 1 ? angles[0] : 0;
                    double s = i < scales.Length ? scales[i] : scales.Length == 1 ? scales[0] : 1;
                    double cos = Math.Cos(a) * s, sin = Math.Sin(a) * s;
                    // 매칭 좌표(픽셀 경계 기준) → XLD 픽셀 인덱스 좌표
                    double cx = cols[i] - 0.5, cy = rows[i] - 0.5;
                    foreach (var contour in Model!.Contours)
                    {
                        var xs = new double[contour.Length];
                        var ys = new double[contour.Length];
                        for (int k = 0; k < contour.Length; k++)
                        {
                            xs[k] = cx + cos * contour[k].X + sin * contour[k].Y;
                            ys[k] = cy - sin * contour[k].X + cos * contour[k].Y;
                        }
                        result.AddOwned(new XldContour(xs, ys, closed: true));
                    }
                }
                Contours = result;
            }
            catch
            {
                result.Release();
                throw;
            }
        }
    }

    internal readonly struct ShapeMatch
    {
        public ShapeMatch(double x, double y, double angle, double scale, double score)
        {
            X = x;
            Y = y;
            Angle = angle;
            Scale = scale;
            Score = score;
        }

        /// <summary>원본 해상도 픽셀 인덱스 좌표 (탐색 영역 기준).</summary>
        public double X { get; }
        public double Y { get; }
        public double Angle { get; }
        public double Scale { get; }
        public double Score { get; }
    }

    /// <summary>
    /// Shape 매칭 공통 (Steger 방식): 점수 = 모델 에지 점에서 모델 방향 벡터와 이미지 그라디언트 단위 벡터의 내적 평균.
    /// 이미지의 대비와 무관하고(정규화된 방향만 사용), 가려진 점은 0 으로 더해져 점수가 그만큼 줄어든다.
    /// 피라미드 최상단에서 전 위치 × 각도 × 스케일을 탐색하고, 후보를 아래 단으로 내려가며 좁힌 뒤 원본에서 서브픽셀 보간한다.
    /// </summary>
    internal static class ShapeOps
    {
        public const int MaxPointsPerLevel = 1500;
        /// <summary>피라미드 단마다 대비 기준을 줄인다 (축소하면 에지가 완만해진다).</summary>
        public const double ContrastFalloff = 0.75;

        public enum MetricKind { UsePolarity, IgnoreGlobal, IgnoreLocal }

        public static MetricKind ParseMetric(string? metric)
        {
            switch ((metric ?? "use_polarity").Trim().ToLowerInvariant())
            {
                case "use_polarity": return MetricKind.UsePolarity;
                case "ignore_global_polarity": return MetricKind.IgnoreGlobal;
                case "ignore_local_polarity": return MetricKind.IgnoreLocal;
                default: throw VisionException.InvalidArgument("Metric", "must be use_polarity, ignore_global_polarity or ignore_local_polarity");
            }
        }

        /// <summary>첫 채널을 float 로.</summary>
        public static Mat ToFloat(Mat src)
        {
            var f = new Mat();
            if (src.Channels() > 1)
            {
                using (var single = new Mat())
                {
                    Cv2.ExtractChannel(src, single, 0);
                    single.ConvertTo(f, MatType.CV_32F);
                }
            }
            else
            {
                src.ConvertTo(f, MatType.CV_32F);
            }
            return f;
        }

        /// <summary>Sobel 그라디언트 (gx = d/d열, gy = d/d행). 계단 높이 h 의 에지에서 크기가 약 h 가 되도록 1/4 배.</summary>
        private static void Gradients(Mat f, out float[] gx, out float[] gy)
        {
            using (var mx = new Mat())
            using (var my = new Mat())
            {
                Cv2.Sobel(f, mx, MatType.CV_32F, 1, 0, 3, 0.25, 0, BorderTypes.Replicate);
                Cv2.Sobel(f, my, MatType.CV_32F, 0, 1, 3, 0.25, 0, BorderTypes.Replicate);
                mx.GetArray(out gx);
                my.GetArray(out gy);
            }
        }

        /// <summary>
        /// 템플릿 에지 점: 크기 ≥ contrast, 그라디언트 방향으로 비최대 억제(1픽셀 두께), 마스크 안. 반환 좌표는 이 단의 픽셀 인덱스.
        /// <paramref name="edgeMap"/> 은 선택된 점의 8U 맵 (윤곽 추출용, 호출자가 Dispose).
        /// </summary>
        public static List<(int x, int y, float dx, float dy)> EdgePoints(Mat f, Mat? mask, double contrast, out Mat edgeMap)
        {
            int w = f.Cols, h = f.Rows;
            Gradients(f, out var gx, out var gy);
            var mag = new float[w * h];
            for (int i = 0; i < mag.Length; i++)
                mag[i] = (float)Math.Sqrt(gx[i] * gx[i] + gy[i] * gy[i]);

            byte[]? m = null;
            if (mask != null)
            {
                using (var cont = mask.IsContinuous() ? null : mask.Clone())
                    (cont ?? mask).GetArray(out m);
            }

            var list = new List<(int, int, float, float)>();
            var map = new byte[w * h];
            const double tan22 = 0.41421356, tan67 = 2.41421356;
            for (int y = 1; y < h - 1; y++)
            {
                for (int x = 1; x < w - 1; x++)
                {
                    int i = y * w + x;
                    float v = mag[i];
                    if (v < contrast) continue;
                    if (m != null && m[i] == 0) continue;
                    float ax = Math.Abs(gx[i]), ay = Math.Abs(gy[i]);
                    float n1, n2;
                    if (ay <= ax * tan22) { n1 = mag[i - 1]; n2 = mag[i + 1]; }
                    else if (ay >= ax * tan67) { n1 = mag[i - w]; n2 = mag[i + w]; }
                    else if (gx[i] * gy[i] > 0) { n1 = mag[i - w - 1]; n2 = mag[i + w + 1]; }
                    else { n1 = mag[i - w + 1]; n2 = mag[i + w - 1]; }
                    if (v < n1 || v <= n2) continue;
                    list.Add((x, y, gx[i] / v, gy[i] / v));
                    map[i] = 255;
                }
            }
            edgeMap = new Mat(h, w, MatType.CV_8UC1);
            edgeMap.SetArray(map);
            return list;
        }

        /// <summary>점들을 기준점 (ox, oy) 기준 좌표로, 너무 많으면 고르게 솎아 낸다.</summary>
        public static ShapeModelLevel MakeLevel(List<(int x, int y, float dx, float dy)> pts, double ox, double oy, int maxPoints)
        {
            int step = Math.Max(1, (int)Math.Ceiling(pts.Count / (double)maxPoints));
            int n = (pts.Count + step - 1) / step;
            var x = new float[n];
            var y = new float[n];
            var dx = new float[n];
            var dy = new float[n];
            for (int k = 0, i = 0; i < pts.Count && k < n; i += step, k++)
            {
                x[k] = (float)(pts[i].x - ox);
                y[k] = (float)(pts[i].y - oy);
                dx[k] = pts[i].dx;
                dy[k] = pts[i].dy;
            }
            return new ShapeModelLevel(x, y, dx, dy);
        }

        /// <summary>표시용 윤곽: 에지 맵의 연결 성분 경계 (기준점 기준 좌표).</summary>
        public static List<PointD[]> Contours(Mat edgeMap, double ox, double oy)
        {
            var list = new List<PointD[]>();
            Cv2.FindContours(edgeMap, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxNone);
            foreach (var c in contours)
            {
                if (c.Length < 4) continue;
                list.Add(c.Select(p => new PointD(p.X - ox, p.Y - oy)).ToArray());
            }
            return list;
        }

        // ------------------------------------------------------------------ 탐색

        /// <summary>탐색 이미지 한 단: 그라디언트 단위 벡터 (크기가 MinContrast 미만이면 0).</summary>
        private sealed class Field
        {
            public int W, H;
            public float[] Ux = Array.Empty<float>(), Uy = Array.Empty<float>();
            /// <summary>그라디언트 크기 (원본 해상도에서만, 최소제곱 서브픽셀 보정용).</summary>
            public float[]? Mag;
        }

        private static Field MakeField(Mat f, double minContrast, bool keepMagnitude = false)
        {
            Gradients(f, out var gx, out var gy);
            float[]? mag = null;
            if (keepMagnitude)
            {
                mag = new float[gx.Length];
                for (int i = 0; i < mag.Length; i++)
                    mag[i] = (float)Math.Sqrt(gx[i] * gx[i] + gy[i] * gy[i]);
            }
            float thr2 = (float)(minContrast * minContrast);
            for (int i = 0; i < gx.Length; i++)
            {
                float m2 = gx[i] * gx[i] + gy[i] * gy[i];
                if (m2 < thr2 || m2 <= 0)
                {
                    gx[i] = 0;
                    gy[i] = 0;
                }
                else
                {
                    float inv = 1f / (float)Math.Sqrt(m2);
                    gx[i] *= inv;
                    gy[i] *= inv;
                }
            }
            return new Field { W = f.Cols, H = f.Rows, Ux = gx, Uy = gy, Mag = mag };
        }

        /// <summary>모델 한 단을 각도/스케일로 변환한 점 (정수 오프셋)과 방향.</summary>
        private sealed class Variant
        {
            public int[] Ox = Array.Empty<int>(), Oy = Array.Empty<int>();
            public float[] Dx = Array.Empty<float>(), Dy = Array.Empty<float>();
            public double Angle, Scale;
        }

        private static Variant Transform(ShapeModelLevel lvl, double angle, double scale)
        {
            int n = lvl.Count;
            var v = new Variant { Ox = new int[n], Oy = new int[n], Dx = new float[n], Dy = new float[n], Angle = angle, Scale = scale };
            double c = Math.Cos(angle), s = Math.Sin(angle);
            for (int i = 0; i < n; i++)
            {
                double x = lvl.X[i], y = lvl.Y[i];
                // 반시계(화면 기준) 회전: 행 좌표가 아래로 증가하므로 y' = -x sin + y cos
                v.Ox[i] = (int)Math.Round(scale * (c * x + s * y));
                v.Oy[i] = (int)Math.Round(scale * (-s * x + c * y));
                v.Dx[i] = (float)(c * lvl.Dx[i] + s * lvl.Dy[i]);
                v.Dy[i] = (float)(-s * lvl.Dx[i] + c * lvl.Dy[i]);
            }
            return v;
        }

        /// <summary>
        /// 위치 (cx, cy) 의 점수. <paramref name="greedy"/> 가 0 이상이면 Steger 의 조기 종료: 남은 점이 모두 맞아도
        /// <paramref name="minScore"/> 에 못 미치게 된 순간 멈춘다 (멈추면 minScore 미만의 값을 돌려준다).
        /// </summary>
        private static float ScoreAt(Field f, Variant v, int cx, int cy, MetricKind metric, float minScore, float greedy)
        {
            int n = v.Ox.Length;
            float sum = 0;
            bool canStop = greedy >= 0 && metric != MetricKind.IgnoreGlobal && minScore > 0 && minScore < 1;
            float fac = canStop ? (1 - greedy * minScore) / (1 - minScore) : 0;
            float invN = 1f / n;
            var ux = f.Ux;
            var uy = f.Uy;
            int w = f.W, h = f.H;
            for (int j = 0; j < n; j++)
            {
                int px = cx + v.Ox[j], py = cy + v.Oy[j];
                if ((uint)px < (uint)w && (uint)py < (uint)h)
                {
                    int idx = py * w + px;
                    float d = ux[idx] * v.Dx[j] + uy[idx] * v.Dy[j];
                    sum += metric == MetricKind.IgnoreLocal ? Math.Abs(d) : d;
                }
                if (canStop && (j & 7) == 7)
                {
                    float done = (j + 1) * invN;
                    float limit = Math.Min(minScore - 1 + fac * done, minScore * done);
                    if (sum * invN < limit) return sum * invN - 1;   // minScore 미만 표시
                }
            }
            float score = sum * invN;
            return metric == MetricKind.IgnoreGlobal ? Math.Abs(score) : score;
        }

        private static double[] Range(double start, double extent, double step)
        {
            if (extent <= 1e-9 || step <= 1e-12) return new[] { start };
            int n = (int)Math.Ceiling(extent / step - 1e-9) + 1;
            var list = new double[n];
            for (int i = 0; i < n; i++) list[i] = start + Math.Min(extent, i * step);
            return list;
        }

        private sealed class Candidate
        {
            public int X, Y;
            public double Angle, Scale;
            public float Score;
        }

        public static List<ShapeMatch> Find(Mat level0, ShapeModel model, double minScore, int numMatches, double maxOverlap,
            double greediness, ParallelOptions? options, OperatorContext ctx)
        {
            var metric = ParseMetric(model.Metric);
            float g = (float)Math.Max(0, Math.Min(1, greediness));

            // 탐색 이미지 피라미드 (모델 단 수까지, 너무 작아지면 그만)
            var fields = new List<Field>();
            var mats = new List<Mat>();
            try
            {
                Mat cur = level0;
                for (int l = 0; l < model.NumLevels; l++)
                {
                    if (l > 0)
                    {
                        if (Math.Min(cur.Cols, cur.Rows) < 16) break;
                        var next = new Mat();
                        Cv2.PyrDown(cur, next);
                        mats.Add(next);
                        cur = next;
                    }
                    fields.Add(MakeField(cur, Math.Max(1, model.MinContrast * Math.Pow(ContrastFalloff, l)), keepMagnitude: l == 0));
                }
            }
            finally
            {
                foreach (var m in mats) m.Dispose();
            }

            int top = fields.Count - 1;
            float minS = (float)minScore;
            // 위 단은 흐려져 점수가 낮게 나오므로 기준을 낮춰 후보를 넉넉히 남긴다
            float Threshold(int level) => level == 0 ? minS : minS * (level == top ? 0.7f : 0.8f);
            double AngleStep(int level) => model.HasRotation ? Math.Min(model.AngleStep * (1 << level), 15 * Math.PI / 180) : 0;
            double ScaleStep(int level) => model.HasScale ? Math.Min(model.ScaleStep * (1 << level), 0.2) : 0;

            // ---- 최상단: 전 위치 × 각도 × 스케일
            var lvlTop = model.Levels[top];
            var fTop = fields[top];
            var variants = new List<Variant>();
            foreach (var s in Range(model.ScaleMin, model.ScaleMax - model.ScaleMin, ScaleStep(top)))
                foreach (var a in Range(model.AngleStart, model.AngleExtent, AngleStep(top)))
                    variants.Add(Transform(lvlTop, a, s));

            int tw = fTop.W, th = fTop.H;
            var best = new float[tw * th];
            var bestVar = new int[tw * th];
            for (int i = 0; i < best.Length; i++) best[i] = -2;
            float thrTop = Threshold(top);
            Action<int> row = y =>
            {
                ctx.ThrowIfCancelled();
                for (int vi = 0; vi < variants.Count; vi++)
                {
                    var v = variants[vi];
                    for (int x = 0; x < tw; x++)
                    {
                        float sc = ScoreAt(fTop, v, x, y, metric, thrTop, g);
                        int i = y * tw + x;
                        if (sc > best[i])
                        {
                            best[i] = sc;
                            bestVar[i] = vi;
                        }
                    }
                }
            };
            if (options != null) Parallel.For(0, th, options, row);
            else for (int y = 0; y < th; y++) row(y);

            // 국소 최대 → 후보
            var candidates = new List<Candidate>();
            for (int y = 0; y < th; y++)
            {
                for (int x = 0; x < tw; x++)
                {
                    float v = best[y * tw + x];
                    if (v < thrTop) continue;
                    bool isMax = true;
                    for (int dy = -1; dy <= 1 && isMax; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= tw || ny >= th) continue;
                            float o = best[ny * tw + nx];
                            if (o > v || (o == v && (dy < 0 || (dy == 0 && dx < 0)))) { isMax = false; break; }
                        }
                    if (!isMax) continue;
                    var vv = variants[bestVar[y * tw + x]];
                    candidates.Add(new Candidate { X = x, Y = y, Angle = vv.Angle, Scale = vv.Scale, Score = v });
                }
            }
            candidates.Sort((p, q) => q.Score.CompareTo(p.Score));
            int maxCandidates = numMatches > 0 ? Math.Max(20, numMatches * 10) : 400;
            if (candidates.Count > maxCandidates) candidates.RemoveRange(maxCandidates, candidates.Count - maxCandidates);

            // ---- 아래 단으로 추적: 위치 ±2, 각도/스케일 ±2 단계 (그 단의 간격)
            for (int l = top - 1; l >= 0; l--)
            {
                var lvl = model.Levels[l];
                var f = fields[l];
                double aStep = AngleStep(l), sStep = ScaleStep(l);
                float thr = Threshold(l);
                var refined = new Candidate?[candidates.Count];
                Action<int> refine = ci =>
                {
                    ctx.ThrowIfCancelled();
                    var c = candidates[ci];
                    Candidate? bestC = null;
                    int ka = model.HasRotation ? 2 : 0, ks = model.HasScale ? 2 : 0;
                    for (int si = -ks; si <= ks; si++)
                    {
                        double s = c.Scale + si * sStep;
                        if (s < model.ScaleMin - sStep || s > model.ScaleMax + sStep || s <= 0.05) continue;
                        for (int ai = -ka; ai <= ka; ai++)
                        {
                            double a = c.Angle + ai * aStep;
                            if (a < model.AngleStart - aStep || a > model.AngleStart + model.AngleExtent + aStep) continue;
                            var v = Transform(lvl, a, s);
                            for (int dy = -2; dy <= 2; dy++)
                                for (int dx = -2; dx <= 2; dx++)
                                {
                                    int x = c.X * 2 + dx, y = c.Y * 2 + dy;
                                    float sc = ScoreAt(f, v, x, y, metric, Math.Max(thr, bestC?.Score ?? 0), g);
                                    if (sc >= thr && (bestC == null || sc > bestC.Score))
                                        bestC = new Candidate { X = x, Y = y, Angle = a, Scale = s, Score = sc };
                                }
                        }
                    }
                    refined[ci] = bestC;
                };
                if (options != null) Parallel.For(0, candidates.Count, options, refine);
                else for (int ci = 0; ci < candidates.Count; ci++) refine(ci);

                candidates = refined.Where(c => c != null).Select(c => c!).ToList();
                // 같은 곳으로 모인 후보 정리
                candidates = Dedupe(candidates);
            }

            // ---- 원본 해상도에서 서브픽셀: 스케일은 점수 포물선, 위치/각도는 에지 위치 최소제곱
            var lvl0 = model.Levels[0];
            var f0 = fields[0];
            var results = new List<(ShapeMatch m, Variant v)>();
            foreach (var c in candidates)
            {
                double angle = c.Angle, scale = c.Scale;
                if (model.HasScale)
                {
                    double st = model.ScaleStep;
                    float l = ScoreAt(f0, Transform(lvl0, angle, scale - st), c.X, c.Y, metric, 0, -1);
                    float r = ScoreAt(f0, Transform(lvl0, angle, scale + st), c.X, c.Y, metric, 0, -1);
                    scale += Parabola(l, c.Score, r) * st;
                }
                if (model.HasRotation)
                {
                    double st = model.AngleStep;
                    float l = ScoreAt(f0, Transform(lvl0, angle - st, scale), c.X, c.Y, metric, 0, -1);
                    float r = ScoreAt(f0, Transform(lvl0, angle + st, scale), c.X, c.Y, metric, 0, -1);
                    angle += Parabola(l, c.Score, r) * st;
                }
                double sx = c.X, sy = c.Y;
                RefineLeastSquares(f0, lvl0, metric, ref sx, ref sy, ref angle, scale, model.HasRotation);
                results.Add((new ShapeMatch(sx, sy, angle, scale, c.Score), Transform(lvl0, angle, scale)));
            }

            // ---- 점수순, 겹침 제거
            results.Sort((p, q) => q.m.Score.CompareTo(p.m.Score));
            var picked = new List<(ShapeMatch m, double x0, double y0, double x1, double y1)>();
            foreach (var (m, v) in results)
            {
                if (m.Score < minScore) continue;
                Bounds(v, m.X, m.Y, out double x0, out double y0, out double x1, out double y1);
                bool overlaps = false;
                foreach (var p in picked)
                {
                    double iw = Math.Min(x1, p.x1) - Math.Max(x0, p.x0);
                    double ih = Math.Min(y1, p.y1) - Math.Max(y0, p.y0);
                    if (iw <= 0 || ih <= 0) continue;
                    double inter = iw * ih;
                    double smaller = Math.Min((x1 - x0) * (y1 - y0), (p.x1 - p.x0) * (p.y1 - p.y0));
                    if (smaller > 0 && inter / smaller > maxOverlap) { overlaps = true; break; }
                }
                if (overlaps) continue;
                picked.Add((m, x0, y0, x1, y1));
                if (numMatches > 0 && picked.Count >= numMatches) break;
            }
            return picked.Select(p => p.m).ToList();
        }

        /// <summary>
        /// 최소제곱 서브픽셀 보정: 각 모델 점에서 법선(모델 그라디언트 방향)을 따라 ±2 픽셀 안의 그라디언트 크기 최대(포물선 보간)를
        /// 실제 에지 위치로 보고, 법선 방향 거리의 제곱합이 최소가 되도록 이동량(과 회전)을 푼다. 방향이 맞지 않거나 에지가 없는
        /// 점(가려진 부분)은 빼므로 가림이 있어도 위치가 치우치지 않는다. 방향 점수는 흐린 에지 폭 안에서 평평해 1픽셀 정도 흔들린다.
        /// </summary>
        private static void RefineLeastSquares(Field f, ShapeModelLevel lvl, MetricKind metric, ref double cx, ref double cy, ref double angle, double scale, bool rotate)
        {
            var mag = f.Mag;
            if (mag == null) return;
            int w = f.W, h = f.H;

            float Sample(double x, double y)
            {
                if (x < 0 || y < 0 || x >= w - 1 || y >= h - 1) return 0;
                int x0 = (int)x, y0 = (int)y;
                double fx = x - x0, fy = y - y0;
                int i = y0 * w + x0;
                return (float)((mag[i] * (1 - fx) + mag[i + 1] * fx) * (1 - fy) + (mag[i + w] * (1 - fx) + mag[i + w + 1] * fx) * fy);
            }

            for (int iter = 0; iter < 3; iter++)
            {
                double c = Math.Cos(angle), s = Math.Sin(angle);
                // 정규방정식 (tx, ty, dθ)
                double a00 = 0, a01 = 0, a02 = 0, a11 = 0, a12 = 0, a22 = 0, b0 = 0, b1 = 0, b2 = 0;
                int used = 0;
                for (int i = 0; i < lvl.Count; i++)
                {
                    double rx = scale * (c * lvl.X[i] + s * lvl.Y[i]);
                    double ry = scale * (-s * lvl.X[i] + c * lvl.Y[i]);
                    double nx = c * lvl.Dx[i] + s * lvl.Dy[i];
                    double ny = -s * lvl.Dx[i] + c * lvl.Dy[i];
                    double px = cx + rx, py = cy + ry;

                    // 법선을 따라 크기 최대
                    int bestT = int.MinValue;
                    float bestM = 0;
                    var m = new float[5];
                    for (int t = -2; t <= 2; t++)
                    {
                        m[t + 2] = Sample(px + t * nx, py + t * ny);
                        if (m[t + 2] > bestM) { bestM = m[t + 2]; bestT = t; }
                    }
                    if (bestT == int.MinValue || bestT == -2 || bestT == 2) continue;   // 창 안에 꼭대기가 없다

                    // 그 위치의 이미지 방향이 모델과 맞는지 (극성 규칙 포함)
                    int qx = (int)Math.Round(px + bestT * nx), qy = (int)Math.Round(py + bestT * ny);
                    if ((uint)qx >= (uint)w || (uint)qy >= (uint)h) continue;
                    double dot = f.Ux[qy * w + qx] * nx + f.Uy[qy * w + qx] * ny;
                    if (metric == MetricKind.UsePolarity ? dot < 0.7 : Math.Abs(dot) < 0.7) continue;

                    double d = bestT + Parabola(m[bestT + 1], m[bestT + 2], m[bestT + 3]);
                    // 잔차 d ≈ n·t + dθ·(n·J), J = d(점)/dθ = (ry, -rx)
                    double g2 = nx * ry - ny * rx;
                    a00 += nx * nx; a01 += nx * ny; a02 += nx * g2;
                    a11 += ny * ny; a12 += ny * g2; a22 += g2 * g2;
                    b0 += nx * d; b1 += ny * d; b2 += g2 * d;
                    used++;
                }
                if (used < 6) return;

                double tx, ty, dt = 0;
                if (rotate)
                {
                    if (!Solve3(a00, a01, a02, a11, a12, a22, b0, b1, b2, out tx, out ty, out dt)) return;
                }
                else
                {
                    double det = a00 * a11 - a01 * a01;
                    if (Math.Abs(det) < 1e-9) return;
                    tx = (b0 * a11 - b1 * a01) / det;
                    ty = (a00 * b1 - a01 * b0) / det;
                }
                // 한 번에 너무 멀리 가지 않도록 (잘못된 대응에 대한 안전장치)
                if (Math.Abs(tx) > 2 || Math.Abs(ty) > 2 || Math.Abs(dt) > 0.05) return;
                cx += tx;
                cy += ty;
                angle += dt;
                if (Math.Abs(tx) < 0.01 && Math.Abs(ty) < 0.01 && Math.Abs(dt) < 1e-4) return;
            }
        }

        /// <summary>대칭 3x3 연립방정식 (크라메르).</summary>
        private static bool Solve3(double a00, double a01, double a02, double a11, double a12, double a22,
            double b0, double b1, double b2, out double x0, out double x1, out double x2)
        {
            double det = a00 * (a11 * a22 - a12 * a12) - a01 * (a01 * a22 - a12 * a02) + a02 * (a01 * a12 - a11 * a02);
            x0 = x1 = x2 = 0;
            if (Math.Abs(det) < 1e-12) return false;
            x0 = (b0 * (a11 * a22 - a12 * a12) - a01 * (b1 * a22 - a12 * b2) + a02 * (b1 * a12 - a11 * b2)) / det;
            x1 = (a00 * (b1 * a22 - a12 * b2) - b0 * (a01 * a22 - a12 * a02) + a02 * (a01 * b2 - b1 * a02)) / det;
            x2 = (a00 * (a11 * b2 - b1 * a12) - a01 * (a01 * b2 - b1 * a02) + b0 * (a01 * a12 - a11 * a02)) / det;
            return true;
        }

        /// <summary>세 점 포물선의 꼭짓점 오프셋 (-0.5..0.5 로 제한).</summary>
        private static double Parabola(double l, double c, double r)
        {
            double d = l - 2 * c + r;
            if (d >= -1e-9) return 0;
            return Math.Max(-0.5, Math.Min(0.5, 0.5 * (l - r) / d));
        }

        private static void Bounds(Variant v, double cx, double cy, out double x0, out double y0, out double x1, out double y1)
        {
            x0 = y0 = double.MaxValue;
            x1 = y1 = double.MinValue;
            for (int i = 0; i < v.Ox.Length; i++)
            {
                x0 = Math.Min(x0, cx + v.Ox[i]);
                x1 = Math.Max(x1, cx + v.Ox[i]);
                y0 = Math.Min(y0, cy + v.Oy[i]);
                y1 = Math.Max(y1, cy + v.Oy[i]);
            }
            x1 += 1;
            y1 += 1;
        }

        /// <summary>같은 위치(±1)·각도로 모인 후보 중 가장 좋은 것만 남긴다.</summary>
        private static List<Candidate> Dedupe(List<Candidate> list)
        {
            list.Sort((p, q) => q.Score.CompareTo(p.Score));
            var kept = new List<Candidate>();
            foreach (var c in list)
            {
                bool dup = kept.Any(k => Math.Abs(k.X - c.X) <= 1 && Math.Abs(k.Y - c.Y) <= 1
                    && Math.Abs(k.Angle - c.Angle) < 1e-6 + 0.05 && Math.Abs(k.Scale - c.Scale) < 0.02);
                if (!dup) kept.Add(c);
            }
            return kept;
        }
    }
}
