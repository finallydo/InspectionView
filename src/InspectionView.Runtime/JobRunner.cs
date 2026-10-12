using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using InspectionView.Core.Backends;
using InspectionView.Core.Errors;
using InspectionView.Core.Memory;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;
using InspectionView.Script.Compilation;
using InspectionView.Script.Parsing;
using InspectionView.Script.Runtime;

namespace InspectionView.Runtime
{
    /// <summary>Job 한 번 실행의 결과. 출력 아이코닉 객체는 결과가 소유하므로 다 쓰면 Dispose 할 것.</summary>
    public sealed class JobResult : IDisposable
    {
        private readonly Dictionary<string, object?> _outputs;

        public ErrorCode Code { get; }
        public string Message { get; }

        /// <summary>오류가 난 스크립트 줄 (없으면 0).</summary>
        public int Line { get; }

        public TimeSpan Elapsed { get; }
        public IReadOnlyList<ExecutionRecord> Records { get; }
        public bool Success => Code == ErrorCode.Ok;
        public IReadOnlyDictionary<string, object?> Outputs => _outputs;

        internal JobResult(ErrorCode code, string message, int line, TimeSpan elapsed, IReadOnlyList<ExecutionRecord> records, Dictionary<string, object?> outputs)
        {
            Code = code;
            Message = message;
            Line = line;
            Elapsed = elapsed;
            Records = records;
            _outputs = outputs;
            foreach (var v in _outputs.Values)
                ScriptValues.Retain(v);
        }

        /// <summary>출력 값을 타입으로 가져온다. 없거나 타입이 다르면 예외.</summary>
        public T GetOutput<T>(string name) where T : class
        {
            if (!_outputs.TryGetValue(name, out var v))
                throw new VisionException(ErrorCode.InvalidArgument, $"Job has no output '{name}'.");
            if (v is T t)
                return t;
            if (typeof(T) == typeof(TupleValue) && v != null)
                return (T)(object)TupleValue.From(v);
            throw VisionException.TypeMismatch(name, typeof(T), v?.GetType());
        }

        public TupleValue GetTuple(string name) => GetOutput<TupleValue>(name);
        public Image GetImage(string name) => GetOutput<Image>(name);
        public Region GetRegion(string name) => GetOutput<Region>(name);

        public bool TryGetOutput(string name, out object? value) => _outputs.TryGetValue(name, out value);

        public void Dispose()
        {
            foreach (var v in _outputs.Values)
                ScriptValues.Release(v);
            _outputs.Clear();
        }

        public override string ToString() => Success ? $"OK ({Elapsed.TotalMilliseconds:F1} ms)" : $"{Code} at line {Line}: {Message}";
    }

    /// <summary>Job 로드/컴파일 단계의 오류 (구문 오류 등).</summary>
    public sealed class JobException : VisionException
    {
        public IReadOnlyList<Diagnostic> Diagnostics { get; }

        public JobException(string message, IReadOnlyList<Diagnostic> diagnostics)
            : base(ErrorCode.InvalidArgument, message)
        {
            Diagnostics = diagnostics;
        }
    }

    /// <summary>
    /// 검사 프로그램용 동기 실행기. 스크립트를 한 번 컴파일하고, 호출 스레드에서 입력 터미널 → 실행 → 출력 터미널을 처리한다.
    /// 리소스(ROI/모델/파라미터)는 변수로 미리 로드되어 실행 간에 유지된다. 스레드 안전하지 않다 (인스턴스당 한 스레드).
    /// </summary>
    public sealed class JobRunner : IDisposable, IScriptHost
    {
        private readonly Interpreter _interp;
        private readonly Instruction[] _code;
        private readonly ExecutionProfiler _profiler = new ExecutionProfiler();

        public JobDocument Document { get; }
        public JobManifest Manifest => Document.Manifest;
        public VariableTable Variables { get; }
        public BackendSettings Settings { get; }
        public Builtins Builtins { get; }

        /// <summary>Print 출력.</summary>
        public event Action<string>? Output;

        /// <summary>Display/ClearWindow 는 런타임에서는 무시되지만 필요하면 받을 수 있다.</summary>
        public event Action<object?, int>? DisplayRequested;

        /// <summary>연산자별 실행 시간 기록 여부 (기본 true).</summary>
        public bool Profile
        {
            get => _profiler.Enabled;
            set => _profiler.Enabled = value;
        }

