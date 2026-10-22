using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using InspectionView.Cameras.GenTL.GenICam;
using InspectionView.Cameras.GenTL.Native;
using InspectionView.Core.Cameras;
using InspectionView.Core.Errors;
using InspectionView.Core.Types;

namespace InspectionView.Cameras.GenTL
{
    /// <summary>GenTL 포트(원격 장치) 를 GenICam 레지스터 포트로.</summary>
    internal sealed class GenTLPort : IRegisterPort
    {
        private readonly GenTLApi _api;
        private readonly IntPtr _port;

        public GenTLPort(GenTLApi api, IntPtr port)
        {
            _api = api;
            _port = port;
        }

        public unsafe void Read(long address, byte[] buffer)
        {
            fixed (byte* p = buffer)
            {
                var size = (UIntPtr)buffer.Length;
                _api.Check(_api.GCReadPort(_port, unchecked((ulong)address), (IntPtr)p, ref size), $"GCReadPort(0x{address:X}, {buffer.Length})");
            }
        }

        public unsafe void Write(long address, byte[] buffer)
        {
            fixed (byte* p = buffer)
            {
                var size = (UIntPtr)buffer.Length;
                _api.Check(_api.GCWritePort(_port, unchecked((ulong)address), (IntPtr)p, ref size), $"GCWritePort(0x{address:X}, {buffer.Length})");
            }
        }
    }

    /// <summary>
    /// GenTL 카메라: 장치 열기 → 원격 포트에서 GenICam XML 로드 → <see cref="NodeMap"/> 으로 파라미터 접근,
    /// 첫 데이터 스트림으로 취득 (버퍼 N 개를 큐에 넣고 NEW_BUFFER 이벤트로 받아 복사 후 다시 큐에).
    /// 공통 파라미터는 목록에 등록하고, 그 밖의 GenICam 기능도 이름으로 Get/SetParameter 할 수 있다.
    /// </summary>
    public sealed class GenTLCamera : CameraBase
    {
        /// <summary>목록에 보여줄 표준 기능 (장치에 있는 것만). 같은 줄의 이름은 대체 이름 (구형 GigE 장치).</summary>
        private static readonly string[][] StandardFeatures =
        {
            new[] { "DeviceVendorName" },
            new[] { "DeviceModelName" },
            new[] { "DeviceSerialNumber", "DeviceID" },
            new[] { "DeviceUserID" },
            new[] { "DeviceTemperature" },
            new[] { "Width" },
            new[] { "Height" },
            new[] { "OffsetX" },
            new[] { "OffsetY" },
            new[] { "ReverseX" },
            new[] { "ReverseY" },
            new[] { "BinningHorizontal" },
            new[] { "BinningVertical" },
            new[] { "PixelFormat" },
            new[] { "AcquisitionMode" },
            new[] { "AcquisitionFrameRateEnable", "AcquisitionFrameRateEnabled" },
            new[] { "AcquisitionFrameRate", "AcquisitionFrameRateAbs" },
            new[] { "AcquisitionLineRate", "AcquisitionLineRateAbs" },
            new[] { "ExposureMode" },
            new[] { "ExposureAuto" },
            new[] { "ExposureTime", "ExposureTimeAbs", "ExposureTimeRaw" },
            new[] { "GainAuto" },
            new[] { "GainSelector" },
            new[] { "Gain", "GainAbs", "GainRaw" },
            new[] { "BlackLevel", "BlackLevelRaw" },
            new[] { "Gamma" },
            new[] { "TriggerSelector" },
            new[] { "TriggerMode" },
            new[] { "TriggerSource" },
            new[] { "TriggerActivation" },
            new[] { "TriggerDelay", "TriggerDelayAbs" },
            new[] { "TriggerSoftware" },
            new[] { "GevSCPSPacketSize" },
            new[] { "GevSCPD" },
            new[] { "DeviceLinkThroughputLimit" },
        };

