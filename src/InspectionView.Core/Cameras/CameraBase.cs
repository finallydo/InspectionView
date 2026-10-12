using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using InspectionView.Core.Errors;
using InspectionView.Core.Types;

namespace InspectionView.Core.Cameras
{
    /// <summary>
    /// <see cref="ICamera"/> 의 공통 구현: 파라미터 테이블(getter/setter 등록), 연속 취득 스레드(<see cref="GrabCore"/> 반복),
    /// 최근 프레임 보관. 벤더 구현은 <see cref="GrabCore"/>, <see cref="CloseCore"/> 와 파라미터 등록만 하면 된다.
    /// SDK 가 자체 콜백을 제공하면 <see cref="StartAcquisitionCore"/>/<see cref="StopAcquisitionCore"/> 를 재정의하고
    /// 콜백에서 <see cref="PublishFrame"/> 을 호출한다.
    /// </summary>
    public abstract class CameraBase : ICamera
    {
        private readonly object _lock = new object();
        private readonly List<CameraParameterInfo> _paramInfos = new List<CameraParameterInfo>();
        private readonly Dictionary<string, (Func<object?> get, Action<object?>? set)> _params =
            new Dictionary<string, (Func<object?>, Action<object?>?)>(StringComparer.OrdinalIgnoreCase);

        private Thread? _thread;
        private volatile bool _acquiring;
        private Image? _latest;
        private long _frameCount;
        private int _open = 1;
        private readonly AutoResetEvent _frameEvent = new AutoResetEvent(false);

        protected CameraBase(CameraInfo info)
        {
            Info = info ?? throw new ArgumentNullException(nameof(info));
            RegisterParameter(new CameraParameterInfo("FrameCount", CameraParameterType.Integer, "Frames acquired since open.", readOnly: true), () => Interlocked.Read(ref _frameCount));
        }

        public CameraInfo Info { get; }
        public bool IsOpen => Volatile.Read(ref _open) != 0;
        public bool IsAcquiring => _acquiring;
        public long FrameCount => Interlocked.Read(ref _frameCount);

        public event Action<ICamera, Image>? FrameReceived;

        /// <summary>취득 루프 사이의 최소 간격(ms). 0 이면 없음. 가상 카메라의 Fps 구현용.</summary>
        protected int FrameIntervalMs { get; set; }

        // ------------------------------------------------------------------ 파라미터

        public IReadOnlyList<CameraParameterInfo> Parameters => _paramInfos;

