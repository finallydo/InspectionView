using System;
using System.IO;
using System.Linq;
using System.Threading;
using InspectionView.Core.Backends;
using InspectionView.Core.Cameras;
using InspectionView.Core.Errors;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;
using OpenCvSharp;
using Xunit;
using ErrorCode = InspectionView.Core.Errors.ErrorCode;

namespace InspectionView.Core.Tests
{
    public class CameraTests
    {
        private static OperatorResult Exec(string op, params object?[] args)
        {
            var ctx = new OperatorContext(new BackendSettings { DefaultBackend = BackendKind.Cpu }, new ExecutionProfiler());
            return OperatorInvoker.Invoke(OperatorRegistry.Default, op, args, ctx);
        }

        private static OperatorResult ExecOk(string op, params object?[] args)
        {
            var r = Exec(op, args);
            Assert.True(r.Success, $"{op} failed: {r.Code} {r.Message}");
            return r;
        }

        private static T Out<T>(OperatorResult r, string name) => Assert.IsType<T>(r.Outputs[name]);

        [Fact]
        public void Registry_HasBuiltInProviders_AndParsesOptions()
        {
            var names = CameraRegistry.Default.Providers.Select(p => p.Name).ToList();
            Assert.Contains("File", names);
            Assert.Contains("Simulation", names);
            Assert.All(CameraRegistry.Default.Providers.Where(p => p.Name == "File" || p.Name == "Simulation"), p => Assert.True(p.IsAvailable));

            var o = CameraRegistry.ParseOptions("Width=32; Loop ;Pattern=Checker");
            Assert.Equal("32", o["width"]);
            Assert.Equal("true", o["Loop"]);
            Assert.Equal("Checker", o["Pattern"]);
            Assert.Empty(CameraRegistry.ParseOptions(null));

            var ex = Assert.Throws<VisionException>(() => CameraRegistry.Default.Open("NoSuchVendor", ""));
            Assert.Equal(ErrorCode.CameraProviderNotFound, ex.Code);
        }

        [Fact]
        public void Simulation_GrabParametersAndContinuousAcquisition()
        {
            using var cam = CameraRegistry.Default.Open("Simulation", "", "Width=64;Height=48;Pattern=Checker;Fps=200");
            Assert.True(cam.IsOpen);
            Assert.Equal("Simulation", cam.Info.Provider);

            using (var f = cam.Grab(1000))
            {
                Assert.Equal(64, f.Width);
                Assert.Equal(48, f.Height);
                Assert.Equal(PixelType.UInt8, f.PixelType);
                Assert.Equal(0L, (long)f.Metadata["FrameId"]);
            }
            Assert.Equal(1L, (long)cam.GetParameter("FrameCount")!);

            cam.SetParameter("Width", 32L);
            cam.SetParameter("Pattern", "Bars");
            Assert.Equal("Bars", cam.GetParameter("Pattern"));
            using (var f = cam.Grab(1000))
                Assert.Equal(32, f.Width);

            var bad = Assert.Throws<VisionException>(() => cam.SetParameter("Pattern", "Nope"));
            Assert.Equal(ErrorCode.CameraParameterInvalid, bad.Code);
            Assert.Equal(ErrorCode.CameraParameterInvalid, Assert.Throws<VisionException>(() => cam.SetParameter("FrameCount", 5L)).Code);
            Assert.Equal(ErrorCode.CameraParameterInvalid, Assert.Throws<VisionException>(() => cam.GetParameter("Unknown")).Code);
            Assert.Equal(ErrorCode.CameraParameterInvalid, Assert.Throws<VisionException>(() => cam.SetParameter("Width", 0L)).Code);
            cam.SetParameter("TriggerSoftware", null);

            int events = 0;
            cam.FrameReceived += (c, img) => Interlocked.Increment(ref events);
            cam.StartAcquisition();
            Assert.True(cam.IsAcquiring);
            using (var f = cam.Grab(2000))
                Assert.Equal(32, f.Width);
            using (var latest = cam.GetLatestFrame())
                Assert.NotNull(latest);
            SpinWait.SpinUntil(() => Volatile.Read(ref events) >= 3, 3000);
            Assert.True(events >= 3);
            cam.StopAcquisition();
            Assert.False(cam.IsAcquiring);

            cam.Dispose();
            Assert.False(cam.IsOpen);
            Assert.Equal(ErrorCode.CameraClosed, Assert.Throws<VisionException>(() => cam.Grab(10)).Code);
        }

        private static string MakeImageFolder(int count)
        {
            var dir = Path.Combine(Path.GetTempPath(), "ivcam_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            for (int i = 0; i < count; i++)
            {
                using var m = new Mat(8, 12, MatType.CV_8UC1, new Scalar(10 * (i + 1)));
                Cv2.ImWrite(Path.Combine(dir, $"frame_{i:D2}.png"), m);
            }
            using (var extra = new Mat(8, 12, MatType.CV_8UC1, new Scalar(99)))
                Cv2.ImWrite(Path.Combine(dir, "zz_extra.png"), extra);          // 정렬상 마지막
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "ignored");
            return dir;
        }

