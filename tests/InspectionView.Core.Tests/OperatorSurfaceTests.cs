using System;
using System.IO;
using System.Linq;
using InspectionView.Core.Backends;
using InspectionView.Core.Interop;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;
using OpenCvSharp;
using Xunit;

namespace InspectionView.Core.Tests
{
    public class OperatorSurfaceTests
    {
        private static OperatorResult Exec(string op, params object?[] args) => ExecOn(BackendKind.Cpu, op, args);

        private static OperatorResult ExecOn(BackendKind backend, string op, params object?[] args)
        {
            var ctx = new OperatorContext(new BackendSettings { DefaultBackend = backend }, new ExecutionProfiler());
            var result = OperatorInvoker.Invoke(OperatorRegistry.Default, op, args, ctx);
            Assert.True(result.Success, $"{op} failed: {result.Code} {result.Message}");
            return result;
        }

        private static T Out<T>(OperatorResult r, string name) => Assert.IsType<T>(r.Outputs[name]);

        private static Image Float(int w, int h, Func<int, int, double> f)
        {
            var img = Image.Create(w, h, PixelType.Float32, 1, clear: true);
            for (int y = 0; y < h; y++)
            {
                var row = img.GetRow<float>(y);
                for (int x = 0; x < w; x++) row[x] = (float)f(x, y);
            }
            return img;
        }

        private static Image Byte(int w, int h, Func<int, int, double> f)
        {
            var img = Image.Create(w, h, PixelType.UInt8, 1, clear: true);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    img.SetPixel(x, y, f(x, y));
            return img;
        }

        private static double MeanOf(Image img)
        {
            double s = 0;
            for (int y = 0; y < img.Height; y++)
                for (int x = 0; x < img.Width; x++)
                    s += img.GetPixel(x, y);
            return s / (img.Width * img.Height);
        }

        // ---------------------------------------------------------------- Photometric stereo

        [Theory]
        [InlineData(BackendKind.Cpu)]
        [InlineData(BackendKind.Parallel)]
        public void PhotometricStereo_RecoversGradientAlbedoAndHeight(BackendKind backend)
        {
            const int size = 64;
            const double amp = 4.0, sigma = 8.0, cx = 31.5, cy = 31.5, albedo = 200.0;
            Func<int, int, double> z = (x, y) => amp * Math.Exp(-((x - cx) * (x - cx) + (y - cy) * (y - cy)) / (2 * sigma * sigma));
            Func<int, int, double> p = (x, y) => -(x - cx) / (sigma * sigma) * z(x, y);
            Func<int, int, double> q = (x, y) => -(y - cy) / (sigma * sigma) * z(x, y);

            double[] slants = { 45, 45, 45, 45 };
            double[] tilts = { 0, 90, 180, 270 };
            var images = new ObjectArray<Image>();
            for (int i = 0; i < 4; i++)
            {
                double s = slants[i] * Math.PI / 180, t = tilts[i] * Math.PI / 180;
                double lx = Math.Sin(s) * Math.Cos(t), ly = -Math.Sin(s) * Math.Sin(t), lz = Math.Cos(s);
                images.AddOwned(Float(size, size, (x, y) =>
                {
                    double px = p(x, y), qy = q(x, y);
                    double norm = Math.Sqrt(px * px + qy * qy + 1);
                    double dot = (-px * lx - qy * ly + lz) / norm;
                    return albedo * Math.Max(0, dot);
                }));
            }

            using (images)
            {
                var r = ExecOn(backend, "PhotometricStereo", images, (TupleValue)slants, (TupleValue)tilts);
                using var gx = Out<Image>(r, "GradientX");
                using var gy = Out<Image>(r, "GradientY");
                using var al = Out<Image>(r, "Albedo");
                using var hf = Out<Image>(r, "HeightField");

                Assert.Equal(PixelType.Float32, gx.PixelType);
                Assert.Equal(size, hf.Width);
                for (int y = 4; y < size - 4; y += 7)
                    for (int x = 4; x < size - 4; x += 5)
                    {
                        Assert.Equal(p(x, y), gx.GetPixel(x, y), 2);
                        Assert.Equal(q(x, y), gy.GetPixel(x, y), 2);
                        Assert.Equal(albedo, al.GetPixel(x, y), 1);
                    }

                // 높이는 상수항을 제외하고 비교
                double meanH = MeanOf(hf), meanZ = 0;
                for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) meanZ += z(x, y);
                meanZ /= size * size;
                double maxErr = 0;
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                        maxErr = Math.Max(maxErr, Math.Abs((hf.GetPixel(x, y) - meanH) - (z(x, y) - meanZ)));
                Assert.True(maxErr < 0.15 * amp, $"height error {maxErr}");
                Assert.InRange(hf.GetPixel(32, 32) - hf.GetPixel(2, 2), 0.8 * amp, 1.2 * amp);
            }
        }

