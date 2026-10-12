using System;
using System.Collections.Generic;
using InspectionView.Core.Cameras;
using InspectionView.Core.Errors;
using InspectionView.Core.Types;
#if IV_HAS_SPINNAKER
using SpinnakerNET;
using SpinnakerNET.GenApi;
#endif

namespace InspectionView.Cameras.Spinnaker
{
    /// <summary>
    /// FLIR/Teledyne Spinnaker 공급자. SpinnakerNET 이 빌드 시 있으면 실제 구현, 없으면 사용 불가 스텁.
    /// 미검증: SDK 가 설치된 PC 에서 컴파일/실행 확인 필요.
    /// </summary>
    public sealed class SpinnakerCameraProvider : ICameraProvider
    {
        public const string ProviderName = "Spinnaker";
        public string Name => ProviderName;

#if IV_HAS_SPINNAKER
        private static ManagedSystem? _system;
        private static ManagedSystem System => _system ?? (_system = new ManagedSystem());

        public bool IsAvailable => true;
        public string? UnavailableReason => null;

        public IReadOnlyList<CameraInfo> Enumerate()
        {
            var list = new List<CameraInfo>();
            using (var cams = System.GetCameras())
            {
                foreach (IManagedCamera cam in cams)
                {
                    using (cam)
                    {
                        var tl = cam.GetTLDeviceNodeMap();
                        string serial = ReadString(tl, "DeviceSerialNumber");
                        string model = ReadString(tl, "DeviceModelName");
                        list.Add(new CameraInfo(ProviderName, serial, model, serial));
                    }
                }
            }
            return list;
        }

        private static string ReadString(INodeMap map, string name)
        {
            try
            {
                var node = map.GetNode<IString>(name);
                return node != null && node.IsReadable ? node.Value : "";
            }
            catch { return ""; }
        }

        public ICamera Open(string deviceId, IReadOnlyDictionary<string, string> options)
        {
            var cams = System.GetCameras();
            try
            {
                if (cams.Count == 0)
                    throw new VisionException(ErrorCode.CameraNotFound, "No Spinnaker camera found.");
                IManagedCamera? target = null;
                if (string.IsNullOrEmpty(deviceId))
                    target = cams[0];
                else
                {
                    foreach (IManagedCamera cam in cams)
                    {
                        if (ReadString(cam.GetTLDeviceNodeMap(), "DeviceSerialNumber") == deviceId) { target = cam; break; }
                        cam.Dispose();
                    }
                }
                if (target == null)
                    throw new VisionException(ErrorCode.CameraNotFound, $"Spinnaker camera '{deviceId}' not found.");
                return new SpinnakerCamera(target, options);
            }
            finally
            {
                cams.Clear();
            }
        }
#else
        public bool IsAvailable => false;
        public string? UnavailableReason => "Spinnaker SDK not found at build time. Install Spinnaker (Program Files\\Teledyne|FLIR Systems\\Spinnaker) and rebuild, or pass -p:IVSpinnakerAssembly=<path to SpinnakerNET_v140.dll>.";
        public IReadOnlyList<CameraInfo> Enumerate() => Array.Empty<CameraInfo>();
        public ICamera Open(string deviceId, IReadOnlyDictionary<string, string> options)
            => throw new VisionException(ErrorCode.CameraSdkNotAvailable, UnavailableReason!);
#endif
    }

#if IV_HAS_SPINNAKER
    public sealed class SpinnakerCamera : CameraBase
    {
        private readonly IManagedCamera _camera;
        private readonly INodeMap _nodes;
        private readonly object _grabLock = new object();
        private bool _streaming;

        public SpinnakerCamera(IManagedCamera camera, IReadOnlyDictionary<string, string> options)
            : base(new CameraInfo(SpinnakerCameraProvider.ProviderName, Serial(camera), Model(camera), Serial(camera)))
        {
            _camera = camera;
            _camera.Init();
            _nodes = _camera.GetNodeMap();

            TrySetEnum("AcquisitionMode", "Continuous");
            TrySetEnum("TriggerMode", "Off");
            foreach (var kv in options)
            {
                try { SetParameter(kv.Key, kv.Value); } catch { /* best-effort */ }
            }

            RegisterNode("ExposureTime", CameraParameterType.Float, "Exposure time (us).");
            RegisterNode("Gain", CameraParameterType.Float, "Gain (dB).");
            RegisterNode("Width", CameraParameterType.Integer, "ROI width.");
            RegisterNode("Height", CameraParameterType.Integer, "ROI height.");
            RegisterNode("OffsetX", CameraParameterType.Integer, "ROI offset X.");
            RegisterNode("OffsetY", CameraParameterType.Integer, "ROI offset Y.");
            RegisterNode("PixelFormat", CameraParameterType.Enum, "Pixel format.");
            RegisterNode("TriggerMode", CameraParameterType.Enum, "On/Off.");
            RegisterNode("TriggerSource", CameraParameterType.Enum, "Software, Line0 ...");
            RegisterNode("TriggerSoftware", CameraParameterType.Command, "Software trigger.");
            RegisterNode("AcquisitionFrameRate", CameraParameterType.Float, "Frame rate (Hz).");
        }

