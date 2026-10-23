using System;
using System.Linq;
using InspectionView.Core.Backends;
using InspectionView.Core.Interop;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;
using OpenCvSharp;
using Xunit;

namespace InspectionView.Core.Tests
{
    /// <summary>에지 기반 Shape 매칭: 위치/각도/스케일, 조명 변화, 가림, 여러 개, 극성.</summary>
    public class ShapeMatchingTests
    {
        private static OperatorResult Exec(string op, BackendKind backend, params object?[] args)
        {
            var ctx = new OperatorContext(new BackendSettings { DefaultBackend = backend }, new ExecutionProfiler());
            var result = OperatorInvoker.Invoke(OperatorRegistry.Default, op, args, ctx);
            Assert.True(result.Success, $"{op} failed: {result.Code} {result.Message}");
            return result;
        }

        private static OperatorResult Exec(string op, params object?[] args) => Exec(op, BackendKind.Cpu, args);

        private static T Out<T>(OperatorResult r, string name) => Assert.IsType<T>(r.Outputs[name]);

        /// <summary>비대칭 'L' (기준점 = 0,0, 원본 크기 48x48).</summary>
        private static readonly (double x, double y)[] LPolygon =
        {
            (-24, -24), (-8, -24), (-8, 8), (24, 8), (24, 24), (-24, 24),
        };

        /// <summary>
        /// 배경 위에 L 들을 그린다. 각 (cx, cy) 는 기준점의 픽셀 중심 좌표, 각도는 반시계 + (도). 정답을 정확히 알도록 다각형 꼭짓점을 직접 변환해 그리고 살짝 흐린다.
        /// </summary>
        private static Image Draw(int w, int h, double fg, double bg, params (double cx, double cy, double deg, double scale)[] shapes)
        {
            using var m = new Mat(h, w, MatType.CV_8UC1, Scalar.All(bg));
            foreach (var (cx, cy, deg, scale) in shapes)
            {
                double a = deg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
                var pts = LPolygon.Select(p => new Point(
                    (int)Math.Round((cx + scale * (c * p.x + s * p.y)) * 16),
                    (int)Math.Round((cy + scale * (-s * p.x + c * p.y)) * 16))).ToArray();
                Cv2.FillPoly(m, new[] { pts }, Scalar.All(fg), LineTypes.AntiAlias, 4);
            }
            using var blurred = new Mat();
            Cv2.GaussianBlur(m, blurred, new Size(0, 0), 1.0);
            return MatInterop.ToImage(blurred);
        }

        /// <summary>템플릿: 81x81, 기준점 (40,40). ROI 박스 중심 = 기준점.</summary>
        private static ShapeModel Model(double angleStart, double angleExtent, double scaleMin = 1, double scaleMax = 1, string metric = "use_polarity")
        {
            using var template = Draw(81, 81, 200, 30, (40, 40, 0, 1));
            using var roi = Region.FromRectangle(new RectI(10, 10, 61, 61));
            return Out<ShapeModel>(Exec("CreateShapeModel", template, roi, 0, angleStart, angleExtent, 0.0, scaleMin, scaleMax, 0.0, metric, 30.0, 10.0), "Model");
        }

        private static (double row, double col, double deg, double scale, double score)[] Find(Image img, ShapeModel model, double minScore = 0.7, int num = 1, BackendKind backend = BackendKind.Cpu)
        {
            var f = Exec("FindShapeModel", backend, img, model, minScore, num, 0.5, 0.9);
            var rows = Out<TupleValue>(f, "Row").ToDoubleArray();
            var cols = Out<TupleValue>(f, "Column").ToDoubleArray();
            var angles = Out<TupleValue>(f, "Angle").ToDoubleArray();
            var scales = Out<TupleValue>(f, "Scale").ToDoubleArray();
            var scores = Out<TupleValue>(f, "Score").ToDoubleArray();
            return rows.Select((r, i) => (r, cols[i], angles[i] * 180 / Math.PI, scales[i], scores[i])).ToArray();
        }

        [Fact]
        public void CreateShapeModel_BuildsPyramidAndContours()
        {
            using var model = Model(0, 0);
            Assert.True(model.NumLevels >= 2, $"levels {model.NumLevels}");
            Assert.True(model.Levels[0].Count > 100);
            Assert.True(model.Levels[1].Count < model.Levels[0].Count);
            Assert.NotEmpty(model.Contours);
            Assert.Equal(61, model.Width);
            Assert.False(model.HasRotation);
        }

        [Theory]
        [InlineData(BackendKind.Cpu)]
        [InlineData(BackendKind.Parallel)]
        public void FindShapeModel_RecoversPositionAndAngle(BackendKind backend)
        {
            using var model = Model(-Math.PI, 2 * Math.PI);
            foreach (var (cx, cy, deg) in new[] { (150.0, 100.0, 0.0), (90.3, 140.6, 37.0), (200.0, 70.0, -120.0) })
            {
                using var img = Draw(300, 220, 200, 30, (cx, cy, deg, 1));
                var m = Find(img, model, backend: backend);
                Assert.Single(m);
                Assert.InRange(m[0].col, cx + 0.5 - 0.6, cx + 0.5 + 0.6);
                Assert.InRange(m[0].row, cy + 0.5 - 0.6, cy + 0.5 + 0.6);
                Assert.InRange(Normalize(m[0].deg - deg), -1.5, 1.5);
                Assert.True(m[0].score > 0.85, $"score {m[0].score} at {deg} deg");
            }
        }

