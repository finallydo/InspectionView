using System;
using System.Runtime.InteropServices;
using System.Text;
using InspectionView.Core.Errors;

namespace InspectionView.Cameras.GenTL.Native
{
    // GenTL 1.5 (GenTL.h) 상수. 값은 표준 헤더와 같다.
    internal static class Gc
    {
        public const int ERR_SUCCESS = 0;
        public const int ERR_ERROR = -1001;
        public const int ERR_NOT_INITIALIZED = -1002;
        public const int ERR_NOT_IMPLEMENTED = -1003;
        public const int ERR_RESOURCE_IN_USE = -1004;
        public const int ERR_ACCESS_DENIED = -1005;
        public const int ERR_INVALID_HANDLE = -1006;
        public const int ERR_INVALID_ID = -1007;
        public const int ERR_NO_DATA = -1008;
        public const int ERR_INVALID_PARAMETER = -1009;
        public const int ERR_IO = -1010;
        public const int ERR_TIMEOUT = -1011;
        public const int ERR_ABORT = -1012;
        public const int ERR_INVALID_BUFFER = -1013;
        public const int ERR_NOT_AVAILABLE = -1014;
        public const int ERR_INVALID_ADDRESS = -1015;
        public const int ERR_BUFFER_TOO_SMALL = -1016;

        // INFO_DATATYPE
        public const int TYPE_STRING = 1;
        public const int TYPE_STRINGLIST = 2;
        public const int TYPE_INT16 = 3;
        public const int TYPE_UINT16 = 4;
        public const int TYPE_INT32 = 5;
        public const int TYPE_UINT32 = 6;
        public const int TYPE_INT64 = 7;
        public const int TYPE_UINT64 = 8;
        public const int TYPE_FLOAT64 = 9;
        public const int TYPE_PTR = 10;
        public const int TYPE_BOOL8 = 11;
        public const int TYPE_SIZET = 12;
        public const int TYPE_BUFFER = 13;
        public const int TYPE_PTRDIFF = 14;

        // TL_INFO_CMD
        public const int TL_INFO_ID = 0;
        public const int TL_INFO_VENDOR = 1;
        public const int TL_INFO_MODEL = 2;
        public const int TL_INFO_VERSION = 3;
        public const int TL_INFO_TLTYPE = 4;
        public const int TL_INFO_NAME = 5;
        public const int TL_INFO_DISPLAYNAME = 7;

        // INTERFACE_INFO_CMD
        public const int INTERFACE_INFO_ID = 0;
        public const int INTERFACE_INFO_DISPLAYNAME = 1;
        public const int INTERFACE_INFO_TLTYPE = 2;

        // DEVICE_INFO_CMD
        public const int DEVICE_INFO_ID = 0;
        public const int DEVICE_INFO_VENDOR = 1;
        public const int DEVICE_INFO_MODEL = 2;
        public const int DEVICE_INFO_TLTYPE = 3;
        public const int DEVICE_INFO_DISPLAYNAME = 4;
        public const int DEVICE_INFO_ACCESS_STATUS = 5;
        public const int DEVICE_INFO_USER_DEFINED_NAME = 6;
        public const int DEVICE_INFO_SERIAL_NUMBER = 7;
        public const int DEVICE_INFO_VERSION = 8;

        // DEVICE_ACCESS_FLAGS
        public const int DEVICE_ACCESS_READONLY = 2;
        public const int DEVICE_ACCESS_CONTROL = 3;
        public const int DEVICE_ACCESS_EXCLUSIVE = 4;

        // STREAM_INFO_CMD
        public const int STREAM_INFO_PAYLOAD_SIZE = 7;
        public const int STREAM_INFO_DEFINES_PAYLOADSIZE = 9;
        public const int STREAM_INFO_BUF_ANNOUNCE_MIN = 12;

