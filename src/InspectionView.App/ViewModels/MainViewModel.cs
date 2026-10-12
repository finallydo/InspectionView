using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using InspectionView.Core.Backends;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;
using InspectionView.Script.Runtime;

namespace InspectionView.App.ViewModels
{
    /// <summary>
    /// 메인 창 상태. ScriptEngine 을 소유하고, 엔진 이벤트(실행 스레드)를 UI 스레드로 모아서 반영한다.
    /// 에디터 자체(텍스트, 캐럿)는 뷰가 가지고 있고, 실행 명령은 뷰가 현재 텍스트를 넘겨 호출한다.
    /// </summary>
    public sealed class MainViewModel : ObservableObject, IDisposable
    {
        private readonly Action<Action> _ui;
        private readonly HashSet<string> _dirtyVariables = new HashSet<string>(StringComparer.Ordinal);
        private readonly object _dirtyLock = new object();
        private readonly StringBuilder _output = new StringBuilder();
        private volatile bool _outputDirty;
        private volatile bool _profileDirty;
        private volatile int _pendingLine = int.MinValue;
        private string _loadedSource = "";

        private EngineState _state = EngineState.Idle;
        private int _currentLine = -1;
        private int _errorLine = -1;
        private string _statusText = "Ready";
        private string _filePath = "";
        private bool _isDirty;
        private bool _fastMode;
        private BackendKind _backend = BackendKind.Auto;
        private VariableItem? _selectedVariable;
        private string _lastIconicVariable = "";
        private bool _autoDisplay = true;
        private string _outputText = "";

        public ScriptEngine Engine { get; }
        public OperatorRegistry Registry { get; }

        public ObservableCollection<VariableItem> Variables { get; } = new ObservableCollection<VariableItem>();
        public ObservableCollection<DiagnosticItem> Diagnostics { get; } = new ObservableCollection<DiagnosticItem>();
        public ObservableCollection<ProfileItem> Profile { get; } = new ObservableCollection<ProfileItem>();
        public ObservableCollection<OperatorNode> OperatorTree { get; } = new ObservableCollection<OperatorNode>();
        public IReadOnlyList<BackendKind> Backends { get; } = new[] { BackendKind.Auto, BackendKind.Cpu, BackendKind.Parallel, BackendKind.Gpu };

        /// <summary>UI 가 알아야 하는 요청: 캐럿을 특정 줄로 (오류 더블클릭 등).</summary>
        public event Action<int>? GoToLineRequested;

        /// <summary>그래픽 창에 변수를 표시해 달라는 요청 (자동 표시).</summary>
        public event Action<VariableItem>? DisplayRequested;

        public MainViewModel(Action<Action> uiInvoke, OperatorRegistry? registry = null)
        {
            _ui = uiInvoke;
            Registry = registry ?? OperatorRegistry.Default;
            Engine = new ScriptEngine(Registry, BackendSettings.Global);

            Engine.StateChanged += OnEngineStateChanged;
            Engine.LineChanged += l => _pendingLine = l;
            Engine.ErrorOccurred += OnEngineError;
            Engine.Output += s => { lock (_output) _output.AppendLine(s); _outputDirty = true; };
            Engine.Variables.Changed += (name, value) =>
            {
                lock (_dirtyLock)
                {
                    _dirtyVariables.Add(name);
                    if (value is Core.Memory.IconicObject)
                        _lastIconicVariable = name;
                }
            };
            Engine.Profiler.Recorded += _ => _profileDirty = true;

            BuildOperatorTree();
        }

        // ------------------------------------------------------------------
        // 바인딩 속성
        // ------------------------------------------------------------------

        public EngineState State
        {
            get => _state;
            private set
            {
                if (Set(ref _state, value))
                {
                    OnPropertyChanged(nameof(IsRunning));
                    OnPropertyChanged(nameof(StateText));
                    RelayCommand.Requery();
                }
            }
        }

        public bool IsRunning => _state == EngineState.Running;
        public string StateText => _state.ToString();

        /// <summary>다음 실행 줄 (하이라이트). 없으면 -1.</summary>
        public int CurrentLine
        {
            get => _currentLine;
            private set => Set(ref _currentLine, value);
        }

        /// <summary>오류 줄 (빨강). 없으면 -1.</summary>
        public int ErrorLine
        {
            get => _errorLine;
            private set => Set(ref _errorLine, value);
        }

        public string StatusText
        {
            get => _statusText;
            set => Set(ref _statusText, value);
        }

        public string FilePath
        {
            get => _filePath;
            private set
            {
                if (Set(ref _filePath, value))
                    OnPropertyChanged(nameof(Title));
            }
        }

        public bool IsDirty
        {
            get => _isDirty;
            set
            {
                if (Set(ref _isDirty, value))
                    OnPropertyChanged(nameof(Title));
            }
        }

