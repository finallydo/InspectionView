using System;
using System.IO;
using System.Linq;
using InspectionView.Core.Backends;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;
using Xunit;

namespace InspectionView.Core.Tests
{
    public class OperatorBatch2Tests
    {
        private static OperatorResult Exec(string op, params object?[] args)
        {
            var ctx = new OperatorContext(new BackendSettings { DefaultBackend = BackendKind.Cpu }, new ExecutionProfiler());
            var result = OperatorInvoker.Invoke(OperatorRegistry.Default, op, args, ctx);
            Assert.True(result.Success, $"{op} failed: {result.Code} {result.Message}");
            return result;
        }

        private static T Out<T>(OperatorResult r, string name) => Assert.IsType<T>(r.Outputs[name]);

        private static Image Square(int size = 48, int x0 = 10, int y0 = 10, int w = 10, int h = 10, double value = 200)
        {
            var img = Image.Create(size, size, PixelType.UInt8, clear: true);
            for (int y = y0; y < y0 + h; y++)
                for (int x = x0; x < x0 + w; x++)
                    img.SetPixel(x, y, value);
            return img;
        }

        [Fact]
        public void EdgesSubPix_FindsSquareOutline()
        {
            using var img = Square();
            using var edges = Out<ObjectArray<XldContour>>(Exec("EdgesSubPix", img, 1.0, 20, 40), "Edges");

            Assert.True(edges.Count >= 1);
            int points = edges.Sum(c => c.Count);
            Assert.InRange(points, 30, 60);                      // 10x10 사각형 둘레 ≈ 36~44
            foreach (var c in edges)
            {
                var bb = c.BoundingBox();
                Assert.InRange(bb.X, 7, 12);
                Assert.InRange(bb.Right, 17, 22);
            }

            // Domain 밖 에지는 제외
            using var roi = Region.FromRectangle(new RectI(0, 0, 15, 48));
            using var reduced = img.ReduceDomain(roi);
            using var partial = Out<ObjectArray<XldContour>>(Exec("EdgesSubPix", reduced, 1.0, 20, 40), "Edges");
            Assert.True(partial.Sum(c => c.Count) < points);
            Assert.All(partial, c => Assert.True(c.BoundingBox().Right <= 15));
        }

        [Fact]
        public void XldFeatures_Select_Length_AreaCenter()
        {
            using var square = new XldContour(new double[] { 0, 10, 10, 0 }, new double[] { 0, 0, 10, 10 }, closed: true);
            using var line = new XldContour(new double[] { 0, 3 }, new double[] { 0, 4 }, closed: false);
            using var arr = new ObjectArray<XldContour>(new[] { square, line });

            Assert.Equal(new[] { 40.0, 5.0 }, Out<TupleValue>(Exec("LengthXld", arr), "Length").ToDoubleArray());

            var ac = Exec("AreaCenterXld", square);
            Assert.Equal(100.0, Out<TupleValue>(ac, "Area").D(), 6);
            Assert.Equal(5.0, Out<TupleValue>(ac, "Row").D(), 6);
            Assert.Equal(5.0, Out<TupleValue>(ac, "Column").D(), 6);

            using var sel = Out<ObjectArray<XldContour>>(Exec("SelectContoursXld", arr, "Length", 10, 100), "Selected");
            Assert.Single(sel);
            Assert.Same(square, sel[0]);
            using var byCount = Out<ObjectArray<XldContour>>(Exec("SelectContoursXld", arr, "Count", 0, 2), "Selected");
            Assert.Same(line, byCount[0]);
        }

