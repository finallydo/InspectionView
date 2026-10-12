using System;
using System.Collections.Generic;
using InspectionView.Core.Errors;
using InspectionView.Core.Memory;

namespace InspectionView.Core.Types
{
    /// <summary>
    /// 범용 이미지. 8bit / 16bit / 32bit 정수, Float 원소와 임의 채널 수(1 = Gray, 3 = Color 등)를 지원한다.
    /// <para>
    /// 메모리 레이아웃은 OpenCV 와 동일한 인터리브(HWC) 방식이며 행 간격(<see cref="Stride"/>)은 64 byte 정렬된다.
    /// 픽셀 데이터는 <see cref="HostDeviceStorage"/> 가 소유하며 Host/Device 간 동기화는 접근 시 자동으로 일어난다.
    /// </para>
    /// <para>
    /// <see cref="Domain"/> 은 Halcon 의 domain 과 같은 개념으로, 연산 대상 영역을 제한한다 (null = 전체).
    /// <see cref="ReduceDomain"/> 은 픽셀을 복사하지 않고 저장소를 공유한다.
    /// </para>
    /// </summary>
    public sealed unsafe class Image : IconicObject
    {
        private const int RowAlignment = 64;

        private HostDeviceStorage? _storage;
        private Region? _domain;

        public override string TypeName => "Image";

        public int Width { get; }
        public int Height { get; }
        public int Channels { get; }
        public PixelType PixelType { get; }

        /// <summary>한 행의 바이트 수 (정렬 패딩 포함).</summary>
        public long Stride { get; }

        /// <summary>한 픽셀(모든 채널)의 바이트 수.</summary>
        public int BytesPerPixel { get; }

        /// <summary>전체 버퍼 크기(바이트).</summary>
        public long SizeInBytes => Stride * Height;

        /// <summary>이미지 전체 사각형.</summary>
        public RectI Bounds => new RectI(0, 0, Width, Height);

        /// <summary>연산 대상 영역. null 이면 전체 이미지.</summary>
        public Region? Domain => _domain;

        /// <summary>Domain 의 바운딩 박스 (Domain 이 없으면 전체).</summary>
        public RectI DomainBounds => _domain?.BoundingBox ?? Bounds;

        /// <summary>파일명, 카메라 정보 등 부가 정보.</summary>
        public Dictionary<string, object> Metadata { get; } = new Dictionary<string, object>(StringComparer.Ordinal);

        /// <summary>픽셀 저장소 (Host/Device 동기화 포함). 여러 Image 가 공유할 수 있다.</summary>
        public HostDeviceStorage Storage
        {
            get
            {
                ThrowIfDisposed();
                return _storage ?? throw VisionException.Disposed(TypeName);
            }
        }

        public bool IsGray => Channels == 1;
        public bool IsColor => Channels == 3;

        /// <summary>새 이미지를 할당한다. 내용은 초기화되지 않는다.</summary>
        public Image(int width, int height, PixelType pixelType, int channels = 1)
        {
            if (width <= 0) throw VisionException.InvalidArgument(nameof(width), "must be > 0");
            if (height <= 0) throw VisionException.InvalidArgument(nameof(height), "must be > 0");
            if (channels <= 0) throw VisionException.InvalidArgument(nameof(channels), "must be > 0");

            Width = width;
            Height = height;
            Channels = channels;
            PixelType = pixelType;
            BytesPerPixel = pixelType.BytesPerElement() * channels;
            Stride = AlignUp((long)width * BytesPerPixel, RowAlignment);

            _storage = new HostDeviceStorage(Stride * height);
        }

        /// <summary>저장소를 공유하는 뷰 생성 (ReduceDomain 용).</summary>
        private Image(Image source, Region? domain)
        {
            Width = source.Width;
            Height = source.Height;
            Channels = source.Channels;
            PixelType = source.PixelType;
            BytesPerPixel = source.BytesPerPixel;
            Stride = source.Stride;

            _storage = source.Storage;
            _storage.AddRef();
            _domain = Retain(domain);

            foreach (var kv in source.Metadata)
                Metadata[kv.Key] = kv.Value;
        }

        /// <summary>새 이미지 생성. <paramref name="clear"/> 가 true 면 0 으로 채운다.</summary>
        public static Image Create(int width, int height, PixelType pixelType, int channels = 1, bool clear = false)
        {
            var img = new Image(width, height, pixelType, channels);
            if (clear)
                img.Clear();
            return img;
        }

        private static long AlignUp(long value, int alignment)
            => (value + alignment - 1) / alignment * alignment;

