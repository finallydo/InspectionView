using System;

namespace InspectionView.Core.Memory
{
    /// <summary>
    /// GPU(Device) 메모리 할당기. 구현은 자체 C++/CUDA DLL (P/Invoke) 가 제공한다.
    /// Core 는 이 인터페이스만 알고 있으므로 GPU 가 없는 환경에서도 동작한다.
    /// </summary>
    public interface IDeviceAllocator
    {
        /// <summary>디바이스 이름 (예: "CUDA:0").</summary>
        string Name { get; }

        /// <summary>현재 프로세스에서 사용 가능한지.</summary>
        bool IsAvailable { get; }

        /// <summary>디바이스 메모리를 할당한다.</summary>
        IDeviceBuffer Allocate(long bytes);

        /// <summary>진행 중인 모든 디바이스 작업이 끝날 때까지 대기한다.</summary>
        void Synchronize();
    }

    /// <summary>디바이스 메모리 블록.</summary>
    public interface IDeviceBuffer : IDisposable
    {
        IntPtr DevicePointer { get; }
        long Length { get; }

        /// <summary>Host → Device 복사.</summary>
        void Upload(IntPtr hostPointer, long bytes);

        /// <summary>Device → Host 복사.</summary>
        void Download(IntPtr hostPointer, long bytes);
    }
}
