using System;
using System.Linq;
using InspectionView.Core.Backends;
using InspectionView.Core.Operators.Filters;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;
using Xunit;
using ErrorCode = InspectionView.Core.Errors.ErrorCode;

namespace InspectionView.Core.Tests
{
    public class OperatorBatch1Tests
    {
        private static OperatorResult Exec(string op, params object?[] args)
        {
            var ctx = new OperatorContext(new BackendSettings { DefaultBackend = BackendKind.Cpu }, new ExecutionProfiler());
            var result = OperatorInvoker.Invoke(OperatorRegistry.Default, op, args, ctx);
            Assert.True(result.Success, $"{op} failed: {result.Code} {result.Message}");
            return result;
        }

        private static T Out<T>(OperatorResult r, string name) => Assert.IsType<T>(r.Outputs[name]);

        private static Image Gray(int w, int h, double value = 0)
        {
            var img = Image.Create(w, h, PixelType.UInt8, clear: true);
            if (value != 0)
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        img.SetPixel(x, y, value);
            return img;
        }

        private static Image Square(int size = 32, int x0 = 10, int y0 = 10, int side = 10, double value = 200)
        {
            var img = Gray(size, size);
            for (int y = y0; y < y0 + side; y++)
                for (int x = x0; x < x0 + side; x++)
                    img.SetPixel(x, y, value);
            return img;
        }

        [Fact]
        public void Registry_ContainsBatch1Operators()
        {
            var names = new[]
            {
                "GaussFilter", "MeanFilter", "MedianFilter", "SobelAmp", "Emphasize", "ScaleImage", "InvertImage", "ConvertImageType", "Rgb1ToGray", "Decompose3", "Compose3",
                "AddImage", "SubImage", "MultImage", "AbsDiffImage", "MinImage", "MaxImage",
                "ErosionRectangle1", "DilationRectangle1", "OpeningRectangle1", "ClosingRectangle1", "ErosionCircle", "DilationCircle", "OpeningCircle", "ClosingCircle",
                "FillUp", "Boundary", "GrayErosionRect", "GrayDilationRect", "GrayOpeningRect", "GrayClosingRect", "GrayTopHat", "GrayBlackHat",
                "Union1", "Union2", "Intersection", "Difference", "Complement", "CountObj", "SelectObj", "AreaCenter", "SmallestRectangle1", "RegionFeatures",
                "GenCircle", "GenRectangle2", "ShapeTrans", "PaintRegion", "RegionToBin", "Intensity", "MinMaxGray", "DynThreshold",
            };
            foreach (var n in names)
                Assert.True(OperatorRegistry.Default.Contains(n), $"missing {n}");

            // 기반 클래스의 입력/출력/백엔드가 파생 연산자에 상속된다
            var d = OperatorRegistry.Default.Get("GaussFilter");
            Assert.Equal(new[] { "Image", "Sigma" }, d.Inputs.Select(p => p.Name).ToArray());
            Assert.Equal("ImageResult", d.Outputs.Single().Name);
            Assert.True(d.HasBackend(BackendKind.Cpu));
        }

        [Fact]
        public void Filters_KeepSizeTypeAndDomain()
        {
            using var img = Square();
            using var roi = Region.FromRectangle(new RectI(0, 0, 16, 16));
            using var reduced = img.ReduceDomain(roi);

            using var g = Out<Image>(Exec("GaussFilter", reduced, 1.5), "ImageResult");
            Assert.Equal(img.Width, g.Width);
            Assert.Equal(PixelType.UInt8, g.PixelType);
            Assert.Equal(new RectI(0, 0, 16, 16), g.DomainBounds);
            Assert.True(g.GetPixel(15, 15) > 0 && g.GetPixel(15, 15) < 200);   // 경계가 흐려짐
            Assert.Equal(0, g.GetPixel(0, 0));

            using var m = Out<Image>(Exec("MeanFilter", img, 3, 3), "ImageResult");
            Assert.Equal(200, m.GetPixel(14, 14));
            Assert.True(m.GetPixel(10, 10) < 200);

            img.SetPixel(2, 2, 255);                                          // 노이즈 한 점
            using var med = Out<Image>(Exec("MedianFilter", img, 3), "ImageResult");
            Assert.Equal(0, med.GetPixel(2, 2));
            Assert.Equal(200, med.GetPixel(14, 14));

            var bad = OperatorInvoker.Invoke(OperatorRegistry.Default, "MedianFilter", new object?[] { img, 4 });
            Assert.Equal(ErrorCode.InvalidArgument, bad.Code);
        }

