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
    [Operator("CreateNccModel", Category = "Matching", Description = "Creates a normalized-cross-correlation template from the image inside the region's bounding box (first channel). Rotation is not supported yet.")]
    public sealed class CreateNccModel : OperatorBase
    {
        [Input(Description = "Template image.")] public Image? Template { get; set; }
        [Input(Optional = true, Description = "Region whose bounding box is the template (default: whole image / domain).")] public Region? Region { get; set; }
        [Output] public NccModel? Model { get; private set; }

        protected override void Validate() => Require(Template, nameof(Template));

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
                    Model = new NccModel(tpl, box.Height / 2.0, box.Width / 2.0);
            }
        }
    }

    [Operator("FindNccModel", Category = "Matching", Description = "Finds the template in the image (translation only): positions of the model origin, scores.")]
    public sealed class FindNccModel : OperatorBase
    {
        [Input] public Image? Image { get; set; }
        [Input] public NccModel? Model { get; set; }
        [Input(Description = "Minimum NCC score (0..1).", Default = 0.7, Min = -1, Max = 1)] public double MinScore { get; set; }
        [Input(Description = "Maximum number of matches (0 = all).", Default = 1, Min = 0)] public int NumMatches { get; set; }
        [Input(Description = "Maximum allowed overlap between matches (0..1).", Default = 0.5, Min = 0, Max = 1)] public double MaxOverlap { get; set; }

        [Output] public TupleValue? Row { get; private set; }
        [Output] public TupleValue? Column { get; private set; }
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

            using (var src = MatInterop.AsMat(img, box))
            using (var search = NccMatching.Prepare(src))
            using (var tplView = MatInterop.AsMat(model.Template))
            using (var tpl = new Mat())
            using (var result = new Mat())
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
                Cv2.MatchTemplate(search, tpl, result, TemplateMatchModes.CCoeffNormed);

                var candidates = NccMatching.LocalMaxima(result, (float)MinScore);
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
                var scores = new double[picked.Count];
                for (int i = 0; i < picked.Count; i++)
                {
                    NccMatching.SubPixel(result, picked[i].x, picked[i].y, out double sx, out double sy);
                    rows[i] = box.Y + sy + model.OriginRow;
                    cols[i] = box.X + sx + model.OriginColumn;
                    scores[i] = picked[i].score;
                }
                Row = rows;
                Column = cols;
                Score = scores;
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