        public JobRunner(JobDocument document, OperatorRegistry? registry = null, BackendSettings? settings = null)
        {
            Document = document ?? throw new ArgumentNullException(nameof(document));
            Settings = settings ?? BackendSettings.Global;
            Variables = new VariableTable { NotifyChanges = false };
            Builtins = new Builtins();

            var program = Parser.Parse(document.Script ?? "", out var parseDiags);
            var diags = parseDiags.ToList();
            Instruction[] code = Array.Empty<Instruction>();
            if (!diags.Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                code = Compiler.Compile(program, out var compileDiags);
                diags.AddRange(compileDiags);
            }
            if (diags.Any(d => d.Severity == DiagnosticSeverity.Error))
                throw new JobException($"Job script has {diags.Count} syntax error(s): {diags[0]}", diags);

            _code = code;
            _interp = new Interpreter(registry ?? OperatorRegistry.Default, Variables, Builtins, this, new OperatorContext(Settings, _profiler));
            _interp.Load(_code);

            foreach (var kv in document.AllResources())
                Variables.Set(kv.Key, kv.Value);
        }

        /// <summary>
        /// 입력을 바인딩하고 스크립트를 끝까지 실행한 뒤 출력 터미널 값을 돌려준다. 예외를 던지지 않고 결과 코드로 알린다.
        /// </summary>
        public JobResult Run(IReadOnlyDictionary<string, object?>? inputs = null, CancellationToken cancellation = default)
        {
            var sw = Stopwatch.StartNew();
            _profiler.Clear();

            // 1. 입력 터미널
            var bound = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in Manifest.Inputs)
            {
                object? value = null;
                bool given = inputs != null && inputs.TryGetValue(t.Name, out value) && value != null;
                if (!given)
                {
                    if (string.IsNullOrEmpty(t.Default))
                        return Fail(ErrorCode.MissingArgument, $"Input '{t.Name}' was not provided.", 0, sw);
                    try
                    {
                        value = ScriptValues.ParseLiteral(t.Default);
                    }
                    catch (ScriptRuntimeException ex)
                    {
                        return Fail(ErrorCode.InvalidArgument, $"Default of input '{t.Name}' is invalid: {ex.Message}", 0, sw);
                    }
                }
                if (!TerminalType.Matches(t.Type, value))
                    return Fail(ErrorCode.TypeMismatch, $"Input '{t.Name}' expects {t.Type} but got {TerminalType.Of(value)}.", 0, sw);

                Variables.Set(t.Name, value is TupleValue || value is IconicObject ? value : TupleValue.From(value));
                bound.Add(t.Name);
            }

            if (inputs != null)
            {
                foreach (var kv in inputs)
                {
                    if (!bound.Contains(kv.Key))
                        return Fail(ErrorCode.InvalidArgument, $"'{kv.Key}' is not an input terminal of this job.", 0, sw);
                }
            }

            // 2. 실행
            _interp.Context = new OperatorContext(Settings, _profiler, cancellation);
            _interp.Reset();
            try
            {
                while (!_interp.IsFinished)
                {
                    if (cancellation.IsCancellationRequested)
                        return Fail(ErrorCode.Cancelled, "Job execution was cancelled.", _interp.Code[_interp.Pc].Line, sw);
                    _interp.ExecuteInstruction();
                }
            }
            catch (ScriptRuntimeException ex)
            {
                return Fail(ex.Code == ErrorCode.Unknown ? ErrorCode.BackendExecutionFailed : ex.Code, ex.Message, ex.Line, sw);
            }
            catch (Exception ex)
            {
                return Fail(ErrorCode.Unknown, ex.Message, 0, sw);
            }

            // 3. 출력 터미널
            var outputs = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var t in Manifest.Outputs)
            {
                if (!Variables.TryGet(t.Name, out var value) || value == null)
                    return Fail(ErrorCode.MissingArgument, $"Output '{t.Name}' was not set by the script.", 0, sw);
                if (!TerminalType.Matches(t.Type, value))
                    return Fail(ErrorCode.TypeMismatch, $"Output '{t.Name}' expects {t.Type} but the script produced {TerminalType.Of(value)}.", 0, sw);
                outputs[t.Name] = value;
            }

            sw.Stop();
            return new JobResult(ErrorCode.Ok, "", 0, sw.Elapsed, _profiler.Records, outputs);
        }

        private JobResult Fail(ErrorCode code, string message, int line, Stopwatch sw)
        {
            sw.Stop();
            return new JobResult(code, message, line, sw.Elapsed, _profiler.Records, new Dictionary<string, object?>());
        }

        /// <summary>터미널/리소스가 아닌 변수를 지우고 리소스를 다시 로드한다.</summary>
        public void ResetVariables()
        {
            Variables.Clear();
            foreach (var kv in Document.AllResources())
                Variables.Set(kv.Key, kv.Value);
        }

        void IScriptHost.Print(string text) => Output?.Invoke(text);
        void IScriptHost.Display(object? value, int window) => DisplayRequested?.Invoke(value, window);
        void IScriptHost.ClearWindow(int window) { }

        public void Dispose()
        {
            Variables.Dispose();
        }
    }
}