        protected void RegisterParameter(CameraParameterInfo info, Func<object?> getter, Action<object?>? setter = null)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));
            lock (_lock)
            {
                int idx = _paramInfos.FindIndex(p => string.Equals(p.Name, info.Name, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) _paramInfos[idx] = info; else _paramInfos.Add(info);
                _params[info.Name] = (getter, setter);
            }
        }

        public CameraParameterInfo? FindParameter(string name)
        {
            lock (_lock)
                return _paramInfos.Find(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public object? GetParameter(string name)
        {
            ThrowIfClosed();
            (Func<object?> get, Action<object?>? set) entry;
            lock (_lock)
            {
                if (!_params.TryGetValue(name ?? "", out entry))
                    throw new VisionException(ErrorCode.CameraParameterInvalid, $"Camera '{Info.DisplayName}' has no parameter '{name}'.");
            }
            return entry.get();
        }

        public void SetParameter(string name, object? value)
        {
            ThrowIfClosed();
            (Func<object?> get, Action<object?>? set) entry;
            CameraParameterInfo? info;
            lock (_lock)
            {
                if (!_params.TryGetValue(name ?? "", out entry))
                    throw new VisionException(ErrorCode.CameraParameterInvalid, $"Camera '{Info.DisplayName}' has no parameter '{name}'.");
                info = FindParameter(name!);
            }
            if (entry.set == null || (info != null && info.ReadOnly))
                throw new VisionException(ErrorCode.CameraParameterInvalid, $"Camera parameter '{name}' is read-only.");
            entry.set(info != null ? Coerce(info, value) : value);
        }

        /// <summary>파라미터 타입에 맞게 값을 변환한다 (스크립트의 Tuple/문자열 허용).</summary>
        protected static object? Coerce(CameraParameterInfo info, object? value)
        {
            if (value is TupleValue t)
                value = t.IsEmpty ? null : t[0];

            switch (info.Type)
            {
                case CameraParameterType.Command:
                    return null;
                case CameraParameterType.Integer:
                {
                    long v = value is string s ? long.Parse(s, CultureInfo.InvariantCulture) : Convert.ToInt64(value, CultureInfo.InvariantCulture);
                    CheckRange(info, v);
                    return v;
                }
                case CameraParameterType.Float:
                {
                    double v = value is string s ? double.Parse(s, CultureInfo.InvariantCulture) : Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    CheckRange(info, v);
                    return v;
                }
                case CameraParameterType.Boolean:
                    if (value is string bs) return bs.Equals("true", StringComparison.OrdinalIgnoreCase) || bs == "1";
                    return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                case CameraParameterType.Enum:
                {
                    string s = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                    if (info.Choices.Length > 0 && Array.FindIndex(info.Choices, c => string.Equals(c, s, StringComparison.OrdinalIgnoreCase)) < 0)
                        throw new VisionException(ErrorCode.CameraParameterInvalid, $"'{s}' is not a valid value for '{info.Name}' (choices: {string.Join(", ", info.Choices)}).");
                    return s;
                }
                default:
                    return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            }
        }

        private static void CheckRange(CameraParameterInfo info, double v)
        {
            if (!double.IsNaN(info.Min) && v < info.Min) throw new VisionException(ErrorCode.CameraParameterInvalid, $"'{info.Name}' = {v} is below the minimum {info.Min}.");
            if (!double.IsNaN(info.Max) && v > info.Max) throw new VisionException(ErrorCode.CameraParameterInvalid, $"'{info.Name}' = {v} is above the maximum {info.Max}.");
        }

        // ------------------------------------------------------------------ 취득

        /// <summary>프레임 하나를 취득해 새 Image(참조 1) 로 돌려준다. 시간 초과 시 <see cref="ErrorCode.CameraTimeout"/> 예외.</summary>
        protected abstract Image GrabCore(int timeoutMs);

        /// <summary>장치 해제.</summary>
        protected abstract void CloseCore();

        public Image Grab(int timeoutMs)
        {
            ThrowIfClosed();
            if (_acquiring)
            {
                // 연속 취득 중: 다음 프레임을 기다린다
                long before = FrameCount;
                var deadline = Environment.TickCount + Math.Max(0, timeoutMs);
                while (FrameCount == before)
                {
                    int remaining = (int)(deadline - Environment.TickCount);
                    if (remaining <= 0 || !_acquiring)
                        throw new VisionException(ErrorCode.CameraTimeout, $"No frame from '{Info.DisplayName}' within {timeoutMs} ms.");
                    _frameEvent.WaitOne(Math.Min(remaining, 50));
                }
                var latest = GetLatestFrame();
                if (latest == null)
                    throw new VisionException(ErrorCode.CameraTimeout, $"No frame from '{Info.DisplayName}'.");
                return latest;
            }

            var img = GrabCore(timeoutMs);
            OnFrame(img, publish: false);
            return img;
        }

        public void StartAcquisition()
        {
            ThrowIfClosed();
            lock (_lock)
            {
                if (_acquiring) return;
                _acquiring = true;
            }
            try
            {
                StartAcquisitionCore();
            }
            catch
            {
                _acquiring = false;
                throw;
            }
        }

        public void StopAcquisition()
        {
            Thread? t;
            lock (_lock)
            {
                if (!_acquiring) return;
                _acquiring = false;
                t = _thread;
                _thread = null;
            }
            StopAcquisitionCore();
            if (t != null && t != Thread.CurrentThread)
                t.Join(5000);
        }

        /// <summary>기본 구현: <see cref="GrabCore"/> 를 반복하는 스레드.</summary>
        protected virtual void StartAcquisitionCore()
        {
            var t = new Thread(AcquisitionLoop) { IsBackground = true, Name = $"Camera {Info.DisplayName}" };
            lock (_lock) _thread = t;
            t.Start();
        }

        protected virtual void StopAcquisitionCore()
        {
        }

        private void AcquisitionLoop()
        {
            while (_acquiring && IsOpen)
            {
                Image? img = null;
                try
                {
                    img = GrabCore(1000);
                }
                catch (VisionException ex) when (ex.Code == ErrorCode.CameraTimeout)
                {
                    continue;
                }
                catch (Exception ex)
                {
                    LastError = ex;
                    Thread.Sleep(50);
                    continue;
                }
                PublishFrame(img);
                if (FrameIntervalMs > 0)
                    Thread.Sleep(FrameIntervalMs);
            }
        }

        /// <summary>연속 취득 중 새 프레임을 등록한다 (소유권을 가져간다). SDK 콜백 구현에서도 호출.</summary>
        protected void PublishFrame(Image frame)
        {
            OnFrame(frame, publish: true);
            frame.Release();
        }

        private void OnFrame(Image frame, bool publish)
        {
            Interlocked.Increment(ref _frameCount);
            if (publish)
            {
                frame.AddRef();
                Image? old;
                lock (_lock)
                {
                    old = _latest;
                    _latest = frame;
                }
                old?.Release();
                _frameEvent.Set();
                try { FrameReceived?.Invoke(this, frame); } catch (Exception ex) { LastError = ex; }
            }
        }

        /// <summary>취득 루프에서 난 마지막 오류 (진단용).</summary>
        public Exception? LastError { get; private set; }

        public Image? GetLatestFrame()
        {
            lock (_lock)
            {
                if (_latest == null) return null;
                _latest.AddRef();
                return _latest;
            }
        }

        // ------------------------------------------------------------------ 수명

        protected void ThrowIfClosed()
        {
            if (!IsOpen)
                throw new VisionException(ErrorCode.CameraClosed, $"Camera '{Info.DisplayName}' is closed.");
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _open, 0) == 0)
                return;
            try { StopAcquisition(); } catch { /* 종료 중 오류 무시 */ }
            try { CloseCore(); } catch { /* 종료 중 오류 무시 */ }
            Image? latest;
            lock (_lock)
            {
                latest = _latest;
                _latest = null;
            }
            latest?.Release();
            _frameEvent.Dispose();
        }

        public override string ToString() => $"{Info.Provider} camera {Info.DisplayName}";
    }
}
