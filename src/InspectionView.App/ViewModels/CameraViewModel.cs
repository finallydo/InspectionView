using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using InspectionView.Core.Cameras;
using InspectionView.Core.Errors;
using InspectionView.Core.Types;
using InspectionView.Script.Runtime;

namespace InspectionView.App.ViewModels
{
    /// <summary>카메라 파라미터 한 줄 (Camera 창의 DataGrid). Value 편집 시 카메라에 적용.</summary>
    public sealed class CameraParameterItem : ObservableObject
    {
        private readonly CameraViewModel _owner;
        private string _value;

        public CameraParameterItem(CameraViewModel owner, CameraParameterInfo info, string value)
        {
            _owner = owner;
            Info = info;
            _value = value;
        }

        public CameraParameterInfo Info { get; }
        public string Name => Info.Name;
        public string Type => Info.Type.ToString();
        public bool IsReadOnly => Info.ReadOnly || Info.Type == CameraParameterType.Command;
        public string Range => double.IsNaN(Info.Min) && double.IsNaN(Info.Max)
            ? (Info.Choices.Length > 0 ? string.Join(" | ", Info.Choices) : "")
            : $"{(double.IsNaN(Info.Min) ? "" : Info.Min.ToString(CultureInfo.InvariantCulture))} .. {(double.IsNaN(Info.Max) ? "" : Info.Max.ToString(CultureInfo.InvariantCulture))}";
        public string Description => Info.Description;

        private static readonly string[] BooleanChoices = { "True", "False" };

        /// <summary>선택형 값 (Enum 항목, Boolean 은 True/False). 비어 있으면 직접 입력.</summary>
        public string[] Choices => Info.Type == CameraParameterType.Boolean ? BooleanChoices : Info.Choices;
        /// <summary>콤보박스로 고를 수 있는지 (선택지가 있고 쓰기 가능).</summary>
        public bool HasChoices => Choices.Length > 0 && !IsReadOnly;
        public bool ShowRangeText => !HasChoices;

        public string Value
        {
            get => _value;
            set
            {
                if (_value == value) return;
                string old = _value;
                _value = value;
                OnPropertyChanged();
                _owner.ApplyParameter(this, value, old);   // 작업 스레드에서 적용, 실패하면 old 로 되돌린다
            }
        }

        internal void Refresh(string value)
        {
            if (_value == value) return;
            _value = value;
            OnPropertyChanged(nameof(Value));
        }
    }

    /// <summary>
    /// IDE 의 Camera 창: 공급자/장치 선택 → 열기 → 스냅(변수로) / 라이브(활성 그래픽 창) / 파라미터 편집.
    /// 카메라는 이 뷰모델이 <see cref="CameraHandle"/> 로 소유하며, "Bind variable" 로 스크립트 변수에도 넣을 수 있다
    /// (스크립트의 GrabImage(Cam) 이 같은 장치를 쓴다).
    /// <para>
    /// 장치 I/O(열거, 열기/닫기, 파라미터 읽기/쓰기, 스냅, 라이브 시작/정지)는 모두 하나의 작업 큐에서 순서대로 실행하고
    /// 결과만 UI 스레드에 반영한다. GigE 장치는 왕복마다 지연이 있고 응답이 없으면 타임아웃까지 기다리므로
    /// UI 스레드에서 하면 프로그램 전체가 멈춘다.
    /// </para>
    /// </summary>
    public sealed class CameraViewModel : ObservableObject
    {
        private readonly VariableTable _variables;
        private readonly Action<string> _status;
        private readonly Dispatcher _dispatcher;
        private readonly object _queueLock = new object();
        private readonly DispatcherTimer _liveTimer;
        private Task _queue = Task.CompletedTask;
        private int _pending;
        private int _enumVersion;
        private CameraHandle? _handle;
        private bool _opening;
        private string _provider = SimulationCameraProvider.ProviderName;
        private string _device = "";
        private string _options = "";
        private string _variableName = "Image";
        private string _handleName = "Cam";
        private bool _live;
        private bool _liveRunning;
        private long _shownFrame = -1;
        private int _lastStatusRefresh;
        private bool _liveRestarting;          // 파라미터 적용을 위해 라이브를 잠깐 멈춘 동안 (Tick 이 라이브 종료로 오해하지 않게)
        // 라이브 통계 (1초마다 Info 에 표시)
        private long _statFrames0;
        private int _statShown;
        private double _statLatencySum;
        private int _statLatencyCount;
        private int _statStart;
        private string _info = "";

