using System;
using System.IO;
using System.Linq;
using InspectionView.Core.Backends;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;
using Xunit;
using ErrorCode = InspectionView.Core.Errors.ErrorCode;

namespace InspectionView.Core.Tests
{
    public class OperatorTests
    {
        private static OperatorContext Ctx(BackendKind backend)
            => new OperatorContext(new BackendSettings { DefaultBackend = backend }, new ExecutionProfiler());

        private static OperatorResult Exec(string op, BackendKind backend, params object?[] args)
        {
            var result = OperatorInvoker.Invoke(OperatorRegistry.Default, op, args, Ctx(backend));
            Assert.True(result.Success, $"{op} failed: {result.Code} {result.Message}");
            return result;
        }

        /// <summary>두 개의 밝은 사각형(10x10 @ (5,5), 20x5 @ (40,30)) 과 대각선으로만 붙은 픽셀 하나가 있는 64x64 그레이 이미지.</summary>
        private static Image MakeTestImage(PixelType type = PixelType.UInt8)
        {
            var img = Image.Create(64, 64, type, clear: true);
            for (int y = 5; y < 15; y++)
                for (int x = 5; x < 15; x++)
                    img.SetPixel(x, y, 200);
            for (int y = 30; y < 35; y++)
                for (int x = 40; x < 60; x++)
                    img.SetPixel(x, y, 200);
            img.SetPixel(15, 15, 200);   // 첫 사각형의 오른쪽 아래 모서리에 대각선으로 접함
            return img;
        }

        [Fact]
        public void Registry_ContainsAllFirstStageOperators()
        {
            var reg = OperatorRegistry.Default;
            foreach (var name in new[] { "ReadImage", "WriteImage", "Threshold", "Connection", "SelectShape", "ReduceDomain", "CropPart" })
                Assert.True(reg.Contains(name), $"missing operator {name}");

            Assert.True(reg.Get("Threshold").HasBackend(BackendKind.Parallel));
            Assert.True(reg.Get("CropPart").HasBackend(BackendKind.Parallel));
            Assert.True(reg.Get("SelectShape").HasBackend(BackendKind.Parallel));
        }

        [Theory]
        [InlineData(BackendKind.Cpu)]
        [InlineData(BackendKind.Parallel)]
        public void Threshold_SelectsExpectedPixels(BackendKind backend)
        {
            using var img = MakeTestImage();
            var result = Exec("Threshold", backend, img, 128, 255);
            using var region = Assert.IsType<Region>(result.ReturnValue);

            Assert.Equal(100 + 100 + 1, region.Area);
            Assert.True(region.Contains(5, 5));
            Assert.True(region.Contains(15, 15));
            Assert.False(region.Contains(4, 5));
            Assert.Equal(new RectI(5, 5, 55, 30), region.BoundingBox);

            if (backend == BackendKind.Parallel && Environment.ProcessorCount > 1)
                Assert.Equal(BackendKind.Parallel, result.Record!.Used);
        }

        [Fact]
        public void Threshold_RespectsDomainAndPixelTypes()
        {
            using var img = MakeTestImage(PixelType.UInt16);
            using var roi = Region.FromRectangle(new RectI(0, 0, 32, 32));
            using var reduced = img.ReduceDomain(roi);

            var result = Exec("Threshold", BackendKind.Cpu, reduced, 100.5, 1000);
            using var region = Assert.IsType<Region>(result.ReturnValue);

            Assert.Equal(101, region.Area);          // 두 번째 사각형은 domain 밖
            Assert.False(region.Contains(40, 30));

            // 범위가 타입 범위 밖이면 빈 영역
            var none = Exec("Threshold", BackendKind.Cpu, img, 70000, 80000);
            using var empty = Assert.IsType<Region>(none.ReturnValue);
            Assert.True(empty.IsEmpty);
        }

        [Fact]
        public void Threshold_ParallelMatchesCpu_OnLargeImage()
        {
            using var img = Image.Create(300, 517, PixelType.UInt8, clear: true);
            var rnd = new Random(1234);
            for (int y = 0; y < img.Height; y++)
            {
                var row = img.GetRow<byte>(y);
                for (int x = 0; x < row.Length; x++)
                    row[x] = (byte)rnd.Next(256);
            }

            using var a = (Region)Exec("Threshold", BackendKind.Cpu, img, 100, 150).ReturnValue!;
            using var b = (Region)Exec("Threshold", BackendKind.Parallel, img, 100, 150).ReturnValue!;

            Assert.Equal(a.Area, b.Area);
            Assert.Equal(a.Runs.ToArray(), b.Runs.ToArray());
        }