        [Fact]
        public void FitLine_And_FitCircle()
        {
            // row = 2 * col + 1
            var cols = Enumerable.Range(0, 20).Select(i => (double)i).ToArray();
            var rows = cols.Select(c => 2 * c + 1).ToArray();
            using var lineContour = new XldContour(cols, rows);
            var fit = Exec("FitLineContourXld", lineContour);
            double nr = Out<double>(fit, "Nr"), nc = Out<double>(fit, "Nc"), dist = Out<double>(fit, "Dist");
            Assert.Equal(1.0, Math.Sqrt(nr * nr + nc * nc), 6);
            for (int i = 0; i < cols.Length; i++)
                Assert.Equal(0.0, nr * rows[i] + nc * cols[i] + dist, 3);
            Assert.Equal(0.0, Out<double>(fit, "ColBegin"), 3);
            Assert.Equal(19.0, Out<double>(fit, "ColEnd"), 3);
            Assert.Equal(39.0, Out<double>(fit, "RowEnd"), 3);

            // 원: 중심 (row 30, col 40), 반지름 10
            var n = 36;
            var cx = Enumerable.Range(0, n).Select(i => 40 + 10 * Math.Cos(2 * Math.PI * i / n)).ToArray();
            var cy = Enumerable.Range(0, n).Select(i => 30 + 10 * Math.Sin(2 * Math.PI * i / n)).ToArray();
            using var circle = new XldContour(cx, cy, closed: true);
            var cf = Exec("FitCircleContourXld", circle);
            Assert.Equal(30.0, Out<double>(cf, "Row"), 4);
            Assert.Equal(40.0, Out<double>(cf, "Column"), 4);
            Assert.Equal(10.0, Out<double>(cf, "Radius"), 4);

            // 지그재그를 스무딩하면 길이가 줄어든다
            var zx = Enumerable.Range(0, 20).Select(i => (double)i).ToArray();
            var zy = Enumerable.Range(0, 20).Select(i => i % 2 == 0 ? 0.0 : 2.0).ToArray();
            using var zig = new XldContour(zx, zy);
            using var smooth = Out<ObjectArray<XldContour>>(Exec("SmoothContoursXld", zig, 5), "Smoothed");
            Assert.True(smooth[0].Length() < zig.Length());
            Assert.Equal(zig.Count, smooth[0].Count);
        }

        [Fact]
        public void RegionContourConversion()
        {
            using var region = Region.FromRectangle(new RectI(10, 20, 10, 10));
            using var contours = Out<ObjectArray<XldContour>>(Exec("GenContourRegionXld", region), "Contours");
            Assert.Single(contours);
            Assert.True(contours[0].IsClosed);
            Assert.InRange(contours[0].Count, 30, 40);

            using var filled = Out<ObjectArray<Region>>(Exec("GenRegionContourXld", contours, "Filled"), "Regions");
            Assert.Equal(100, filled[0].Area);
            Assert.Equal(region.BoundingBox, filled[0].BoundingBox);

            using var margin = Out<ObjectArray<Region>>(Exec("GenRegionContourXld", contours, "Margin"), "Regions");
            Assert.Equal(36, margin[0].Area);
        }

        [Fact]
        public void MeasurePos_FindsSubPixelEdges()
        {
            // 열 20..29 가 밝은 세로 띠
            using var img = Square(48, 20, 0, 10, 48);

            var m = Exec("MeasurePos", img, 16.0, 16.0, 0.0, 16.0, 2.0, 1.0, 30.0);
            var cols = Out<TupleValue>(m, "ColumnEdge").ToDoubleArray();
            var amps = Out<TupleValue>(m, "Amplitude").ToDoubleArray();
            Assert.Equal(2, cols.Length);
            Assert.InRange(cols[0], 19.2, 19.8);                     // 픽셀 19|20 경계 = 19.5
            Assert.InRange(cols[1], 29.2, 29.8);
            Assert.True(amps[0] > 0 && amps[1] < 0);
            Assert.InRange(Out<TupleValue>(m, "Distance").D(), 9.6, 10.4);
            Assert.All(Out<TupleValue>(m, "RowEdge").ToDoubleArray(), r => Assert.Equal(16.0, r, 6));

            var neg = Exec("MeasurePos", img, 16.0, 16.0, 0.0, 16.0, 2.0, 1.0, 30.0, "Negative");
            Assert.Single(Out<TupleValue>(neg, "ColumnEdge"));
            var first = Exec("MeasurePos", img, 16.0, 16.0, 0.0, 16.0, 2.0, 1.0, 30.0, "All", "First");
            Assert.InRange(Out<TupleValue>(first, "ColumnEdge").D(), 19.2, 19.8);

            // 반대 방향(Phi = pi)으로 재면 29.5 쪽 에지를 먼저 만나고, 각 에지의 부호는 뒤집힌다
            var back = Exec("MeasurePos", img, 16.0, 16.0, Math.PI, 16.0, 2.0, 1.0, 30.0);
            var backCols = Out<TupleValue>(back, "ColumnEdge").ToDoubleArray();
            var backAmps = Out<TupleValue>(back, "Amplitude").ToDoubleArray();
            Assert.Equal(2, backCols.Length);
            Assert.InRange(backCols[0], 29.2, 29.8);
            Assert.InRange(backCols[1], 19.2, 19.8);
            Assert.True(backAmps[0] > 0 && backAmps[1] < 0);

            // 세로 방향 측정(Phi = -pi/2 → 행 증가 방향)은 띠를 가로지르지 않으므로 에지 없음
            var none = Exec("MeasurePos", img, 16.0, 25.0, -Math.PI / 2, 10.0, 1.0, 1.0, 30.0);
            Assert.True(Out<TupleValue>(none, "ColumnEdge").IsEmpty);
        }

