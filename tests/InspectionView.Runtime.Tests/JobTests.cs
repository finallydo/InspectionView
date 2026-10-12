using System;
using System.Collections.Generic;
using System.IO;
using InspectionView.Core.Backends;
using InspectionView.Core.Types;
using InspectionView.Script.Runtime;
using Xunit;
using ErrorCode = InspectionView.Core.Errors.ErrorCode;

namespace InspectionView.Runtime.Tests
{
    public class JobTests
    {
        private static Image MakeTestImage()
        {
            var img = Image.Create(64, 64, PixelType.UInt8, clear: true);
            for (int y = 5; y < 15; y++)
                for (int x = 5; x < 15; x++)
                    img.SetPixel(x, y, 200);
            for (int y = 30; y < 35; y++)
                for (int x = 40; x < 60; x++)
                    img.SetPixel(x, y, 200);
            return img;
        }

        private static JobDocument MakeDocument()
        {
            var doc = new JobDocument();
            doc.Manifest.Name = "Blob count";
            doc.Manifest.Description = "Counts bright blobs inside the ROI";
            doc.Manifest.Inputs.Add(new JobTerminal("Image", TerminalType.Image, "Input image"));
            doc.Manifest.Inputs.Add(new JobTerminal("MinGray", TerminalType.Tuple, "Threshold", "128"));
            doc.Manifest.Outputs.Add(new JobTerminal("Count", TerminalType.Tuple));
            doc.Manifest.Outputs.Add(new JobTerminal("Blobs", TerminalType.RegionArray));
            doc.Manifest.Outputs.Add(new JobTerminal("Largest", TerminalType.Region));
            doc.Script = @"
Reduced = ReduceDomain(Image, Roi)
Region = Threshold(Reduced, MinGray, 255)
Blobs = SelectShape(Connection(Region), 'Area', MinArea, 100000)
Count = Len(Blobs)
Largest = Blobs[0]
Print('count', Count)
";
            using (var roi = Region.FromRectangle(new RectI(0, 0, 64, 25)))
                doc.SetRegion("Roi", roi);
            doc.SetParameter("MinArea", new TupleValue(50L));
            using (var model = Image.Create(8, 8, PixelType.UInt16, clear: true))
            {
                model.SetPixel(3, 3, 1234);
                doc.SetImage("Model", model);
            }
            return doc;
        }

