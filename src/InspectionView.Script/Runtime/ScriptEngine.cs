using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InspectionView.Core.Backends;
using InspectionView.Core.Errors;
using InspectionView.Core.Registry;
using InspectionView.Script.Ast;
using InspectionView.Script.Compilation;
using InspectionView.Script.Parsing;

namespace InspectionView.Script.Runtime
{
    public enum EngineState
    {
        /// <summary>로드됨, 아직 실행 전 (PC = 0).</summary>
        Idle,
        /// <summary>백그라운드 스레드에서 실행 중.</summary>
        Running,
        /// <summary>브레이크포인트 / 한 줄 실행 / 정지로 멈춤. Run/Step 으로 이어서 실행.</summary>
        Paused,
        /// <summary>오류로 멈춤. 해당 줄에서 Run/Step 하면 다시 시도.</summary>
        Error,
        /// <summary>끝까지 실행됨. Run 하면 처음부터 다시.</summary>
        Finished,
    }

    public enum RunMode
    {
        /// <summary>F5: 브레이크포인트/끝까지.</summary>
        Continuous,
        /// <summary>F6: 한 문장.</summary>
        Step,
    }

    /// <summary>화면 갱신 / 고속 모드 옵션.</summary>
    public sealed class EngineOptions
    {
        /// <summary>실행 중 줄이 바뀔 때마다 LineChanged 를 발생시킨다 (끄면 멈출 때만).</summary>
        public bool NotifyLineChanges { get; set; } = true;

        /// <summary>변수 변경 이벤트 (변수창 갱신).</summary>
        public bool NotifyVariableChanges { get; set; } = true;

        /// <summary>연산자별 실행 시간 기록.</summary>
        public bool Profile { get; set; } = true;

        /// <summary>고속 모드: 위 세 가지를 모두 끈다.</summary>
        public void SetFastMode(bool fast)
        {
            NotifyLineChanges = !fast;
            NotifyVariableChanges = !fast;
            Profile = !fast;
        }
    }

    /// <summary>
    /// 스크립트 실행 엔진 (HDevelop 의 실행 제어에 대응).
    /// <list type="bullet">
    /// <item><see cref="Load"/>: 파싱 + 컴파일, 오류는 <see cref="Diagnostics"/></item>
    /// <item><see cref="Run"/> (F5) / <see cref="Step"/> (F6) / <see cref="Stop"/> / <see cref="Reset"/> / <see cref="SetNextLine"/> (PC 이동)</item>
    /// <item>브레이크포인트: 줄 번호 집합</item>
    /// <item>이벤트는 실행 스레드에서 발생하므로 UI 는 Dispatcher 로 마샬링할 것</item>
    /// </list>
    /// </summary>
    public sealed class ScriptEngine : IScriptHost, IDisposable
    {
        private readonly object _sync = new object();
        private readonly HashSet<int> _breakpoints = new HashSet<int>();
        private readonly ManualResetEventSlim _breakEvent = new ManualResetEventSlim(true);
        private readonly Interpreter _interp;

        private CancellationTokenSource? _cts;
        private Task? _worker;
        private volatile bool _pauseRequested;

        public OperatorRegistry Registry { get; }
        public VariableTable Variables { get; }
        public BackendSettings Settings { get; }
        public ExecutionProfiler Profiler { get; }
        public Builtins Builtins { get; }
        public EngineOptions Options { get; } = new EngineOptions();

        public string Source { get; private set; } = "";
        public ProgramNode? Program { get; private set; }
        public Instruction[] Code { get; private set; } = Array.Empty<Instruction>();
        public IReadOnlyList<Diagnostic> Diagnostics { get; private set; } = Array.Empty<Diagnostic>();
        public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

        public EngineState State { get; private set; } = EngineState.Idle;

        /// <summary>다음에 실행할(또는 멈춘) 줄. 없으면 -1.</summary>
        public int CurrentLine { get; private set; } = -1;

        public ScriptRuntimeException? LastError { get; private set; }

        public event Action<EngineState>? StateChanged;
        public event Action<int>? LineChanged;
        public event Action<ScriptRuntimeException>? ErrorOccurred;
        public event Action<string>? Output;