        public string Title => (string.IsNullOrEmpty(FilePath) ? "Untitled.ivs" : Path.GetFileName(FilePath)) + (IsDirty ? " *" : "") + " - InspectionView";

        /// <summary>고속 모드: 줄/변수 갱신과 프로파일링을 끈다.</summary>
        public bool FastMode
        {
            get => _fastMode;
            set
            {
                if (Set(ref _fastMode, value))
                    Engine.Options.SetFastMode(value);
            }
        }

        /// <summary>전역 기본 백엔드.</summary>
        public BackendKind Backend
        {
            get => _backend;
            set
            {
                if (Set(ref _backend, value))
                    BackendSettings.Global.DefaultBackend = value;
            }
        }

        public string GpuText => GpuRuntime.IsAvailable ? $"GPU: {GpuRuntime.DefaultAllocator?.Name}" : "GPU: not available";

        public VariableItem? SelectedVariable
        {
            get => _selectedVariable;
            set => Set(ref _selectedVariable, value);
        }

        /// <summary>실행이 멈출 때 마지막으로 바뀐 아이코닉 변수를 그래픽 창에 자동 표시.</summary>
        public bool AutoDisplay
        {
            get => _autoDisplay;
            set => Set(ref _autoDisplay, value);
        }

        public string OutputText
        {
            get => _outputText;
            private set => Set(ref _outputText, value);
        }

        // ------------------------------------------------------------------
        // 실행 제어 (뷰가 현재 에디터 텍스트를 넘긴다)
        // ------------------------------------------------------------------

        public bool CanRun => State != EngineState.Running;
        public bool CanStop => State == EngineState.Running;

        /// <summary>F5. 텍스트가 바뀌었으면 다시 로드하고, 멈춰 있던 줄이 있으면 그 줄로 PC 를 옮긴다.</summary>
        public void Run(string source) => Execute(source, step: false);

        /// <summary>F6.</summary>
        public void Step(string source) => Execute(source, step: true);

        private void Execute(string source, bool step)
        {
            if (State == EngineState.Running)
                return;

            if (!EnsureLoaded(source))
                return;

            bool started = step ? Engine.Step() : Engine.Run();
            if (!started)
                StatusText = "Nothing to run";
        }

        /// <summary>텍스트가 마지막 로드와 다르면 다시 로드한다. 구문 오류가 있으면 false.</summary>
        public bool EnsureLoaded(string source)
        {
            if (string.Equals(source, _loadedSource, StringComparison.Ordinal) && Engine.Code.Length > 0)
                return true;

            int resumeLine = State == EngineState.Paused || State == EngineState.Error ? Engine.CurrentLine : -1;

            bool ok = Engine.Load(source);
            _loadedSource = source;
            RefreshDiagnostics(Engine.Diagnostics.Select(d => new DiagnosticItem(d)));

            if (!ok || Engine.HasErrors)
            {
                StatusText = $"{Engine.Diagnostics.Count} syntax error(s)";
                ErrorLine = Engine.Diagnostics.Count > 0 ? Engine.Diagnostics[0].Line : -1;
                CurrentLine = -1;
                return false;
            }

            ErrorLine = -1;
            if (resumeLine > 0 && Engine.SetNextLine(resumeLine))
                StatusText = $"Script reloaded; continuing at line {Engine.CurrentLine}";
            else
                StatusText = "Script loaded";

            CurrentLine = Engine.CurrentLine;
            return true;
        }

        public void Stop()
        {
            Engine.Stop();
            StatusText = "Stopping...";
        }

        public void Reset()
        {
            Engine.Reset();
            ErrorLine = -1;
            CurrentLine = Engine.CurrentLine;
            StatusText = "Reset";
        }

        public void ClearVariables()
        {
            if (State == EngineState.Running)
                return;
            Engine.ClearVariables();
            Variables.Clear();
            StatusText = "Variables cleared";
        }

        public void ClearOutput()
        {
            lock (_output) _output.Clear();
            OutputText = "";
        }

        public bool ToggleBreakpoint(int line) => Engine.ToggleBreakpoint(line);

        public bool SetNextLine(int line)
        {
            if (!Engine.SetNextLine(line))
                return false;
            ErrorLine = -1;
            CurrentLine = Engine.CurrentLine;
            StatusText = $"Next statement: line {line}";
            return true;
        }

        /// <summary>에디터 실시간 파싱 결과 반영 (실행 중이 아닐 때만 오류 목록 갱신).</summary>
        public void UpdateSyntaxDiagnostics(string source)
        {
            if (State == EngineState.Running)
                return;
            var diags = ScriptEngine.Check(source);
            RefreshDiagnostics(diags.Select(d => new DiagnosticItem(d)));
        }