        [Fact]
        public void Package_RoundTrips_ManifestScriptAndResources()
        {
            var path = Path.Combine(Path.GetTempPath(), $"iv_job_{Guid.NewGuid():N}.ivjob");
            try
            {
                using (var doc = MakeDocument())
                    JobPackage.Save(path, doc);

                Assert.True(File.Exists(path));
                using var loaded = JobPackage.Load(path);

                Assert.Equal("Blob count", loaded.Manifest.Name);
                Assert.Equal(2, loaded.Manifest.Inputs.Count);
                Assert.Equal("128", loaded.Manifest.Inputs[1].Default);
                Assert.Equal(3, loaded.Manifest.Outputs.Count);
                Assert.Equal(TerminalType.RegionArray, loaded.Manifest.Outputs[1].Type);
                Assert.Contains("Threshold(Reduced", loaded.Script);

                Assert.Equal(3, loaded.Manifest.Resources.Count);
                Assert.Equal(64L * 25, loaded.Regions["Roi"].Area);
                Assert.Equal(50L, loaded.Parameters["MinArea"].L());
                var model = loaded.Images["Model"];
                Assert.Equal(PixelType.UInt16, model.PixelType);
                Assert.Equal(1234, model.GetPixel(3, 3));
                Assert.Equal(1, model.RefCount);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Manifest_Json_IsReadable()
        {
            var m = new JobManifest { Name = "x" };
            m.Inputs.Add(new JobTerminal("Image", TerminalType.Image));
            var json = JobPackage.SerializeManifest(m);
            Assert.Contains("\"name\":\"x\"", json);
            Assert.Contains("\"inputs\":[{", json);

            var back = JobPackage.DeserializeManifest(json);
            Assert.Equal("x", back.Name);
            Assert.Single(back.Inputs);
            Assert.NotNull(back.Resources);

            // 빠진 필드는 기본값
            var minimal = JobPackage.DeserializeManifest("{\"formatVersion\":1}");
            Assert.Equal("script.ivs", minimal.ScriptFile);
            Assert.Empty(minimal.Outputs);
        }

        [Fact]
        public void RegionIO_RoundTrip()
        {
            using var a = Region.FromRectangle(new RectI(3, 4, 10, 2));
            using var b = Region.FromRectangle(new RectI(20, 4, 1, 1));
            using var r = a.Union(b);
            using var back = RegionIO.FromBytes(RegionIO.ToBytes(r));
            Assert.Equal(r.Area, back.Area);
            Assert.Equal(r.BoundingBox, back.BoundingBox);
            Assert.Throws<Core.Errors.VisionException>(() => RegionIO.FromBytes(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        }

        [Fact]
        public void Runner_BindsInputs_RunsScript_ReturnsOutputs()
        {
            using var doc = MakeDocument();
            using var runner = new JobRunner(doc, settings: new BackendSettings());
            var printed = new List<string>();
            runner.Output += printed.Add;

            using var img = MakeTestImage();
            using var result = runner.Run(new Dictionary<string, object?> { ["Image"] = img });

            Assert.True(result.Success, result.ToString());
            Assert.Equal(1L, result.GetTuple("Count").L());         // 두 번째 사각형(30..34행)은 ROI(0..24행) 밖
            var blobs = result.GetOutput<ObjectArray<Region>>("Blobs");
            Assert.Single(blobs);
            Assert.Equal(100, result.GetRegion("Largest").Area);
            Assert.Single(printed);
            Assert.Equal("count 1", printed[0]);
            Assert.True(result.Elapsed > TimeSpan.Zero);
            Assert.Contains(result.Records, r => r.OperatorName == "Threshold");

            // 기본값(MinGray=128) 대신 넘기면 반영된다: 250 이면 블롭이 없어 Blobs[0] 에서 실패 (6번째 줄)
            using var none = runner.Run(new Dictionary<string, object?> { ["Image"] = img, ["MinGray"] = 250 });
            Assert.False(none.Success);
            Assert.Equal(6, none.Line);
            Assert.Contains("out of range", none.Message);
        }

        [Fact]
        public void Runner_ReportsErrorsAsCodes()
        {
            using var doc = MakeDocument();
            using var runner = new JobRunner(doc, settings: new BackendSettings());
            using var img = MakeTestImage();

            using var missing = runner.Run();
            Assert.Equal(ErrorCode.MissingArgument, missing.Code);
            Assert.Contains("Image", missing.Message);

            using var wrongType = runner.Run(new Dictionary<string, object?> { ["Image"] = new TupleValue(1L) });
            Assert.Equal(ErrorCode.TypeMismatch, wrongType.Code);

            using var unknown = runner.Run(new Dictionary<string, object?> { ["Image"] = img, ["Nope"] = 1 });
            Assert.Equal(ErrorCode.InvalidArgument, unknown.Code);

            // 스크립트 실행 오류는 줄 번호와 함께
            doc.Script = "A = 1\nB = Missing + 1\nCount = 0\nBlobs = Connection(Roi)\nLargest = Roi\n";
            using var runner2 = new JobRunner(doc, settings: new BackendSettings());
            using var failed = runner2.Run(new Dictionary<string, object?> { ["Image"] = img });
            Assert.False(failed.Success);
            Assert.Equal(2, failed.Line);
            Assert.Contains("Missing", failed.Message);

            // 구문 오류는 로드 시 JobException
            doc.Script = "A = (1 +\n";
            Assert.Throws<JobException>(() => new JobRunner(doc));
        }

        [Fact]
        public void Runner_ChecksOutputTypes_AndKeepsResourcesBetweenRuns()
        {
            using var doc = new JobDocument();
            doc.Manifest.Outputs.Add(new JobTerminal("R", TerminalType.Region));
            doc.Script = "R = 5\n";
            using (var runner = new JobRunner(doc))
            using (var r = runner.Run())
                Assert.Equal(ErrorCode.TypeMismatch, r.Code);

            doc.Manifest.Outputs.Clear();
            doc.Manifest.Outputs.Add(new JobTerminal("N", TerminalType.Tuple));
            doc.Script = "N = N + 1\n";
            doc.SetParameter("N", new TupleValue(0L));
            using (var runner = new JobRunner(doc))
            {
                using (var r1 = runner.Run()) Assert.Equal(1L, r1.GetTuple("N").L());
                using (var r2 = runner.Run()) Assert.Equal(2L, r2.GetTuple("N").L());   // 변수는 실행 간 유지
                runner.ResetVariables();
                using (var r3 = runner.Run()) Assert.Equal(1L, r3.GetTuple("N").L());
            }
        }

        [Fact]
        public void InspectionJob_Facade_ManagesReferenceCounts()
        {
            var path = Path.Combine(Path.GetTempPath(), $"iv_job_{Guid.NewGuid():N}.ivjob");
            try
            {
                using (var doc = MakeDocument())
                    JobPackage.Save(path, doc);

                var img = MakeTestImage();
                Region largest;
                using (var job = InspectionJob.Load(path, settings: new BackendSettings()))
                {
                    Assert.Equal("Blob count", job.Manifest.Name);
                    job.SetInput("Image", img);
                    Assert.Equal(2, img.RefCount);

                    var result = job.Run();
                    Assert.True(result.Success, result.ToString());
                    Assert.Equal(1L, result.GetTuple("Count").L());
                    Assert.Equal(2, img.RefCount);          // Job 의 입력 참조는 해제, 변수 테이블이 보유

                    largest = result.GetRegion("Largest");
                    largest.AddRef();                        // 호출자가 보관

                    Assert.Throws<Core.Errors.VisionException>(() => job.SetInput("Nope", 1.0));
                }
                Assert.Equal(1, img.RefCount);
                Assert.False(largest.IsDisposed);
                Assert.Equal(100, largest.Area);
                largest.Release();
                img.Release();
                Assert.True(img.IsDisposed);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public unsafe void InspectionJob_SetInputImage_FromRawBuffer()
        {
            using var doc = new JobDocument();
            doc.Manifest.Inputs.Add(new JobTerminal("Image", TerminalType.Image));
            doc.Manifest.Outputs.Add(new JobTerminal("W", TerminalType.Tuple));
            doc.Manifest.Outputs.Add(new JobTerminal("V", TerminalType.Tuple));
            doc.Script = "W = Image.Width\nV = Threshold(Image, 7, 7).Area\n";

            var raw = new byte[16 * 4];
            raw[2 * 16 + 3] = 7;
            using var job = InspectionJob.FromDocument(doc, settings: new BackendSettings());
            fixed (byte* p = raw)
                job.SetInputImage("Image", (IntPtr)p, 10, 4, 16, PixelType.UInt8);

            using var result = job.Run();
            Assert.True(result.Success, result.ToString());
            Assert.Equal(10L, result.GetTuple("W").L());
            Assert.Equal(1L, result.GetTuple("V").L());
        }
    }
}
