using System;
using System.Collections.Generic;
using InspectionView.Core.Backends;
using InspectionView.Core.Cameras;
using InspectionView.Core.Errors;
using InspectionView.Core.Operators.Surface;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;

namespace InspectionView.Core.Operators.Camera
{
    /// <summary>
    /// 자동 초점: 카메라의 포커스 파라미터(모터 렌즈/스테이지 위치)를 Min..Max 로 움직이며 프레임을 잡고
    /// <see cref="FocusOps"/> 측도가 최대인 위치를 찾는다 (거친 탐색 → 최대 주변 세밀 탐색 → 포물선 보간).
    /// 끝나면 카메라를 최적 위치로 둔다. 스크립트: <c>Best = AutoFocus(Cam, 'FocusPosition', 0, 100)</c>.
    /// </summary>
    [Operator("AutoFocus", Category = "Camera", Description = "Scans a camera focus parameter between Min and Max, grabbing a frame per step, and moves to the position with the highest focus measure (coarse-to-fine, parabolic refinement).")]
    public sealed class AutoFocus : OperatorBase
    {
        [Input] public CameraHandle? Camera { get; set; }
        [Input(Name = "Parameter", Description = "Camera parameter that moves the focus (e.g. FocusPosition).", Default = "FocusPosition")] public string ParamName { get; set; } = "";
        [Input(Description = "Scan start.")] public double Min { get; set; }
        [Input(Description = "Scan end.")] public double Max { get; set; }
        [Input(Description = "Number of coarse steps.", Default = 10, Min = 2)] public int Steps { get; set; }
        [Input(Description = "Number of fine steps around the coarse maximum (0 = coarse only).", Default = 6, Min = 0)] public int FineSteps { get; set; }
        [Input(Default = FocusMethod.ModifiedLaplacian)] public FocusMethod Method { get; set; }
        [Input(Description = "Focus measure window size.", Default = 5, Min = 1)] public int Size { get; set; }
        [Input(Description = "Region to evaluate (default: whole frame).", Optional = true)] public Region? Region { get; set; }
        [Input(Description = "Grab timeout per step (ms).", Default = 5000, Min = 0)] public int Timeout { get; set; }
        [Input(Description = "Settle delay after moving (ms).", Default = 0, Min = 0)] public int SettleMs { get; set; }

        [Output(Description = "Focus parameter value with the highest score.")] public TupleValue? BestPosition { get; private set; }
        [Output(Description = "Focus score at the best position.")] public TupleValue? BestScore { get; private set; }
        [Output(Description = "All scanned positions.")] public TupleValue? Positions { get; private set; }
        [Output(Description = "Focus score of each scanned position.")] public TupleValue? Scores { get; private set; }
        [Output(Description = "Frame grabbed at the best position.")] public Image? Image { get; private set; }

        protected override void Validate()
        {
            Require(Camera, nameof(Camera));
            RequireNotEmpty(ParamName, "Parameter");
            if (Max <= Min) throw VisionException.InvalidArgument(nameof(Max), "must be greater than Min");
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var cam = Camera!.Camera;
            var positions = new List<double>();
            var scores = new List<double>();

            // 거친 탐색
            double bestPos = Min, bestScore = double.NegativeInfinity;
            int n = Steps;
            for (int i = 0; i < n; i++)
            {
                ctx.ThrowIfCancelled();
                double pos = Min + (Max - Min) * i / (n - 1);
                double score = Measure(cam, pos);
                positions.Add(pos); scores.Add(score);
                if (score > bestScore) { bestScore = score; bestPos = pos; }
            }

            // 세밀 탐색: 거친 최대 양옆 한 칸 범위
            if (FineSteps > 0)
            {
                double step = (Max - Min) / (n - 1);
                double lo = Math.Max(Min, bestPos - step), hi = Math.Min(Max, bestPos + step);
                for (int i = 0; i <= FineSteps; i++)
                {
                    ctx.ThrowIfCancelled();
                    double pos = lo + (hi - lo) * i / FineSteps;
                    if (positions.Exists(p => Math.Abs(p - pos) < 1e-9)) continue;
                    double score = Measure(cam, pos);
                    positions.Add(pos); scores.Add(score);
                    if (score > bestScore) { bestScore = score; bestPos = pos; }
                }
            }

            // 포물선 보간: 최대 위치 양옆의 가장 가까운 측정점으로
            double refined = bestPos;
            {
                int bi = positions.IndexOf(bestPos);
                double? lp = null, lv = null, rp = null, rv = null;
                for (int i = 0; i < positions.Count; i++)
                {
                    if (i == bi) continue;
                    if (positions[i] < bestPos && (lp == null || positions[i] > lp)) { lp = positions[i]; lv = scores[i]; }
                    if (positions[i] > bestPos && (rp == null || positions[i] < rp)) { rp = positions[i]; rv = scores[i]; }
                }
                if (lp != null && rp != null)
                {
                    // 세 점 (lp,lv) (bestPos,bestScore) (rp,rv) 을 지나는 포물선의 꼭짓점
                    double x0 = lp.Value, x1 = bestPos, x2 = rp.Value, y0 = lv!.Value, y1 = bestScore, y2 = rv!.Value;
                    double denom = (x0 - x1) * (x0 - x2) * (x1 - x2);
                    if (Math.Abs(denom) > 1e-12)
                    {
                        double a = (x2 * (y1 - y0) + x1 * (y0 - y2) + x0 * (y2 - y1)) / denom;
                        double b = (x2 * x2 * (y0 - y1) + x1 * x1 * (y2 - y0) + x0 * x0 * (y1 - y2)) / denom;
                        if (a < 0)
                        {
                            double v = -b / (2 * a);
                            if (v > x0 && v < x2) refined = v;
                        }
                    }
                }
            }

            cam.SetParameter(ParamName, refined);
            if (SettleMs > 0) System.Threading.Thread.Sleep(SettleMs);
            Image = cam.Grab(Timeout);

            BestPosition = refined;
            BestScore = bestScore;
            Positions = positions.ToArray();
            Scores = scores.ToArray();
            ctx.Info($"AutoFocus: best {ParamName} = {refined:F3} (score {bestScore:F2}, {positions.Count} frames)");
        }

        private double Measure(ICamera cam, double position)
        {
            cam.SetParameter(ParamName, position);
            if (SettleMs > 0) System.Threading.Thread.Sleep(SettleMs);
            using (var frame = cam.Grab(Timeout))
            using (var focus = FocusOps.Measure(frame, Method, Size))
                return FocusOps.Score(focus, Region ?? frame.Domain);
        }
    }
}