        [Fact]
        public void FindShapeModel_IsSubpixelAccurate()
        {
            using var model = Model(-Math.PI, 2 * Math.PI);
            var rnd = new Random(7);
            double maxPos = 0, maxAng = 0;
            for (int k = 0; k < 8; k++)
            {
                double cx = 100 + rnd.NextDouble() * 100, cy = 80 + rnd.NextDouble() * 60, deg = rnd.NextDouble() * 360 - 180;
                using var img = Draw(300, 220, 200, 30, (cx, cy, deg, 1));
                var m = Find(img, model);
                Assert.Single(m);
                maxPos = Math.Max(maxPos, Math.Max(Math.Abs(m[0].col - (cx + 0.5)), Math.Abs(m[0].row - (cy + 0.5))));
                maxAng = Math.Max(maxAng, Math.Abs(Normalize(m[0].deg - deg)));
            }
            Assert.True(maxPos < 0.25, $"max position error {maxPos:F3} px");
            Assert.True(maxAng < 0.3, $"max angle error {maxAng:F3} deg");
        }

        private static double Normalize(double deg)
        {
            while (deg > 180) deg -= 360;
            while (deg < -180) deg += 360;
            return deg;
        }

        [Fact]
        public void FindShapeModel_IsRobustToContrastChange()
        {
            using var model = Model(0, 0);
            // 대비 1/4, 밝기 이동: 그라디언트 크기가 아니라 방향만 쓰므로 점수가 거의 같다
            using var img = Draw(200, 160, 110, 70, (100, 80, 0, 1));
            var m = Find(img, model);
            Assert.Single(m);
            Assert.True(m[0].score > 0.85, $"score {m[0].score}");
        }

        [Fact]
        public void FindShapeModel_FindsPartlyOccludedShape()
        {
            using var model = Model(0, 0);
            using var img = Draw(200, 160, 200, 30, (100, 80, 0, 1));
            // 오른쪽 아래 막대 일부를 배경과 같은 밝기로 가린다
            using (var mat = MatInterop.AsMat(img, forWrite: true))
                Cv2.Rectangle(mat, new Rect(105, 82, 30, 30), Scalar.All(30), -1);
            var none = Find(img, model, minScore: 0.95);
            Assert.Empty(none);
            var m = Find(img, model, minScore: 0.5);
            Assert.Single(m);
            Assert.InRange(m[0].col, 99.5, 101.5);
            Assert.InRange(m[0].row, 79.5, 81.5);
            Assert.InRange(m[0].score, 0.5, 0.95);
        }

        [Fact]
        public void FindShapeModel_RecoversScale()
        {
            using var model = Model(0, 0, 0.8, 1.25);
            Assert.True(model.HasScale);
            using var img = Draw(220, 180, 200, 30, (110, 90, 0, 1.15));
            var m = Find(img, model);
            Assert.Single(m);
            Assert.InRange(m[0].scale, 1.12, 1.18);
            Assert.InRange(m[0].col, 110.5 - 0.8, 110.5 + 0.8);
        }

        [Fact]
        public void FindShapeModel_FindsAllInstances()
        {
            using var model = Model(-Math.PI / 2, Math.PI);
            using var img = Draw(400, 300, 200, 30, (80, 80, 0, 1), (250, 90, 45, 1), (150, 210, -60, 1));
            var m = Find(img, model, minScore: 0.7, num: 0);
            Assert.Equal(3, m.Length);
            Assert.Contains(m, x => Math.Abs(x.col - 250.5) < 1 && Math.Abs(x.row - 90.5) < 1 && Math.Abs(x.deg - 45) < 2);
            Assert.Contains(m, x => Math.Abs(x.col - 150.5) < 1 && Math.Abs(x.row - 210.5) < 1 && Math.Abs(x.deg + 60) < 2);

            // NumMatches 로 개수 제한 (점수가 높은 것부터)
            Assert.Equal(2, Find(img, model, minScore: 0.7, num: 2).Length);
        }

        [Fact]
        public void Metric_ControlsPolarity()
        {
            // 밝은 바탕에 어두운 L (반전)
            using var inverted = Draw(200, 160, 30, 200, (100, 80, 0, 1));
            using var polar = Model(0, 0);
            Assert.Empty(Find(inverted, polar, minScore: 0.5));

            using var global = Model(0, 0, metric: "ignore_global_polarity");
            var m = Find(inverted, global, minScore: 0.7);
            Assert.Single(m);
            Assert.True(m[0].score > 0.85);
        }

        [Fact]
        public void GetShapeMatchContours_PlacesContoursAtMatch()
        {
            using var model = Model(-Math.PI, 2 * Math.PI);
            using var img = Draw(200, 160, 200, 30, (100, 80, 30, 1));
            var f = Exec("FindShapeModel", img, model, 0.7, 1, 0.5, 0.9);
            var c = Exec("GetShapeMatchContours", model, f.Outputs["Row"], f.Outputs["Column"], f.Outputs["Angle"], f.Outputs["Scale"]);
            using var contours = Out<ObjectArray<XldContour>>(c, "Contours");
            Assert.NotEmpty(contours);
            // 윤곽 점은 모양 경계 근처: 기준점에서 반지름 34 (대각선 24√2) 이내
            foreach (var xld in contours)
                for (int k = 0; k < xld.Count; k++)
                    Assert.InRange(Math.Sqrt(Math.Pow(xld.X[k] - 100, 2) + Math.Pow(xld.Y[k] - 80, 2)), 0, 36);
        }
    }
}