        private void RefreshDiagnostics(IEnumerable<DiagnosticItem> items)
        {
            Diagnostics.Clear();
            foreach (var d in items)
                Diagnostics.Add(d);
        }

        public void RequestGoToLine(int line) => GoToLineRequested?.Invoke(line);

        // ------------------------------------------------------------------
        // 파일
        // ------------------------------------------------------------------

        public string NewScript()
        {
            FilePath = "";
            IsDirty = false;
            _loadedSource = "";
            Engine.Load("");
            CurrentLine = -1;
            ErrorLine = -1;
            StatusText = "New script";
            return "// InspectionView script\n";
        }

        public string OpenScript(string path)
        {
            var text = File.ReadAllText(path);
            FilePath = path;
            IsDirty = false;
            Engine.ClearBreakpoints();
            StatusText = $"Opened {path}";
            return text;
        }

        public void SaveScript(string path, string text)
        {
            File.WriteAllText(path, text);
            FilePath = path;
            IsDirty = false;
            StatusText = $"Saved {path}";
        }

        // ------------------------------------------------------------------
        // 엔진 이벤트 (실행 스레드) → UI 반영
        // ------------------------------------------------------------------

        private void OnEngineStateChanged(EngineState state)
        {
            _ui(() =>
            {
                State = state;
                switch (state)
                {
                    case EngineState.Running:
                        ErrorLine = -1;
                        StatusText = "Running...";
                        break;
                    case EngineState.Paused:
                        StatusText = $"Paused at line {Engine.CurrentLine}";
                        break;
                    case EngineState.Finished:
                        StatusText = $"Finished ({Engine.Profiler.TotalElapsed.TotalMilliseconds:F1} ms in operators)";
                        break;
                    case EngineState.Idle:
                        StatusText = "Ready";
                        break;
                }
                Tick();
                if (state != EngineState.Running)
                    AutoDisplayLast();
            });
        }

        private void OnEngineError(ScriptRuntimeException ex)
        {
            _ui(() =>
            {
                ErrorLine = ex.Line;
                StatusText = $"Error at line {ex.Line}: {ex.Message}";
                lock (_output) _output.AppendLine($"[Error] line {ex.Line}: {ex.Message}");
                _outputDirty = true;
                Diagnostics.Add(new DiagnosticItem("Runtime", ex.Line, 1, 1, ex.Message));
                Tick();
            });
        }

        /// <summary>UI 타이머가 주기적으로 호출: 모아둔 변경을 반영한다.</summary>
        public void Tick()
        {
            int line = _pendingLine;
            if (line != int.MinValue)
            {
                _pendingLine = int.MinValue;
                CurrentLine = line;
            }

            string[] dirty;
            lock (_dirtyLock)
            {
                if (_dirtyVariables.Count == 0)
                {
                    dirty = Array.Empty<string>();
                }
                else
                {
                    dirty = _dirtyVariables.ToArray();
                    _dirtyVariables.Clear();
                }
            }

            foreach (var name in dirty)
            {
                var item = Variables.FirstOrDefault(v => v.Name == name);
                if (!Engine.Variables.TryGet(name, out var value))
                {
                    if (item != null) Variables.Remove(item);
                    continue;
                }
                if (item == null)
                {
                    item = new VariableItem(name);
                    Variables.Add(item);
                }
                item.Update(value);
            }

            if (_outputDirty)
            {
                _outputDirty = false;
                lock (_output) OutputText = _output.ToString();
            }

            if (_profileDirty)
            {
                _profileDirty = false;
                var records = Engine.Profiler.Records;
                if (records.Count < Profile.Count)
                    Profile.Clear();
                for (int i = Profile.Count; i < records.Count; i++)
                    Profile.Add(new ProfileItem(i + 1, records[i]));
            }
        }

        public void ClearProfile()
        {
            Engine.Profiler.Clear();
            Profile.Clear();
        }

        private void AutoDisplayLast()
        {
            if (!AutoDisplay)
                return;

            string name;
            lock (_dirtyLock)
            {
                name = _lastIconicVariable;
                _lastIconicVariable = "";
            }
            if (name.Length == 0)
                return;

            var item = Variables.FirstOrDefault(v => v.Name == name);
            if (item != null && item.Raw != null)
                DisplayRequested?.Invoke(item);
        }

        // ------------------------------------------------------------------
        // 연산자 트리
        // ------------------------------------------------------------------

        private void BuildOperatorTree()
        {
            OperatorTree.Clear();
            foreach (var category in Registry.Categories)
            {
                var node = new OperatorNode(category);
                foreach (var d in Registry.ByCategory(category))
                    node.Children.Add(new OperatorNode(d));
                OperatorTree.Add(node);
            }
        }

        public void Dispose()
        {
            Engine.Dispose();
        }
    }
}