        public CameraViewModel(VariableTable variables, Action<string> status)
        {
            _variables = variables;
            _status = status;
            _dispatcher = Dispatcher.CurrentDispatcher;
            // 라이브 표시는 100ms 공용 타이머가 아니라 약 30fps 전용 타이머로 (프레임은 항상 최신 것만)
            // 우선순위는 입력보다 낮게 (Render 로 두면 마우스/키보드 처리가 밀려 프로그램 전체가 버벅인다)
            _liveTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(30) };
            _liveTimer.Tick += (_, __) => ShowLatestFrame();
            RefreshProviders();
        }

        public ObservableCollection<string> Providers { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> Devices { get; } = new ObservableCollection<string>();
        public ObservableCollection<CameraParameterItem> Parameters { get; } = new ObservableCollection<CameraParameterItem>();

        /// <summary>라이브 프레임 (UI 스레드). 인자는 호출 동안만 유효 (표시 측이 AddRef).</summary>
        public event Action<Image>? LiveFrame;

        /// <summary>스냅한 프레임 (UI 스레드). 인자는 호출 동안만 유효 (표시 측이 AddRef).</summary>
        public event Action<Image, string>? SnapFrame;

        public string Provider
        {
            get => _provider;
            set { if (Set(ref _provider, value ?? "")) RefreshDevices(); }
        }

        public string Device { get => _device; set => Set(ref _device, value ?? ""); }
        public string Options { get => _options; set => Set(ref _options, value); }
        public string VariableName { get => _variableName; set => Set(ref _variableName, value); }
        public string HandleName { get => _handleName; set => Set(ref _handleName, value); }
        public string Info { get => _info; private set => Set(ref _info, value); }

        public bool IsOpen => _handle != null && _handle.IsOpen;
        /// <summary>열 수 있는 상태 (열려 있지도, 여는 중도 아님).</summary>
        public bool IsClosed => _handle == null && !_opening;
        /// <summary>작업 큐에 처리 중인 장치 작업이 있는지.</summary>
        public bool IsBusy => Volatile.Read(ref _pending) > 0;
        public ICamera? Camera => IsOpen ? _handle!.Camera : null;

        // ------------------------------------------------------------------ 작업 큐

        /// <summary>장치 작업을 큐에 넣는다. <paramref name="done"/>/<paramref name="failed"/> 는 UI 스레드에서 호출된다.</summary>
        private void Enqueue<T>(string what, Func<T> work, Action<T>? done = null, Action<Exception>? failed = null)
        {
            Interlocked.Increment(ref _pending);
            OnPropertyChanged(nameof(IsBusy));
            lock (_queueLock)
            {
                _queue = _queue.ContinueWith(_ =>
                {
                    T result = default!;
                    Exception? error = null;
                    try { result = work(); }
                    catch (Exception ex) { error = ex; }
                    _dispatcher.BeginInvoke(new Action(() =>
                    {
                        Interlocked.Decrement(ref _pending);
                        OnPropertyChanged(nameof(IsBusy));
                        if (error != null)
                        {
                            if (failed != null) failed(error);
                            else _status($"{what}: {error.Message}");
                        }
                        else
                        {
                            done?.Invoke(result);
                        }
                    }));
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
        }

        private void Enqueue(string what, Action work, Action? done = null, Action<Exception>? failed = null)
            => Enqueue<bool>(what, () => { work(); return true; }, done == null ? null : new Action<bool>(_ => done()), failed);

        // ------------------------------------------------------------------ 공급자 / 장치

        public void RefreshProviders()
        {
            Providers.Clear();
            foreach (var p in CameraRegistry.Default.Providers)
                Providers.Add(p.IsAvailable ? p.Name : $"{p.Name} (unavailable)");
            if (!Providers.Contains(_provider) && Providers.Count > 0)
                Provider = Providers.First(p => !p.EndsWith(" (unavailable)", StringComparison.Ordinal));
            else
                RefreshDevices();
        }

        private string ProviderName => _provider.EndsWith(" (unavailable)", StringComparison.Ordinal)
            ? _provider.Substring(0, _provider.Length - " (unavailable)".Length) : _provider;

        /// <summary>장치 목록을 작업 큐에서 다시 찾는다 (GigE 탐색은 수백 ms~수 초).</summary>
        public void RefreshDevices()
        {
            Devices.Clear();
            var p = CameraRegistry.Default.Find(ProviderName);
            if (p == null) return;
            if (!p.IsAvailable)
            {
                Info = p.UnavailableReason ?? "SDK not available";
                return;
            }
            int version = ++_enumVersion;
            Info = $"Searching {p.Name} devices...";
            Enqueue($"{p.Name} enumeration", () => p.Enumerate(), found =>
            {
                if (version != _enumVersion) return;     // 그 사이 다른 공급자를 골랐다
                Devices.Clear();
                foreach (var d in found)
                    Devices.Add(d.DeviceId);
                Info = p.Name == FileCameraProvider.ProviderName
                    ? "Device = folder, 'folder/*.png' pattern or file. Options: Loop, Recursive, AsGray, Fps"
                    : found.Count == 0 ? "0 device(s)"
                    : $"{found.Count} device(s): {string.Join("; ", found.Select(d => d.DisplayName))}";
                if (string.IsNullOrEmpty(_device) && Devices.Count > 0)
                    Device = Devices[0];
            }, ex =>
            {
                if (version == _enumVersion) Info = $"Enumeration failed: {ex.Message}";
            });
        }

        // ------------------------------------------------------------------ 열기 / 닫기

        public void Open()
        {
            if (_opening) return;
            Close();
            string provider = ProviderName, device = _device, options = _options;
            _opening = true;
            RaiseOpenState();
            Info = $"Opening {provider} {device}...";
            Enqueue("Camera open", () =>
            {
                var cam = CameraRegistry.Default.Open(provider, device, options);
                var handle = new CameraHandle(cam);
                var items = cam.Parameters.Select(p => (info: p, value: Format(SafeGet(cam, p.Name)))).ToList();
                return (handle, items);
            }, r =>
            {
                _opening = false;
                _handle = r.handle;
                var cam = r.handle.Camera;
                Info = $"{cam.Info.Provider}: {cam.Info.DisplayName}";
                _status($"Camera opened: {cam}");
                Parameters.Clear();
                foreach (var (info, value) in r.items)
                    Parameters.Add(new CameraParameterItem(this, info, value));
                RaiseOpenState();
            }, ex =>
            {
                _opening = false;
                Info = ex.Message;
                _status($"Camera open failed: {ex.Message}");
                RaiseOpenState();
            });
        }

        public void Close()
        {
            if (_handle == null) return;
            StopLiveDisplay();
            if (_live)
            {
                _live = false;
                OnPropertyChanged(nameof(Live));
            }
            var h = _handle;
            _handle = null;
            Parameters.Clear();
            _shownFrame = -1;
            RaiseOpenState();
            Enqueue("Camera close", () =>
            {
                try { h.Close(); } catch { /* 닫는 중 오류 무시 */ }
                h.Release();
            });
        }

        private void RaiseOpenState()
        {
            OnPropertyChanged(nameof(IsOpen));
            OnPropertyChanged(nameof(IsClosed));
        }

        // ------------------------------------------------------------------ 스냅 / 라이브

        /// <summary>프레임 하나를 잡아 스크립트 변수에 넣고 <see cref="SnapFrame"/> 으로 알린다.</summary>
        public void Snap()
        {
            var cam = Camera;
            if (cam == null) return;
            string name = string.IsNullOrWhiteSpace(_variableName) ? "Image" : _variableName.Trim();
            Enqueue("Grab", () => cam.Grab(5000), img =>
            {
                _status($"Snapped {img} into '{name}'");
                _variables.Set(name, img);
                try { SnapFrame?.Invoke(img, name); }
                finally { img.Release(); }           // 변수 테이블이 AddRef 했으므로 생성 참조는 해제
                if (!_live) RefreshParameterValues();
            }, ex => _status($"Grab failed: {ex.Message}"));
        }

        public bool Live
        {
            get => _live;
            set
            {
                if (!Set(ref _live, value)) return;
                var cam = Camera;
                if (cam == null)
                {
                    _live = false;
                    OnPropertyChanged();
                    return;
                }
                if (value)
                {
                    Enqueue("Live", () => cam.StartAcquisition(), () =>
                    {
                        if (_live && Camera == cam) StartLiveDisplay();
                    }, ex =>
                    {
                        _live = false;
                        OnPropertyChanged(nameof(Live));
                        _status($"Camera: {ex.Message}");
                    });
                }
                else
                {
                    StopLiveDisplay();
                    Info = $"{cam.Info.Provider}: {cam.Info.DisplayName}";
                    Enqueue("Live", () => cam.StopAcquisition(), () => RefreshParameterValues());
                }
            }
        }

        private void StartLiveDisplay()
        {
            _liveRunning = true;
            _shownFrame = -1;
            ResetLiveStats();
            _liveTimer.Start();
        }

        private void ResetLiveStats()
        {
            _statFrames0 = Camera is CameraBase cb ? cb.FrameCount : 0;
            _statShown = 0;
            _statLatencySum = 0;
            _statLatencyCount = 0;
            _statStart = Environment.TickCount;
        }

        /// <summary>
        /// 라이브 통계를 Info 에 표시: 카메라에서 받은 fps, 화면에 그린 fps, 수신→표시 지연, 건너뛴/불완전 프레임.
        /// 수신 fps 가 카메라 설정보다 낮으면 카메라/네트워크 쪽, 표시 지연이 크면 이 프로그램 쪽 문제다.
        /// </summary>
        private void UpdateLiveStats()
        {
            var cam = Camera;
            int elapsed = Environment.TickCount - _statStart;
            if (cam == null || elapsed < 1000) return;
            double sec = elapsed / 1000.0;
            long frames = cam is CameraBase cb ? cb.FrameCount : 0;
            double camFps = (frames - _statFrames0) / sec;
            double shownFps = _statShown / sec;
            string latency = _statLatencyCount > 0 ? $"{_statLatencySum / _statLatencyCount:0} ms" : "n/a";
            string Get(string n) => Format(SafeGetStatus(cam, n));
            Info = $"Live: received {camFps:0.0} fps, shown {shownFps:0.0} fps, receive→show {latency}, skipped {Get("SkippedFrames")}, incomplete {Get("IncompleteFrames")}";
            ResetLiveStats();
        }

        /// <summary>상태 값만 읽는다 (장치 기능이면 읽지 않음: UI 스레드에서 장치 I/O 금지).</summary>
        private static object? SafeGetStatus(ICamera cam, string name)
        {
            foreach (var p in cam.Parameters)
                if (p.IsStatus && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                    return SafeGet(cam, name);
            return null;
        }

        private void StopLiveDisplay()
        {
            _liveRunning = false;
            _liveTimer.Stop();
        }

        /// <summary>라이브 타이머(약 30ms): 새 프레임이 있으면 최신 것 하나만 활성 창에 보낸다.</summary>
        private void ShowLatestFrame()
        {
            var cam = Camera;
            if (cam == null || !_liveRunning) return;
            long count = cam is CameraBase cb ? cb.FrameCount : _shownFrame + 1;
            if (count == _shownFrame) return;
            var frame = cam.GetLatestFrame();
            if (frame == null) return;
            _shownFrame = count;
            try
            {
                LiveFrame?.Invoke(frame);
                _statShown++;
                if (frame.Metadata.TryGetValue("ReceivedTicks", out var t) && t is long received)
                {
                    _statLatencySum += (Stopwatch.GetTimestamp() - received) * 1000.0 / Stopwatch.Frequency;
                    _statLatencyCount++;
                }
            }
            finally { frame.Release(); }
        }

        /// <summary>열린 카메라 핸들을 스크립트 변수로 바인딩한다 (스크립트의 GrabImage(Cam) 용).</summary>
        public void BindVariable()
        {
            if (_handle == null || !_handle.IsOpen) return;
            _variables.Set(string.IsNullOrWhiteSpace(_handleName) ? "Cam" : _handleName.Trim(), _handle);
            _status($"Camera bound to variable '{_handleName}'");
        }

        // ------------------------------------------------------------------ 파라미터

        /// <summary>모든 파라미터 값을 작업 큐에서 다시 읽는다.</summary>
        private void RefreshParameterValues()
        {
            var cam = Camera;
            if (cam == null || Parameters.Count == 0) return;
            var names = Parameters.Select(i => i.Name).ToList();
            Enqueue("Camera parameters", () => names.Select(n => (name: n, value: Format(SafeGet(cam, n)))).ToList(), values =>
            {
                if (Camera != cam) return;
                foreach (var (name, value) in values)
                    Parameters.FirstOrDefault(i => i.Name == name)?.Refresh(value);
            });
        }

        /// <summary>상태 값(장치 왕복 없음: FrameCount, IncompleteFrames, LastError ...)만 UI 스레드에서 바로 갱신.</summary>
        private void RefreshStatusValues()
        {
            var cam = Camera;
            if (cam == null) return;
            foreach (var item in Parameters)
                if (item.Info.IsStatus)
                    item.Refresh(Format(SafeGet(cam, item.Name)));
        }

        private static object? SafeGet(ICamera cam, string name)
        {
            try { return cam.GetParameter(name); } catch { return null; }
        }

        internal static string Format(object? v)
            => v == null ? "" : v is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : v.ToString() ?? "";

        /// <summary>
        /// 값을 작업 큐에서 쓰고 장치에서 다시 읽어 표시한다. 실패하면 <paramref name="previous"/> 로 되돌린다.
        /// 라이브가 아니면 다른 값(자동 노출, 범위 등)이 바뀌었을 수 있으므로 전체를 다시 읽는다.
        /// </summary>
        internal void ApplyParameter(CameraParameterItem item, string value, string previous)
        {
            var cam = Camera;
            if (cam == null)
            {
                item.Refresh(previous);
                return;
            }
            object? arg = item.Info.Type == CameraParameterType.Command ? null : value;
            bool live = _liveRunning;
            if (live) _liveRestarting = true;
            Enqueue($"Camera parameter '{item.Name}'", () =>
            {
                try
                {
                    cam.SetParameter(item.Name, arg);
                }
                catch (Exception) when (cam.IsAcquiring)
                {
                    // 라이브 중 잠기는 기능(PixelFormat, Width, Height ...): 잠깐 멈추고 쓴 뒤 다시 시작
                    cam.StopAcquisition();
                    try
                    {
                        cam.SetParameter(item.Name, arg);
                    }
                    finally
                    {
                        cam.StartAcquisition();
                    }
                }
                return Format(SafeGet(cam, item.Name));
            }, readBack =>
            {
                _liveRestarting = false;
                item.Refresh(readBack);
                if (!_live) RefreshParameterValues();
                else if (live) _shownFrame = -1;          // 형식/크기가 바뀌었을 수 있으니 다음 프레임을 바로 표시
            }, ex =>
            {
                _liveRestarting = false;
                item.Refresh(previous);
                _status($"Camera parameter '{item.Name}': {ex.Message}");
            });
        }

        /// <summary>명령 파라미터(소프트웨어 트리거 등) 실행.</summary>
        public void ExecuteCommand(CameraParameterItem item) => ApplyParameter(item, "", item.Value);

        /// <summary>UI 타이머(100ms): 라이브 상태 확인과 상태 값 갱신 (1초마다).</summary>
        public void Tick()
        {
            if (!_liveRunning) return;
            var cam = Camera;
            if (cam == null || (!cam.IsAcquiring && !_liveRestarting))
            {
                StopLiveDisplay();
                if (_live) { _live = false; OnPropertyChanged(nameof(Live)); }
                return;
            }
            if (Environment.TickCount - _lastStatusRefresh >= 1000)
            {
                _lastStatusRefresh = Environment.TickCount;
                RefreshStatusValues();
                UpdateLiveStats();
            }
        }

        /// <summary>프로그램 종료: 카메라를 닫고 큐가 끝나기를 잠시 기다린다 (장치를 놓아 줘야 다음에 바로 열린다).</summary>
        public void Dispose()
        {
            Close();
            Task q;
            lock (_queueLock) q = _queue;
            try { q.Wait(5000); } catch { /* 종료 중 오류 무시 */ }
        }
    }
}
