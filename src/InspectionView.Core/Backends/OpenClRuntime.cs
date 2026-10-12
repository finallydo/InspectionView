using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace InspectionView.Core.Backends
{
    /// <summary>
    /// OpenCL 디바이스 탐지 (Khronos ICD 로더 OpenCL.dll 직접 호출). OpenCV 의 T-API(UMat) 는 OpenCL 런타임이 있으면
    /// 자동으로 그것을 쓰므로, 이 클래스는 "GPU 가 실제로 있는지" 와 디바이스 이름을 알아내 백엔드 선택/표시에 쓴다.
    /// NVIDIA(CUDA) 뿐 아니라 Intel/AMD 내장 GPU 에서도 동작한다.
    /// </summary>
    public static class OpenClRuntime
    {
        private static readonly Lazy<string[]> _devices = new Lazy<string[]>(Probe);
        private static readonly object _lock = new object();
        private static bool? _openCvEnabled;

        /// <summary>OpenCL GPU 디바이스가 하나 이상 있는지.</summary>
        public static bool IsAvailable => _devices.Value.Length > 0;

        /// <summary>발견된 GPU 디바이스 이름들.</summary>
        public static IReadOnlyList<string> Devices => _devices.Value;

        /// <summary>첫 GPU 디바이스 이름 (없으면 빈 문자열).</summary>
        public static string DeviceName => _devices.Value.Length > 0 ? _devices.Value[0] : "";

        /// <summary>OpenCvSharp 에 SetUseOpenCL 바인딩이 있으면 켠다. 결과: true/false, 바인딩이 없으면 null (OpenCV 기본값 = 사용).</summary>
        public static bool? EnableInOpenCv()
        {
            lock (_lock)
            {
                if (_openCvEnabled.HasValue) return _openCvEnabled;
                try
                {
                    var cv2 = typeof(OpenCvSharp.Cv2);
                    var set = cv2.GetMethod("SetUseOpenCL", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(bool) }, null);
                    set?.Invoke(null, new object[] { true });
                    var use = cv2.GetMethod("UseOpenCL", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                    _openCvEnabled = use != null ? (bool?)(bool)use.Invoke(null, null)! : null;
                }
                catch
                {
                    _openCvEnabled = null;
                }
                return _openCvEnabled;
            }
        }

        private static string[] Probe()
        {
            try
            {
                var list = new List<string>();
                if (Native.clGetPlatformIDs(0, null, out uint platformCount) != 0 || platformCount == 0)
                    return Array.Empty<string>();
                var platforms = new IntPtr[platformCount];
                if (Native.clGetPlatformIDs(platformCount, platforms, out platformCount) != 0)
                    return Array.Empty<string>();

                foreach (var platform in platforms)
                {
                    if (Native.clGetDeviceIDs(platform, Native.CL_DEVICE_TYPE_GPU, 0, null, out uint deviceCount) != 0 || deviceCount == 0)
                        continue;
                    var devices = new IntPtr[deviceCount];
                    if (Native.clGetDeviceIDs(platform, Native.CL_DEVICE_TYPE_GPU, deviceCount, devices, out deviceCount) != 0)
                        continue;
                    foreach (var dev in devices)
                        list.Add(DeviceString(dev, Native.CL_DEVICE_NAME));
                }
                return list.ToArray();
            }
            catch (DllNotFoundException) { return Array.Empty<string>(); }
            catch (EntryPointNotFoundException) { return Array.Empty<string>(); }
            catch (BadImageFormatException) { return Array.Empty<string>(); }
            catch (Exception) { return Array.Empty<string>(); }
        }

        private static string DeviceString(IntPtr device, uint param)
        {
            var buf = new byte[512];
            if (Native.clGetDeviceInfo(device, param, (UIntPtr)buf.Length, buf, out UIntPtr size) != 0)
                return "OpenCL device";
            int n = (int)Math.Min((ulong)size, (ulong)buf.Length);
            while (n > 0 && buf[n - 1] == 0) n--;
            return Encoding.ASCII.GetString(buf, 0, n).Trim();
        }

        private static class Native
        {
            public const ulong CL_DEVICE_TYPE_GPU = 1 << 2;
            public const uint CL_DEVICE_NAME = 0x102B;

            private const string Lib = "OpenCL";

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern int clGetPlatformIDs(uint numEntries, IntPtr[]? platforms, out uint numPlatforms);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern int clGetDeviceIDs(IntPtr platform, ulong deviceType, uint numEntries, IntPtr[]? devices, out uint numDevices);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern int clGetDeviceInfo(IntPtr device, uint paramName, UIntPtr paramValueSize, byte[] paramValue, out UIntPtr paramValueSizeRet);
        }
    }
}