        // BUFFER_INFO_CMD
        public const int BUFFER_INFO_BASE = 0;
        public const int BUFFER_INFO_SIZE = 1;
        public const int BUFFER_INFO_TIMESTAMP = 3;
        public const int BUFFER_INFO_IS_INCOMPLETE = 7;
        public const int BUFFER_INFO_SIZE_FILLED = 9;
        public const int BUFFER_INFO_WIDTH = 10;
        public const int BUFFER_INFO_HEIGHT = 11;
        public const int BUFFER_INFO_XPADDING = 14;
        public const int BUFFER_INFO_FRAMEID = 16;
        public const int BUFFER_INFO_IMAGEPRESENT = 17;
        public const int BUFFER_INFO_IMAGEOFFSET = 18;
        public const int BUFFER_INFO_PIXELFORMAT = 20;
        public const int BUFFER_INFO_PIXELFORMAT_NAMESPACE = 21;
        public const int BUFFER_INFO_DELIVERED_IMAGEHEIGHT = 22;

        // URL_INFO_CMD
        public const int URL_INFO_URL = 0;

        // ACQ_QUEUE_TYPE
        public const int ACQ_QUEUE_ALL_DISCARD = 4;

        // ACQ_STOP_FLAGS
        public const int ACQ_STOP_FLAGS_DEFAULT = 0;
        public const int ACQ_STOP_FLAGS_KILL = 1;

        // EVENT_TYPE
        public const int EVENT_NEW_BUFFER = 1;

        public const ulong INFINITE = ulong.MaxValue;

        public static string ErrorName(int code)
        {
            switch (code)
            {
                case ERR_ERROR: return "GC_ERR_ERROR";
                case ERR_NOT_INITIALIZED: return "GC_ERR_NOT_INITIALIZED";
                case ERR_NOT_IMPLEMENTED: return "GC_ERR_NOT_IMPLEMENTED";
                case ERR_RESOURCE_IN_USE: return "GC_ERR_RESOURCE_IN_USE";
                case ERR_ACCESS_DENIED: return "GC_ERR_ACCESS_DENIED";
                case ERR_INVALID_HANDLE: return "GC_ERR_INVALID_HANDLE";
                case ERR_INVALID_ID: return "GC_ERR_INVALID_ID";
                case ERR_NO_DATA: return "GC_ERR_NO_DATA";
                case ERR_INVALID_PARAMETER: return "GC_ERR_INVALID_PARAMETER";
                case ERR_IO: return "GC_ERR_IO";
                case ERR_TIMEOUT: return "GC_ERR_TIMEOUT";
                case ERR_ABORT: return "GC_ERR_ABORT";
                case ERR_INVALID_BUFFER: return "GC_ERR_INVALID_BUFFER";
                case ERR_NOT_AVAILABLE: return "GC_ERR_NOT_AVAILABLE";
                case ERR_INVALID_ADDRESS: return "GC_ERR_INVALID_ADDRESS";
                case ERR_BUFFER_TOO_SMALL: return "GC_ERR_BUFFER_TOO_SMALL";
                default: return $"GC error {code}";
            }
        }
    }

    /// <summary>GenTL 호출 실패.</summary>
    public sealed class GenTLException : VisionException
    {
        public int GcError { get; }

        public GenTLException(int gcError, string message)
            : base(gcError == Gc.ERR_TIMEOUT ? ErrorCode.CameraTimeout : ErrorCode.DeviceError, message)
        {
            GcError = gcError;
        }
    }

