using System;
using System.Runtime.InteropServices;
using InspectionView.Core.Errors;

namespace InspectionView.Core.Memory
{
    /// <summary>
    /// 정렬된(기본 64 byte) 비관리 Host 메모리 블록.
    /// OpenCV Mat 이 복사 없이 감쌀 수 있도록 고정 주소를 제공하며, 수 GB 크기도 허용한다.
    /// </summary>
    public sealed unsafe class NativeBuffer : IDisposable
    {
        private IntPtr _raw;
        private readonly long _reserved;

        /// <summary>정렬된 시작 주소.</summary>
        public IntPtr Pointer { get; private set; }

        /// <summary>사용 가능한 바이트 수.</summary>
        public long Length { get; }

        /// <summary>정렬 단위(바이트).</summary>
        public int Alignment { get; }

        public bool IsDisposed => _raw == IntPtr.Zero;

        public NativeBuffer(long length, int alignment = 64)
        {
            if (length < 0)
                throw VisionException.InvalidArgument(nameof(length), "must be >= 0");
            if (alignment <= 0 || (alignment & (alignment - 1)) != 0)
                throw VisionException.InvalidArgument(nameof(alignment), "must be a power of two");

            Length = length;
            Alignment = alignment;
            _reserved = length + alignment;

            try
            {
                _raw = Marshal.AllocHGlobal(new IntPtr(_reserved));
            }
            catch (OutOfMemoryException ex)
            {
                throw new VisionException(ErrorCode.OutOfMemory, $"Failed to allocate {length:N0} bytes of host memory.", ex);
            }

            long p = _raw.ToInt64();
            p = (p + alignment - 1) & ~((long)alignment - 1);
            Pointer = new IntPtr(p);

            GC.AddMemoryPressure(_reserved);
        }

        /// <summary>전체 버퍼를 Span 으로 노출한다. 2GB 이상이면 예외.</summary>
        public Span<byte> AsSpan()
        {
            ThrowIfDisposed();
            if (Length > int.MaxValue)
                throw new VisionException(ErrorCode.OutOfRange, "Buffer is larger than 2GB; use Slice(offset, length) instead.");
            return new Span<byte>((void*)Pointer, (int)Length);
        }

        /// <summary>부분 구간을 Span 으로 노출한다.</summary>
        public Span<byte> Slice(long offset, int length)
        {
            ThrowIfDisposed();
            if (offset < 0 || length < 0 || offset + length > Length)
                throw VisionException.OutOfRange("offset/length", $"{offset}+{length}", 0, Length);
            return new Span<byte>((byte*)Pointer + offset, length);
        }

        /// <summary>전체를 0 으로 채운다 (대용량도 청크 단위로 처리).</summary>
        public void Clear()
        {
            ThrowIfDisposed();
            const int chunk = 1 << 30;
            long remaining = Length;
            byte* p = (byte*)Pointer;
            while (remaining > 0)
            {
                int n = remaining > chunk ? chunk : (int)remaining;
                new Span<byte>(p, n).Clear();
                p += n;
                remaining -= n;
            }
        }

        /// <summary>다른 버퍼로 바이트 복사.</summary>
        public void CopyTo(NativeBuffer dest, long bytes)
        {
            ThrowIfDisposed();
            dest.ThrowIfDisposed();
            if (bytes < 0 || bytes > Length || bytes > dest.Length)
                throw VisionException.OutOfRange(nameof(bytes), bytes, 0, Math.Min(Length, dest.Length));
            Buffer.MemoryCopy((void*)Pointer, (void*)dest.Pointer, dest.Length, bytes);
        }

        private void ThrowIfDisposed()
        {
            if (IsDisposed)
                throw VisionException.Disposed(nameof(NativeBuffer));
        }

        public void Dispose()
        {
            Free();
            GC.SuppressFinalize(this);
        }

        ~NativeBuffer()
        {
            Free();
        }

        private void Free()
        {
            if (_raw == IntPtr.Zero)
                return;
            Marshal.FreeHGlobal(_raw);
            _raw = IntPtr.Zero;
            Pointer = IntPtr.Zero;
            GC.RemoveMemoryPressure(_reserved);
        }
    }
}