        private static string Serial(IManagedCamera cam)
        {
            try { var n = cam.GetTLDeviceNodeMap().GetNode<IString>("DeviceSerialNumber"); return n != null && n.IsReadable ? n.Value : ""; } catch { return ""; }
        }

        private static string Model(IManagedCamera cam)
        {
            try { var n = cam.GetTLDeviceNodeMap().GetNode<IString>("DeviceModelName"); return n != null && n.IsReadable ? n.Value : ""; } catch { return ""; }
        }

        private void RegisterNode(string name, CameraParameterType type, string description)
        {
            INode node;
            try { node = _nodes.GetNode(name); } catch { return; }
            if (node == null || !node.IsImplemented) return;
            double min = double.NaN, max = double.NaN;
            string[]? choices = null;
            try
            {
                if (node is IInteger i) { min = i.Min; max = i.Max; }
                else if (node is IFloat f) { min = f.Min; max = f.Max; }
                else if (node is IEnum e)
                {
                    var names = new List<string>();
                    foreach (var entry in e.Entries) names.Add(entry.Symbolic);
                    choices = names.ToArray();
                }
            }
            catch { }
            RegisterParameter(new CameraParameterInfo(name, type, description, readOnly: !node.IsWritable, min, max, choices),
                () => ReadNode(name), v => WriteNode(name, v));
        }

        private bool TrySetEnum(string name, string value)
        {
            try
            {
                var e = _nodes.GetNode<IEnum>(name);
                if (e == null || !e.IsWritable) return false;
                var entry = e.GetEntryByName(value);
                if (entry == null || !entry.IsReadable) return false;
                e.Value = entry.Symbolic;
                return true;
            }
            catch { return false; }
        }

        private object? ReadNode(string name)
        {
            var node = _nodes.GetNode(name);
            switch (node)
            {
                case IInteger i: return i.Value;
                case IFloat f: return f.Value;
                case IBool b: return b.Value;
                case IEnum e: return e.Value;
                case IString s: return s.Value;
                case ICommand _: return null;
                default: return node?.ToString();
            }
        }

        private void WriteNode(string name, object? value)
        {
            var node = _nodes.GetNode(name);
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            string str = Convert.ToString(value, ci) ?? "";
            switch (node)
            {
                case IInteger i: i.Value = Convert.ToInt64(value, ci); break;
                case IFloat f: f.Value = Convert.ToDouble(value, ci); break;
                case IBool b: b.Value = value is bool bv ? bv : str.Equals("true", StringComparison.OrdinalIgnoreCase) || str == "1"; break;
                case IEnum e:
                {
                    var entry = e.GetEntryByName(str);
                    if (entry == null) throw new VisionException(ErrorCode.CameraParameterInvalid, $"'{str}' is not a valid value for '{name}'.");
                    e.Value = entry.Symbolic;
                    break;
                }
                case IString s: s.Value = str; break;
                case ICommand c: c.Execute(); break;
                default: throw new VisionException(ErrorCode.CameraParameterInvalid, $"Cannot write parameter '{name}'.");
            }
        }

        public override object? GetParameter(string name)
        {
            if (HasRegisteredParameter(name)) return base.GetParameter(name);
            ThrowIfClosed();
            return ReadNode(name) ?? throw new VisionException(ErrorCode.CameraParameterInvalid, $"Unknown Spinnaker parameter '{name}'.");
        }

        public override void SetParameter(string name, object? value)
        {
            if (HasRegisteredParameter(name)) { base.SetParameter(name, value); return; }
            ThrowIfClosed();
            if (value is TupleValue t) value = t.IsEmpty ? null : t[0];
            WriteNode(name, value);
        }

        protected override Image GrabCore(int timeoutMs)
        {
            lock (_grabLock)
            {
                if (!_streaming)
                {
                    _camera.BeginAcquisition();
                    _streaming = true;
                }
                IManagedImage? raw = null;
                try
                {
                    try { raw = _camera.GetNextImage((ulong)Math.Max(1, timeoutMs)); }
                    catch (SpinnakerException ex) { throw new VisionException(ErrorCode.CameraTimeout, $"No frame within {timeoutMs} ms: {ex.Message}"); }
                    if (raw.IsIncomplete)
                        throw new VisionException(ErrorCode.DeviceError, $"Incomplete image: {raw.ImageStatus}");
                    string pixelFormat = raw.PixelFormatName;      // 예: Mono8, BayerRG8, RGB8
                    var img = CameraFrames.FromPixelFormat(pixelFormat, raw.DataPtr, (int)raw.Width, (int)raw.Height, (long)raw.Stride);
                    img.Metadata["FrameId"] = (long)raw.FrameID;
                    img.Metadata["Timestamp"] = (long)raw.TimeStamp;
                    return img;
                }
                finally
                {
                    raw?.Release();
                    raw?.Dispose();
                }
            }
        }

        protected override void CloseCore()
        {
            try { if (_streaming) _camera.EndAcquisition(); } catch { }
            _streaming = false;
            try { _camera.DeInit(); } catch { }
            _camera.Dispose();
        }
    }
#endif
}
