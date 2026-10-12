using System;
using System.Linq;
using InspectionView.Core.Backends;
using InspectionView.Core.Memory;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;
using Xunit;

namespace InspectionView.Core.Tests
{
    /// <summary>
    /// GPU(OpenCL T-API/UMat) 백엔드 경로. OpenCL 디바이스가 없는 머신(CI)에서도 UMat 연산은 OpenCV 가 CPU 로 수행하므로
    /// 가짜 할당기로 GPU 백엔드를 강제해 코드 경로와 결과 일치를 검증한다.
    /// </summary>
    public class GpuBackendTests
    {
        private sealed class FakeGpu : IDeviceAllocator
        {
            public string Name => "FakeGPU";
            public bool IsAvailable => true;
            public IDeviceBuffer Allocate(long bytes) => throw new NotSupportedException();
            public void Synchronize() { }
        }

        private static (OperatorResult result, ExecutionProfiler profiler) Run(BackendKind requested, bool fakeGpu, string op, params object?[] args)
        {
            var settings = new BackendSettings { DefaultBackend = requested, UseOpenCl = false, Gpu = fakeGpu ? new FakeGpu() : null };
            var profiler = new ExecutionProfiler();
            var ctx = new OperatorContext(settings, profiler);
            var r = OperatorInvoker.Invoke(OperatorRegistry.Default, op, args, ctx);
            Assert.True(r.Success, $"{op} failed: {r.Code} {r.Message}");
            return (r, profiler);
        }

        private static Image Gray(int size = 32)
        {
            var img = Image.Create(size, size, PixelType.UInt8, 1, clear: true);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    img.SetPixel(x, y, (x * 7 + y * 3) % 256);
            return img;
        }

        private static Image Color(int size = 16)
        {
            var img = Image.Create(size, size, PixelType.UInt8, 3, clear: true);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    img.SetPixel(x, y, x * 10, 0);
                    img.SetPixel(x, y, y * 10, 1);
                    img.SetPixel(x, y, 100, 2);
                }
            return img;
        }

        private static void AssertSame(Image a, Image b, double tolerance = 0)
        {
            Assert.Equal(a.Width, b.Width);
            Assert.Equal(a.Height, b.Height);
            Assert.Equal(a.PixelType, b.PixelType);
            Assert.Equal(a.Channels, b.Channels);
            for (int y = 0; y < a.Height; y++)
                for (int x = 0; x < a.Width; x++)
                    for (int c = 0; c < a.Channels; c++)
                        Assert.True(Math.Abs(a.GetPixel(x, y, c) - b.GetPixel(x, y, c)) <= tolerance, $"pixel ({x},{y},{c}): {a.GetPixel(x, y, c)} vs {b.GetPixel(x, y, c)}");
        }

        [Theory]
        [InlineData("GaussFilter", 1.5)]
        [InlineData("MeanFilter", 3, 3)]
        [InlineData("MedianFilter", 3)]
        [InlineData("ScaleImage", 2.0, 10.0)]
        [InlineData("InvertImage")]
        [InlineData("GrayDilationRect", 3, 3)]
        [InlineData("GrayTopHat", 5, 5)]
        public void UnaryFilters_GpuPathMatchesCpu(string op, params object[] args)
        {
            using var img = Gray();
            var cpuArgs = new object?[] { img }.Concat(args).ToArray();

            var (cpu, _) = Run(BackendKind.Cpu, false, op, cpuArgs);
            var (gpu, profiler) = Run(BackendKind.Gpu, true, op, cpuArgs);
            using var a = Assert.IsType<Image>(cpu.Outputs["ImageResult"]);
            using var b = Assert.IsType<Image>(gpu.Outputs["ImageResult"]);

            Assert.Equal(BackendKind.Gpu, profiler.Records.Last().Used);
            Assert.Equal(BackendKind.Gpu, gpu.Record!.Used);
            AssertSame(a, b, tolerance: 1);
        }

