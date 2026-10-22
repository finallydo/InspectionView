using System;
using System.Collections.Generic;
using System.IO;
using InspectionView.Cameras.GenTL.Native;

namespace InspectionView.Cameras.GenTL
{
    /// <summary>열거된 장치 하나 (어느 프로듀서/인터페이스에서 찾았는지 포함).</summary>
    public sealed class GenTLDeviceEntry
    {
        internal GenTLDeviceEntry(GenTLProducer producer, string interfaceId, string deviceId)
        {
            Producer = producer;
            InterfaceId = interfaceId;
            DeviceId = deviceId;
        }

        internal GenTLProducer Producer { get; }
        public string ProducerName => Producer.ShortName;
        public string InterfaceId { get; }
        public string DeviceId { get; }
        public string Vendor { get; internal set; } = "";
        public string Model { get; internal set; } = "";
        public string Serial { get; internal set; } = "";
        public string UserName { get; internal set; } = "";
        public string DisplayName { get; internal set; } = "";
        public string TLType { get; internal set; } = "";

        /// <summary>
        /// 같은 장치를 여러 프로듀서가 보여줄 때 묶는 키: 시리얼 (프로듀서마다 제조사 표기가 달라 시리얼만 쓴다), 없으면 모델+장치 ID.
        /// </summary>
        public string Key => !string.IsNullOrEmpty(Serial) ? "SN|" + Serial : Model + "|" + DeviceId;

        /// <summary>장치 제조사가 만든 프로듀서인지 (같은 장치가 여러 프로듀서에 보이면 이쪽을 우선).</summary>
        public bool IsVendorProducer
        {
            get
            {
                string v = FirstWord(Vendor);
                return v.Length >= 3 && (Producer.Vendor.IndexOf(v, StringComparison.OrdinalIgnoreCase) >= 0
                    || Producer.Path.IndexOf(v, StringComparison.OrdinalIgnoreCase) >= 0);
            }
        }

        private static string FirstWord(string s)
        {
            s = (s ?? "").Trim();
            int sp = s.IndexOfAny(new[] { ' ', ',', '-', '_' });
            return sp > 0 ? s.Substring(0, sp) : s;
        }

        public override string ToString() => $"{Vendor} {Model} ({Serial}) via {ProducerName}";
    }

    /// <summary>
    /// GenTL 프로듀서(.cti) 하나: 라이브러리 로드, GCInitLib, TL 열기, 인터페이스/장치 열거.
    /// 프로세스가 끝날 때까지 언로드하지 않는다 (열린 장치 핸들이 프로듀서 수명에 묶여 있음).
    /// </summary>
    public sealed class GenTLProducer
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, IntPtr> _interfaces = new Dictionary<string, IntPtr>(StringComparer.Ordinal);

        internal GenTLApi Api { get; }
        internal IntPtr TL { get; }
        public string Path { get; }
        public string ShortName => System.IO.Path.GetFileNameWithoutExtension(Path);
        public string Vendor { get; }
        public string Model { get; }
        public string TLType { get; }

        private GenTLProducer(string path, GenTLApi api, IntPtr tl)
        {
            Path = path;
            Api = api;
            TL = tl;
            Vendor = GenTLApi.InfoString(api.GCGetInfo.Invoke, Gc.TL_INFO_VENDOR);
            Model = GenTLApi.InfoString(api.GCGetInfo.Invoke, Gc.TL_INFO_MODEL);
            TLType = GenTLApi.InfoString(api.GCGetInfo.Invoke, Gc.TL_INFO_TLTYPE);
        }

        internal object SyncRoot => _lock;

        public static GenTLProducer Load(string path)
        {
            var api = GenTLApi.Load(path);
            int rc = api.GCInitLib();
            if (rc != Gc.ERR_SUCCESS && rc != Gc.ERR_RESOURCE_IN_USE)
                api.Check(rc, $"GCInitLib ({System.IO.Path.GetFileName(path)})");
            api.Check(api.TLOpen(out IntPtr tl), $"TLOpen ({System.IO.Path.GetFileName(path)})");
            return new GenTLProducer(path, api, tl);
        }

        /// <summary>
        /// GENICAM_GENTL64_PATH(64bit 프로세스) 의 폴더들에서 .cti 파일을 찾는다. IV_GENTL_PATH 가 있으면 그것을 대신 쓴다
        /// (폴더 또는 .cti 파일을 ';' 로 구분).
        /// </summary>
        public static IReadOnlyList<string> FindProducerFiles()
        {
            string? env = Environment.GetEnvironmentVariable("IV_GENTL_PATH");
            if (string.IsNullOrWhiteSpace(env))
                env = Environment.GetEnvironmentVariable(IntPtr.Size == 8 ? "GENICAM_GENTL64_PATH" : "GENICAM_GENTL32_PATH");
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(env)) return list;