    /// <summary>
    /// GenTL 프로듀서(.cti) 하나의 함수 테이블. .cti 는 표준 C 함수를 내보내는 DLL 이다.
    /// 의존 DLL 이 .cti 폴더에 있으므로 LOAD_WITH_ALTERED_SEARCH_PATH 로 로드한다.
    /// </summary>
    internal sealed class GenTLApi
    {
        // ------------------------------------------------------------------ 함수 형식 (GC_CALLTYPE = __stdcall, x64 에서는 의미 없음)
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int GCInitLibFn();
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int GCCloseLibFn();
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int GCGetLastErrorFn(out int code, IntPtr text, ref UIntPtr size);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int GCGetInfoFn(int cmd, out int type, IntPtr buffer, ref UIntPtr size);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int GCReadPortFn(IntPtr port, ulong address, IntPtr buffer, ref UIntPtr size);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int GCWritePortFn(IntPtr port, ulong address, IntPtr buffer, ref UIntPtr size);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int GCGetPortURLFn(IntPtr port, IntPtr url, ref UIntPtr size);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int GCGetNumPortURLsFn(IntPtr port, out uint count);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int GCGetPortURLInfoFn(IntPtr port, uint index, int cmd, out int type, IntPtr buffer, ref UIntPtr size);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int GCRegisterEventFn(IntPtr source, int eventType, out IntPtr evt);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int GCUnregisterEventFn(IntPtr source, int eventType);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int EventGetDataFn(IntPtr evt, IntPtr buffer, ref UIntPtr size, ulong timeout);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int EventKillFn(IntPtr evt);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int EventFlushFn(IntPtr evt);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int TLOpenFn(out IntPtr tl);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int TLCloseFn(IntPtr tl);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int TLUpdateInterfaceListFn(IntPtr tl, out byte changed, ulong timeout);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int TLGetNumInterfacesFn(IntPtr tl, out uint count);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int TLGetInterfaceIDFn(IntPtr tl, uint index, IntPtr id, ref UIntPtr size);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int TLGetInterfaceInfoFn(IntPtr tl, byte[] ifaceId, int cmd, out int type, IntPtr buffer, ref UIntPtr size);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int TLOpenInterfaceFn(IntPtr tl, byte[] ifaceId, out IntPtr iface);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int IFCloseFn(IntPtr iface);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int IFUpdateDeviceListFn(IntPtr iface, out byte changed, ulong timeout);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int IFGetNumDevicesFn(IntPtr iface, out uint count);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int IFGetDeviceIDFn(IntPtr iface, uint index, IntPtr id, ref UIntPtr size);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int IFGetDeviceInfoFn(IntPtr iface, byte[] deviceId, int cmd, out int type, IntPtr buffer, ref UIntPtr size);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int IFOpenDeviceFn(IntPtr iface, byte[] deviceId, int access, out IntPtr device);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DevGetPortFn(IntPtr device, out IntPtr port);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DevGetNumDataStreamsFn(IntPtr device, out uint count);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DevGetDataStreamIDFn(IntPtr device, uint index, IntPtr id, ref UIntPtr size);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DevOpenDataStreamFn(IntPtr device, byte[] streamId, out IntPtr stream);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DevCloseFn(IntPtr device);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DSAnnounceBufferFn(IntPtr stream, IntPtr buffer, UIntPtr size, IntPtr userData, out IntPtr bufferHandle);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DSAllocAndAnnounceBufferFn(IntPtr stream, UIntPtr size, IntPtr userData, out IntPtr bufferHandle);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DSFlushQueueFn(IntPtr stream, int operation);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DSStartAcquisitionFn(IntPtr stream, int flags, ulong count);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DSStopAcquisitionFn(IntPtr stream, int flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DSGetInfoFn(IntPtr stream, int cmd, out int type, IntPtr buffer, ref UIntPtr size);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DSCloseFn(IntPtr stream);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DSRevokeBufferFn(IntPtr stream, IntPtr bufferHandle, out IntPtr buffer, out IntPtr userData);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DSQueueBufferFn(IntPtr stream, IntPtr bufferHandle);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DSGetBufferInfoFn(IntPtr stream, IntPtr bufferHandle, int cmd, out int type, IntPtr buffer, ref UIntPtr size);

        /// <summary>정보 조회 함수 공통 모양 (cmd → type, buffer, size).</summary>
        public delegate int InfoQuery(int cmd, out int type, IntPtr buffer, ref UIntPtr size);
        /// <summary>문자열 조회 함수 공통 모양 (buffer, size).</summary>
        public delegate int StringQuery(IntPtr buffer, ref UIntPtr size);

        public GCInitLibFn GCInitLib = null!;
        public GCCloseLibFn GCCloseLib = null!;
        public GCGetLastErrorFn GCGetLastError = null!;
        public GCGetInfoFn GCGetInfo = null!;
        public GCReadPortFn GCReadPort = null!;
        public GCWritePortFn GCWritePort = null!;
        public GCGetPortURLFn? GCGetPortURL;
        public GCGetNumPortURLsFn? GCGetNumPortURLs;
        public GCGetPortURLInfoFn? GCGetPortURLInfo;
        public GCRegisterEventFn GCRegisterEvent = null!;
        public GCUnregisterEventFn GCUnregisterEvent = null!;
        public EventGetDataFn EventGetData = null!;
        public EventKillFn EventKill = null!;
        public EventFlushFn EventFlush = null!;
        public TLOpenFn TLOpen = null!;
        public TLCloseFn TLClose = null!;
        public TLUpdateInterfaceListFn TLUpdateInterfaceList = null!;
        public TLGetNumInterfacesFn TLGetNumInterfaces = null!;
        public TLGetInterfaceIDFn TLGetInterfaceID = null!;
        public TLGetInterfaceInfoFn TLGetInterfaceInfo = null!;
        public TLOpenInterfaceFn TLOpenInterface = null!;
        public IFCloseFn IFClose = null!;
        public IFUpdateDeviceListFn IFUpdateDeviceList = null!;
        public IFGetNumDevicesFn IFGetNumDevices = null!;
        public IFGetDeviceIDFn IFGetDeviceID = null!;
        public IFGetDeviceInfoFn IFGetDeviceInfo = null!;
        public IFOpenDeviceFn IFOpenDevice = null!;
        public DevGetPortFn DevGetPort = null!;
        public DevGetNumDataStreamsFn DevGetNumDataStreams = null!;
        public DevGetDataStreamIDFn DevGetDataStreamID = null!;
        public DevOpenDataStreamFn DevOpenDataStream = null!;
        public DevCloseFn DevClose = null!;
        public DSAnnounceBufferFn DSAnnounceBuffer = null!;
        public DSAllocAndAnnounceBufferFn? DSAllocAndAnnounceBuffer;
        public DSFlushQueueFn DSFlushQueue = null!;
        public DSStartAcquisitionFn DSStartAcquisition = null!;
        public DSStopAcquisitionFn DSStopAcquisition = null!;
        public DSGetInfoFn DSGetInfo = null!;
        public DSCloseFn DSClose = null!;
        public DSRevokeBufferFn DSRevokeBuffer = null!;
        public DSQueueBufferFn DSQueueBuffer = null!;
        public DSGetBufferInfoFn DSGetBufferInfo = null!;

        public string Path { get; }
        private readonly IntPtr _module;

        private GenTLApi(string path, IntPtr module)
        {
            Path = path;
            _module = module;
        }

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryExW(string fileName, IntPtr reserved, uint flags);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        private const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;

        public static GenTLApi Load(string path)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                throw new VisionException(ErrorCode.CameraSdkNotAvailable, "GenTL producers can only be loaded on Windows.");
            IntPtr module = LoadLibraryExW(path, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
            if (module == IntPtr.Zero)
                throw new VisionException(ErrorCode.CameraSdkNotAvailable, $"Cannot load GenTL producer '{path}' (Win32 error {Marshal.GetLastWin32Error()}).");

            var api = new GenTLApi(path, module);
            api.GCInitLib = api.Get<GCInitLibFn>("GCInitLib");
            api.GCCloseLib = api.Get<GCCloseLibFn>("GCCloseLib");
            api.GCGetLastError = api.Get<GCGetLastErrorFn>("GCGetLastError");
            api.GCGetInfo = api.Get<GCGetInfoFn>("GCGetInfo");
            api.GCReadPort = api.Get<GCReadPortFn>("GCReadPort");
            api.GCWritePort = api.Get<GCWritePortFn>("GCWritePort");
            api.GCGetPortURL = api.TryGet<GCGetPortURLFn>("GCGetPortURL");
            api.GCGetNumPortURLs = api.TryGet<GCGetNumPortURLsFn>("GCGetNumPortURLs");
            api.GCGetPortURLInfo = api.TryGet<GCGetPortURLInfoFn>("GCGetPortURLInfo");
            api.GCRegisterEvent = api.Get<GCRegisterEventFn>("GCRegisterEvent");
            api.GCUnregisterEvent = api.Get<GCUnregisterEventFn>("GCUnregisterEvent");
            api.EventGetData = api.Get<EventGetDataFn>("EventGetData");
            api.EventKill = api.Get<EventKillFn>("EventKill");
            api.EventFlush = api.Get<EventFlushFn>("EventFlush");
            api.TLOpen = api.Get<TLOpenFn>("TLOpen");
            api.TLClose = api.Get<TLCloseFn>("TLClose");
            api.TLUpdateInterfaceList = api.Get<TLUpdateInterfaceListFn>("TLUpdateInterfaceList");
            api.TLGetNumInterfaces = api.Get<TLGetNumInterfacesFn>("TLGetNumInterfaces");
            api.TLGetInterfaceID = api.Get<TLGetInterfaceIDFn>("TLGetInterfaceID");
            api.TLGetInterfaceInfo = api.Get<TLGetInterfaceInfoFn>("TLGetInterfaceInfo");
            api.TLOpenInterface = api.Get<TLOpenInterfaceFn>("TLOpenInterface");
            api.IFClose = api.Get<IFCloseFn>("IFClose");
            api.IFUpdateDeviceList = api.Get<IFUpdateDeviceListFn>("IFUpdateDeviceList");
            api.IFGetNumDevices = api.Get<IFGetNumDevicesFn>("IFGetNumDevices");
            api.IFGetDeviceID = api.Get<IFGetDeviceIDFn>("IFGetDeviceID");
            api.IFGetDeviceInfo = api.Get<IFGetDeviceInfoFn>("IFGetDeviceInfo");
            api.IFOpenDevice = api.Get<IFOpenDeviceFn>("IFOpenDevice");
            api.DevGetPort = api.Get<DevGetPortFn>("DevGetPort");
            api.DevGetNumDataStreams = api.Get<DevGetNumDataStreamsFn>("DevGetNumDataStreams");
            api.DevGetDataStreamID = api.Get<DevGetDataStreamIDFn>("DevGetDataStreamID");
            api.DevOpenDataStream = api.Get<DevOpenDataStreamFn>("DevOpenDataStream");
            api.DevClose = api.Get<DevCloseFn>("DevClose");
            api.DSAnnounceBuffer = api.Get<DSAnnounceBufferFn>("DSAnnounceBuffer");
            api.DSAllocAndAnnounceBuffer = api.TryGet<DSAllocAndAnnounceBufferFn>("DSAllocAndAnnounceBuffer");
            api.DSFlushQueue = api.Get<DSFlushQueueFn>("DSFlushQueue");
            api.DSStartAcquisition = api.Get<DSStartAcquisitionFn>("DSStartAcquisition");
            api.DSStopAcquisition = api.Get<DSStopAcquisitionFn>("DSStopAcquisition");
            api.DSGetInfo = api.Get<DSGetInfoFn>("DSGetInfo");
            api.DSClose = api.Get<DSCloseFn>("DSClose");
            api.DSRevokeBuffer = api.Get<DSRevokeBufferFn>("DSRevokeBuffer");
            api.DSQueueBuffer = api.Get<DSQueueBufferFn>("DSQueueBuffer");
            api.DSGetBufferInfo = api.Get<DSGetBufferInfoFn>("DSGetBufferInfo");
            return api;
        }

        private T? TryGet<T>(string name) where T : Delegate
        {
            IntPtr p = GetProcAddress(_module, name);
            return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(p);
        }

        private T Get<T>(string name) where T : Delegate
            => TryGet<T>(name) ?? throw new VisionException(ErrorCode.CameraSdkNotAvailable, $"GenTL producer '{Path}' does not export {name}.");

        // ------------------------------------------------------------------ 오류

        public string LastErrorText()
        {
            try
            {
                var size = (UIntPtr)1024;
                IntPtr buf = Marshal.AllocHGlobal(1024);
                try
                {
                    if (GCGetLastError(out _, buf, ref size) != Gc.ERR_SUCCESS) return "";
                    return Marshal.PtrToStringAnsi(buf) ?? "";
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
            catch
            {
                return "";
            }
        }

        public void Check(int rc, string what)
        {
            if (rc == Gc.ERR_SUCCESS) return;
            string text = LastErrorText();
            throw new GenTLException(rc, $"{what} failed: {Gc.ErrorName(rc)}{(string.IsNullOrEmpty(text) ? "" : " - " + text)}");
        }

        // ------------------------------------------------------------------ 조회 도우미

        public static byte[] Id(string id)
        {
            var b = Encoding.ASCII.GetBytes(id ?? "");
            Array.Resize(ref b, b.Length + 1);
            return b;
        }

        public static string QueryString(StringQuery query)
        {
            int capacity = 512;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                IntPtr buf = Marshal.AllocHGlobal(capacity);
                try
                {
                    var size = (UIntPtr)capacity;
                    int rc = query(buf, ref size);
                    if (rc == Gc.ERR_SUCCESS) return Marshal.PtrToStringAnsi(buf) ?? "";
                    if (rc != Gc.ERR_BUFFER_TOO_SMALL || (int)size <= capacity) throw new GenTLException(rc, $"GenTL string query failed: {Gc.ErrorName(rc)}");
                    capacity = (int)size;
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
            throw new GenTLException(Gc.ERR_BUFFER_TOO_SMALL, "GenTL string query failed: buffer too small.");
        }

        /// <summary>문자열 정보 (실패하면 빈 문자열).</summary>
        public static string InfoString(InfoQuery query, int cmd)
        {
            try
            {
                return QueryString((IntPtr buf, ref UIntPtr size) => query(cmd, out _, buf, ref size));
            }
            catch
            {
                return "";
            }
        }

        /// <summary>숫자 정보 (실패하면 null). 포인터/size_t/bool 도 정수로 돌려준다.</summary>
        public static long? InfoInt(InfoQuery query, int cmd)
        {
            IntPtr buf = Marshal.AllocHGlobal(16);
            try
            {
                Marshal.WriteInt64(buf, 0);
                Marshal.WriteInt64(buf, 8, 0);
                var size = (UIntPtr)16;
                if (query(cmd, out int type, buf, ref size) != Gc.ERR_SUCCESS) return null;
                switch (type)
                {
                    case Gc.TYPE_INT16: return Marshal.ReadInt16(buf);
                    case Gc.TYPE_UINT16: return (ushort)Marshal.ReadInt16(buf);
                    case Gc.TYPE_INT32: return Marshal.ReadInt32(buf);
                    case Gc.TYPE_UINT32: return (uint)Marshal.ReadInt32(buf);
                    case Gc.TYPE_BOOL8: return Marshal.ReadByte(buf);
                    case Gc.TYPE_FLOAT64: return (long)BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buf));
                    case Gc.TYPE_PTR:
                    case Gc.TYPE_SIZET:
                    case Gc.TYPE_PTRDIFF:
                        return IntPtr.Size == 8 ? Marshal.ReadInt64(buf) : Marshal.ReadInt32(buf);
                    default:
                        // INT64/UINT64 및 형식을 알리지 않는 프로듀서: size 로 판단
                        long s = (long)(ulong)size;
                        return s == 1 ? Marshal.ReadByte(buf) : s == 2 ? (ushort)Marshal.ReadInt16(buf) : s == 4 ? (uint)Marshal.ReadInt32(buf) : Marshal.ReadInt64(buf);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
    }
}
