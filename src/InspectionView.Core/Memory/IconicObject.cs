using System;
using System.Threading;
using InspectionView.Core.Errors;

namespace InspectionView.Core.Memory
{
    /// <summary>
    /// 참조 카운트로 수명을 관리하는 모든 아이코닉 객체(Image, Region, XLD, ObjectArray)의 기반.
    /// <para>
    /// 규칙:
    /// <list type="bullet">
    /// <item>생성 직후 참조 카운트는 1 이며 생성자가 소유한다.</item>
    /// <item>다른 소유자가 생기면 <see cref="AddRef"/>, 소유를 끝내면 <see cref="Release"/> (= <see cref="Dispose"/>).</item>
    /// <item>카운트가 0 이 되는 순간 Host/Device 메모리를 즉시 해제한다. 파이널라이저는 안전망일 뿐이다.</item>
    /// </list>
    /// </para>
    /// </summary>
    public abstract class IconicObject : IDisposable
    {
        private int _refCount = 1;
        private int _disposed;

        /// <summary>현재 참조 카운트.</summary>
        public int RefCount => Volatile.Read(ref _refCount);

        /// <summary>자원이 이미 해제되었는지 여부.</summary>
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        /// <summary>스크립트/변수창에 표시되는 타입 이름 (예: "Image", "Region").</summary>
        public abstract string TypeName { get; }

        /// <summary>참조 카운트를 1 증가시키고 자기 자신을 반환한다.</summary>
        public IconicObject AddRef()
        {
            ThrowIfDisposed();
            Interlocked.Increment(ref _refCount);
            return this;
        }

        /// <summary>참조 카운트를 1 감소시키고 0 이 되면 자원을 해제한다.</summary>
        public void Release()
        {
            if (IsDisposed)
                return;

            var n = Interlocked.Decrement(ref _refCount);
            if (n <= 0)
                DisposeCore(true);
        }

        /// <summary><see cref="Release"/> 와 동일 (using 패턴 지원).</summary>
        public void Dispose() => Release();

        ~IconicObject()
        {
            DisposeCore(false);
        }

        private void DisposeCore(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try
            {
                ReleaseResources(disposing);
            }
            finally
            {
                if (disposing)
                    GC.SuppressFinalize(this);
            }
        }

        /// <summary>
        /// 실제 자원 해제. <paramref name="disposing"/> 이 false 이면 파이널라이저에서 호출된 것이므로
        /// 관리 객체를 건드리지 말고 비관리 메모리만 정리해야 한다.
        /// </summary>
        protected abstract void ReleaseResources(bool disposing);

        protected void ThrowIfDisposed()
        {
            if (IsDisposed)
                throw VisionException.Disposed(TypeName);
        }

        /// <summary>null 허용 AddRef 헬퍼.</summary>
        public static T? Retain<T>(T? obj) where T : IconicObject
        {
            obj?.AddRef();
            return obj;
        }

        /// <summary>null 허용 Release 헬퍼. 호출 후 참조를 null 로 만든다.</summary>
        public static void ReleaseAndNull<T>(ref T? obj) where T : IconicObject
        {
            obj?.Release();
            obj = null;
        }
    }
}