        /// <summary>
        /// 외부 메모리(카메라 버퍼, MFC 비트맵 등)를 복사해 새 이미지를 만든다.
        /// <paramref name="stride"/> 는 원본의 행 간격(바이트). 0 이면 촘촘히 채워진 것으로 본다.
        /// </summary>
        public static Image FromBuffer(IntPtr data, int width, int height, long stride, PixelType pixelType, int channels = 1)
        {
            if (data == IntPtr.Zero)
                throw VisionException.NullArgument(nameof(data));

            var img = new Image(width, height, pixelType, channels);
            long rowBytes = (long)width * img.BytesPerPixel;
            if (stride <= 0) stride = rowBytes;
            if (stride < rowBytes)
                throw VisionException.InvalidArgument(nameof(stride), $"must be >= {rowBytes}");

            byte* src = (byte*)data;
            byte* dst = (byte*)img.GetHostPointer(forWrite: true);
            for (int y = 0; y < height; y++)
                Buffer.MemoryCopy(src + stride * y, dst + img.Stride * y, img.Stride, rowBytes);
            return img;
        }

        /// <summary>픽셀을 외부 메모리로 복사한다 (검사 프로그램으로 결과 반환). <paramref name="dstStride"/> 0 이면 촘촘히.</summary>
        public void CopyTo(IntPtr destination, long dstStride = 0)
        {
            ThrowIfDisposed();
            if (destination == IntPtr.Zero)
                throw VisionException.NullArgument(nameof(destination));

            long rowBytes = (long)Width * BytesPerPixel;
            if (dstStride <= 0) dstStride = rowBytes;
            if (dstStride < rowBytes)
                throw VisionException.InvalidArgument(nameof(dstStride), $"must be >= {rowBytes}");

            byte* src = (byte*)GetHostPointer(forWrite: false);
            byte* dst = (byte*)destination;
            for (int y = 0; y < Height; y++)
                Buffer.MemoryCopy(src + Stride * y, dst + dstStride * y, rowBytes, rowBytes);
        }

        // ------------------------------------------------------------------
        // Domain
        // ------------------------------------------------------------------

        /// <summary>
        /// 픽셀을 복사하지 않고 Domain 만 줄인 새 Image 를 돌려준다 (저장소 공유).
        /// 반환 객체는 호출자가 소유한다.
        /// </summary>
        public Image ReduceDomain(Region region)
        {
            ThrowIfDisposed();
            if (region == null)
                throw VisionException.NullArgument(nameof(region));

            // 이미지 밖으로 나간 영역은 잘라낸다.
            using (var clipped = region.Clip(Bounds))
            {
                return new Image(this, clipped);
            }
        }

        /// <summary>Domain 을 전체로 되돌린 뷰.</summary>
        public Image FullDomain()
        {
            ThrowIfDisposed();
            return new Image(this, null);
        }

        // ------------------------------------------------------------------
        // Host / Device 접근
        // ------------------------------------------------------------------

        /// <summary>Host 메모리 시작 주소. 쓰기 접근이면 Device 사본이 무효화된다.</summary>
        public IntPtr GetHostPointer(bool forWrite = false)
            => Storage.AcquireHost(forWrite);

        /// <summary>Device 메모리 시작 주소. 필요 시 자동 업로드.</summary>
        public IntPtr GetDevicePointer(IDeviceAllocator allocator, bool forWrite = false)
            => Storage.AcquireDevice(allocator, forWrite);

        /// <summary>행 시작 주소 (Host).</summary>
        public IntPtr GetRowPointer(int y, bool forWrite = false)
        {
            CheckRow(y);
            return new IntPtr(GetHostPointer(forWrite).ToInt64() + Stride * y);
        }

        /// <summary>
        /// 행을 원소 타입 Span 으로 노출한다 (쓰기 가능, 길이 = Width * Channels).
        /// T 의 크기는 PixelType 의 원소 크기와 같아야 한다.
        /// </summary>
        public Span<T> GetRow<T>(int y) where T : unmanaged
        {
            CheckElementType<T>();
            return new Span<T>((void*)GetRowPointer(y, forWrite: true), Width * Channels);
        }

        /// <summary>행을 읽기 전용 Span 으로 노출한다.</summary>
        public ReadOnlySpan<T> GetRowReadOnly<T>(int y) where T : unmanaged
        {
            CheckElementType<T>();
            return new ReadOnlySpan<T>((void*)GetRowPointer(y, forWrite: false), Width * Channels);
        }