        [Fact]
        public void FileCamera_PlaysFolderInOrder()
        {
            var dir = MakeImageFolder(3);
            try
            {
                using var cam = CameraRegistry.Default.Open("File", dir, "Loop=false");
                Assert.Equal(4L, (long)cam.GetParameter("Count")!);                  // notes.txt 는 제외
                for (int i = 0; i < 3; i++)
                {
                    using var f = cam.Grab(1000);
                    Assert.Equal(12, f.Width);
                    Assert.Equal(10.0 * (i + 1), f.GetPixel(0, 0));
                    Assert.EndsWith($"frame_{i:D2}.png", (string)f.Metadata["FileName"]);
                    Assert.EndsWith($"frame_{i:D2}.png", (string)cam.GetParameter("FileName")!);
                }
                using (var f = cam.Grab(1000)) Assert.Equal(99.0, f.GetPixel(0, 0));
                Assert.Equal(ErrorCode.CameraTimeout, Assert.Throws<VisionException>(() => cam.Grab(10)).Code);

                cam.SetParameter("Loop", true);
                using (var f = cam.Grab(1000)) Assert.Equal(10.0, f.GetPixel(0, 0));
                cam.SetParameter("Index", 2L);
                using (var f = cam.Grab(1000)) Assert.Equal(30.0, f.GetPixel(0, 0));

                using var pattern = CameraRegistry.Default.Open("File", Path.Combine(dir, "frame_*.png"));
                Assert.Equal(3L, (long)pattern.GetParameter("Count")!);
                using var single = CameraRegistry.Default.Open("File", Path.Combine(dir, "frame_01.png"), "AsGray=true");
                Assert.Equal(1L, (long)single.GetParameter("Count")!);

                Assert.Equal(ErrorCode.CameraNotFound, Assert.Throws<VisionException>(() => CameraRegistry.Default.Open("File", Path.Combine(dir, "nothing_*.png"))).Code);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void FramegrabberOperators_EndToEnd()
        {
            var info = ExecOk("InfoFramegrabber");
            Assert.Contains("Simulation", Out<TupleValue>(info, "Providers").ToStringArray());
            Assert.Contains("Simulation:default", Out<TupleValue>(info, "Devices").ToStringArray());
            Assert.Equal(Out<TupleValue>(info, "Devices").Length, Out<TupleValue>(info, "Names").Length);

            var open = ExecOk("OpenFramegrabber", "Simulation", "", "Width=40;Height=20");
            using var cam = Out<CameraHandle>(open, "Camera");
            Assert.Equal("Camera", cam.TypeName);
            Assert.True(cam.IsOpen);

            using (var img = Out<Image>(ExecOk("GrabImage", cam, 1000), "Image"))
            {
                Assert.Equal(40, img.Width);
                Assert.Equal(20, img.Height);
            }

            var names = Out<TupleValue>(ExecOk("GetFramegrabberParam", cam, "AvailableParamNames"), "Value").ToStringArray();
            Assert.Contains("Width", names);
            Assert.Equal("Simulation", Out<TupleValue>(ExecOk("GetFramegrabberParam", cam, "Provider"), "Value").S());

            ExecOk("SetFramegrabberParam", cam, "Width", 24);
            Assert.Equal(24L, Out<TupleValue>(ExecOk("GetFramegrabberParam", cam, "Width"), "Value").L());
            using (var img = Out<Image>(ExecOk("GrabImage", cam), "Image"))
                Assert.Equal(24, img.Width);

            var bad = Exec("SetFramegrabberParam", cam, "Pattern", "Nope");
            Assert.False(bad.Success);
            Assert.Equal(ErrorCode.CameraParameterInvalid, bad.Code);

            ExecOk("GrabImageStart", cam);
            Assert.True(Out<TupleValue>(ExecOk("GetFramegrabberParam", cam, "IsAcquiring"), "Value").B());
            using (var img = Out<Image>(ExecOk("GrabImageAsync", cam, 2000), "Image"))
                Assert.Equal(24, img.Width);
            ExecOk("GrabImageStop", cam);

            ExecOk("CloseFramegrabber", cam);
            Assert.False(cam.IsOpen);
            var closed = Exec("GrabImage", cam);
            Assert.False(closed.Success);
            Assert.Equal(ErrorCode.CameraClosed, closed.Code);

            var noProvider = Exec("OpenFramegrabber", "NoSuchVendor");
            Assert.Equal(ErrorCode.CameraProviderNotFound, noProvider.Code);
        }

        /// <summary>플러그인 DLL 의 공급자처럼 스캔되는 테스트용 공급자 (SDK 없음 → 사용 불가).</summary>
        public sealed class FakeVendorProvider : ICameraProvider
        {
            public string Name => "FakeVendor";
            public bool IsAvailable => false;
            public string? UnavailableReason => "no SDK";
            public System.Collections.Generic.IReadOnlyList<CameraInfo> Enumerate() => Array.Empty<CameraInfo>();
            public ICamera Open(string deviceId, System.Collections.Generic.IReadOnlyDictionary<string, string> options) => throw new NotSupportedException();
        }

        [Fact]
        public void Registry_ScansAssemblyForProviders_AndReportsUnavailable()
        {
            var registry = new CameraRegistry();
            Assert.Equal(1, registry.ScanAssembly(typeof(CameraTests).Assembly));
            Assert.Equal(0, registry.ScanAssembly(typeof(CameraTests).Assembly));      // 중복 스캔 안 함
            var p = registry.Find("fakevendor");
            Assert.NotNull(p);
            Assert.False(p!.IsAvailable);
            var ex = Assert.Throws<VisionException>(() => registry.Open("FakeVendor", ""));
            Assert.Equal(ErrorCode.CameraSdkNotAvailable, ex.Code);
            Assert.Empty(registry.EnumerateAll());
        }

        [Fact]
        public void CameraHandle_ClosesDeviceOnLastRelease()
        {
            var cam = CameraRegistry.Default.Open("Simulation", "");
            var handle = new CameraHandle(cam);
            handle.AddRef();
            handle.Release();
            Assert.True(cam.IsOpen);
            handle.Release();
            Assert.False(cam.IsOpen);
            Assert.True(handle.IsDisposed);
        }
    }
}
