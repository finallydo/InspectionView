using System;
using System.Collections.Generic;
using InspectionView.Core.Backends;
using InspectionView.Core.Errors;
using InspectionView.Core.Operators.Common;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;
using ErrorCode = InspectionView.Core.Errors.ErrorCode;

namespace InspectionView.Core.Operators.Surface
{
    /// <summary>
    /// 품질 유도(quality-guided) 위상 언래핑. 품질(보통 PhaseShiftDecode 의 Modulation)이 높은 픽셀부터
    /// 이웃으로 퍼져 나가며 2π 배수를 더해 연속 위상을 만든다. 품질이 MinQuality 미만인 픽셀은 건너뛰고 0 으로 둔다.
    /// </summary>
    [Operator("UnwrapPhase", Category = "Surface", Description = "Quality-guided 2-D phase unwrapping of a wrapped phase image (-pi..pi) using a quality image (e.g. modulation).")]
    public sealed class UnwrapPhase : OperatorBase
    {
        [Input(Description = "Wrapped phase in radians (Float32).")] public Image? Phase { get; set; }
        [Input(Description = "Quality per pixel (higher = more reliable). Default: constant.", Optional = true)] public Image? Quality { get; set; }
        [Input(Description = "Pixels with quality below this are not unwrapped (left at 0).", Default = 0.0)] public double MinQuality { get; set; }

        [Output(Description = "Unwrapped phase (Float32).")] public Image? Unwrapped { get; private set; }
        [Output(Description = "Pixels that were unwrapped.")] public Region? Valid { get; private set; }

        protected override void Validate()
        {
            var p = Require(Phase, nameof(Phase));
            if (p.Channels != 1) throw new VisionException(ErrorCode.TypeMismatch, "Phase must be single-channel.");
            if (Quality != null && (Quality.Width != p.Width || Quality.Height != p.Height))
                throw new VisionException(ErrorCode.SizeMismatch, "Quality must have the same size as Phase.");
        }

        [Backend(BackendKind.Cpu)]
        private unsafe void RunCpu(OperatorContext ctx)
        {
            var phaseImg = StackOps.ToFloat(Phase!);
            Image? qualImg = Quality != null ? StackOps.ToFloat(Quality) : null;
            Image? result = null;
            try
            {
                int w = phaseImg.Width, h = phaseImg.Height;
                result = StackOps.NewFloat(w, h);
                var done = new byte[w * h];
                var mask = Phase!.Domain?.ToMask(w, h);

                // 후보 힙: 품질 내림차순
                var heap = new List<(float q, int idx)>();
                void Push(float q, int idx)
                {
                    heap.Add((q, idx));
                    int i = heap.Count - 1;
                    while (i > 0)
                    {
                        int parent = (i - 1) / 2;
                        if (heap[parent].q >= heap[i].q) break;
                        (heap[parent], heap[i]) = (heap[i], heap[parent]);
                        i = parent;
                    }
                }
                (float q, int idx) Pop()
                {
                    var top = heap[0];
                    var last = heap[heap.Count - 1];
                    heap.RemoveAt(heap.Count - 1);
                    if (heap.Count > 0)
                    {
                        heap[0] = last;
                        int i = 0;
                        while (true)
                        {
                            int l = 2 * i + 1, r = l + 1, m = i;
                            if (l < heap.Count && heap[l].q > heap[m].q) m = l;
                            if (r < heap.Count && heap[r].q > heap[m].q) m = r;
                            if (m == i) break;
                            (heap[m], heap[i]) = (heap[i], heap[m]);
                            i = m;
                        }
                    }
                    return top;
                }

                float QAt(int x, int y) => qualImg != null ? ((float*)qualImg.GetRowPointer(y))[x] : 1f;
                bool Usable(int x, int y)
                {
                    if (mask != null && mask.At<byte>(y, x) == 0) return false;
                    return QAt(x, y) >= MinQuality;
                }

                // 시작점: 품질 최대 픽셀
                int sx = -1, sy = -1; float best = float.NegativeInfinity;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        if (!Usable(x, y)) continue;
                        float q = QAt(x, y);
                        if (q > best) { best = q; sx = x; sy = y; }
                    }
                if (sx < 0)
                    throw new VisionException(ErrorCode.EmptyObject, "No pixel satisfies MinQuality.");

                var runs = new List<Run>();
                int remaining = 0;
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) if (Usable(x, y)) remaining++;

                int[] dx = { 1, -1, 0, 0 }, dy = { 0, 0, 1, -1 };
                while (remaining > 0)
                {
                    // (다시) 시작: 아직 안 된 픽셀 중 품질 최대
                    if (sx < 0)
                    {
                        best = float.NegativeInfinity;
                        for (int y = 0; y < h && sx < 0; y++)
                            for (int x = 0; x < w; x++)
                                if (done[y * w + x] == 0 && Usable(x, y) && QAt(x, y) > best) { best = QAt(x, y); sx = x; sy = y; }
                        if (sx < 0) break;
                    }
                    ((float*)result.GetRowPointer(sy, true))[sx] = ((float*)phaseImg.GetRowPointer(sy))[sx];
                    done[sy * w + sx] = 1;
                    remaining--;
                    Push(QAt(sx, sy), sy * w + sx);

                    while (heap.Count > 0)
                    {
                        var (q, idx) = Pop();
                        int cx = idx % w, cy = idx / w;
                        float cur = ((float*)result.GetRowPointer(cy))[cx];
                        for (int k = 0; k < 4; k++)
                        {
                            int nx = cx + dx[k], ny = cy + dy[k];
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                            int nidx = ny * w + nx;
                            if (done[nidx] != 0 || !Usable(nx, ny)) continue;
                            float wrapped = ((float*)phaseImg.GetRowPointer(ny))[nx];
                            double d = wrapped - cur;
                            d -= 2 * Math.PI * Math.Round(d / (2 * Math.PI));
                            ((float*)result.GetRowPointer(ny, true))[nx] = (float)(cur + d);
                            done[nidx] = 1;
                            remaining--;
                            Push(QAt(nx, ny), nidx);
                        }
                    }
                    sx = -1;
                }

                for (int y = 0; y < h; y++)
                {
                    int start = -1;
                    for (int x = 0; x <= w; x++)
                    {
                        bool on = x < w && done[y * w + x] != 0;
                        if (on && start < 0) start = x;
                        else if (!on && start >= 0) { runs.Add(new Run(y, start, x - 1)); start = -1; }
                    }
                }
                Valid = Types.Region.FromNormalizedRuns(runs.ToArray());
                Unwrapped = ImageOps.KeepDomain(result, Phase!);
                result = null;
            }
            finally
            {
                result?.Release();
                phaseImg.Release();
                qualImg?.Release();
            }
        }
    }
}