        public ScriptEngine(OperatorRegistry? registry = null, BackendSettings? settings = null)
        {
            Registry = registry ?? OperatorRegistry.Default;
            Settings = settings ?? BackendSettings.Global;
            Profiler = new ExecutionProfiler();
            Variables = new VariableTable();
            Builtins = new Builtins();
            _interp = new Interpreter(Registry, Variables, Builtins, this, new OperatorContext(Settings, Profiler));
        }

        // ------------------------------------------------------------------
        // 로드
        // ------------------------------------------------------------------

        /// <summary>소스를 파싱/컴파일한다. 실행 중이면 false. 구문 오류가 있으면 코드는 비워지고 false.</summary>
        public bool Load(string source)
        {
            lock (_sync)
            {
                if (State == EngineState.Running)
                    return false;

                Source = source ?? "";
                Program = Parser.Parse(Source, out var parseDiags);
                var diags = new List<Diagnostic>(parseDiags);

                Instruction[] code = Array.Empty<Instruction>();
                if (!diags.Any(d => d.Severity == DiagnosticSeverity.Error))
                {
                    code = Compiler.Compile(Program, out var compileDiags);
                    diags.AddRange(compileDiags);
                }

                Diagnostics = diags;
                if (diags.Any(d => d.Severity == DiagnosticSeverity.Error))
                    code = Array.Empty<Instruction>();

                Code = code;
                _interp.Load(code);
                LastError = null;
                CurrentLine = code.Length > 0 ? code[0].Line : -1;
                SetState(EngineState.Idle);
                return code.Length > 0 || Source.Trim().Length == 0;
            }
        }

        /// <summary>실행하지 않고 구문 검사만 한다 (에디터 실시간 파싱용).</summary>
        public static IReadOnlyList<Diagnostic> Check(string source)
        {
            var program = Parser.Parse(source ?? "", out var parseDiags);
            if (parseDiags.Any(d => d.Severity == DiagnosticSeverity.Error))
                return parseDiags;
            Compiler.Compile(program, out var compileDiags);
            return parseDiags.Concat(compileDiags).ToList();
        }

        // ------------------------------------------------------------------
        // 브레이크포인트
        // ------------------------------------------------------------------

        public IReadOnlyCollection<int> Breakpoints
        {
            get { lock (_breakpoints) return _breakpoints.ToArray(); }
        }

        public bool HasBreakpoint(int line)
        {
            lock (_breakpoints) return _breakpoints.Contains(line);
        }

        public void AddBreakpoint(int line)
        {
            lock (_breakpoints) _breakpoints.Add(line);
        }

        public void RemoveBreakpoint(int line)
        {
            lock (_breakpoints) _breakpoints.Remove(line);
        }

        /// <summary>토글 후 설정 여부를 돌려준다.</summary>
        public bool ToggleBreakpoint(int line)
        {
            lock (_breakpoints)
            {
                if (_breakpoints.Remove(line)) return false;
                _breakpoints.Add(line);
                return true;
            }
        }

        public void ClearBreakpoints()
        {
            lock (_breakpoints) _breakpoints.Clear();
        }

        /// <summary>해당 줄에 실행 가능한 명령이 있는지 (브레이크포인트 표시 가능 여부).</summary>
        public bool IsExecutableLine(int line) => Code.Any(i => i.Line == line);

        // ------------------------------------------------------------------
        // 실행 제어
        // ------------------------------------------------------------------

        /// <summary>F5: 브레이크포인트나 끝까지 실행. 시작했으면 true.</summary>
        public bool Run() => Start(RunMode.Continuous);

        /// <summary>F6: 한 문장 실행. 시작했으면 true.</summary>
        public bool Step() => Start(RunMode.Step);

        /// <summary>다음 문장 경계에서 멈춘다 (실행 중인 연산자는 끝까지 기다림).</summary>
        public void Pause()
        {
            _pauseRequested = true;
        }

        /// <summary>즉시 정지: 실행 중인 연산자를 취소하고 현재 줄에서 멈춘다 (Paused).</summary>
        public void Stop()
        {
            _pauseRequested = true;
            lock (_sync)
                _cts?.Cancel();
        }

        /// <summary>PC 를 처음으로 되돌린다. 변수는 유지한다. 실행 중이면 먼저 정지한다.</summary>
        public void Reset()
        {
            Stop();
            WaitForBreak(Timeout.Infinite);
            lock (_sync)
            {
                _interp.Reset();
                LastError = null;
                CurrentLine = Code.Length > 0 ? Code[0].Line : -1;
                SetState(EngineState.Idle);
            }
            LineChanged?.Invoke(CurrentLine);
        }

