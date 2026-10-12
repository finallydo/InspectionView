using System;
using InspectionView.Core.Errors;

namespace InspectionView.Core.Memory
{
    /// <summary>
    /// Host(NativeBuffer) 와 Device(IDeviceBuffer) 를 묶어 "어느 쪽이 최신인가" 를 추적하고
    /// 접근 시점에 자동으로 동기화하는 저장소.
    /// <para>
    /// 여러 Image 가 같은 저장소를 공유할 수 있으므로(ReduceDomain 등) 이 클래스 자체도 참조 카운트를 가진다.
    /// </para>
    /// </summary>
    public sealed class HostDeviceStorage : IconicObject
    {
        private readonly object _sync = new object();
        private NativeBuffer? _host;
        private IDeviceBuffer? _device;
        private IDeviceAllocator? _deviceOwner;
        private bool _hostValid;
        private bool _deviceValid;

        public override string TypeName => "Storage";

        /// <summary>바이트 크기.</summary>
        public long Length { get; }

        public bool HasDeviceCopy => _device != null;
        public bool IsHostValid => _hostValid;
        public bool IsDeviceValid => _deviceValid;

        public HostDeviceStorage(long bytes)
        {
            Length = bytes;
            _host = new NativeBuffer(bytes);
            _hostValid = true;
        }

        /// <summary>
        /// Host 포인터를 얻는다. Device 쪽이 최신이면 먼저 내려받는다.
        /// <paramref name="forWrite"/> 가 true 면 Device 사본은 무효화된다.
        /// </summary>
        public IntPtr AcquireHost(bool forWrite)
        {
            ThrowIfDisposed();
            lock (_sync)
            {
                var host = _host ?? throw VisionException.Disposed(TypeName);

                if (!_hostValid && _deviceValid && _device != null)
                    _device.Download(host.Pointer, Length);

                _hostValid = true;
                if (forWrite)
                    _deviceValid = false;

                return host.Pointer;
            }
        }

        /// <summary>
        /// Device 포인터를 얻는다. 필요하면 할당하고 Host 데이터를 올린다.
        /// <paramref name="forWrite"/> 가 true 면 Host 사본은 무효화된다.
        /// </summary>
        public IntPtr AcquireDevice(IDeviceAllocator allocator, bool forWrite)
        {
            ThrowIfDisposed();
            if (allocator == null)
                throw VisionException.NullArgument(nameof(allocator));
            if (!allocator.IsAvailable)
                throw new VisionException(ErrorCode.DeviceNotAvailable, $"Device '{allocator.Name}' is not available.");

            lock (_sync)
            {
                var host = _host ?? throw VisionException.Disposed(TypeName);

                if (_device == null)
                {
                    _device = allocator.Allocate(Length);
                    _deviceOwner = allocator;
                    _deviceValid = false;
                }

                if (!_deviceValid && _hostValid)
                    _device.Upload(host.Pointer, Length);

                _deviceValid = true;
                if (forWrite)
                    _hostValid = false;

                return _device.DevicePointer;
            }
        }

        /// <summary>Device 사본을 버린다 (필요하면 먼저 Host 로 내려받는다).</summary>
        public void ReleaseDevice()
        {
            lock (_sync)
            {
                if (_device == null)
                    return;

                if (_deviceValid && !_hostValid && _host != null)
                {
                    _device.Download(_host.Pointer, Length);
                    _hostValid = true;
                }

                _device.Dispose();
                _device = null;
                _deviceOwner = null;
                _deviceValid = false;
            }
        }

        /// <summary>Host 데이터가 최신임을 보장한다 (저장/표시 전 호출).</summary>
        public void SyncToHost() => AcquireHost(forWrite: false);

        /// <summary>Host 버퍼 (직접 접근용). 동기화 상태를 바꾸지 않으므로 주의해서 사용.</summary>
        public NativeBuffer HostBuffer
        {
            get
            {
                ThrowIfDisposed();
                return _host ?? throw VisionException.Disposed(TypeName);
            }
        }

        protected override void ReleaseResources(bool disposing)
        {
            // 비관리 자원이므로 파이널라이저에서도 해제한다.
            _device?.Dispose();
            _device = null;
            _deviceOwner = null;
            _host?.Dispose();
            _host = null;
            _hostValid = false;
            _deviceValid = false;
        }
    }
}
