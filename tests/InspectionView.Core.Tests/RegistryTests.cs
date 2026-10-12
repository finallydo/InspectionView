using System;
using System.IO;
using System.Linq;
using InspectionView.Core.Backends;
using InspectionView.Core.Errors;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;
using OpenCvSharp;
using Xunit;

namespace InspectionView.Core.Tests
{
    public class RegistryTests
    {
        [Fact]
        public void Default_ContainsReadImage()
        {
            var reg = OperatorRegistry.Default;
            Assert.True(reg.TryGet("ReadImage", out var d));
            Assert.Equal("IO", d.Category);
            Assert.Equal("FileName", d.Inputs[0].Name);
            Assert.Equal("AsGray", d.Inputs[1].Name);
            Assert.True(d.Inputs[1].IsOptional);
            Assert.Single(d.Outputs);
            Assert.Equal("Image", d.Outputs[0].Name);
            Assert.True(d.HasBackend(BackendKind.Cpu));
        }

        [Fact]
        public void Invoke_ReadImage_LoadsFile()
        {
            var path = Path.Combine(Path.GetTempPath(), $"iv_test_{Guid.NewGuid():N}.png");
            using (var mat = new Mat(20, 30, MatType.CV_8UC3, new Scalar(10, 20, 30)))
                Cv2.ImWrite(path, mat);

            try
            {
                var ctx = new OperatorContext(new BackendSettings(), new ExecutionProfiler());
                var result = OperatorInvoker.Invoke(OperatorRegistry.Default, "ReadImage", new object?[] { path }, ctx);

                Assert.True(result.Success, result.Message);
                var img = Assert.IsType<Image>(result.ReturnValue);
                using (img)
                {
                    Assert.Equal(30, img.Width);
                    Assert.Equal(20, img.Height);
                    Assert.Equal(3, img.Channels);
                    Assert.Equal(PixelType.UInt8, img.PixelType);
                    Assert.Equal(10, img.GetPixel(0, 0, 0));
                }

                var rec = Assert.Single(ctx.Profiler.Records);
                Assert.Equal(BackendKind.Cpu, rec.Used);
                Assert.Equal(ErrorCode.Ok, rec.Code);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Invoke_MissingFile_ReturnsErrorCode()
        {
            var result = OperatorInvoker.Invoke(OperatorRegistry.Default, "ReadImage", new object?[] { "Z:/does/not/exist.png" });

            Assert.False(result.Success);
            Assert.Equal(ErrorCode.FileNotFound, result.Code);
            Assert.Equal("ReadImage", result.Error!.OperatorName);
        }

        [Fact]
        public void Invoke_MissingArgument_ReturnsErrorCode()
        {
            var result = OperatorInvoker.Invoke(OperatorRegistry.Default, "ReadImage", Array.Empty<object?>());
            Assert.Equal(ErrorCode.MissingArgument, result.Code);
        }

        [Fact]
        public void Invoke_UnknownOperator_ReturnsErrorCode()
        {
            var result = OperatorInvoker.Invoke(OperatorRegistry.Default, "NoSuchOperator", Array.Empty<object?>());
            Assert.Equal(ErrorCode.OperatorNotFound, result.Code);
        }

        [Fact]
        public void BackendSettings_FallsBackWhenGpuMissing()
        {
            var settings = new BackendSettings { DefaultBackend = BackendKind.Gpu, Gpu = null };
            var d = OperatorRegistry.Default.Get("ReadImage");

            var used = d.SelectBackend(settings, out var requested);
            Assert.Equal(BackendKind.Gpu, requested);
            Assert.Equal(BackendKind.Cpu, used.Kind);

            settings.SetOverride("ReadImage", BackendKind.Cpu);
            d.SelectBackend(settings, out requested);
            Assert.Equal(BackendKind.Cpu, requested);
        }

        [Fact]
        public void TupleValue_ConvertsScalars()
        {
            var t = new TupleValue(1, 2.5, "x", true);
            Assert.Equal(4, t.Length);
            Assert.Equal(1L, t.L(0));
            Assert.Equal(2.5, t.D(1));
            Assert.Equal("x", t.S(2));
            Assert.True(t.B(3));
            Assert.Equal("[1, 2.5, 'x', true]", t.ToString());

            var d = OperatorRegistry.Default.Get("ReadImage").GetInput("AsGray");
            Assert.Equal(true, d.ConvertValue(new TupleValue(1L)));
            Assert.Equal(false, d.ConvertValue("false"));
        }
    }
}