        [Fact]
        public void NccModel_CreateAndFind()
        {
            using var img = Square(64, 10, 10, 10, 10);
            for (int y = 30; y < 40; y++)
                for (int x = 40; x < 50; x++)
                    img.SetPixel(x, y, 200);
            for (int y = 34; y < 37; y++)                                // 두 번째 패턴은 가운데 3x3 이 비어 있음
                for (int x = 44; x < 47; x++)
                    img.SetPixel(x, y, 0);

            using var roi = Region.FromRectangle(new RectI(8, 8, 14, 14));
            using var model = Out<NccModel>(Exec("CreateNccModel", img, roi), "Model");
            Assert.Equal(14, model.Width);
            Assert.Equal("NccModel", model.TypeName);

            var f = Exec("FindNccModel", img, model, 0.8, 2, 0.5);
            var rows = Out<TupleValue>(f, "Row").ToDoubleArray();
            var cols = Out<TupleValue>(f, "Column").ToDoubleArray();
            var scores = Out<TupleValue>(f, "Score").ToDoubleArray();
            Assert.Equal(2, rows.Length);
            Assert.Equal(15.0, rows[0], 1);                              // 첫 매치 = 원본 위치 (점수 1.0)
            Assert.Equal(15.0, cols[0], 1);
            Assert.InRange(scores[0], 0.99, 1.0);
            Assert.Equal(35.0, rows[1], 1);
            Assert.Equal(45.0, cols[1], 1);
            Assert.True(scores[1] < scores[0] && scores[1] > 0.8);

            var one = Exec("FindNccModel", img, model, 0.8, 1, 0.5);
            Assert.Single(Out<TupleValue>(one, "Row"));
            var strict = Exec("FindNccModel", img, model, 0.995, 0, 0.5);
            Assert.Single(Out<TupleValue>(strict, "Row"));

            // 16bit 탐색 이미지에서도 동작 (깊이 자동 변환)
            using var img16 = Out<Image>(Exec("ConvertImageType", img, "UInt16"), "ImageResult");
            var f16 = Exec("FindNccModel", img16, model, 0.8, 1, 0.5);
            Assert.Equal(15.0, Out<TupleValue>(f16, "Row").D(), 1);
        }

        [Fact]
        public void ListFiles_And_FileExists()
        {
            var dir = Path.Combine(Path.GetTempPath(), "iv_files_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "b.png"), "");
                File.WriteAllText(Path.Combine(dir, "a.png"), "");
                File.WriteAllText(Path.Combine(dir, "c.txt"), "");

                var files = Out<TupleValue>(Exec("ListFiles", dir, "*.png"), "Files").ToStringArray();
                Assert.Equal(2, files.Length);
                Assert.EndsWith("a.png", files[0]);
                Assert.EndsWith("b.png", files[1]);
                Assert.Equal(3, Out<TupleValue>(Exec("ListFiles", dir), "Files").Length);
                Assert.True(Out<TupleValue>(Exec("ListFiles", dir, "*.bmp"), "Files").IsEmpty);

                Assert.True(Out<bool>(Exec("FileExists", files[0]), "Exists"));
                Assert.False(Out<bool>(Exec("FileExists", Path.Combine(dir, "nope.png")), "Exists"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