        [Fact]
        public void Sobel_Emphasize_Scale_Invert_Convert()
        {
            using var img = Square();

            using var sob = Out<Image>(Exec("SobelAmp", img, SobelMode.SumAbs, 3), "ImageResult");
            Assert.Equal(0, sob.GetPixel(14, 14));          // 평탄 영역
            Assert.True(sob.GetPixel(10, 14) > 100);        // 수직 에지
            using var sobReal = Out<Image>(Exec("SobelAmp", img, "Sqrt", 3, true), "ImageResult");
            Assert.Equal(PixelType.Float32, sobReal.PixelType);

            using var emp = Out<Image>(Exec("Emphasize", img, 7, 7, 1.0), "ImageResult");
            Assert.True(emp.GetPixel(10, 14) >= 200);        // 에지 안쪽이 강조됨
            Assert.Equal(0, emp.GetPixel(0, 0));

            using var sc = Out<Image>(Exec("ScaleImage", img, 0.5, 10), "ImageResult");
            Assert.Equal(110, sc.GetPixel(14, 14));
            Assert.Equal(10, sc.GetPixel(0, 0));

            using var inv = Out<Image>(Exec("InvertImage", img), "ImageResult");
            Assert.Equal(55, inv.GetPixel(14, 14));
            Assert.Equal(255, inv.GetPixel(0, 0));

            using var f = Out<Image>(Exec("ConvertImageType", img, "Float32"), "ImageResult");
            Assert.Equal(PixelType.Float32, f.PixelType);
            Assert.Equal(200.0, f.GetPixel(14, 14));
            using var u16 = Out<Image>(Exec("ConvertImageType", f, PixelType.UInt16), "ImageResult");
            Assert.Equal(200, u16.GetPixel(14, 14));
        }

        [Fact]
        public void Color_Decompose_Compose_Gray()
        {
            using var color = Image.Create(8, 8, PixelType.UInt8, channels: 3, clear: true);
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    color.SetPixel(x, y, 10, 0);   // B
                    color.SetPixel(x, y, 20, 1);   // G
                    color.SetPixel(x, y, 30, 2);   // R
                }

            var dec = Exec("Decompose3", color);
            using var b = Out<Image>(dec, "Image1");
            using var g = Out<Image>(dec, "Image2");
            using var r = Out<Image>(dec, "Image3");
            Assert.Equal(10, b.GetPixel(3, 3));
            Assert.Equal(20, g.GetPixel(3, 3));
            Assert.Equal(30, r.GetPixel(3, 3));
            Assert.Equal(1, b.Channels);

            using var back = Out<Image>(Exec("Compose3", b, g, r), "ImageResult");
            Assert.Equal(3, back.Channels);
            Assert.Equal(30, back.GetPixel(3, 3, 2));

            using var gray = Out<Image>(Exec("Rgb1ToGray", color), "ImageResult");
            Assert.Equal(1, gray.Channels);
            Assert.InRange(gray.GetPixel(3, 3), 19, 23);    // 0.299*30 + 0.587*20 + 0.114*10 = 21.8

            Assert.Equal(ErrorCode.TypeMismatch, OperatorInvoker.Invoke(OperatorRegistry.Default, "Rgb1ToGray", new object?[] { b }).Code);
        }