        [Fact]
        public void PhotometricStereo_RejectsDegenerateLights()
        {
            using var a = Float(8, 8, (x, y) => 100);
            using var arr = new ObjectArray<Image>(new[] { a, a, a });
            var ctx = new OperatorContext(new BackendSettings { DefaultBackend = BackendKind.Cpu }, new ExecutionProfiler());
            var r = OperatorInvoker.Invoke(OperatorRegistry.Default, "PhotometricStereo", new object[] { arr, (TupleValue)new double[] { 45, 45, 45 }, (TupleValue)new double[] { 0, 0, 0 } }, ctx);
            Assert.False(r.Success);
            Assert.Equal(Core.Errors.ErrorCode.InvalidArgument, r.Code);
        }

        // ---------------------------------------------------------------- Vector field derivatives

        [Theory]
        [InlineData(BackendKind.Cpu)]
        [InlineData(BackendKind.Parallel)]
        public void DerivateVectorField_DivergenceCurlAndCurvature(BackendKind backend)
        {
            // 포물면 z = a(x² + y²): p = 2ax, q = 2ay → div = 4a, curl = 0, 꼭짓점 평균 곡률 = 2a.
            // 띠(256행) 경계를 지나도록 세로를 크게 한다.
            const int w = 80, h = 600;
            const double a = 0.01, cx = 40, cy = 300;
            using var p = Float(w, h, (x, y) => 2 * a * (x - cx));
            using var q = Float(w, h, (x, y) => 2 * a * (y - cy));
            using var div = Out<Image>(ExecOn(backend, "DerivateVectorField", p, q, 1.5, "divergence"), "ImageResult");
            using var curl = Out<Image>(ExecOn(backend, "DerivateVectorField", p, q, 1.5, "curl"), "ImageResult");
            using var mean = Out<Image>(ExecOn(backend, "DerivateVectorField", p, q, 1.5, "mean_curvature"), "ImageResult");
            Assert.Equal(PixelType.Float32, div.PixelType);
            for (int y = 10; y < h - 10; y += 7)
                for (int x = 10; x < w - 10; x += 9)
                {
                    Assert.Equal(4 * a, div.GetPixel(x, y), 4);
                    Assert.Equal(0, curl.GetPixel(x, y), 4);
                }
            Assert.Equal(2 * a, mean.GetPixel(40, 300), 4);
            // 꼭짓점에서 멀어지면 기울기가 커져 평균 곡률이 줄어든다
            Assert.True(mean.GetPixel(40, 100) < mean.GetPixel(40, 300));

            // 회전장 (-y, x): curl = 2, div = 0. 바이트 입력도 받는다
            using var rx = Float(w, h, (x, y) => -(y - cy) * 0.1);
            using var ry = Float(w, h, (x, y) => (x - cx) * 0.1);
            using var c2 = Out<Image>(ExecOn(backend, "DerivateVectorField", rx, ry, 1.0, "curl"), "ImageResult");
            using var d2 = Out<Image>(ExecOn(backend, "DerivateVectorField", rx, ry, 1.0, "div"), "ImageResult");
            Assert.Equal(0.2, c2.GetPixel(20, 400), 4);
            Assert.Equal(0, d2.GetPixel(20, 400), 4);
        }

