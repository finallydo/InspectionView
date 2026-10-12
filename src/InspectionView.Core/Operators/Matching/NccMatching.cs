using System;
using System.Collections.Generic;
using System.Linq;
using InspectionView.Core.Backends;
using InspectionView.Core.Errors;
using InspectionView.Core.Interop;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;
using OpenCvSharp;
using ErrorCode = InspectionView.Core.Errors.ErrorCode;

namespace InspectionView.Core.Operators.Matching
{
    [Operator("CreateNccModel", Category = "Matching", Description = "Creates a normalized-cross-correlation template from the image inside the region's bounding box (first channel), optionally with a rotation (radians) and scale search range.")]
    public sealed class CreateNccModel : OperatorBase
    {
        [Input(Description = "Template image.")] public Image? Template { get; set; }
        [Input(Optional = true, Description = "Region whose bounding box is the template (default: whole image / domain).")] public Region? Region { get; set; }
        [Input(Description = "Start of the rotation range in radians (counter-clockwise positive).", Default = 0.0)] public double AngleStart { get; set; }
        [Input(Description = "Extent of the rotation range in radians (0 = no rotation).", Default = 0.0, Min = 0)] public double AngleExtent { get; set; }
        [Input(Description = "Rotation step in radians (0 = automatic from the template size).", Default = 0.0, Min = 0)] public double AngleStep { get; set; }
        [Input(Description = "Minimum scale.", Default = 1.0, Min = 0.1)] public double ScaleMin { get; set; }
        [Input(Description = "Maximum scale.", Default = 1.0, Min = 0.1)] public double ScaleMax { get; set; }
        [Input(Description = "Scale step (0 = 0.05).", Default = 0.0, Min = 0)] public double ScaleStep { get; set; }
        [Output] public NccModel? Model { get; private set; }

        protected override void Validate()
        {
            Require(Template, nameof(Template));
            if (ScaleMax < ScaleMin) throw VisionException.InvalidArgument(nameof(ScaleMax), "must be >= ScaleMin");
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var img = Template!;
            var box = Region != null ? Region.BoundingBox.Intersect(img.Bounds) : img.DomainBounds;
            if (box.IsEmpty)
                throw new VisionException(ErrorCode.EmptyObject, "Template region is empty.");

            using (var src = MatInterop.AsMat(img, box))
            using (var prepared = NccMatching.Prepare(src))
            {
                using (var tpl = MatInterop.ToImage(prepared))
                    Model = new NccModel(tpl, box.Height / 2.0, box.Width / 2.0, AngleStart, AngleExtent, AngleStep, ScaleMin, ScaleMax, ScaleStep);
            }
        }
    }

    [Operator("FindNccModel", Category = "Matching", Description = "Finds the template in the image over the model's rotation/scale range: positions of the model origin, angles (radians), scales and scores.")]
    public sealed class FindNccModel : OperatorBase
    {
        [Input] public Image? Image { get; set; }
        [Input] public NccModel? Model { get; set; }
        [Input(Description = "Minimum NCC score (0..1).", Default = 0.7, Min = -1, Max = 1)] public double MinScore { get; set; }
        [Input(Description = "Maximum number of matches (0 = all).", Default = 1, Min = 0)] public int NumMatches { get; set; }
        [Input(Description = "Maximum allowed overlap between matches (0..1).", Default = 0.5, Min = 0, Max = 1)] public double MaxOverlap { get; set; }

        [Output] public TupleValue? Row { get; private set; }
        [Output] public TupleValue? Column { get; private set; }
        [Output(Description = "Rotation of each match in radians.")] public TupleValue? Angle { get; private set; }
        [Output(Description = "Scale of each match.")] public TupleValue? Scale { get; private set; }
        [Output] public TupleValue? Score { get; private set; }

