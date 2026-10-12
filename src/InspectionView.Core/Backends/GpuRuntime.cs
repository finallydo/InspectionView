using System;
using System.Runtime.InteropServices;
using InspectionView.Core.Errors;
using InspectionView.Core.Memory;

namespace InspectionView.Core.Backends
{
    /// <summary>
    /// 자체 C++/CUDA DLL (InspectionView.Native.dll) 에 대한 P/Invoke 래퍼.
    /// DLL 이 없거나 CUDA 디바이스가 없으면 <see cref="IsAvailable"/> 이 false 가 되고 GPU 백엔드는 건너뛴다.
    /// </summary>
    public static class GpuRuntime
    {
        public const string DllName = "InspectionView.Native";

        private static readonly Lazy<bool> _available = new Lazy<bool>(Probe);
        private static readonly Lazy<NativeDeviceAllocator?> _allocator =
            new Lazy<NativeDeviceAllocator?>(() => _available.Value ? new NativeDeviceAllocator(0) : null);

        public static bool IsAvailable => _available.Value;

        /// <summary>기본 디바이스(0번) 할당기. GPU 가 없으면 null.</summary>
        public static IDeviceAllocator? DefaultAllocator => _allocator.Value;

        /// <summary>네이티브 DLL 버전 (major*10000 + minor*100 + patch). DLL 이 없으면 -1.</summary>
        public static int NativeVersion
        {
            get
            {
                try { return Native.ivn_get_version(); }
                catch { return -1; }
            }
        }

        private static bool Probe()
        {
            try
            {
                return Native.ivn_device_count() > 0;
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
            catch (BadImageFormatException) { return false; }
        }

        internal static void Check(int status, string what)
        {
            if (status != 0)
                throw new VisionException(ErrorCode.DeviceError, $"{what} failed with native status {status}.");
        }

        /// <summary>네이티브 함수 선언. 시그니처는 InspectionView.Native/include/ivnative.h 와 일치해야 한다.</summary>
        internal static class Native
        {
            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            public static extern int ivn_get_version();

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            public static extern int ivn_device_count();

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            public static extern int ivn_set_device(int device);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            public static extern int ivn_malloc(long bytes, out IntPtr devicePtr);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            public static extern int ivn_free(IntPtr devicePtr);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            public static extern int ivn_memcpy_h2d(IntPtr devicePtr, IntPtr hostPtr, long bytes);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            public static extern int ivn_memcpy_d2h(IntPtr hostPtr, IntPtr devicePtr, long bytes);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            public static extern int ivn_synchronize();
        }
    }

    /// <summary>네이티브 DLL 기반 디바이스 할당기.</summary>
    public sealed class NativeDeviceAllocator : IDeviceAllocator
    {
        public int DeviceIndex { get; }
        public string Name => $"CUDA:{DeviceIndex}";
        public bool IsAvailable => GpuRuntime.IsAvailable;

        public NativeDeviceAllocator(int deviceIndex)
        {
            DeviceIndex = deviceIndex;
        }

        public IDeviceBuffer Allocate(long bytes)
        {
            GpuRuntime.Check(GpuRuntime.Native.ivn_set_device(DeviceIndex), "ivn_set_device");
            GpuRuntime.Check(GpuRuntime.Native.ivn_malloc(bytes, out var ptr), "ivn_malloc");
            return new NativeDeviceBuffer(ptr, bytes);
        }

        public void Synchronize()
            => GpuRuntime.Check(GpuRuntime.Native.ivn_synchronize(), "ivn_synchronize");

        private sealed class NativeDeviceBuffer : IDeviceBuffer
        {
            private IntPtr _ptr;

            public IntPtr DevicePointer => _ptr;
            public long Length { get; }

            public NativeDeviceBuffer(IntPtr ptr, long length)
            {
                _ptr = ptr;
                Length = length;
            }

            public void Upload(IntPtr hostPointer, long bytes)
            {
                CheckSize(bytes);
                GpuRuntime.Check(GpuRuntime.Native.ivn_memcpy_h2d(_ptr, hostPointer, bytes), "ivn_memcpy_h2d");
            }

            public void Download(IntPtr hostPointer, long bytes)
            {
                CheckSize(bytes);
                GpuRuntime.Check(GpuRuntime.Native.ivn_memcpy_d2h(hostPointer, _ptr, bytes), "ivn_memcpy_d2h");
            }

            private void CheckSize(long bytes)
            {
                if (_ptr == IntPtr.Zero)
                    throw VisionException.Disposed(nameof(NativeDeviceBuffer));
                if (bytes < 0 || bytes > Length)
                    throw VisionException.OutOfRange(nameof(bytes), bytes, 0, Length);
            }

            public void Dispose()
            {
                if (_ptr == IntPtr.Zero)
                    return;
                try { GpuRuntime.Native.ivn_free(_ptr); }
                catch { /* 프로세스 종료 중 DLL 언로드 등은 무시 */ }
                _ptr = IntPtr.Zero;
                GC.SuppressFinalize(this);
            }

            ~NativeDeviceBuffer()
            {
                Dispose();
            }
        }
    }
}