        [Fact]
        public void DerivateVectorField_ValidatesInputs()
        {
            using var a = Float(16, 16, (x, y) => 0);
            using var b = Float(16, 12, (x, y) => 0);
            var ctx = new OperatorContext(new BackendSettings(), new ExecutionProfiler());
            Assert.Equal(Core.Errors.ErrorCode.SizeMismatch, OperatorInvoker.Invoke(OperatorRegistry.Default, "DerivateVectorField", new object?[] { a, b }, ctx).Code);
            Assert.False(OperatorInvoker.Invoke(OperatorRegistry.Default, "DerivateVectorField", new object?[] { a, a, 1.0, "gauss" }, ctx).Success);
            Assert.False(OperatorInvoker.Invoke(OperatorRegistry.Default, "DerivateVectorField", new object?[] { a, a, 0.0 }, ctx).Success);
        }

        /// <summary>저장소의 samples 폴더 (없으면 null).</summary>
        private static string? SampleDir(string name)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "InspectionView.sln")))
                {
                    var path = Path.Combine(dir.FullName, "samples", name);
                    return Directory.Exists(path) ? path : null;
                }
            }
            return null;
        }

        private static Image ReadCrop(string path, Rect roi)
        {
            using var m = Cv2.ImRead(path, ImreadModes.Grayscale);
            Assert.False(m.Empty(), path);
            using var sub = new Mat(m, roi);
            return MatInterop.ToImage(sub);
        }

        private static double Correlation(Image a, Image b, int margin, out double meanAbsAfterFit, out double slope, out double offset)
        {
            int n = 0;
            double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
            for (int y = 0; y < b.Height; y++)
                for (int x = 0; x < b.Width; x++)
                {
                    double u = a.GetPixel(x + margin, y + margin), v = b.GetPixel(x, y);
                    n++; sx += u; sy += v; sxx += u * u; syy += v * v; sxy += u * v;
                }
            double cov = sxy / n - sx / n * sy / n, vx = sxx / n - sx / n * sx / n, vy = syy / n - sy / n * sy / n;
            slope = cov / vx;
            offset = sy / n - slope * sx / n;
            double err = 0;
            for (int y = 0; y < b.Height; y++)
                for (int x = 0; x < b.Width; x++)
                    err += Math.Abs(slope * a.GetPixel(x + margin, y + margin) + offset - b.GetPixel(x, y));
            meanAbsAfterFit = err / n;
            return cov / Math.Sqrt(vx * vy);
        }

        /// <summary>
        /// 실제 샘플 (samples/CAVTEST_0022: 상하좌우 조명 4장 + 기존 OpenCV 변환 결과 Albedo/Divergence) 과 비교한다.
        /// 기존 결과는 원본의 열 400 부터 자른 영역이고, 조명 Slant ≈ 27°, Divergence ≈ 127.5 − 124·div (기울기 부호가 반대인 규약).
        /// </summary>
        [Fact]
        public void PhotometricStereo_MatchesReferenceSample()
        {
            var dir = SampleDir("CAVTEST_0022");
            if (dir == null) return;   // 샘플이 없는 체크아웃에서는 건너뜀

            const int x0 = 1100, y0 = 2600, size = 600, margin = 20, cropX = 400;
            var roi = new Rect(x0 + cropX - margin, y0 - margin, size + 2 * margin, size + 2 * margin);
            var stack = new ObjectArray<Image>();
            foreach (var name in new[] { "Top", "Bot", "Left", "Right" })
                stack.AddOwned(ReadCrop(Path.Combine(dir, name + ".bmp"), roi));
            using (stack)
            using (var refAlbedo = ReadCrop(Path.Combine(dir, "Albedo.bmp"), new Rect(x0, y0, size, size)))
            using (var refDiv = ReadCrop(Path.Combine(dir, "Divergence.bmp"), new Rect(x0, y0, size, size)))
            {
                var ps = ExecOn(BackendKind.Parallel, "PhotometricStereo", stack, (TupleValue)new double[] { 27, 27, 27, 27 }, (TupleValue)new double[] { 90, 270, 180, 0 }, false);
                using var gx = Out<Image>(ps, "GradientX");
                using var gy = Out<Image>(ps, "GradientY");
                using var albedo = Out<Image>(ps, "Albedo");
                using var div = Out<Image>(ExecOn(BackendKind.Parallel, "DerivateVectorField", gx, gy, 1.1, "divergence"), "ImageResult");

                double ca = Correlation(albedo, refAlbedo, margin, out double errA, out _, out _);
                Assert.True(ca > 0.99, $"albedo correlation {ca:F4}");
                Assert.True(errA < 4, $"albedo error {errA:F2}");

                double cd = Correlation(div, refDiv, margin, out double errD, out double slope, out double offset);
                Assert.True(cd < -0.95, $"divergence correlation {cd:F4}");
                Assert.True(errD < 1.5, $"divergence error {errD:F2}");
                Assert.InRange(slope, -150, -100);
                Assert.InRange(offset, 125, 130);
            }
        }

        // ---------------------------------------------------------------- Focus

        private static Image Checker(int size, int period)
            => Byte(size, size, (x, y) => ((x / period + y / period) % 2 == 0) ? 220 : 30);

        [Theory]
        [InlineData("ModifiedLaplacian")]
        [InlineData("Laplacian")]
        [InlineData("Tenengrad")]
        [InlineData("Variance")]
        public void FocusMeasure_SharpScoresHigherThanBlurred(string method)
        {
            using var sharp = Checker(48, 3);
            using var blurred = Out<Image>(Exec("GaussFilter", sharp, 2.0), "ImageResult");

            var rs = Exec("FocusMeasure", sharp, method, 5);
            var rb = Exec("FocusMeasure", blurred, method, 5);
            double scoreSharp = Out<TupleValue>(rs, "Score").D();
            double scoreBlur = Out<TupleValue>(rb, "Score").D();
            Assert.True(scoreSharp > 2 * scoreBlur, $"{method}: sharp {scoreSharp} vs blurred {scoreBlur}");

            using var fi = Out<Image>(rs, "FocusImage");
            Assert.Equal(PixelType.Float32, fi.PixelType);
            Assert.Equal(sharp.Width, fi.Width);
        }

        [Theory]
        [InlineData(BackendKind.Cpu)]
        [InlineData(BackendKind.Parallel)]
        public void DepthFromFocus_FindsSharpestSlice(BackendKind backend)
        {
            const int size = 48;
            using var tex = Checker(size, 2);
            using var blur = Out<Image>(Exec("GaussFilter", tex, 2.0), "ImageResult");

            // 왼쪽 절반은 슬라이스 0 에서, 오른쪽 절반은 슬라이스 2 에서 선명
            Image Slice(Func<int, bool> sharpAt)
                => Byte(size, size, (x, y) => sharpAt(x) ? tex.GetPixel(x, y) : blur.GetPixel(x, y));
            using var stack = new ObjectArray<Image>();
            stack.AddOwned(Slice(x => x < size / 2));
            stack.AddOwned(Slice(x => false));
            stack.AddOwned(Slice(x => x >= size / 2));

            var r = ExecOn(backend, "DepthFromFocus", stack, "ModifiedLaplacian", 5, 0.0, true);
            using var depth = Out<Image>(r, "Depth");
            using var conf = Out<Image>(r, "Confidence");
            using var sharp = Out<Image>(r, "Sharp");

            Assert.Equal(PixelType.UInt8, sharp.PixelType);
            for (int y = 6; y < size - 6; y += 5)
            {
                for (int x = 6; x < size / 2 - 6; x += 4)
                {
                    Assert.InRange(depth.GetPixel(x, y), -0.5, 0.4);
                    Assert.Equal(tex.GetPixel(x, y), sharp.GetPixel(x, y));
                    Assert.True(conf.GetPixel(x, y) > 0.2);
                }
                for (int x = size / 2 + 6; x < size - 6; x += 4)
                {
                    Assert.InRange(depth.GetPixel(x, y), 1.6, 2.5);
                    Assert.Equal(tex.GetPixel(x, y), sharp.GetPixel(x, y));
                }
            }
        }

        // ---------------------------------------------------------------- Sheet of light

        private static double LineRow(int x) => 20.3 + 0.05 * x;

        private static Image LaserImage(int w = 64, int h = 48, Func<int, double>? center = null)
        {
            center = center ?? LineRow;
            return Byte(w, h, (x, y) =>
            {
                if (x < 10) return 0;                               // 왼쪽 10열은 라인 없음
                double d = y - center(x);
                return 200 * Math.Exp(-d * d / (2 * 1.5 * 1.5));
            });
        }

        [Fact]
        public void ExtractLaserLine_SubPixelPositions()
        {
            using var img = LaserImage();

            var cog = Exec("ExtractLaserLine", img, "Column", 30.0, "CenterOfGravity", 7);
            var pos = Out<TupleValue>(cog, "Position").ToDoubleArray();
            var amp = Out<TupleValue>(cog, "Amplitude").ToDoubleArray();
            Assert.Equal(64, pos.Length);
            for (int x = 0; x < 10; x++) Assert.Equal(-1.0, pos[x]);
            for (int x = 10; x < 64; x++)
            {
                Assert.InRange(pos[x], LineRow(x) - 0.15, LineRow(x) + 0.15);
                Assert.InRange(amp[x], 180, 200);
            }
            using var line = Out<XldContour>(cog, "Line");
            Assert.Equal(54, line.Count);
            Assert.Equal(10, line[0].X, 6);

            var par = Exec("ExtractLaserLine", img, "Column", 30.0, "Parabolic", 7);
            var ppos = Out<TupleValue>(par, "Position").ToDoubleArray();
            for (int x = 10; x < 64; x++) Assert.InRange(ppos[x], LineRow(x) - 0.25, LineRow(x) + 0.25);

            var peak = Exec("ExtractLaserLine", img, "Column", 30.0, "Peak", 7);
            var kpos = Out<TupleValue>(peak, "Position").ToDoubleArray();
            for (int x = 10; x < 64; x++) Assert.True(Math.Abs(kpos[x] - LineRow(x)) <= 0.5 + 1e-9, $"peak at {x}: {kpos[x]}");

            // 세로 라인: Direction = Row
            using var vertical = Byte(48, 64, (x, y) => y < 10 ? 0 : 200 * Math.Exp(-(x - LineRow(y)) * (x - LineRow(y)) / (2 * 1.5 * 1.5)));
            var rowDir = Exec("ExtractLaserLine", vertical, "Row", 30.0, "CenterOfGravity", 7);
            var vpos = Out<TupleValue>(rowDir, "Position").ToDoubleArray();
            Assert.Equal(64, vpos.Length);
            for (int y = 10; y < 64; y++) Assert.InRange(vpos[y], LineRow(y) - 0.15, LineRow(y) + 0.15);
        }

        [Theory]
        [InlineData(BackendKind.Cpu)]
        [InlineData(BackendKind.Parallel)]
        public void SheetOfLight_DisparityAndHeight(BackendKind backend)
        {
            using var stack = new ObjectArray<Image>();
            stack.AddOwned(LaserImage());
            stack.AddOwned(LaserImage(center: x => LineRow(x) + 3));

            var r = ExecOn(backend, "MeasureProfileSheetOfLight", stack, "Column", 30.0, "CenterOfGravity", 7);
            using var disp = Out<Image>(r, "Disparity");
            Assert.Equal(64, disp.Width);
            Assert.Equal(2, disp.Height);
            Assert.Equal(PixelType.Float32, disp.PixelType);
            Assert.Equal(-1.0, disp.GetPixel(3, 0));
            Assert.InRange(disp.GetPixel(30, 0), LineRow(30) - 0.15, LineRow(30) + 0.15);
            Assert.InRange(disp.GetPixel(30, 1), LineRow(30) + 3 - 0.15, LineRow(30) + 3 + 0.15);

            var h = Exec("DisparityToHeight", disp, 2.0, 20.0, -5.0);
            using var height = Out<Image>(h, "Height");
            Assert.Equal(-5.0, height.GetPixel(3, 0));
            Assert.InRange(height.GetPixel(30, 1), (LineRow(30) + 3 - 20) * 2 - 0.3, (LineRow(30) + 3 - 20) * 2 + 0.3);
        }

        // ---------------------------------------------------------------- Phase shift

        private static double WrapDiff(double a, double b) => Math.Atan2(Math.Sin(a - b), Math.Cos(a - b));

        [Theory]
        [InlineData(BackendKind.Cpu)]
        [InlineData(BackendKind.Parallel)]
        public void PhaseShiftDecode_FourStep(BackendKind backend)
        {
            const int w = 64, h = 16;
            Func<int, int, double> phi = (x, y) => 2 * Math.PI * x / 32.0;
            using var stack = new ObjectArray<Image>();
            for (int k = 0; k < 4; k++)
            {
                double shift = Math.PI / 2 * k;
                stack.AddOwned(Float(w, h, (x, y) => 100 + 80 * Math.Cos(phi(x, y) + shift)));
            }

            var r = ExecOn(backend, "PhaseShiftDecode", stack);
            using var phase = Out<Image>(r, "Phase");
            using var mod = Out<Image>(r, "Modulation");
            using var mean = Out<Image>(r, "Mean");
            for (int x = 0; x < w; x += 3)
            {
                Assert.True(Math.Abs(WrapDiff(phase.GetPixel(x, 5), phi(x, 5))) < 1e-3);
                Assert.Equal(80.0, mod.GetPixel(x, 5), 2);
                Assert.Equal(100.0, mean.GetPixel(x, 5), 2);
            }
            Assert.InRange(phase.GetPixel(40, 0), -Math.PI, Math.PI);
        }

        [Fact]
        public void PhaseShiftDecode_ExplicitShifts()
        {
            double[] shifts = { 0.0, 2.0, 4.0 };
            Func<int, int, double> phi = (x, y) => 0.1 * x - 1.0;
            using var stack = new ObjectArray<Image>();
            foreach (var s in shifts)
                stack.AddOwned(Float(32, 4, (x, y) => 50 + 40 * Math.Cos(phi(x, y) + s)));

            var r = Exec("PhaseShiftDecode", stack, (TupleValue)shifts);
            using var phase = Out<Image>(r, "Phase");
            for (int x = 0; x < 32; x++)
                Assert.True(Math.Abs(WrapDiff(phase.GetPixel(x, 1), phi(x, 1))) < 1e-3, $"x={x}");
        }

        [Fact]
        public void UnwrapPhase_RecoversContinuousPhase_AndHonorsQuality()
        {
            const int w = 40, h = 30;
            Func<int, int, double> phi = (x, y) => 0.3 * x + 0.1 * y;
            using var wrapped = Float(w, h, (x, y) => Math.Atan2(Math.Sin(phi(x, y)), Math.Cos(phi(x, y))));

            var r = Exec("UnwrapPhase", wrapped);
            using var u = Out<Image>(r, "Unwrapped");
            using var valid = Out<Region>(r, "Valid");
            Assert.Equal((long)(w * h), valid.Area);
            double offset = u.GetPixel(0, 0) - phi(0, 0);
            for (int y = 0; y < h; y += 3)
                for (int x = 0; x < w; x += 3)
                    Assert.True(Math.Abs(u.GetPixel(x, y) - phi(x, y) - offset) < 1e-3, $"({x},{y}) {u.GetPixel(x, y)} vs {phi(x, y) + offset}");

            // 품질이 낮은 블록은 건너뛴다
            using var quality = Float(w, h, (x, y) => (x >= 10 && x < 15 && y >= 10 && y < 14) ? 0.0 : 1.0);
            var r2 = Exec("UnwrapPhase", wrapped, quality, 0.5);
            using var u2 = Out<Image>(r2, "Unwrapped");
            using var valid2 = Out<Region>(r2, "Valid");
            Assert.Equal((long)(w * h - 5 * 4), valid2.Area);
            Assert.False(valid2.Contains(12, 11));
            Assert.Equal(0.0, u2.GetPixel(12, 11));
            double off2 = u2.GetPixel(0, 0) - phi(0, 0);
            Assert.True(Math.Abs(u2.GetPixel(30, 20) - phi(30, 20) - off2) < 1e-3);
        }

        // ---------------------------------------------------------------- Illumination fusion

        [Theory]
        [InlineData(BackendKind.Cpu)]
        [InlineData(BackendKind.Parallel)]
        public void ComposeIllumination_Statistics(BackendKind backend)
        {
            using var stack = new ObjectArray<Image>();
            stack.AddOwned(Byte(8, 8, (x, y) => 10));
            stack.AddOwned(Byte(8, 8, (x, y) => 20));
            stack.AddOwned(Byte(8, 8, (x, y) => 60));

            void Check(string method, double expected, PixelType type)
            {
                using var res = Out<Image>(ExecOn(backend, "ComposeIllumination", stack, method), "ImageResult");
                Assert.Equal(type, res.PixelType);
                Assert.Equal(expected, res.GetPixel(3, 3), 1);
            }

            Check("Max", 60, PixelType.UInt8);
            Check("Min", 10, PixelType.UInt8);
            Check("Mean", 30, PixelType.UInt8);
            Check("Median", 20, PixelType.UInt8);
            Check("Range", 50, PixelType.UInt8);
            Check("Sum", 90, PixelType.Float32);
            Check("StdDev", Math.Sqrt((400 + 100 + 900) / 3.0), PixelType.Float32);
        }

        [Fact]
        public void ComposeIllumination_KeepsDomainAndRejectsMixedSizes()
        {
            using var a = Byte(16, 16, (x, y) => x);
            using var roi = Region.FromRectangle(new RectI(2, 2, 5, 5));
            using var reduced = a.ReduceDomain(roi);
            using var b = Byte(16, 16, (x, y) => 2 * x);
            using var stack = new ObjectArray<Image>(new[] { reduced, b });
            using var res = Out<Image>(Exec("ComposeIllumination", stack, "Max"), "ImageResult");
            Assert.NotNull(res.Domain);
            Assert.Equal(25L, res.Domain!.Area);
            Assert.Equal(6.0, res.GetPixel(3, 3));

            using var small = Byte(8, 8, (x, y) => 0);
            using var bad = new ObjectArray<Image>(new[] { a, small });
            var ctx = new OperatorContext(new BackendSettings { DefaultBackend = BackendKind.Cpu }, new ExecutionProfiler());
            var r = OperatorInvoker.Invoke(OperatorRegistry.Default, "ComposeIllumination", new object[] { bad, "Max" }, ctx);
            Assert.False(r.Success);
            Assert.Equal(Core.Errors.ErrorCode.SizeMismatch, r.Code);
        }
    }
}