        protected override void Validate()
        {
            var img = Require(Image, nameof(Image));
            var model = Require(Model, nameof(Model));
            if (model.Width > img.Width || model.Height > img.Height)
                throw new VisionException(ErrorCode.SizeMismatch, "Template is larger than the search image.");
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var img = Image!;
            var model = Model!;
            var box = img.DomainBounds;
            var angles = model.Angles();
            var scales = model.Scales();

            using (var src = MatInterop.AsMat(img, box))
            using (var search = NccMatching.Prepare(src))
            using (var tplView = MatInterop.AsMat(model.Template))
            using (var tpl = new Mat())
            {
                // 템플릿과 탐색 이미지의 깊이가 다르면 둘 다 float 로
                if (search.Depth() != tplView.Depth())
                {
                    tplView.ConvertTo(tpl, MatType.CV_32F);
                    if (search.Depth() != MatType.CV_32F)
                        search.ConvertTo(search, MatType.CV_32F);
                }
                else
                {
                    tplView.CopyTo(tpl);
                }

                // 각도/스케일마다 매칭하고 위치별 최대 점수와 그때의 (각도, 스케일) 인덱스를 남긴다
                Mat? best = null, bestIdx = null;
                var canvasSizes = new Dictionary<int, Size>();
                try
                {
                    int sw = search.Cols, sh = search.Rows;
                    best = new Mat(sh, sw, MatType.CV_32FC1, new Scalar(float.NegativeInfinity));
                    bestIdx = new Mat(sh, sw, MatType.CV_32SC1, new Scalar(-1));

                    for (int si = 0; si < scales.Length; si++)
                    {
                        for (int ai = 0; ai < angles.Length; ai++)
                        {
                            ctx.ThrowIfCancelled();
                            int idx = si * angles.Length + ai;
                            using (var warped = NccMatching.Warp(tpl, angles[ai], scales[si], out var mask))
                            using (mask)
                            using (var result = new Mat())
                            {
                                if (warped.Cols > sw || warped.Rows > sh) continue;
                                canvasSizes[idx] = warped.Size();
                                // 마스크는 회전으로 캔버스에 빈 모서리가 생길 때만 (스케일만 바뀌면 전체가 유효)
                                if (NccMatching.IsFull(mask)) Cv2.MatchTemplate(search, warped, result, TemplateMatchModes.CCoeffNormed);
                                else Cv2.MatchTemplate(search, warped, result, TemplateMatchModes.CCoeffNormed, mask);

                                // result 는 (sw-cw+1, sh-ch+1): 캔버스 좌상단 기준. 중심 기준으로 비교하기 위해 캔버스 중심(인덱스) 오프셋만큼 옮겨 기록
                                int ox = (warped.Cols - 1) / 2, oy = (warped.Rows - 1) / 2;
                                NccMatching.UpdateBest(result, best, bestIdx, idx, ox, oy);
                            }
                        }
                    }

                    var candidates = NccMatching.LocalMaxima(best, (float)MinScore);
                    candidates.Sort((a, b) => b.score.CompareTo(a.score));

                    var picked = new List<(int x, int y, float score)>();
                    int minDx = (int)Math.Ceiling(model.Width * (1 - MaxOverlap));
                    int minDy = (int)Math.Ceiling(model.Height * (1 - MaxOverlap));
                    foreach (var c in candidates)
                    {
                        bool overlaps = picked.Any(p => Math.Abs(p.x - c.x) < minDx && Math.Abs(p.y - c.y) < minDy);
                        if (overlaps) continue;
                        picked.Add(c);
                        if (NumMatches > 0 && picked.Count >= NumMatches) break;
                    }

                    var rows = new double[picked.Count];
                    var cols = new double[picked.Count];
                    var angs = new double[picked.Count];
                    var scls = new double[picked.Count];
                    var scores = new double[picked.Count];
                    for (int i = 0; i < picked.Count; i++)
                    {
                        int idx = bestIdx.At<int>(picked[i].y, picked[i].x);
                        int si = idx / angles.Length, ai = idx % angles.Length;
                        double angle = angles[ai], scale = scales[si];
                        double score = picked[i].score;

                        NccMatching.SubPixel(best, picked[i].x, picked[i].y, out double sx, out double sy);

                        // 각도 세밀화: 이웃 각도의 점수로 포물선 보간
                        if (angles.Length > 1 && ai > 0 && ai < angles.Length - 1)
                        {
                            double l = NccMatching.ScoreAt(search, tpl, angles[ai - 1], scale, picked[i].x, picked[i].y);
                            double r = NccMatching.ScoreAt(search, tpl, angles[ai + 1], scale, picked[i].x, picked[i].y);
                            double d = l - 2 * score + r;
                            if (d < -1e-9)
                            {
                                double off = 0.5 * (l - r) / d;
                                angle += Math.Max(-1, Math.Min(1, off)) * (angles[ai + 1] - angles[ai]);
                            }
                        }

                        // best 맵은 캔버스의 정수 중심(인덱스) 기준. 실수 중심과의 차이와, 인덱스 → 픽셀 경계 좌표(+0.5) 보정
                        var cs = canvasSizes[idx];
                        rows[i] = box.Y + sy + ((cs.Height - 1) / 2.0 - (cs.Height - 1) / 2) + 0.5;
                        cols[i] = box.X + sx + ((cs.Width - 1) / 2.0 - (cs.Width - 1) / 2) + 0.5;
                        angs[i] = angle;
                        scls[i] = scale;
                        scores[i] = score;
                    }
                    Row = rows;
                    Column = cols;
                    Angle = angs;
                    Scale = scls;
                    Score = scores;
                }
                finally
                {
                    best?.Dispose();
                    bestIdx?.Dispose();
                }
            }
        }
    }