        private readonly GenTLApi _api;
        private readonly GenTLProducer _producer;
        private readonly object _streamLock = new object();
        private readonly List<(IntPtr handle, IntPtr memory)> _buffers = new List<(IntPtr, IntPtr)>();
        private readonly int _bufferOption;     // 0 = 자동
        private string _lastError = "";
        private IntPtr _device;
        private IntPtr _stream;
        private IntPtr _event;
        private bool _streaming;
        private Thread? _thread;
        private volatile bool _running;
        private long _incomplete;

        public NodeMap Nodes { get; }
        public GenTLDeviceEntry Device { get; }

        internal GenTLCamera(GenTLDeviceEntry entry, IReadOnlyDictionary<string, string> options)
            : base(new CameraInfo(GenTLCameraProvider.ProviderName, GenTLCameraProvider.IdOf(entry), entry.Model, entry.Serial, GenTLCameraProvider.DisplayNameOf(entry)))
        {
            Device = entry;
            _producer = entry.Producer;
            _api = _producer.Api;
            _bufferOption = options.TryGetValue("Buffers", out var nb) && int.TryParse(nb, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? Math.Max(1, Math.Min(64, n)) : 0;

            try
            {
                lock (_producer.SyncRoot)
                {
                    IntPtr iface = _producer.GetInterface(entry.InterfaceId);
                    byte[] id = GenTLApi.Id(entry.DeviceId);
                    int rc = _api.IFOpenDevice(iface, id, Gc.DEVICE_ACCESS_CONTROL, out IntPtr device);
                    if (rc != Gc.ERR_SUCCESS)
                        rc = _api.IFOpenDevice(iface, id, Gc.DEVICE_ACCESS_EXCLUSIVE, out device);
                    if (rc == Gc.ERR_SUCCESS)
                        _device = device;
                    else
                        throw new GenTLException(rc, $"Cannot open '{entry}' ({Gc.ErrorName(rc)}{(rc == Gc.ERR_ACCESS_DENIED || rc == Gc.ERR_RESOURCE_IN_USE ? " - is it open in another program?" : "")} {_api.LastErrorText()}).");
                }

                _api.Check(_api.DevGetPort(_device, out IntPtr remote), "DevGetPort");
                var port = new GenTLPort(_api, remote);
                string xml = DeviceDescription.Load(DescriptionUrl(remote), port);
                Nodes = NodeMap.Parse(xml, port);

                _api.Check(_api.DevGetNumDataStreams(_device, out uint streams), "DevGetNumDataStreams");
                if (streams == 0)
                    throw new VisionException(ErrorCode.CameraOpenFailed, $"'{entry}' has no data stream.");
                string streamId = GenTLApi.QueryString((IntPtr buf, ref UIntPtr size) => _api.DevGetDataStreamID(_device, 0, buf, ref size));
                _api.Check(_api.DevOpenDataStream(_device, GenTLApi.Id(streamId), out IntPtr stream), "DevOpenDataStream");
                _stream = stream;
            }
            catch (Exception ex)
            {
                CloseHandles();
                if (ex is VisionException) throw;
                throw new VisionException(ErrorCode.CameraOpenFailed, $"Cannot open GenTL camera '{entry}': {ex.Message}", ex);
            }

            RegisterParameter(new CameraParameterInfo("IncompleteFrames", CameraParameterType.Integer, "Frames dropped as incomplete (GigE: packet loss, check GevSCPSPacketSize / GevSCPD).", readOnly: true),
                () => Interlocked.Read(ref _incomplete));
            RegisterParameter(new CameraParameterInfo("Producer", CameraParameterType.String, "GenTL producer (.cti) used for this camera.", readOnly: true),
                () => _producer.Path);
            RegisterParameter(new CameraParameterInfo("LastError", CameraParameterType.String, "Last error in continuous acquisition.", readOnly: true),
                () => _lastError);

            // 기본: 프리런 (스냅이 트리거를 기다리지 않도록). 옵션으로 덮어쓸 수 있다.
            TryWrite("TriggerSelector", "FrameStart");
            TryWrite("TriggerMode", "Off");
            TryWrite("AcquisitionMode", "Continuous");
            if (options.TryGetValue("PixelFormat", out var pf)) TryWrite("PixelFormat", pf);
            foreach (var kv in options)
            {
                if (kv.Key.Equals("PixelFormat", StringComparison.OrdinalIgnoreCase) || kv.Key.Equals("Buffers", StringComparison.OrdinalIgnoreCase)
                    || kv.Key.Equals("Producer", StringComparison.OrdinalIgnoreCase)) continue;
                try { SetParameter(kv.Key, kv.Value); } catch { /* 옵션은 best-effort */ }
            }

            foreach (var names in StandardFeatures)
                RegisterFeature(names);
        }

        private string DescriptionUrl(IntPtr remote)
        {
            // GenTL 1.1+: GCGetPortURLInfo(0, URL_INFO_URL). 없으면 구형 GCGetPortURL.
            if (_api.GCGetNumPortURLs != null && _api.GCGetPortURLInfo != null
                && _api.GCGetNumPortURLs(remote, out uint count) == Gc.ERR_SUCCESS && count > 0)
            {
                try
                {
                    string url = GenTLApi.QueryString((IntPtr buf, ref UIntPtr size) => _api.GCGetPortURLInfo!(remote, 0, Gc.URL_INFO_URL, out _, buf, ref size));
                    if (url.IndexOf(':') > 0) return url;
                }
                catch
                {
                    // 구형 함수로 재시도
                }
            }
            if (_api.GCGetPortURL != null)
                return GenTLApi.QueryString((IntPtr buf, ref UIntPtr size) => _api.GCGetPortURL!(remote, buf, ref size));
            throw new VisionException(ErrorCode.CameraOpenFailed, "The GenTL producer does not provide the device description URL.");
        }

        // ------------------------------------------------------------------ 파라미터

        private void RegisterFeature(string[] names)
        {
            Node? node = null;
            lock (Nodes.SyncRoot)
            {
                foreach (var n in names)
                {
                    var candidate = Nodes.Find(n);
                    if (candidate == null) continue;
                    try
                    {
                        if (!candidate.IsImplemented) continue;
                    }
                    catch
                    {
                        continue;
                    }
                    node = candidate;
                    break;
                }
            }
            if (node == null) return;

            CameraParameterInfo info;
            try
            {
                info = Describe(node);
            }
            catch
            {
                return;
            }
            string name = node.Name;
            RegisterParameter(info, () => ReadFeature(name), v => WriteFeature(name, v));
        }

        private CameraParameterInfo Describe(Node node)
        {
            lock (Nodes.SyncRoot)
            {
                bool readOnly;
                try { readOnly = !node.IsWritable; } catch { readOnly = true; }
                string desc = node.Description;
                switch (node)
                {
                    case CommandNode _:
                        return new CameraParameterInfo(node.Name, CameraParameterType.Command, desc, readOnly);
                    case EnumerationNode e:
                        return new CameraParameterInfo(node.Name, CameraParameterType.Enum, desc, readOnly, choices: ToArray(e.AvailableSymbols));
                    case BooleanNode _:
                        return new CameraParameterInfo(node.Name, CameraParameterType.Boolean, desc, readOnly);
                    case ValueNode v:
                    {
                        double min = double.NaN, max = double.NaN;
                        try
                        {
                            min = v.GetMin().AsDouble;
                            max = v.GetMax().AsDouble;
                            if (double.IsInfinity(min) || min <= long.MinValue) min = double.NaN;
                            if (double.IsInfinity(max) || max >= long.MaxValue) max = double.NaN;
                        }
                        catch
                        {
                            // 범위를 못 읽으면 표시만 생략
                        }
                        return new CameraParameterInfo(node.Name, v.IsFloat ? CameraParameterType.Float : CameraParameterType.Integer, desc, readOnly, min, max);
                    }
                    default:
                        return new CameraParameterInfo(node.Name, CameraParameterType.String, desc, readOnly);
                }
            }
        }

        private static string[] ToArray(IReadOnlyList<string> list)
        {
            var a = new string[list.Count];
            for (int i = 0; i < a.Length; i++) a[i] = list[i];
            return a;
        }

        private object? ReadFeature(string name)
        {
            lock (Nodes.SyncRoot)
            {
                var node = Nodes.Find(name) ?? throw new VisionException(ErrorCode.CameraParameterInvalid, $"Unknown GenICam feature '{name}'.");
                switch (node)
                {
                    case CommandNode _: return null;
                    case EnumerationNode e: return e.GetSymbol();
                    case BooleanNode b: return b.GetBool();
                    case ValueNode v: return v.IsFloat ? (object)v.GetFloat() : v.GetInt();
                    case IStringValue s: return s.GetString();
                    default: throw new VisionException(ErrorCode.CameraParameterInvalid, $"GenICam feature '{name}' ({node.Interface}) has no readable value.");
                }
            }
        }

        private void WriteFeature(string name, object? value)
        {
            if (value is TupleValue t) value = t.IsEmpty ? null : t[0];
            string str = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            lock (Nodes.SyncRoot)
            {
                var node = Nodes.Find(name) ?? throw new VisionException(ErrorCode.CameraParameterInvalid, $"Unknown GenICam feature '{name}'.");
                switch (node)
                {
                    case CommandNode c:
                        c.Execute();
                        break;
                    case EnumerationNode e:
                        if (value is string || !IsNumber(value)) e.SetSymbol(str);
                        else e.SetSymbol(e.Entries.Count > 0 && e.FindEntry(str) != null ? str : SymbolOf(e, Convert.ToInt64(value, CultureInfo.InvariantCulture)));
                        break;
                    case BooleanNode b:
                        b.SetBool(value is bool bv ? bv : str.Equals("true", StringComparison.OrdinalIgnoreCase) || str == "1" || str.Equals("on", StringComparison.OrdinalIgnoreCase));
                        break;
                    case ValueNode v:
                    {
                        if (v.IsFloat)
                            v.SetValue(Num.Real(value is string ? double.Parse(str, NumberStyles.Float, CultureInfo.InvariantCulture) : Convert.ToDouble(value, CultureInfo.InvariantCulture)));
                        else
                            v.SetValue(Num.Int(value is string ? (long)Math.Round(double.Parse(str, NumberStyles.Float, CultureInfo.InvariantCulture)) : Convert.ToInt64(value, CultureInfo.InvariantCulture)));
                        break;
                    }
                    case IStringValue s:
                        s.SetString(str);
                        break;
                    default:
                        throw new VisionException(ErrorCode.CameraParameterInvalid, $"GenICam feature '{name}' ({node.Interface}) cannot be written.");
                }
            }
        }

        private static bool IsNumber(object? v)
            => v is sbyte || v is byte || v is short || v is ushort || v is int || v is uint || v is long || v is ulong || v is float || v is double || v is decimal;

        private static string SymbolOf(EnumerationNode e, long value)
        {
            foreach (var entry in e.Entries)
                if (entry.Value == value) return entry.Symbol;
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private bool TryWrite(string name, object value)
        {
            try
            {
                if (Nodes.Find(name) == null) return false;
                WriteFeature(name, value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public override object? GetParameter(string name)
        {
            if (HasRegisteredParameter(name)) return base.GetParameter(name);
            ThrowIfClosed();
            return ReadFeature(name);
        }

        /// <summary>
        /// GenICam 기능은 등록 여부와 관계없이 노드맵의 현재 범위/상태로 검사해 쓴다
        /// (등록 시점의 Min/Max 는 다른 기능을 바꾸면 달라지므로 사용하지 않음).
        /// </summary>
        public override void SetParameter(string name, object? value)
        {
            ThrowIfClosed();
            if (Nodes.Find(name) == null)
            {
                base.SetParameter(name, value);
                return;
            }
            WriteFeature(name, value);
        }

        // ------------------------------------------------------------------ 스트림

        private long PayloadSize()
        {
            GenTLApi.InfoQuery q = (int cmd, out int type, IntPtr buf, ref UIntPtr size) => _api.DSGetInfo(_stream, cmd, out type, buf, ref size);
            if ((GenTLApi.InfoInt(q, Gc.STREAM_INFO_DEFINES_PAYLOADSIZE) ?? 0) != 0)
            {
                long? ds = GenTLApi.InfoInt(q, Gc.STREAM_INFO_PAYLOAD_SIZE);
                if (ds > 0) return ds.Value;
            }
            lock (Nodes.SyncRoot)
            {
                if (Nodes.Find("PayloadSize") is ValueNode p)
                {
                    long v = p.GetInt();
                    if (v > 0) return v;
                }
            }
            long? fallback = GenTLApi.InfoInt(q, Gc.STREAM_INFO_PAYLOAD_SIZE);
            if (fallback > 0) return fallback.Value;
            throw new VisionException(ErrorCode.DeviceError, "Cannot determine the payload size (neither the stream nor PayloadSize reports it).");
        }

        /// <summary>
        /// 스트림 버퍼 수: 옵션 Buffers 가 있으면 그대로, 없으면 연속 취득 8개 / 단일 취득 2개를 합계 256MB 안으로 줄인다 (최소 2).
        /// </summary>
        private int BufferCount(long payload, bool continuous)
        {
            if (_bufferOption > 0) return _bufferOption;
            int wanted = continuous ? 8 : 2;
            long budget = 256L * 1024 * 1024;
            return (int)Math.Max(2, Math.Min(wanted, budget / Math.Max(1, payload)));
        }

        private void StartStream(bool continuous)
        {
            if (_streaming) return;
            lock (Nodes.SyncRoot)
            {
                if (Nodes.Find("TLParamsLocked") is ValueNode locked)
                {
                    try { locked.SetValue(Num.Int(1)); } catch { }
                }
            }
            try
            {
                long payload = PayloadSize();
                GenTLApi.InfoQuery q = (int cmd, out int type, IntPtr buf, ref UIntPtr size) => _api.DSGetInfo(_stream, cmd, out type, buf, ref size);
                int count = (int)Math.Max(BufferCount(payload, continuous), GenTLApi.InfoInt(q, Gc.STREAM_INFO_BUF_ANNOUNCE_MIN) ?? 1);
                for (int i = 0; i < count; i++)
                {
                    IntPtr handle = IntPtr.Zero;
                    IntPtr memory = IntPtr.Zero;
                    int rc = _api.DSAllocAndAnnounceBuffer != null
                        ? _api.DSAllocAndAnnounceBuffer(_stream, (UIntPtr)payload, IntPtr.Zero, out handle)
                        : Gc.ERR_NOT_IMPLEMENTED;
                    if (rc != Gc.ERR_SUCCESS)
                    {
                        memory = Marshal.AllocHGlobal((IntPtr)payload);
                        rc = _api.DSAnnounceBuffer(_stream, memory, (UIntPtr)payload, IntPtr.Zero, out handle);
                        if (rc != Gc.ERR_SUCCESS)
                        {
                            Marshal.FreeHGlobal(memory);
                            _api.Check(rc, "DSAnnounceBuffer");
                        }
                    }
                    _buffers.Add((handle, memory));
                    _api.Check(_api.DSQueueBuffer(_stream, handle), "DSQueueBuffer");
                }

                _api.Check(_api.GCRegisterEvent(_stream, Gc.EVENT_NEW_BUFFER, out IntPtr evt), "GCRegisterEvent(NEW_BUFFER)");
                _event = evt;
                _api.Check(_api.DSStartAcquisition(_stream, 0, Gc.INFINITE), "DSStartAcquisition");
                _streaming = true;
                lock (Nodes.SyncRoot)
                {
                    if (Nodes.Find("AcquisitionStart") is CommandNode start) start.Execute();
                }
            }
            catch
            {
                StopStream();
                throw;
            }
        }

        private void StopStream()
        {
            lock (Nodes.SyncRoot)
            {
                if (_streaming && Nodes.Find("AcquisitionStop") is CommandNode stop)
                {
                    try { stop.Execute(); } catch { }
                }
            }
            if (_streaming)
            {
                if (_api.DSStopAcquisition(_stream, Gc.ACQ_STOP_FLAGS_DEFAULT) != Gc.ERR_SUCCESS)
                    _api.DSStopAcquisition(_stream, Gc.ACQ_STOP_FLAGS_KILL);
                _streaming = false;
            }
            if (_stream != IntPtr.Zero)
                _api.DSFlushQueue(_stream, Gc.ACQ_QUEUE_ALL_DISCARD);
            foreach (var (handle, memory) in _buffers)
            {
                _api.DSRevokeBuffer(_stream, handle, out _, out _);
                if (memory != IntPtr.Zero) Marshal.FreeHGlobal(memory);
            }
            _buffers.Clear();
            if (_event != IntPtr.Zero)
            {
                _api.GCUnregisterEvent(_stream, Gc.EVENT_NEW_BUFFER);
                _event = IntPtr.Zero;
            }
            lock (Nodes.SyncRoot)
            {
                if (Nodes.Find("TLParamsLocked") is ValueNode locked)
                {
                    try { locked.SetValue(Num.Int(0)); } catch { }
                }
            }
        }

        /// <summary>다음 완전한 프레임을 기다려 복사한다. 불완전 프레임은 세고 버린다.</summary>
        private unsafe Image WaitFrame(int timeoutMs)
        {
            int deadline = Environment.TickCount + Math.Max(0, timeoutMs);
            var data = stackalloc byte[2 * IntPtr.Size];   // EVENT_NEW_BUFFER_DATA { BufferHandle, pUserPointer }
            while (true)
            {
                int remaining = Math.Max(0, deadline - Environment.TickCount);
                var size = (UIntPtr)(2 * IntPtr.Size);
                int rc = _api.EventGetData(_event, (IntPtr)data, ref size, (ulong)remaining);
                if (rc == Gc.ERR_TIMEOUT)
                    throw new VisionException(ErrorCode.CameraTimeout, $"No frame from '{Info.DisplayName}' within {timeoutMs} ms.");
                if (rc == Gc.ERR_ABORT)
                    throw new VisionException(ErrorCode.CameraTimeout, "Acquisition was stopped.");
                _api.Check(rc, "EventGetData");

                IntPtr buffer = *(IntPtr*)data;
                Image? img = null;
                try
                {
                    img = FrameFromBuffer(buffer);
                }
                finally
                {
                    if (_streaming) _api.DSQueueBuffer(_stream, buffer);
                }
                if (img != null) return img;
                if (Environment.TickCount - deadline >= 0)
                    throw new VisionException(ErrorCode.CameraTimeout, $"Only incomplete frames from '{Info.DisplayName}' within {timeoutMs} ms (IncompleteFrames = {Interlocked.Read(ref _incomplete)}).");
            }
        }

        /// <summary>버퍼 → 이미지 (불완전하면 null).</summary>
        private Image? FrameFromBuffer(IntPtr buffer)
        {
            GenTLApi.InfoQuery q = (int cmd, out int type, IntPtr buf, ref UIntPtr size) => _api.DSGetBufferInfo(_stream, buffer, cmd, out type, buf, ref size);
            if ((GenTLApi.InfoInt(q, Gc.BUFFER_INFO_IS_INCOMPLETE) ?? 0) != 0)
            {
                Interlocked.Increment(ref _incomplete);
                return null;
            }

            IntPtr basePtr = (IntPtr)(GenTLApi.InfoInt(q, Gc.BUFFER_INFO_BASE) ?? 0);
            if (basePtr == IntPtr.Zero)
                throw new VisionException(ErrorCode.DeviceError, "The GenTL buffer has no base address.");
            long offset = GenTLApi.InfoInt(q, Gc.BUFFER_INFO_IMAGEOFFSET) ?? 0;
            long width = GenTLApi.InfoInt(q, Gc.BUFFER_INFO_WIDTH) ?? 0;
            long height = GenTLApi.InfoInt(q, Gc.BUFFER_INFO_HEIGHT) ?? 0;
            long delivered = GenTLApi.InfoInt(q, Gc.BUFFER_INFO_DELIVERED_IMAGEHEIGHT) ?? 0;
            long xPadding = GenTLApi.InfoInt(q, Gc.BUFFER_INFO_XPADDING) ?? 0;
            long? format = GenTLApi.InfoInt(q, Gc.BUFFER_INFO_PIXELFORMAT);
            long filled = GenTLApi.InfoInt(q, Gc.BUFFER_INFO_SIZE_FILLED) ?? 0;

            lock (Nodes.SyncRoot)
            {
                // 프로듀서가 알려주지 않는 값은 노드맵에서
                if (width <= 0 && Nodes.Find("Width") is ValueNode w) width = w.GetInt();
                if (height <= 0 && Nodes.Find("Height") is ValueNode h) height = h.GetInt();
                if ((format ?? 0) == 0 && Nodes.Find("PixelFormat") is EnumerationNode pf) format = pf.GetInt();
            }
            if (delivered > 0 && delivered < height) height = delivered;   // 라인스캔: 일부 줄만 전달된 프레임
            if (width <= 0 || height <= 0)
                throw new VisionException(ErrorCode.DeviceError, $"Invalid frame size {width}x{height}.");

            long code = format ?? 0;
            long lineBytes = PixelFormats.LineBytes(code, (int)width) + xPadding;
            if (filled > offset && lineBytes > 0 && (filled - offset) / lineBytes < height)
                height = (filled - offset) / lineBytes;
            if (height <= 0)
            {
                Interlocked.Increment(ref _incomplete);
                return null;
            }

            var img = PixelFormats.ToImage(code, basePtr + (int)offset, (int)width, (int)height, xPadding);
            img.Metadata["FrameId"] = GenTLApi.InfoInt(q, Gc.BUFFER_INFO_FRAMEID) ?? 0;
            img.Metadata["Timestamp"] = GenTLApi.InfoInt(q, Gc.BUFFER_INFO_TIMESTAMP) ?? 0;
            img.Metadata["PixelFormat"] = PixelFormats.NameOf(code) ?? code.ToString(CultureInfo.InvariantCulture);
            return img;
        }

        /// <summary>단일 취득: 스트림을 시작해 첫 완전한 프레임을 받고 멈춘다 (오래된 버퍼를 돌려주지 않도록).</summary>
        protected override Image GrabCore(int timeoutMs)
        {
            lock (_streamLock)
            {
                StartStream(continuous: false);
                try
                {
                    return WaitFrame(timeoutMs);
                }
                finally
                {
                    StopStream();
                }
            }
        }

        /// <summary>연속 취득: 스트림을 켜 두고 전용 스레드에서 프레임을 받아 <see cref="CameraBase.PublishFrame"/>.</summary>
        protected override void StartAcquisitionCore()
        {
            lock (_streamLock)
            {
                _lastError = "";
                StartStream(continuous: true);
                _running = true;
                _thread = new Thread(AcquisitionLoop) { IsBackground = true, Name = $"GenTL {Info.DisplayName}" };
                _thread.Start();
            }
        }

        private void AcquisitionLoop()
        {
            while (_running)
            {
                Image img;
                try
                {
                    img = WaitFrame(500);
                }
                catch (VisionException ex) when (ex.Code == ErrorCode.CameraTimeout)
                {
                    continue;
                }
                catch (Exception ex)
                {
                    if (!_running) break;
                    _lastError = ex.Message;
                    Thread.Sleep(50);
                    continue;
                }
                if (!_running)
                {
                    img.Release();
                    break;
                }
                PublishFrame(img);
            }
        }

        protected override void StopAcquisitionCore()
        {
            Thread? t;
            lock (_streamLock)
            {
                _running = false;
                t = _thread;
                _thread = null;
                if (_event != IntPtr.Zero) _api.EventKill(_event);   // EventGetData 대기를 깨운다
            }
            if (t != null && t != Thread.CurrentThread) t.Join(3000);
            lock (_streamLock)
                StopStream();
        }

        protected override void CloseCore()
        {
            lock (_streamLock)
            {
                try { StopStream(); } catch { }
                CloseHandles();
            }
        }

        private void CloseHandles()
        {
            if (_stream != IntPtr.Zero)
            {
                _api.DSClose(_stream);
                _stream = IntPtr.Zero;
            }
            if (_device != IntPtr.Zero)
            {
                _api.DevClose(_device);
                _device = IntPtr.Zero;
            }
        }
    }
}