        [Fact]
        public void Connection_SplitsComponents_8And4()
        {
            using var img = MakeTestImage();
            using var region = (Region)Exec("Threshold", BackendKind.Cpu, img, 128, 255).ReturnValue!;

            using var c8 = Assert.IsType<ObjectArray<Region>>(Exec("Connection", BackendKind.Cpu, region).ReturnValue);
            Assert.Equal(2, c8.Count);
            Assert.Equal(101, c8[0].Area);           // 대각선 픽셀이 붙음, 행 우선 순서
            Assert.Equal(100, c8[1].Area);

            using var c4 = Assert.IsType<ObjectArray<Region>>(Exec("Connection", BackendKind.Cpu, region, 4).ReturnValue);
            Assert.Equal(3, c4.Count);
            Assert.Equal(100, c4[0].Area);
            Assert.Equal(1, c4[1].Area);
            Assert.Equal(100, c4[2].Area);

            var bad = OperatorInvoker.Invoke(OperatorRegistry.Default, "Connection", new object?[] { region, 6 });
            Assert.Equal(ErrorCode.InvalidArgument, bad.Code);
        }

        [Theory]
        [InlineData(BackendKind.Cpu)]
        [InlineData(BackendKind.Parallel)]
        public void SelectShape_FiltersByFeature(BackendKind backend)
        {
            using var img = MakeTestImage();
            using var region = (Region)Exec("Threshold", BackendKind.Cpu, img, 128, 255).ReturnValue!;
            using var parts = (ObjectArray<Region>)Exec("Connection", BackendKind.Cpu, region, 4).ReturnValue!;

            using var big = Assert.IsType<ObjectArray<Region>>(Exec("SelectShape", backend, parts, "Area", 50, 1000).ReturnValue);
            Assert.Equal(2, big.Count);

            using var wide = Assert.IsType<ObjectArray<Region>>(Exec("SelectShape", backend, parts, ShapeFeature.Width, 15, 100).ReturnValue);
            Assert.Single(wide);
            Assert.Equal(new RectI(40, 30, 20, 5), wide[0].BoundingBox);

            // 단일 Region 도 바로 넘길 수 있다 (자동 래핑)
            using var single = Assert.IsType<ObjectArray<Region>>(Exec("SelectShape", backend, region, "Area", 0, 10).ReturnValue);
            Assert.Empty(single);
        }

        [Fact]
        public void SelectShape_SharesRegionsByReference()
        {
            using var a = Region.FromRectangle(new RectI(0, 0, 10, 10));
            using var b = Region.FromRectangle(new RectI(20, 0, 2, 2));
            using var input = new ObjectArray<Region>(new[] { a, b });
            Assert.Equal(2, a.RefCount);

            var result = Exec("SelectShape", BackendKind.Cpu, input, "Area", 50, double.MaxValue);
            using (var selected = Assert.IsType<ObjectArray<Region>>(result.ReturnValue))
            {
                Assert.Single(selected);
                Assert.Same(a, selected[0]);
                Assert.Equal(3, a.RefCount);
            }
            Assert.Equal(2, a.RefCount);
        }

        [Fact]
        public void ShapeFeatures_AreComputed()
        {
            using var square = Region.FromRectangle(new RectI(10, 20, 5, 5));
            Assert.Equal(25, square.Feature(ShapeFeature.Area));
            Assert.Equal(22, square.Feature(ShapeFeature.Row));
            Assert.Equal(12, square.Feature(ShapeFeature.Column));
            Assert.Equal(20, square.Feature(ShapeFeature.Row1));
            Assert.Equal(24, square.Feature(ShapeFeature.Row2));
            Assert.Equal(10, square.Feature(ShapeFeature.Column1));
            Assert.Equal(14, square.Feature(ShapeFeature.Column2));
            Assert.Equal(1.0, square.Feature(ShapeFeature.Rectangularity));
            Assert.InRange(square.Feature(ShapeFeature.Circularity), 0.9, 1.0);

            // 얇은 선은 원형도가 낮다
            using var line = Region.FromRectangle(new RectI(0, 0, 50, 1));
            Assert.InRange(line.Feature(ShapeFeature.Circularity), 0.0, 0.1);
        }

        [Fact]
        public void ReduceDomain_SharesStorageAndIntersects()
        {
            using var img = MakeTestImage();
            using var roi = Region.FromRectangle(new RectI(0, 0, 32, 32));

            using var reduced = Assert.IsType<Image>(Exec("ReduceDomain", BackendKind.Cpu, img, roi).ReturnValue);
            Assert.Same(img.Storage, reduced.Storage);
            Assert.Equal(new RectI(0, 0, 32, 32), reduced.DomainBounds);

            using var roi2 = Region.FromRectangle(new RectI(16, 16, 32, 32));
            using var reduced2 = Assert.IsType<Image>(Exec("ReduceDomain", BackendKind.Cpu, reduced, roi2).ReturnValue);
            Assert.Equal(new RectI(16, 16, 16, 16), reduced2.DomainBounds);
        }