        /// <summary>픽셀 값을 double 로 읽는다 (타입 무관). 성능이 중요한 곳에서는 GetRow 를 사용할 것.</summary>
        public double GetPixel(int x, int y, int channel = 0)
        {
            CheckPixel(x, y, channel);
            byte* p = (byte*)GetRowPointer(y, forWrite: false) + (long)x * BytesPerPixel;
            switch (PixelType)
            {
                case PixelType.UInt8: return ((byte*)p)[channel];
                case PixelType.Int16: return ((short*)p)[channel];
                case PixelType.UInt16: return ((ushort*)p)[channel];
                case PixelType.Int32: return ((int*)p)[channel];
                case PixelType.Float32: return ((float*)p)[channel];
                case PixelType.Float64: return ((double*)p)[channel];
                default: throw new VisionException(ErrorCode.UnsupportedFormat, $"Unknown pixel type {PixelType}");
            }
        }

        /// <summary>픽셀 값을 기록한다 (정수형은 포화 변환).</summary>
        public void SetPixel(int x, int y, double value, int channel = 0)
        {
            CheckPixel(x, y, channel);
            byte* p = (byte*)GetRowPointer(y, forWrite: true) + (long)x * BytesPerPixel;
            switch (PixelType)
            {
                case PixelType.UInt8: ((byte*)p)[channel] = (byte)Clamp(value, byte.MinValue, byte.MaxValue); break;
                case PixelType.Int16: ((short*)p)[channel] = (short)Clamp(value, short.MinValue, short.MaxValue); break;
                case PixelType.UInt16: ((ushort*)p)[channel] = (ushort)Clamp(value, ushort.MinValue, ushort.MaxValue); break;
                case PixelType.Int32: ((int*)p)[channel] = (int)Clamp(value, int.MinValue, int.MaxValue); break;
                case PixelType.Float32: ((float*)p)[channel] = (float)value; break;
                case PixelType.Float64: ((double*)p)[channel] = value; break;
                default: throw new VisionException(ErrorCode.UnsupportedFormat, $"Unknown pixel type {PixelType}");
            }
        }

        private static double Clamp(double v, double min, double max)
            => v < min ? min : (v > max ? max : Math.Round(v));

        /// <summary>모든 픽셀을 0 으로 채운다.</summary>
        public void Clear()
        {
            GetHostPointer(forWrite: true);
            Storage.HostBuffer.Clear();
        }

        /// <summary>픽셀을 깊은 복사한 새 이미지. Domain 도 함께 유지된다.</summary>
        public Image Clone()
        {
            ThrowIfDisposed();
            var dst = new Image(Width, Height, PixelType, Channels);
            byte* src = (byte*)GetHostPointer(forWrite: false);
            byte* dstPtr = (byte*)dst.GetHostPointer(forWrite: true);
            Buffer.MemoryCopy(src, dstPtr, dst.SizeInBytes, SizeInBytes);

            dst._domain = Retain(_domain);
            foreach (var kv in Metadata)
                dst.Metadata[kv.Key] = kv.Value;
            return dst;
        }

        /// <summary>같은 크기/타입/채널인지.</summary>
        public bool HasSameFormat(Image other)
            => other != null && Width == other.Width && Height == other.Height
               && Channels == other.Channels && PixelType == other.PixelType;

        // ------------------------------------------------------------------
        // 검증
        // ------------------------------------------------------------------

        private void CheckRow(int y)
        {
            if ((uint)y >= (uint)Height)
                throw VisionException.OutOfRange("y", y, 0, Height - 1);
        }

        private void CheckPixel(int x, int y, int channel)
        {
            if ((uint)x >= (uint)Width)
                throw VisionException.OutOfRange("x", x, 0, Width - 1);
            CheckRow(y);
            if ((uint)channel >= (uint)Channels)
                throw VisionException.OutOfRange("channel", channel, 0, Channels - 1);
        }

        private void CheckElementType<T>() where T : unmanaged
        {
            if (sizeof(T) != PixelType.BytesPerElement())
                throw new VisionException(ErrorCode.TypeMismatch,
                    $"Element type {typeof(T).Name} ({sizeof(T)} bytes) does not match pixel type {PixelType} ({PixelType.BytesPerElement()} bytes).");
        }

        protected override void ReleaseResources(bool disposing)
        {
            // 파이널라이저 경로에서는 다른 관리 객체를 건드리지 않는다.
            // (저장소는 자체 파이널라이저로 메모리를 회수한다)
            if (!disposing)
                return;

            ReleaseAndNull(ref _domain);
            ReleaseAndNull(ref _storage);
        }

        public override string ToString()
            => $"Image {Width}x{Height} {PixelType.ShortName()}" + (Channels > 1 ? $"x{Channels}" : "")
               + (_domain != null ? " (reduced domain)" : "");
    }
}