        [Fact]
        public void Arithmetic_Operators()
        {
            using var a = Gray(8, 8, 100);
            using var b = Gray(8, 8, 30);

            Assert.Equal(65, Out<Image>(Exec("AddImage", a, b, 0.5, 0), "ImageResult").GetPixel(1, 1));
            Assert.Equal(70, Out<Image>(Exec("SubImage", a, b, 1.0, 0), "ImageResult").GetPixel(1, 1));
            Assert.Equal(0, Out<Image>(Exec("SubImage", b, a, 1.0, 0), "ImageResult").GetPixel(1, 1));      // 포화
            Assert.Equal(70, Out<Image>(Exec("AbsDiffImage", b, a), "ImageResult").GetPixel(1, 1));
            Assert.Equal(30, Out<Image>(Exec("MinImage", a, b), "ImageResult").GetPixel(1, 1));
            Assert.Equal(100, Out<Image>(Exec("MaxImage", a, b), "ImageResult").GetPixel(1, 1));
            Assert.Equal(30, Out<Image>(Exec("MultImage", a, b, 0.01, 0), "ImageResult").GetPixel(1, 1));

            using var c = Gray(4, 4, 1);
            Assert.Equal(ErrorCode.SizeMismatch, OperatorInvoker.Invoke(OperatorRegistry.Default, "AddImage", new object?[] { a, c }).Code);
        }

        [Fact]
        public void RegionMorphology()
        {
            using var sq = Region.FromRectangle(new RectI(10, 10, 10, 10));

            Assert.Equal(64, Out<Region>(Exec("ErosionRectangle1", sq, 3, 3), "RegionResult").Area);
            using var dil = Out<Region>(Exec("DilationRectangle1", sq, 3, 3), "RegionResult");
            Assert.Equal(144, dil.Area);
            Assert.Equal(new RectI(9, 9, 12, 12), dil.BoundingBox);

            // 1픽셀 돌기는 열림으로 사라진다
            using var spur = Region.FromRectangle(new RectI(20, 15, 5, 1));
            using var withSpur = sq.Union(spur);
            Assert.Equal(100, Out<Region>(Exec("OpeningRectangle1", withSpur, 3, 3), "RegionResult").Area);

            // 1픽셀 떨어진 두 사각형은 닫힘으로 연결된다
            using var sq2 = Region.FromRectangle(new RectI(21, 10, 10, 10));
            using var two = sq.Union(sq2);
            using var closed = Out<Region>(Exec("ClosingRectangle1", two, 3, 3), "RegionResult");
            Assert.Equal(210, closed.Area);

            using var ero = Out<Region>(Exec("ErosionCircle", sq, 1.5), "RegionResult");
            Assert.True(ero.Area < 100 && ero.Area > 0);
            using var dilc = Out<Region>(Exec("DilationCircle", sq, 1.5), "RegionResult");
            Assert.True(dilc.Area > 100);
            Assert.True(Out<Region>(Exec("OpeningCircle", sq, 1.5), "RegionResult").Area <= 100);
            Assert.True(Out<Region>(Exec("ClosingCircle", sq, 1.5), "RegionResult").Area >= 100);

            // 구멍 채우기
            using var hole = Region.FromRectangle(new RectI(13, 13, 4, 4));
            using var ring = sq.Difference(hole);
            Assert.Equal(84, ring.Area);
            Assert.Equal(100, Out<Region>(Exec("FillUp", ring), "RegionResult").Area);

            Assert.Equal(36, Out<Region>(Exec("Boundary", sq, "Inner"), "RegionResult").Area);
            Assert.Equal(44, Out<Region>(Exec("Boundary", sq, "Outer"), "RegionResult").Area);

            Assert.True(Out<Region>(Exec("ErosionRectangle1", Region.Empty, 3, 3), "RegionResult").IsEmpty);
        }

        [Fact]
        public void GrayMorphology()
        {
            using var img = Gray(16, 16);
            img.SetPixel(8, 8, 200);

            using var dil = Out<Image>(Exec("GrayDilationRect", img, 3, 3), "ImageResult");
            int count = 0;
            for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) if (dil.GetPixel(x, y) == 200) count++;
            Assert.Equal(9, count);

            using var ero = Out<Image>(Exec("GrayErosionRect", img, 3, 3), "ImageResult");
            Assert.Equal(0, ero.GetPixel(8, 8));