        [Theory]
        [InlineData(BackendKind.Cpu)]
        [InlineData(BackendKind.Parallel)]
        public void CropPart_CopiesPixels(BackendKind backend)
        {
            using var img = MakeTestImage();
            using var part = Assert.IsType<Image>(Exec("CropPart", backend, img, 3, 4, 20, 15).ReturnValue);

            Assert.Equal(20, part.Width);
            Assert.Equal(15, part.Height);
            Assert.Null(part.Domain);
            Assert.Equal(200, part.GetPixel(1, 2));       // (x=5,y=5) → (1,2)
            Assert.Equal(0, part.GetPixel(0, 0));
            Assert.Equal(200, part.GetPixel(11, 12));     // (15,15)
            Assert.Equal(0, part.GetPixel(12, 12));

            var bad = OperatorInvoker.Invoke(OperatorRegistry.Default, "CropPart", new object?[] { img, 60, 0, 10, 10 });
            Assert.Equal(ErrorCode.OutOfRange, bad.Code);
        }

        [Fact]
        public void CropPart_ParallelPath_OnLargeColorImage()
        {
            using var img = Image.Create(2048, 1024, PixelType.UInt8, channels: 3, clear: true);
            img.SetPixel(700, 300, 7, channel: 0);
            img.SetPixel(700, 300, 8, channel: 1);
            img.SetPixel(700, 300, 9, channel: 2);

            using var part = Assert.IsType<Image>(Exec("CropPart", BackendKind.Parallel, img, 100, 200, 1500, 800).ReturnValue);
            Assert.Equal(7, part.GetPixel(500, 200, 0));
            Assert.Equal(8, part.GetPixel(500, 200, 1));
            Assert.Equal(9, part.GetPixel(500, 200, 2));
            Assert.Equal(0, part.GetPixel(499, 200, 0));
        }

        [Fact]
        public void WriteImage_ReadImage_RoundTrip16Bit()
        {
            var path = Path.Combine(Path.GetTempPath(), $"iv_rt_{Guid.NewGuid():N}.png");
            try
            {
                using var img = MakeTestImage(PixelType.UInt16);
                img.SetPixel(1, 1, 60000);

                var w = Exec("WriteImage", BackendKind.Cpu, img, path);
                Assert.Empty(w.Outputs);
                Assert.True(File.Exists(path));

                using var back = Assert.IsType<Image>(Exec("ReadImage", BackendKind.Cpu, path).ReturnValue);
                Assert.Equal(PixelType.UInt16, back.PixelType);
                Assert.Equal(60000, back.GetPixel(1, 1));
                Assert.Equal(200, back.GetPixel(10, 10));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void WriteImage_BadDirectory_ReturnsErrorCode()
        {
            using var img = MakeTestImage();
            var r = OperatorInvoker.Invoke(OperatorRegistry.Default, "WriteImage",
                new object?[] { img, Path.Combine(Path.GetTempPath(), "iv_no_such_dir_" + Guid.NewGuid().ToString("N"), "a.png") });
            Assert.Equal(ErrorCode.FileWriteFailed, r.Code);
        }

        [Fact]
        public void Pipeline_ThresholdConnectionSelectShape_ProfilesEachStep()
        {
            var ctx = Ctx(BackendKind.Auto);
            using var img = MakeTestImage();

            var reg = OperatorRegistry.Default;
            var region = OperatorInvoker.InvokeOrThrow(reg, "Threshold", new object?[] { img, 128, 255 }, null, ctx).ReturnValue;
            var parts = OperatorInvoker.InvokeOrThrow(reg, "Connection", new object?[] { region }, null, ctx).ReturnValue;
            var selected = OperatorInvoker.InvokeOrThrow(reg, "SelectShape", new object?[] { parts, "Area", 100, 100 }, null, ctx).ReturnValue;

            using var sel = Assert.IsType<ObjectArray<Region>>(selected);
            Assert.Single(sel);
            Assert.Equal(new RectI(40, 30, 20, 5), sel[0].BoundingBox);

            var names = ctx.Profiler.Records.Select(r => r.OperatorName).ToArray();
            Assert.Equal(new[] { "Threshold", "Connection", "SelectShape" }, names);
            Assert.All(ctx.Profiler.Records, r => Assert.Equal(ErrorCode.Ok, r.Code));

            ((Region)region!).Release();
            ((ObjectArray<Region>)parts!).Release();
        }
    }
}
