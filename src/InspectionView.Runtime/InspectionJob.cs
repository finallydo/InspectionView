using System;
using System.Collections.Generic;
using InspectionView.Core.Backends;
using InspectionView.Core.Errors;
using InspectionView.Core.Memory;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;
using InspectionView.Script.Runtime;

namespace InspectionView.Runtime
{
    /// <summary>
    /// 검사 프로그램용 최상위 API (C# / C++/CLI 브리지가 사용).
    /// <code>
    /// using var job = InspectionJob.Load(@"C:\jobs\front.ivjob");
    /// job.SetInput("Image", image);
    /// using var result = job.Run();
    /// if (result.Success) var ng = result.GetTuple("DefectCount").L();
    /// </code>
    /// 입력 값은 Run 이 끝나면 자동으로 해제되므로(참조 카운트), 호출자가 계속 쓰려면 AddRef 해 두거나 Run 후 다시 Set 할 것.
    /// </summary>
    public sealed class InspectionJob : IDisposable
    {
        private readonly Dictionary<string, object?> _inputs = new Dictionary<string, object?>(StringComparer.Ordinal);

        public JobDocument Document { get; }
        public JobRunner Runner { get; }
        public JobManifest Manifest => Document.Manifest;

        /// <summary>마지막 실행 결과 (Dispose 전까지 유지).</summary>
        public JobResult? LastResult { get; private set; }

        public event Action<string>? Output
        {
            add => Runner.Output += value;
            remove => Runner.Output -= value;
        }

        private InspectionJob(JobDocument document, OperatorRegistry? registry, BackendSettings? settings)
        {
            Document = document;
            Runner = new JobRunner(document, registry, settings);
        }

        /// <summary>패키지 파일에서 로드. 구문 오류는 <see cref="JobException"/>, 파일 오류는 <see cref="VisionException"/>.</summary>
        public static InspectionJob Load(string path, OperatorRegistry? registry = null, BackendSettings? settings = null)
        {
            var doc = JobPackage.Load(path);
            try
            {
                return new InspectionJob(doc, registry, settings);
            }
            catch
            {
                doc.Dispose();
                throw;
            }
        }

        /// <summary>메모리상의 문서로 생성 (문서 소유권은 Job 으로 넘어간다).</summary>
        public static InspectionJob FromDocument(JobDocument document, OperatorRegistry? registry = null, BackendSettings? settings = null)
            => new InspectionJob(document, registry, settings);

        /// <summary>입력 터미널 값 설정. 아이코닉 객체는 AddRef 되어 Run 뒤 해제된다.</summary>
        public void SetInput(string name, object? value)
        {
            if (Manifest.FindInput(name) == null)
                throw new VisionException(ErrorCode.InvalidArgument, $"'{name}' is not an input terminal (inputs: {string.Join(", ", Manifest.Inputs.ConvertAll(t => t.Name))}).");

            if (_inputs.TryGetValue(name, out var old))
                ScriptValues.Release(old);
            ScriptValues.Retain(value);
            _inputs[name] = value;
        }

        public void SetInput(string name, double value) => SetInput(name, new TupleValue(value));
        public void SetInput(string name, long value) => SetInput(name, new TupleValue(value));
        public void SetInput(string name, string value) => SetInput(name, new TupleValue(value));

        /// <summary>외부 버퍼를 복사해 이미지 입력으로 설정한다.</summary>
        public void SetInputImage(string name, IntPtr data, int width, int height, long stride, PixelType pixelType, int channels = 1)
        {
            using (var img = Image.FromBuffer(data, width, height, stride, pixelType, channels))
                SetInput(name, img);
        }

        public void ClearInputs()
        {
            foreach (var v in _inputs.Values)
                ScriptValues.Release(v);
            _inputs.Clear();
        }

        /// <summary>실행. 예외 대신 결과 코드. 이전 결과는 해제된다.</summary>
        public JobResult Run(System.Threading.CancellationToken cancellation = default)
        {
            LastResult?.Dispose();
            var result = Runner.Run(_inputs, cancellation);
            ClearInputs();
            LastResult = result;
            return result;
        }

        public void Dispose()
        {
            ClearInputs();
            LastResult?.Dispose();
            LastResult = null;
            Runner.Dispose();
            Document.Dispose();
        }
    }
}