        /// <summary>PC 이동: 해당 줄(없으면 그 다음 실행 가능한 줄)의 첫 명령으로 옮긴다. 실행 중이면 false.</summary>
        public bool SetNextLine(int line)
        {
            lock (_sync)
            {
                if (State == EngineState.Running || Code.Length == 0)
                    return false;

                int index = -1;
                for (int i = 0; i < Code.Length; i++)
                {
                    if (Code[i].Line == line) { index = i; break; }
                    if (Code[i].Line > line && (index < 0 || Code[i].Line < Code[index].Line)) index = i;
                }
                if (index < 0)
                    return false;

                _interp.Pc = index;
                LastError = null;
                CurrentLine = Code[index].Line;
                SetState(EngineState.Paused);
            }
            LineChanged?.Invoke(CurrentLine);
            return true;
        }

        /// <summary>변수 전부 해제.</summary>
        public void ClearVariables()
        {
            if (State == EngineState.Running)
                throw new InvalidOperationException("Cannot clear variables while running.");
            Variables.Clear();
        }

        /// <summary>실행이 멈출 때까지(Paused/Error/Finished) 기다린다. 실행 중이 아니면 즉시 true.</summary>
        public bool WaitForBreak(int timeoutMs) => _breakEvent.Wait(timeoutMs);

        private bool Start(RunMode mode)
        {
            lock (_sync)
            {
                if (State == EngineState.Running || Code.Length == 0)
                    return false;

                if (_interp.IsFinished)
                    _interp.Reset();

                _pauseRequested = false;
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                Profiler.Enabled = Options.Profile;
                Variables.NotifyChanges = Options.NotifyVariableChanges;
                _interp.Context = new OperatorContext(Settings, Profiler, _cts.Token);
                LastError = null;

                _breakEvent.Reset();
                SetState(EngineState.Running);
                _worker = Task.Factory.StartNew(() => Worker(mode), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                return true;
            }
        }

        private void Worker(RunMode mode)
        {
            var code = _interp.Code;
            var final = EngineState.Paused;
            bool first = true;
            int prevPc = -2, prevStatement = -1, lastLine = -1;

            try
            {
                while (!_interp.IsFinished)
                {
                    int pc = _interp.Pc;
                    var ins = code[pc];

                    if (_pauseRequested)
                        break;

                    // 새 문장의 시작인지 (같은 문장의 연속 명령이면 아님)
                    bool newStatement = ins.StatementId != prevStatement || pc != prevPc + 1;
                    if (!first && newStatement)
                    {
                        if (mode == RunMode.Step)
                            break;
                        if (HasBreakpoint(ins.Line))
                            break;
                    }
                    first = false;

                    if (ins.Line != lastLine)
                    {
                        lastLine = ins.Line;
                        CurrentLine = ins.Line;
                        if (Options.NotifyLineChanges)
                            LineChanged?.Invoke(ins.Line);
                    }

                    prevPc = pc;
                    prevStatement = ins.StatementId;

                    try
                    {
                        _interp.ExecuteInstruction();
                    }
                    catch (ScriptRuntimeException ex)
                    {
                        if (ex.IsCancelled)
                            break;

                        LastError = ex;
                        final = EngineState.Error;
                        break;
                    }
                }

                if (final != EngineState.Error && _interp.IsFinished)
                    final = EngineState.Finished;
            }
            catch (Exception ex)
            {
                LastError = new ScriptRuntimeException(CurrentLine, ex.Message, ErrorCode.Unknown, ex);
                final = EngineState.Error;
            }
            finally
            {
                CurrentLine = _interp.IsFinished ? -1 : code[_interp.Pc].Line;
                lock (_sync)
                    SetState(final);

                if (final == EngineState.Error && LastError != null)
                    ErrorOccurred?.Invoke(LastError);
                LineChanged?.Invoke(CurrentLine);

                _breakEvent.Set();
            }
        }

        private void SetState(EngineState state)
        {
            if (State == state)
                return;
            State = state;
            StateChanged?.Invoke(state);
        }

        void IScriptHost.Print(string text) => Output?.Invoke(text);

        public void Dispose()
        {
            Stop();
            WaitForBreak(5000);
            lock (_sync)
            {
                _cts?.Dispose();
                _cts = null;
            }
            Variables.Dispose();
            _breakEvent.Dispose();
        }
    }
}