            foreach (var raw in env!.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string entry = raw.Trim().Trim('"');
                if (entry.Length == 0) continue;
                try
                {
                    if (File.Exists(entry) && entry.EndsWith(".cti", StringComparison.OrdinalIgnoreCase))
                    {
                        if (seen.Add(System.IO.Path.GetFullPath(entry))) list.Add(System.IO.Path.GetFullPath(entry));
                    }
                    else if (Directory.Exists(entry))
                    {
                        var files = Directory.GetFiles(entry, "*.cti");
                        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                        foreach (var f in files)
                            if (seen.Add(System.IO.Path.GetFullPath(f))) list.Add(System.IO.Path.GetFullPath(f));
                    }
                }
                catch
                {
                    // 접근할 수 없는 폴더는 건너뜀
                }
            }
            return list;
        }

        // ------------------------------------------------------------------ 열거

        private IntPtr OpenInterface(string interfaceId)
        {
            if (_interfaces.TryGetValue(interfaceId, out var h)) return h;
            Api.Check(Api.TLOpenInterface(TL, GenTLApi.Id(interfaceId), out h), $"TLOpenInterface({interfaceId})");
            _interfaces[interfaceId] = h;
            return h;
        }

        internal IntPtr GetInterface(string interfaceId)
        {
            lock (_lock) return OpenInterface(interfaceId);
        }

        /// <summary>모든 인터페이스의 장치 목록을 갱신해 돌려준다.</summary>
        public IReadOnlyList<GenTLDeviceEntry> EnumerateDevices(int timeoutMs)
        {
            var result = new List<GenTLDeviceEntry>();
            lock (_lock)
            {
                Api.TLUpdateInterfaceList(TL, out _, (ulong)timeoutMs);
                if (Api.TLGetNumInterfaces(TL, out uint count) != Gc.ERR_SUCCESS) return result;
                for (uint i = 0; i < count; i++)
                {
                    string ifaceId;
                    try
                    {
                        uint index = i;
                        ifaceId = GenTLApi.QueryString((IntPtr buf, ref UIntPtr size) => Api.TLGetInterfaceID(TL, index, buf, ref size));
                    }
                    catch
                    {
                        continue;
                    }

                    IntPtr iface;
                    try { iface = OpenInterface(ifaceId); }
                    catch { continue; }

                    Api.IFUpdateDeviceList(iface, out _, (ulong)timeoutMs);
                    if (Api.IFGetNumDevices(iface, out uint devCount) != Gc.ERR_SUCCESS) continue;
                    for (uint d = 0; d < devCount; d++)
                    {
                        string devId;
                        try
                        {
                            uint index = d;
                            devId = GenTLApi.QueryString((IntPtr buf, ref UIntPtr size) => Api.IFGetDeviceID(iface, index, buf, ref size));
                        }
                        catch
                        {
                            continue;
                        }

                        byte[] idBytes = GenTLApi.Id(devId);
                        GenTLApi.InfoQuery q = (int cmd, out int type, IntPtr buf, ref UIntPtr size) => Api.IFGetDeviceInfo(iface, idBytes, cmd, out type, buf, ref size);
                        var entry = new GenTLDeviceEntry(this, ifaceId, devId)
                        {
                            Vendor = GenTLApi.InfoString(q, Gc.DEVICE_INFO_VENDOR),
                            Model = GenTLApi.InfoString(q, Gc.DEVICE_INFO_MODEL),
                            Serial = GenTLApi.InfoString(q, Gc.DEVICE_INFO_SERIAL_NUMBER),
                            UserName = GenTLApi.InfoString(q, Gc.DEVICE_INFO_USER_DEFINED_NAME),
                            DisplayName = GenTLApi.InfoString(q, Gc.DEVICE_INFO_DISPLAYNAME),
                            TLType = GenTLApi.InfoString(q, Gc.DEVICE_INFO_TLTYPE),
                        };
                        result.Add(entry);
                    }
                }
            }
            return result;
        }

        /// <summary>인터페이스와 TL 을 닫고 라이브러리를 해제 준비한다 (프로세스 종료 시). 열린 장치는 먼저 닫혀 있어야 한다.</summary>
        internal void Close()
        {
            lock (_lock)
            {
                foreach (var h in _interfaces.Values)
                {
                    try { Api.IFClose(h); } catch { }
                }
                _interfaces.Clear();
                try { Api.TLClose(TL); } catch { }
                try { Api.GCCloseLib(); } catch { }
            }
        }

        public override string ToString() => $"{ShortName} ({Vendor} {TLType})";
    }
}