            using var open = Out<Image>(Exec("GrayOpeningRect", img, 3, 3), "ImageResult");
            Assert.Equal(0, open.GetPixel(8, 8));                 // 점은 열림으로 사라짐
            using var top = Out<Image>(Exec("GrayTopHat", img, 3, 3), "ImageResult");
            Assert.Equal(200, top.GetPixel(8, 8));                // 그리고 탑햇에 남는다
            Assert.Equal(0, Out<Image>(Exec("GrayBlackHat", img, 3, 3), "ImageResult").GetPixel(8, 8));
            Assert.Equal(200, Out<Image>(Exec("GrayClosingRect", img, 3, 3), "ImageResult").GetPixel(8, 8));
        }

        [Fact]
        public void RegionSets_Count_Select()
        {
            using var a = Region.FromRectangle(new RectI(0, 0, 10, 10));
            using var b = Region.FromRectangle(new RectI(5, 5, 10, 10));
            using var arr = new ObjectArray<Region>(new[] { a, b });

            Assert.Equal(175, Out<Region>(Exec("Union1", arr), "RegionUnion").Area);
            Assert.Equal(175, Out<Region>(Exec("Union2", a, b), "RegionUnion").Area);
            Assert.Equal(25, Out<Region>(Exec("Intersection", a, b), "RegionIntersection").Area);
            Assert.Equal(75, Out<Region>(Exec("Difference", a, b), "RegionDifference").Area);
            Assert.Equal(400 - 100, Out<Region>(Exec("Complement", a, 20, 20), "RegionComplement").Area);
            Assert.Equal(2L, Assert.IsType<long>(Exec("CountObj", arr).Outputs["Number"]));

            using (var sel = Out<Region>(Exec("SelectObj", arr, 1), "RegionSelected"))
            {
                Assert.Same(b, sel);
                Assert.Equal(3, b.RefCount);     // b + arr + 출력 참조
            }
            Assert.Equal(2, b.RefCount);
            Assert.Equal(ErrorCode.OutOfRange, OperatorInvoker.Invoke(OperatorRegistry.Default, "SelectObj", new object?[] { arr, 5 }).Code);
        }

        [Fact]
        public void RegionFeatures_AreaCenter_SmallestRectangle()
        {
            using var a = Region.FromRectangle(new RectI(10, 20, 5, 5));
            using var b = Region.FromRectangle(new RectI(0, 0, 2, 3));
            using var arr = new ObjectArray<Region>(new[] { a, b });

            var ac = Exec("AreaCenter", arr);
            Assert.Equal(new long[] { 25, 6 }, Out<TupleValue>(ac, "Area").ToLongArray());
            Assert.Equal(new[] { 22.0, 1.0 }, Out<TupleValue>(ac, "Row").ToDoubleArray());
            Assert.Equal(new[] { 12.0, 0.5 }, Out<TupleValue>(ac, "Column").ToDoubleArray());

            var sr = Exec("SmallestRectangle1", a);               // 단일 Region 자동 래핑
            Assert.Equal(20L, Out<TupleValue>(sr, "Row1").L());
            Assert.Equal(14L, Out<TupleValue>(sr, "Column2").L());

            Assert.Equal(new[] { 5.0, 2.0 }, Out<TupleValue>(Exec("RegionFeatures", arr, "Width"), "Value").ToDoubleArray());

            // 스크립트 스타일: 결과 객체 멤버
            Assert.Equal(3, ac.Outputs.Count);
            var res = Assert.IsType<ResultObject>(ac.ReturnValue);
            Assert.Equal(25L, Assert.IsType<TupleValue>(res["Area"]).L(0));
        }

        [Fact]
        public void Generators_And_ShapeTrans()
        {
            using var circle = Out<Region>(Exec("GenCircle", 50.0, 60.0, 10.0), "Region");
            Assert.InRange(circle.Area, 300, 330);                   // pi * 100 ≈ 314
            Assert.True(circle.Contains(60, 50));
            Assert.False(circle.Contains(71, 50));
            Assert.Equal(new RectI(50, 40, 20, 20), circle.BoundingBox);

            using var rect = Out<Region>(Exec("GenRectangle2", 50.0, 60.0, 0.0, 10.0, 4.0), "Region");
            Assert.InRange(rect.Area, 150, 190);                     // 20 x 8 = 160
            Assert.True(rect.BoundingBox.Width > rect.BoundingBox.Height);
            using var rot = Out<Region>(Exec("GenRectangle2", 50.0, 60.0, Math.PI / 2, 10.0, 4.0), "Region");
            Assert.True(rot.BoundingBox.Height > rot.BoundingBox.Width);

            // L 자 모양 → 볼록 껍질은 더 크고, 바운딩 박스는 더 크고, 외접원은 모두 포함
            using var l1 = Region.FromRectangle(new RectI(10, 10, 20, 5));
            using var l2 = Region.FromRectangle(new RectI(10, 10, 5, 20));
            using var l = l1.Union(l2);
            using var convex = Out<ObjectArray<Region>>(Exec("ShapeTrans", l, "Convex"), "RegionsTrans");
            Assert.Single(convex);
            Assert.True(convex[0].Area > l.Area);
            Assert.True(convex[0].Intersection(l).Area == l.Area);
            using var r1 = Out<ObjectArray<Region>>(Exec("ShapeTrans", l, "Rectangle1"), "RegionsTrans");
            Assert.Equal(400, r1[0].Area);
            using var oc = Out<ObjectArray<Region>>(Exec("ShapeTrans", l, "OuterCircle"), "RegionsTrans");
            Assert.True(oc[0].Intersection(l).Area == l.Area);
            using var r2 = Out<ObjectArray<Region>>(Exec("ShapeTrans", l1, "Rectangle2"), "RegionsTrans");
            Assert.InRange(r2[0].Area, 100, 130);
        }

        [Fact]
        public void PaintRegion_RegionToBin_Intensity_MinMax()
        {
            using var img = Square();
            using var roi = Region.FromRectangle(new RectI(0, 0, 5, 5));

            using var painted = Out<Image>(Exec("PaintRegion", img, roi, 77), "ImageResult");
            Assert.Equal(77, painted.GetPixel(2, 2));
            Assert.Equal(0, img.GetPixel(2, 2));                   // 원본은 그대로

            using var bin = Out<Image>(Exec("RegionToBin", roi, 255, 10, 8, 8), "BinImage");
            Assert.Equal(255, bin.GetPixel(4, 4));
            Assert.Equal(10, bin.GetPixel(7, 7));

            var it = Exec("Intensity", img);
            Assert.Equal(200.0 * 100 / 1024, Out<TupleValue>(it, "Mean").D(), 3);
            using var sq = Region.FromRectangle(new RectI(10, 10, 10, 10));
            Assert.Equal(200.0, Out<TupleValue>(Exec("Intensity", img, sq), "Mean").D(), 6);
            Assert.Equal(0.0, Out<TupleValue>(Exec("Intensity", img, sq), "Deviation").D(), 6);

            var mm = Exec("MinMaxGray", img);
            Assert.Equal(0.0, Out<TupleValue>(mm, "Min").D());
            Assert.Equal(200.0, Out<TupleValue>(mm, "Max").D());
            Assert.Equal(200.0, Out<TupleValue>(mm, "Range").D());
            Assert.Equal(200.0, Out<TupleValue>(Exec("MinMaxGray", img, sq), "Min").D());
        }

        [Fact]
        public void DynThreshold_LightAndDark()
        {
            using var img = Square();
            img.SetPixel(2, 2, 60);
            using var mean = Out<Image>(Exec("MeanFilter", img, 9, 9), "ImageResult");

            using var light = Out<Region>(Exec("DynThreshold", img, mean, 20, "Light"), "Region");
            Assert.True(light.Contains(14, 14));
            Assert.True(light.Contains(2, 2));
            Assert.False(light.Contains(0, 0));

            using var dark = Out<Region>(Exec("DynThreshold", img, mean, 20, "Dark"), "Region");
            Assert.False(dark.Contains(14, 14));
            Assert.True(dark.Contains(9, 14));                       // 사각형 바로 바깥은 평균보다 어둡다

            using var roi = Region.FromRectangle(new RectI(0, 0, 8, 8));
            using var reduced = img.ReduceDomain(roi);
            using var inRoi = Out<Region>(Exec("DynThreshold", reduced, mean, 20, "Light"), "Region");
            Assert.True(inRoi.Contains(2, 2));
            Assert.False(inRoi.Contains(14, 14));
        }
    }
}
