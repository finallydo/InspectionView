using System;
using System.Collections.Generic;
using InspectionView.Core.Cameras;
using InspectionView.Core.Errors;
using InspectionView.Core.Types;
#if IV_HAS_PYLON
using Basler.Pylon;
#endif

namespace InspectionView.Cameras.Pylon
{
    /// <summary>
    /// Basler pylon 공급자. SDK(Basler.Pylon.dll) 가 빌드 시 있으면 실제 구현, 없으면 사용 불가 스텁.
    /// 미검증: SDK 가 설치된 PC 에서 컴파일/실행 확인 필요.
    /// </summary>
    public sealed class PylonCameraProvider : ICameraProvider
    {
        public const string ProviderName = "Pylon";
        public string Name => ProviderName;

#if IV_HAS_PYLON
        public bool IsAvailable => true;
        public string? UnavailableReason => null;

        public IReadOnlyList<CameraInfo> Enumerate()
        {
            var list = new List<CameraInfo>();
            foreach (var info in CameraFinder.Enumerate())
            {
                string serial = info.ContainsKey(CameraInfoKey.SerialNumber) ? info[CameraInfoKey.SerialNumber] : "";
                string model = info.ContainsKey(CameraInfoKey.ModelName) ? info[CameraInfoKey.ModelName] : "";
                string friendly = info.ContainsKey(CameraInfoKey.FriendlyName) ? info[CameraInfoKey.FriendlyName] : null!;
                list.Add(new CameraInfo(ProviderName, serial, model, serial, friendly));
            }
            return list;
        }

        public ICamera Open(string deviceId, IReadOnlyDictionary<string, string> options)
        {
            var devices = CameraFinder.Enumerate();
            if (devices.Count == 0)
                throw new VisionException(ErrorCode.CameraNotFound, "No pylon camera found.");
            ICameraInfo? target = null;
            if (string.IsNullOrEmpty(deviceId))
                target = devices[0];
            else
            {
                foreach (var d in devices)
                {
                    if ((d.ContainsKey(CameraInfoKey.SerialNumber) && d[CameraInfoKey.SerialNumber] == deviceId) ||
                        (d.ContainsKey(CameraInfoKey.UserDefinedName) && d[CameraInfoKey.UserDefinedName] == deviceId) ||
                        (d.ContainsKey(CameraInfoKey.FriendlyName) && d[CameraInfoKey.FriendlyName] == deviceId))
                    {
                        target = d;
                        break;
                    }
                }
            }
            if (target == null)
                throw new VisionException(ErrorCode.CameraNotFound, $"pylon camera '{deviceId}' not found.");
            return new PylonCamera(target, options);
        }
#else
        public bool IsAvailable => false;
        public string? UnavailableReason => "pylon SDK not found at build time. Install pylon (sets PYLON_DEV_DIR) and rebuild, or pass -p:IVPylonAssembly=<path to Basler.Pylon.dll>.";
        public IReadOnlyList<CameraInfo> Enumerate() => Array.Empty<CameraInfo>();
        public ICamera Open(string deviceId, IReadOnlyDictionary<string, string> options)
            => throw new VisionException(ErrorCode.CameraSdkNotAvailable, UnavailableReason!);
#endif
    }

#if IV_HAS_PYLON
    /// <summary>pylon 카메라. 등록된 공통 파라미터 외의 이름은 GenICam 노드맵(camera.Parameters[name])으로 직접 접근한다.</summary>
    public sealed class PylonCamera : CameraBase
    {
        private readonly Camera _camera;
        private readonly object _grabLock = new object();
        private bool _streaming;

        public PylonCamera(ICameraInfo info, IReadOnlyDictionary<string, string> options)
            : base(new CameraInfo(PylonCameraProvider.ProviderName,
                info.ContainsKey(CameraInfoKey.SerialNumber) ? info[CameraInfoKey.SerialNumber] : "",
                info.ContainsKey(CameraInfoKey.ModelName) ? info[CameraInfoKey.ModelName] : "",
                info.ContainsKey(CameraInfoKey.SerialNumber) ? info[CameraInfoKey.SerialNumber] : ""))
        {
            _camera = new Camera(info);
            _camera.Open();

            // 기본: 프리런, 가능하면 Mono8
            TrySetEnum("TriggerMode", "Off");
            if (options.TryGetValue("PixelFormat", out var pf)) TrySetEnum("PixelFormat", pf);
            foreach (var kv in options)
            {
                if (kv.Key.Equals("PixelFormat", StringComparison.OrdinalIgnoreCase)) continue;
                try { SetParameter(kv.Key, kv.Value); } catch { /* 옵션은 best-effort */ }
            }

            RegisterNode("ExposureTime", CameraParameterType.Float, "Exposure time (us).");
            RegisterNode("Gain", CameraParameterType.Float, "Gain.");
            RegisterNode("Width", CameraParameterType.Integer, "ROI width.");
            RegisterNode("Height", CameraParameterType.Integer, "ROI height.");
            RegisterNode("OffsetX", CameraParameterType.Integer, "ROI offset X.");
            RegisterNode("OffsetY", CameraParameterType.Integer, "ROI offset Y.");
            RegisterNode("PixelFormat", CameraParameterType.Enum, "Pixel format (Mono8, Mono12, BayerRG8, RGB8 ...).");
            RegisterNode("TriggerMode", CameraParameterType.Enum, "On/Off.");
            RegisterNode("TriggerSource", CameraParameterType.Enum, "Software, Line1 ...");
            RegisterNode("TriggerSoftware", CameraParameterType.Command, "Software trigger.");
            RegisterNode("AcquisitionFrameRate", CameraParameterType.Float, "Frame rate (Hz).");
        }

