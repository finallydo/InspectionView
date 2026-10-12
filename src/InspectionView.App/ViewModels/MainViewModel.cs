using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using InspectionView.Core.Backends;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;
using InspectionView.Runtime;
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
        private readonly Queue<(object? Value, int Window, bool Clear)> _displayQueue = new Queue<(object?, int, bool)>();
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
        private readonly List<string> _changedIconics = new List<string>();
        private bool _autoDisplay = true;
        private string _outputText = "";

        public ScriptEngine Engine { get; }
        public OperatorRegistry Registry { get; }

        public ObservableCollection<VariableItem> Variables { get; } = new ObservableCollection<VariableItem>();
        public ObservableCollection<DiagnosticItem> Diagnostics { get; } = new ObservableCollection<DiagnosticItem>();
        public ObservableCollection<ProfileItem> Profile { get; } = new ObservableCollection<ProfileItem>();
        public ObservableCollection<OperatorNode> OperatorTree { get; } = new ObservableCollection<OperatorNode>();

        // ---- Job ----
        private JobDocument _job = new JobDocument();
        private string _jobPath = "";
        private string _jobName = "";
        private string _jobDescription = "";

        public ObservableCollection<TerminalItem> JobInputs { get; } = new ObservableCollection<TerminalItem>();
        public ObservableCollection<TerminalItem> JobOutputs { get; } = new ObservableCollection<TerminalItem>();
        public ObservableCollection<ResourceItem> JobResources { get; } = new ObservableCollection<ResourceItem>();

        public string JobPath
        {
            get => _jobPath;
            private set
            {
                if (Set(ref _jobPath, value))
                    OnPropertyChanged(nameof(Title));
            }
        }

        public string JobName
        {
            get => _jobName;
            set => Set(ref _jobName, value ?? "");
        }

        public string JobDescription
        {
            get => _jobDescription;
            set => Set(ref _jobDescription, value ?? "");
        }
        public IReadOnlyList<BackendKind> Backends { get; } = new[] { BackendKind.Auto, BackendKind.Cpu, BackendKind.Parallel, BackendKind.Gpu };

        /// <summary>UI 가 알아야 하는 요청: 캐럿을 특정 줄로 (오류 더블클릭 등).</summary>
        public event Action<int>? GoToLineRequested;

        /// <summary>그래픽 창에 변수를 표시해 달라는 요청 (변수창 선택).</summary>
        public event Action<VariableItem>? DisplayRequested;

        /// <summary>실행이 멈춘 뒤 바뀐 아이코닉 변수들(순서대로)을 표시해 달라는 요청 (자동 표시).</summary>
        public event Action<IReadOnlyList<VariableItem>>? ResultsRequested;

        /// <summary>스크립트의 Display(Value, Window): (값, 창 번호). 값은 호출 동안만 유효하다 (호출 후 Release 됨).</summary>
        public event Action<object, int>? ScriptDisplayRequested;

        /// <summary>스크립트의 ClearWindow(Window).</summary>
        public event Action<int>? ScriptClearWindowRequested;

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
                    {
                        _changedIconics.Remove(name);
                        _changedIconics.Add(name);
                    }
                }
            };
            Engine.Profiler.Recorded += _ => _profileDirty = true;
            Engine.DisplayRequested += (value, window) =>
            {
                // 실행 스레드: UI 가 처리할 때까지 살아 있도록 AddRef
                if (value is Core.Memory.IconicObject io)
                    io.AddRef();
                lock (_displayQueue)
                    _displayQueue.Enqueue((value, window, false));
            };
            Engine.ClearWindowRequested += window =>
            {
                lock (_displayQueue)
                    _displayQueue.Enqueue((null, window, true));
            };

            Camera = new CameraViewModel(Engine.Variables, msg => { StatusText = msg; Log(msg); });

            BuildOperatorTree();
        }

        /// <summary>Camera 창 뷰모델.</summary>
        public CameraViewModel Camera { get; }

        private void Log(string message)
        {
            lock (_output) _output.AppendLine(message);
            _outputDirty = true;
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

        public string Title
        {
            get
            {
                string name = !string.IsNullOrEmpty(JobPath) ? Path.GetFileName(JobPath)
                            : !string.IsNullOrEmpty(FilePath) ? Path.GetFileName(FilePath)
                            : "Untitled.ivs";
                return name + (IsDirty ? " *" : "") + " - InspectionView";
            }
        }

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
        // Job (패키지)
        // ------------------------------------------------------------------

        /// <summary>새 Job: 터미널/리소스/경로를 비운다. 스크립트는 그대로 둔다.</summary>
        public void NewJob()
        {
            _job.Dispose();
            _job = new JobDocument();
            JobPath = "";
            JobName = "";
            JobDescription = "";
            JobInputs.Clear();
            JobOutputs.Clear();
            JobResources.Clear();
            StatusText = "New job";
        }

        /// <summary>패키지를 열어 터미널/리소스를 채우고 리소스를 변수로 로드한다. 스크립트 텍스트를 돌려준다.</summary>
        public string OpenJob(string path)
        {
            var doc = JobPackage.Load(path);
            _job.Dispose();
            _job = doc;
            JobPath = path;
            FilePath = "";
            JobName = doc.Manifest.Name;
            JobDescription = doc.Manifest.Description;

            JobInputs.Clear();
            foreach (var t in doc.Manifest.Inputs) JobInputs.Add(new TerminalItem(t));
            JobOutputs.Clear();
            foreach (var t in doc.Manifest.Outputs) JobOutputs.Add(new TerminalItem(t));
            RefreshJobResources();

            if (State != EngineState.Running)
            {
                foreach (var kv in doc.AllResources())
                    Engine.Variables.Set(kv.Key, kv.Value);
                Tick();
            }

            Engine.ClearBreakpoints();
            IsDirty = false;
            StatusText = $"Opened job {path}";
            return doc.Script;
        }

        /// <summary>터미널 편집 내용과 스크립트를 패키지로 저장한다.</summary>
        public void SaveJob(string path, string script)
        {
            SyncJobManifest();
            _job.Script = script;
            JobPackage.Save(path, _job);
            JobPath = path;
            IsDirty = false;
            StatusText = $"Saved job {path}";
        }

        private void SyncJobManifest()
        {
            var m = _job.Manifest;
            m.Name = JobName;
            m.Description = JobDescription;
            m.Inputs.Clear();
            foreach (var t in JobInputs)
                if (t.Name.Length > 0) m.Inputs.Add(t.ToTerminal());
            m.Outputs.Clear();
            foreach (var t in JobOutputs)
                if (t.Name.Length > 0) m.Outputs.Add(t.ToTerminal());
        }

        private void RefreshJobResources()
        {
            JobResources.Clear();
            foreach (var kv in _job.Regions) JobResources.Add(new ResourceItem(kv.Key, TerminalType.Region, kv.Value.ToString() ?? ""));
            foreach (var kv in _job.Images) JobResources.Add(new ResourceItem(kv.Key, TerminalType.Image, kv.Value.ToString() ?? ""));
            foreach (var kv in _job.Parameters) JobResources.Add(new ResourceItem(kv.Key, TerminalType.Tuple, kv.Value.ToString()));
        }

        /// <summary>현재 변수를 패키지 리소스로 추가한다 (Region / Image / Tuple).</summary>
        public bool AddJobResource(string variableName)
        {
            if (State == EngineState.Running || !Engine.Variables.TryGet(variableName, out var value))
            {
                StatusText = $"Variable '{variableName}' is not available";
                return false;
            }

            try
            {
                switch (value)
                {
                    case Region r: _job.SetRegion(variableName, r); break;
                    case Image i: _job.SetImage(variableName, i); break;
                    case TupleValue t: _job.SetParameter(variableName, t); break;
                    default:
                        StatusText = $"'{variableName}' ({ScriptValues.TypeName(value)}) cannot be stored in a job (Region, Image or Tuple only)";
                        return false;
                }
            }
            catch (ArgumentException ex)
            {
                StatusText = ex.Message;
                return false;
            }

            RefreshJobResources();
            IsDirty = true;
            StatusText = $"Added '{variableName}' to the job";
            return true;
        }

        public void RemoveJobResource(string name)
        {
            if (_job.RemoveResource(name))
            {
                RefreshJobResources();
                IsDirty = true;
            }
        }

        /// <summary>
        /// 런타임과 같은 경로로 Job 을 시험 실행한다: 입력 터미널은 같은 이름의 현재 변수에서 가져온다.
        /// 결과는 출력 창에 기록한다.
        /// </summary>
        public void TestRunJob(string script)
        {
            if (State == EngineState.Running)
                return;

            SyncJobManifest();
            _job.Script = script;

            var inputs = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var t in _job.Manifest.Inputs)
            {
                if (Engine.Variables.TryGet(t.Name, out var v) && v != null)
                    inputs[t.Name] = v;
            }

            try
            {
                using (var runner = new JobRunner(_job, Registry, BackendSettings.Global))
                {
                    runner.Output += line => AppendOutput("[job] " + line);
                    using (var result = runner.Run(inputs))
                    {
                        if (result.Success)
                        {
                            AppendOutput($"[job] OK in {result.Elapsed.TotalMilliseconds:F1} ms, {result.Records.Count} operator call(s)");
                            foreach (var kv in result.Outputs)
                                AppendOutput($"[job]   {kv.Key} = {ScriptValues.Format(kv.Value)}");
                            StatusText = "Job test run OK";
                        }
                        else
                        {
                            AppendOutput($"[job] FAILED {result.Code} at line {result.Line}: {result.Message}");
                            StatusText = $"Job test run failed: {result.Code}";
                            if (result.Line > 0)
                                ErrorLine = result.Line;
                        }
                    }
                }
            }
            catch (JobException ex)
            {
                AppendOutput($"[job] syntax error: {ex.Message}");
                StatusText = "Job test run failed: syntax error";
            }
            catch (Exception ex)
            {
                AppendOutput($"[job] error: {ex.Message}");
                StatusText = "Job test run failed";
            }
            _outputDirty = true;
            Tick();
        }

        private void AppendOutput(string line)
        {
            lock (_output) _output.AppendLine(line);
            _outputDirty = true;
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
            Camera.Tick();

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

            // 썸네일은 엔진이 멈춰 있을 때만 (실행 중에는 객체가 교체될 수 있음)
            if (State != EngineState.Running)
            {
                foreach (var v in Variables)
                {
                    if (v.ThumbnailDirty)
                        v.RefreshThumbnail();
                }
            }

            // 스크립트 Display / ClearWindow
            while (true)
            {
                (object? Value, int Window, bool Clear) req;
                lock (_displayQueue)
                {
                    if (_displayQueue.Count == 0)
                        break;
                    req = _displayQueue.Dequeue();
                }

                if (req.Clear)
                {
                    ScriptClearWindowRequested?.Invoke(req.Window);
                    continue;
                }

                try
                {
                    if (req.Value != null)
                        ScriptDisplayRequested?.Invoke(req.Value, req.Window);
                }
                finally
                {
                    if (req.Value is Core.Memory.IconicObject io)
                        io.Release();
                }
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
            string[] names;
            lock (_dirtyLock)
            {
                names = _changedIconics.ToArray();
                _changedIconics.Clear();
            }
            if (!AutoDisplay || names.Length == 0)
                return;

            var items = new List<VariableItem>();
            foreach (var name in names)
            {
                var item = Variables.FirstOrDefault(v => v.Name == name);
                if (item != null && item.Raw != null)
                    items.Add(item);
            }
            if (items.Count > 0)
                ResultsRequested?.Invoke(items);
        }

        /// <summary>뷰어 진단 로그를 출력 창에 기록한다.</summary>
        public void LogViewer(string message)
        {
            AppendOutput("[graphics] " + message);
            Tick();
        }

        /// <summary>뷰어 OpenGL 오류를 출력 창과 상태줄에 기록한다.</summary>
        public void ReportViewerError(string message)
        {
            AppendOutput("[graphics] " + message);
            StatusText = "Graphics error (see Output)";
            Tick();
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
            Camera.Dispose();
            Engine.Dispose();
            _job.Dispose();
        }
    }
}
