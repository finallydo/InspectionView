using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
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

        public string Value
        {
            get => _value;
            set
            {
                if (_value == value) return;
                string old = _value;
                _value = value;
                OnPropertyChanged();
                if (!_owner.ApplyParameter(this, value))
                {
                    _value = old;
                    OnPropertyChanged();
                }
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
    /// </summary>
    public sealed class CameraViewModel : ObservableObject
    {
        private readonly VariableTable _variables;
        private readonly Action<string> _status;
        private CameraHandle? _handle;
        private string _provider = SimulationCameraProvider.ProviderName;
        private string _device = "";
        private string _options = "";
        private string _variableName = "Image";
        private string _handleName = "Cam";
        private bool _live;
        private long _shownFrame = -1;
        private string _info = "";

        public CameraViewModel(VariableTable variables, Action<string> status)
        {
            _variables = variables;
            _status = status;
            RefreshProviders();
        }

        public ObservableCollection<string> Providers { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> Devices { get; } = new ObservableCollection<string>();
        public ObservableCollection<CameraParameterItem> Parameters { get; } = new ObservableCollection<CameraParameterItem>();

        /// <summary>라이브 프레임 (UI 스레드, Tick). 인자는 호출 동안만 유효 (표시 측이 AddRef).</summary>
        public event Action<Image>? LiveFrame;

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
        public bool IsClosed => !IsOpen;
        public ICamera? Camera => IsOpen ? _handle!.Camera : null;

        public bool Live
        {
            get => _live;
            set
            {
                if (!Set(ref _live, value)) return;
                try
                {
                    var cam = Camera;
                    if (cam == null) { _live = false; OnPropertyChanged(); return; }
                    if (value) cam.StartAcquisition(); else cam.StopAcquisition();
                }
                catch (Exception ex)
                {
                    _live = false;
                    OnPropertyChanged();
                    _status($"Camera: {ex.Message}");
                }
            }
        }

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
            try
            {
                var found = p.Enumerate();
                foreach (var d in found)
                    Devices.Add(d.DeviceId);
                Info = p.Name == FileCameraProvider.ProviderName
                    ? "Device = folder, 'folder/*.png' pattern or file. Options: Loop, Recursive, AsGray, Fps"
                    : found.Count == 0 ? "0 device(s)"
                    : $"{found.Count} device(s): {string.Join("; ", found.Select(d => d.DisplayName))}";
            }
            catch (Exception ex)
            {
                Info = $"Enumeration failed: {ex.Message}";
            }
            if (string.IsNullOrEmpty(_device) && Devices.Count > 0)
                Device = Devices[0];
        }

        public void Open()
        {
            Close();
            try
            {
                var cam = CameraRegistry.Default.Open(ProviderName, _device, _options);
                _handle = new CameraHandle(cam);
                Info = $"{cam.Info.Provider}: {cam.Info.DisplayName}";
                _status($"Camera opened: {cam}");
                RefreshParameters();
            }
            catch (Exception ex)
            {
                Info = ex.Message;
                _status($"Camera open failed: {ex.Message}");
            }
            RaiseOpenState();
        }

        public void Close()
        {
            if (_handle == null) return;
            _live = false;
            OnPropertyChanged(nameof(Live));
            var h = _handle;
            _handle = null;
            try { h.Close(); } catch { /* 닫는 중 오류 무시 */ }
            h.Release();
            Parameters.Clear();
            _shownFrame = -1;
            RaiseOpenState();
        }

        private void RaiseOpenState()
        {
            OnPropertyChanged(nameof(IsOpen));
            OnPropertyChanged(nameof(IsClosed));
        }

        /// <summary>프레임 하나를 잡아 스크립트 변수에 넣는다.</summary>
        public bool Snap()
        {
            var cam = Camera;
            if (cam == null) return false;
            try
            {
                var img = cam.Grab(5000);
                string name = string.IsNullOrWhiteSpace(_variableName) ? "Image" : _variableName.Trim();
                _status($"Snapped {img} into '{name}'");
                _variables.Set(name, img);
                img.Release();                       // 변수 테이블이 AddRef 했으므로 생성 참조는 해제
                RefreshParameterValues();
                return true;
            }
            catch (Exception ex)
            {
                _status($"Grab failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>열린 카메라 핸들을 스크립트 변수로 바인딩한다 (스크립트의 GrabImage(Cam) 용).</summary>
        public void BindVariable()
        {
            if (_handle == null || !_handle.IsOpen) return;
            _variables.Set(string.IsNullOrWhiteSpace(_handleName) ? "Cam" : _handleName.Trim(), _handle);
            _status($"Camera bound to variable '{_handleName}'");
        }

        private void RefreshParameters()
        {
            Parameters.Clear();
            var cam = Camera;
            if (cam == null) return;
            foreach (var p in cam.Parameters)
                Parameters.Add(new CameraParameterItem(this, p, Format(SafeGet(cam, p.Name))));
        }

        private void RefreshParameterValues()
        {
            var cam = Camera;
            if (cam == null) return;
            foreach (var item in Parameters)
                item.Refresh(Format(SafeGet(cam, item.Name)));
        }

        private static object? SafeGet(ICamera cam, string name)
        {
            try { return cam.GetParameter(name); } catch { return null; }
        }

        internal static string Format(object? v)
            => v == null ? "" : v is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : v.ToString() ?? "";

        internal bool ApplyParameter(CameraParameterItem item, string value)
        {
            var cam = Camera;
            if (cam == null) return false;
            try
            {
                cam.SetParameter(item.Name, item.Info.Type == CameraParameterType.Command ? null : value);
                item.Refresh(Format(SafeGet(cam, item.Name)));
                return true;
            }
            catch (VisionException ex)
            {
                _status($"Camera parameter '{item.Name}': {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                _status($"Camera parameter '{item.Name}': {ex.Message}");
                return false;
            }
        }

        /// <summary>명령 파라미터(소프트웨어 트리거 등) 실행.</summary>
        public void ExecuteCommand(CameraParameterItem item) => ApplyParameter(item, "");

        /// <summary>UI 타이머(100ms) 에서 호출: 라이브 중이면 새 프레임을 활성 창에 보낸다.</summary>
        public void Tick()
        {
            if (!_live) return;
            var cam = Camera;
            if (cam == null || !cam.IsAcquiring)
            {
                if (_live) { _live = false; OnPropertyChanged(nameof(Live)); }
                return;
            }
            long count = cam is CameraBase cb ? cb.FrameCount : _shownFrame + 1;
            if (count == _shownFrame) return;
            var frame = cam.GetLatestFrame();
            if (frame == null) return;
            _shownFrame = count;
            try { LiveFrame?.Invoke(frame); }
            finally { frame.Release(); }
            if ((count & 7) == 0) RefreshParameterValues();
        }

        public void Dispose() => Close();
    }
}