        private void RegisterNode(string name, CameraParameterType type, string description)
        {
            if (!_camera.Parameters.Contains(name)) return;
            var p = _camera.Parameters[name];
            double min = double.NaN, max = double.NaN;
            string[]? choices = null;
            if (p is IIntegerParameter ip) { try { min = ip.GetMinimum(); max = ip.GetMaximum(); } catch { } }
            else if (p is IFloatParameter fp) { try { min = fp.GetMinimum(); max = fp.GetMaximum(); } catch { } }
            else if (p is IEnumParameter ep) { try { choices = new List<string>(ep.GetAllValues()).ToArray(); } catch { } }
            RegisterParameter(new CameraParameterInfo(name, type, description, readOnly: !p.IsWritable, min, max, choices),
                () => ReadNode(name), v => WriteNode(name, v));
        }

        private bool TrySetEnum(string name, string value)
        {
            try
            {
                if (_camera.Parameters.Contains(name) && _camera.Parameters[name] is IEnumParameter e && e.IsWritable && e.CanSetValue(value))
                {
                    e.SetValue(value);
                    return true;
                }
            }
            catch { }
            return false;
        }

        private object? ReadNode(string name)
        {
            var p = _camera.Parameters[name];
            switch (p)
            {
                case IIntegerParameter i: return i.GetValue();
                case IFloatParameter f: return f.GetValue();
                case IBooleanParameter b: return b.GetValue();
                case IEnumParameter e: return e.GetValue();
                case IStringParameter s: return s.GetValue();
                case ICommandParameter _: return null;
                default: return p.ToString();
            }
        }

        private void WriteNode(string name, object? value)
        {
            var p = _camera.Parameters[name];
            string str = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
            switch (p)
            {
                case IIntegerParameter i: i.SetValue(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)); break;
                case IFloatParameter f: f.SetValue(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)); break;
                case IBooleanParameter b: b.SetValue(value is bool bv ? bv : str.Equals("true", StringComparison.OrdinalIgnoreCase) || str == "1"); break;
                case IEnumParameter e: e.SetValue(str); break;
                case IStringParameter s: s.SetValue(str); break;
                case ICommandParameter c: c.Execute(); break;
                default: throw new VisionException(ErrorCode.CameraParameterInvalid, $"Cannot write parameter '{name}'.");
            }
        }

        public override object? GetParameter(string name)
        {
            if (HasRegisteredParameter(name)) return base.GetParameter(name);
            ThrowIfClosed();
            if (!_camera.Parameters.Contains(name))
                throw new VisionException(ErrorCode.CameraParameterInvalid, $"Unknown pylon parameter '{name}'.");
            return ReadNode(name);
        }

        public override void SetParameter(string name, object? value)
        {
            if (HasRegisteredParameter(name)) { base.SetParameter(name, value); return; }
            ThrowIfClosed();
            if (!_camera.Parameters.Contains(name))
                throw new VisionException(ErrorCode.CameraParameterInvalid, $"Unknown pylon parameter '{name}'.");
            if (value is TupleValue t) value = t.IsEmpty ? null : t[0];
            WriteNode(name, value);
        }

        private void EnsureStreaming()
        {
            if (_streaming) return;
            _camera.StreamGrabber.Start();
            _streaming = true;
        }

        protected override Image GrabCore(int timeoutMs)
        {
            lock (_grabLock)
            {
                EnsureStreaming();
                using (IGrabResult result = _camera.StreamGrabber.RetrieveResult(timeoutMs, TimeoutHandling.Return))
                {
                    if (result == null || !result.IsValid)
                        throw new VisionException(ErrorCode.CameraTimeout, $"No frame within {timeoutMs} ms.");
                    if (!result.GrabSucceeded)
                        throw new VisionException(ErrorCode.DeviceError, $"Grab failed: {result.ErrorDescription}");
                    string pixelFormat = result.PixelTypeValue.ToString();   // 예: Mono8, BayerRG8, RGB8packed
                    if (pixelFormat.EndsWith("packed", StringComparison.OrdinalIgnoreCase))
                        pixelFormat = pixelFormat.Substring(0, pixelFormat.Length - "packed".Length);
                    long stride;
                    if (!result.ComputeStride(out stride)) stride = 0;
                    var img = CameraFrames.FromPixelFormat(pixelFormat, result.PixelDataPointer, result.Width, result.Height, stride);
                    img.Metadata["FrameId"] = result.ImageNumber;
                    img.Metadata["Timestamp"] = result.Timestamp;
                    return img;
                }
            }
        }

        protected override void CloseCore()
        {
            try { if (_streaming) _camera.StreamGrabber.Stop(); } catch { }
            _streaming = false;
            try { _camera.Close(); } catch { }
            _camera.Dispose();
        }
    }
#endif
}