    internal static class NccMatching
    {
        /// <summary>매칭용 입력 준비: 첫 채널, 8bit 는 그대로, 그 외는 float.</summary>
        public static Mat Prepare(Mat src)
        {
            var single = new Mat();
            if (src.Channels() > 1)
                Cv2.ExtractChannel(src, single, 0);
            else
                src.CopyTo(single);

            if (single.Depth() == MatType.CV_8U || single.Depth() == MatType.CV_32F)
                return single;

            var f = new Mat();
            single.ConvertTo(f, MatType.CV_32F);
            single.Dispose();
            return f;
        }

        /// <summary>
        /// 템플릿을 중심 기준으로 회전/스케일한 캔버스(회전된 사각형을 담는 크기)와 유효 영역 마스크(8U).
        /// 각도는 반시계 +(라디안).
        /// </summary>
        public static Mat Warp(Mat tpl, double angle, double scale, out Mat mask)
        {
            int w = tpl.Cols, h = tpl.Rows;
            if (angle == 0 && scale == 1)
            {
                mask = new Mat(h, w, MatType.CV_8UC1, new Scalar(255));
                return tpl.Clone();
            }
            double c = Math.Abs(Math.Cos(angle)), s = Math.Abs(Math.Sin(angle));
            int cw = (int)Math.Ceiling((w * c + h * s) * scale);
            int ch = (int)Math.Ceiling((w * s + h * c) * scale);
            // 픽셀 인덱스 좌표계: 템플릿의 인덱스 중심 ((w-1)/2, (h-1)/2) 을 캔버스의 인덱스 중심으로 보낸다
            using (var m = Cv2.GetRotationMatrix2D(new Point2f((w - 1) / 2f, (h - 1) / 2f), angle * 180 / Math.PI, scale))
            {
                m.Set(0, 2, m.At<double>(0, 2) + (cw - 1) / 2.0 - (w - 1) / 2.0);
                m.Set(1, 2, m.At<double>(1, 2) + (ch - 1) / 2.0 - (h - 1) / 2.0);
                var dst = new Mat();
                Cv2.WarpAffine(tpl, dst, m, new Size(cw, ch), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
                using (var ones = new Mat(h, w, MatType.CV_8UC1, new Scalar(255)))
                {
                    mask = new Mat();
                    Cv2.WarpAffine(ones, mask, m, new Size(cw, ch), InterpolationFlags.Nearest, BorderTypes.Constant, Scalar.All(0));
                }
                return dst;
            }
        }

        /// <summary>마스크가 전부 유효한지 (그러면 마스크 없이 매칭하는 편이 정확하고 빠르다).</summary>
        public static bool IsFull(Mat mask) => Cv2.CountNonZero(mask) == mask.Rows * mask.Cols;

        /// <summary>result(캔버스 좌상단 기준) 를 중심 오프셋만큼 옮겨 best/bestIdx 에 반영한다.</summary>
        public static void UpdateBest(Mat result, Mat best, Mat bestIdx, int idx, int ox, int oy)
        {
            for (int y = 0; y < result.Rows; y++)
            {
                int by = y + oy;
                if (by < 0 || by >= best.Rows) continue;
                for (int x = 0; x < result.Cols; x++)
                {
                    int bx = x + ox;
                    if (bx < 0 || bx >= best.Cols) continue;
                    float v = result.At<float>(y, x);
                    if (float.IsNaN(v) || float.IsInfinity(v)) continue;
                    if (v > best.At<float>(by, bx))
                    {
                        best.Set(by, bx, v);
                        bestIdx.Set(by, bx, idx);
                    }
                }
            }
        }

        /// <summary>중심 (cx, cy) 에 각도/스케일로 둔 템플릿의 NCC 점수 (캔버스가 이미지 밖이면 -1).</summary>
        public static double ScoreAt(Mat search, Mat tpl, double angle, double scale, int cx, int cy)
        {
            using (var warped = Warp(tpl, angle, scale, out var mask))
            using (mask)
            {
                int x0 = cx - (warped.Cols - 1) / 2, y0 = cy - (warped.Rows - 1) / 2;
                if (x0 < 0 || y0 < 0 || x0 + warped.Cols > search.Cols || y0 + warped.Rows > search.Rows) return -1;
                using (var roi = new Mat(search, new Rect(x0, y0, warped.Cols, warped.Rows)))
                using (var result = new Mat())
                {
                    if (IsFull(mask)) Cv2.MatchTemplate(roi, warped, result, TemplateMatchModes.CCoeffNormed);
                    else Cv2.MatchTemplate(roi, warped, result, TemplateMatchModes.CCoeffNormed, mask);
                    float v = result.At<float>(0, 0);
                    return float.IsNaN(v) ? -1 : v;
                }
            }
        }

        /// <summary>점수 맵에서 임계값 이상인 3x3 국소 최대값.</summary>
        public static List<(int x, int y, float score)> LocalMaxima(Mat result, float minScore)
        {
            var list = new List<(int, int, float)>();
            int w = result.Cols, h = result.Rows;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float v = result.At<float>(y, x);
                    if (v < minScore || float.IsNaN(v)) continue;
                    bool max = true;
                    for (int dy = -1; dy <= 1 && max; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                            float o = result.At<float>(ny, nx);
                            if (o > v || (o == v && (dy < 0 || (dy == 0 && dx < 0)))) { max = false; break; }
                        }
                    if (max) list.Add((x, y, v));
                }
            }
            return list;
        }

        /// <summary>포물선 보간으로 서브픽셀 최대 위치.</summary>
        public static void SubPixel(Mat result, int x, int y, out double sx, out double sy)
        {
            sx = x; sy = y;
            if (x > 0 && x < result.Cols - 1)
            {
                double l = result.At<float>(y, x - 1), c = result.At<float>(y, x), r = result.At<float>(y, x + 1);
                double d = l - 2 * c + r;
                if (Math.Abs(d) > 1e-9) sx = x + 0.5 * (l - r) / d;
            }
            if (y > 0 && y < result.Rows - 1)
            {
                double u = result.At<float>(y - 1, x), c = result.At<float>(y, x), dn = result.At<float>(y + 1, x);
                double d = u - 2 * c + dn;
                if (Math.Abs(d) > 1e-9) sy = y + 0.5 * (u - dn) / d;
            }
        }
    }
}
