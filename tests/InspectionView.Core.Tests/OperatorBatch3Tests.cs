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
    /// <summary>MeasurePairs, 회전/스케일 NCC 매칭.</summary>
    public class OperatorBatch3Tests
    {
        private static OperatorResult Exec(string op, params object?[] args)
        {
            var ctx = new OperatorContext(new BackendSettings { DefaultBackend = BackendKind.Cpu }, new ExecutionProfiler());
            var result = OperatorInvoker.Invoke(OperatorRegistry.Default, op, args, ctx);
            Assert.True(result.Success, $"{op} failed: {result.Code} {result.Message}");
            return result;
        }

        private static T Out<T>(OperatorResult r, string name) => Assert.IsType<T>(r.Outputs[name]);

        private static Image Stripes(int size, params (int from, int to)[] brightColumns)
        {
            var img = Image.Create(size, size, PixelType.UInt8, 1, clear: true);
            foreach (var (from, to) in brightColumns)
                for (int y = 0; y < size; y++)
                    for (int x = from; x <= to; x++)
                        img.SetPixel(x, y, 200);
            return img;
        }

        [Fact]
        public void MeasurePairs_FindsOppositePolarityPairs()
        {
            using var one = Stripes(48, (20, 29));
            var m = Exec("MeasurePairs", one, 16.0, 16.0, 0.0, 16.0, 2.0, 1.0, 30.0);
            var c1 = Out<TupleValue>(m, "ColumnEdgeFirst").ToDoubleArray();
            var c2 = Out<TupleValue>(m, "ColumnEdgeSecond").ToDoubleArray();
            Assert.Single(c1);
            Assert.InRange(c1[0], 19.2, 19.8);
            Assert.InRange(c2[0], 29.2, 29.8);
            Assert.True(Out<TupleValue>(m, "AmplitudeFirst").D() > 0);
            Assert.True(Out<TupleValue>(m, "AmplitudeSecond").D() < 0);
            Assert.InRange(Out<TupleValue>(m, "IntraDistance").D(), 9.6, 10.4);
            Assert.True(Out<TupleValue>(m, "InterDistance").IsEmpty);
            Assert.Equal(16.0, Out<TupleValue>(m, "RowEdgeFirst").D(), 6);

            // 어두워지는 에지로 시작하는 쌍은 없다 (29.5 뒤에 밝아지는 에지가 없음)
            var neg = Exec("MeasurePairs", one, 16.0, 16.0, 0.0, 16.0, 2.0, 1.0, 30.0, "Negative");
            Assert.True(Out<TupleValue>(neg, "ColumnEdgeFirst").IsEmpty);

            using var two = Stripes(48, (10, 14), (24, 28));
            var p = Exec("MeasurePairs", two, 20.0, 20.0, 0.0, 19.0, 2.0, 1.0, 30.0, "Positive");
            var f = Out<TupleValue>(p, "ColumnEdgeFirst").ToDoubleArray();
            var s = Out<TupleValue>(p, "ColumnEdgeSecond").ToDoubleArray();
            Assert.Equal(2, f.Length);
            Assert.InRange(f[0], 9.2, 9.8); Assert.InRange(s[0], 14.2, 14.8);
            Assert.InRange(f[1], 23.2, 23.8); Assert.InRange(s[1], 28.2, 28.8);
            var intra = Out<TupleValue>(p, "IntraDistance").ToDoubleArray();
            Assert.All(intra, d => Assert.InRange(d, 4.6, 5.4));
            Assert.InRange(Out<TupleValue>(p, "InterDistance").D(), 13.6, 14.4);

            var last = Exec("MeasurePairs", two, 20.0, 20.0, 0.0, 19.0, 2.0, 1.0, 30.0, "Positive", "Last");
            Assert.InRange(Out<TupleValue>(last, "ColumnEdgeFirst").D(), 23.2, 23.8);
        }

        /// <summary>64x64, 중심 (32,32) 에 24x24 'L' 모양 (비대칭이라 각도가 정해진다). 반 픽셀 정렬 차이에 덜 민감하도록 살짝 흐림.</summary>
        private static Image LShape()
        {
            using var img = Image.Create(64, 64, PixelType.UInt8, 1, clear: true);
            for (int y = 20; y < 44; y++)
                for (int x = 20; x < 44; x++)
                {
                    bool bar = x < 28 || y >= 36;
                    img.SetPixel(x, y, bar ? 200 : 30);
                }
            return Out<Image>(Exec("GaussFilter", img, 1.0), "ImageResult");
        }

        private static Image Rotate(Image src, double degrees, double scale = 1.0)
        {
            using var m = MatInterop.AsMat(src);
            using var rot = Cv2.GetRotationMatrix2D(new Point2f(32, 32), degrees, scale);
            using var dst = new Mat();
            Cv2.WarpAffine(m, dst, rot, new Size(64, 64), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
            return MatInterop.ToImage(dst);
        }

        [Fact]
        public void FindNccModel_WithRotationRange_RecoversAngleAndPosition()
        {
            using var template = LShape();
            using var roi = Region.FromRectangle(new RectI(20, 20, 24, 24));
            double deg5 = 5 * Math.PI / 180;
            using var model = Out<NccModel>(Exec("CreateNccModel", template, roi, -Math.PI / 3, 2 * Math.PI / 3, deg5), "Model");
            Assert.True(model.HasRotation);
            Assert.False(model.HasScale);
            Assert.Equal(25, model.Angles().Length);

            foreach (double deg in new[] { 30.0, -20.0, 0.0 })
            {
                using var search = Rotate(template, deg);
                var f = Exec("FindNccModel", search, model, 0.6, 1, 0.5);
                var rows = Out<TupleValue>(f, "Row").ToDoubleArray();
                Assert.Single(rows);
                Assert.InRange(rows[0], 30.5, 33.5);
                Assert.InRange(Out<TupleValue>(f, "Column").D(), 30.5, 33.5);
                double angle = Out<TupleValue>(f, "Angle").D() * 180 / Math.PI;
                Assert.InRange(angle, deg - 4, deg + 4);
                Assert.Equal(1.0, Out<TupleValue>(f, "Scale").D());
                Assert.True(Out<TupleValue>(f, "Score").D() > 0.8, $"score {Out<TupleValue>(f, "Score").D()} at {deg} deg");
            }

            // 회전 범위 밖(90도)은 점수가 낮아 찾지 못해야 한다
            using var far = Rotate(template, 90);
            var none = Exec("FindNccModel", far, model, 0.9, 1, 0.5);
            Assert.True(Out<TupleValue>(none, "Row").IsEmpty);
        }

        [Fact]
        public void FindNccModel_WithScaleRange()
        {
            using var template = LShape();
            using var roi = Region.FromRectangle(new RectI(20, 20, 24, 24));
            using var model = Out<NccModel>(Exec("CreateNccModel", template, roi, 0.0, 0.0, 0.0, 0.8, 1.2, 0.1), "Model");
            Assert.True(model.HasScale);
            Assert.Equal(5, model.Scales().Length);

            using var search = Rotate(template, 0, 1.2);

            // 진단: 스케일을 고정한 모델들의 점수 (1.2 가 가장 높아야 한다)
            var diag = new System.Text.StringBuilder();
            foreach (double sc in new[] { 0.8, 1.0, 1.2 })
            {
                using var fixedModel = Out<NccModel>(Exec("CreateNccModel", template, roi, 0.0, 0.0, 0.0, sc, sc, 0.0), "Model");
                var ff = Exec("FindNccModel", search, fixedModel, 0.0, 1, 0.5);
                var sco = Out<TupleValue>(ff, "Score");
                diag.Append($"scale {sc}: score {(sco.IsEmpty ? "none" : sco.D().ToString("F4"))} at ({(sco.IsEmpty ? "-" : Out<TupleValue>(ff, "Row").D().ToString("F2"))},{(sco.IsEmpty ? "-" : Out<TupleValue>(ff, "Column").D().ToString("F2"))}); ");
            }

            var f = Exec("FindNccModel", search, model, 0.6, 1, 0.5);
            Assert.Single(Out<TupleValue>(f, "Row"));
            Assert.True(Math.Abs(Out<TupleValue>(f, "Scale").D() - 1.2) < 0.05, $"scale {Out<TupleValue>(f, "Scale").D()} score {Out<TupleValue>(f, "Score").D():F4}; {diag}");
            Assert.InRange(Out<TupleValue>(f, "Row").D(), 30.5, 33.5);
            Assert.InRange(Out<TupleValue>(f, "Column").D(), 30.5, 33.5);
        }
    }
}