        [Theory]
        [InlineData("AddImage", 0.5, 0.0)]
        [InlineData("SubImage", 1.0, 128.0)]
        [InlineData("AbsDiffImage")]
        [InlineData("MinImage")]
        [InlineData("MaxImage")]
        public void BinaryOps_GpuPathMatchesCpu(string op, params object[] args)
        {
            using var x = Gray();
            using var y = Gray();
            for (int r = 0; r < y.Height; r++) for (int c = 0; c < y.Width; c++) y.SetPixel(c, r, (c * 5 + r * 11) % 256);
            var cpuArgs = new object?[] { x, y }.Concat(args).ToArray();

            var (cpu, _) = Run(BackendKind.Cpu, false, op, cpuArgs);
            var (gpu, profiler) = Run(BackendKind.Gpu, true, op, cpuArgs);
            using var a = Assert.IsType<Image>(cpu.Outputs["ImageResult"]);
            using var b = Assert.IsType<Image>(gpu.Outputs["ImageResult"]);
            Assert.Equal(BackendKind.Gpu, profiler.Records.Last().Used);
            AssertSame(a, b, tolerance: 1);
        }

        [Fact]
        public void Rgb1ToGray_GpuPath_AndDomainKept()
        {
            using var color = Color();
            using var roi = Region.FromRectangle(new RectI(2, 2, 6, 6));
            using var reduced = color.ReduceDomain(roi);
            var (gpu, profiler) = Run(BackendKind.Gpu, true, "Rgb1ToGray", reduced);
            using var g = Assert.IsType<Image>(gpu.Outputs["ImageResult"]);
            Assert.Equal(BackendKind.Gpu, profiler.Records.Last().Used);
            Assert.Equal(1, g.Channels);
            Assert.NotNull(g.Domain);
            Assert.Equal(36L, g.Domain!.Area);

            var (cpu, _) = Run(BackendKind.Cpu, false, "Rgb1ToGray", reduced);
            using var c = Assert.IsType<Image>(cpu.Outputs["ImageResult"]);
            AssertSame(c, g, tolerance: 1);
        }

        [Fact]
        public void GpuRequest_FallsBackWhenNoDevice()
        {
            using var img = Gray();
            var (r, profiler) = Run(BackendKind.Gpu, false, "GaussFilter", img, 1.0);
            var rec = profiler.Records.Last();
            Assert.Equal(BackendKind.Gpu, rec.Requested);
            Assert.NotEqual(BackendKind.Gpu, rec.Used);
            Assert.True(rec.FellBack);
            Assert.IsType<Image>(r.Outputs["ImageResult"]).Dispose();
        }

        [Fact]
        public void OpenClProbe_DoesNotThrow_AndSettingsReflectIt()
        {
            bool available = OpenClRuntime.IsAvailable;             // CI 러너에서는 보통 false
            Assert.Equal(available, OpenClRuntime.Devices.Count > 0);
            Assert.Equal(available ? OpenClRuntime.Devices[0] : "", OpenClRuntime.DeviceName);
            _ = OpenClRuntime.EnableInOpenCv();

            var s = new BackendSettings { UseOpenCl = false };
            Assert.False(s.IsGpuAvailable);
            Assert.Equal("not available", s.GpuDescription);
            Assert.DoesNotContain(BackendKind.Gpu, s.PreferenceOrder(BackendKind.Gpu));

            s.UseOpenCl = true;
            Assert.Equal(available, s.IsGpuAvailable);
            if (available) Assert.StartsWith("OpenCL: ", s.GpuDescription);

            s.Gpu = new FakeGpu();
            Assert.True(s.IsGpuAvailable);
            Assert.Equal("FakeGPU", s.GpuDescription);
            Assert.Equal(BackendKind.Gpu, s.PreferenceOrder(BackendKind.Auto).First());
        }
    }
}
