using System;
using System.Collections;
using System.Collections.Generic;
using InspectionView.Core.Errors;
using InspectionView.Core.Memory;

namespace InspectionView.Core.Types
{
    /// <summary>비제네릭 접근용 인터페이스 (레지스트리/변수창에서 사용).</summary>
    public interface IObjectArray
    {
        int Count { get; }
        Type ItemType { get; }
        IconicObject ItemAt(int index);
    }

    /// <summary>
    /// 아이코닉 객체 배열 (Halcon 의 object tuple 에 대응).
    /// Connection 결과(여러 Region), 2.5D 다중 입력(여러 Image) 등에 사용한다.
    /// 항목을 추가하면 AddRef 하고, 배열이 해제될 때 각 항목을 Release 한다.
    /// </summary>
    public sealed class ObjectArray<T> : IconicObject, IObjectArray, IReadOnlyList<T> where T : IconicObject
    {
        private readonly List<T> _items = new List<T>();

        public override string TypeName => $"{ItemTypeName}[]";

        private static string ItemTypeName
        {
            get
            {
                if (typeof(T) == typeof(Image)) return "Image";
                if (typeof(T) == typeof(Region)) return "Region";
                if (typeof(T) == typeof(XldContour)) return "XLD";
                return typeof(T).Name;
            }
        }

        public ObjectArray()
        {
        }

        /// <summary>항목들로 생성. 각 항목에 AddRef 한다 (호출자의 참조는 그대로 유지됨).</summary>
        public ObjectArray(IEnumerable<T> items)
        {
            if (items == null) throw VisionException.NullArgument(nameof(items));
            foreach (var it in items)
                Add(it);
        }

        public int Count => _items.Count;
        public Type ItemType => typeof(T);

        public T this[int index]
        {
            get
            {
                ThrowIfDisposed();
                if ((uint)index >= (uint)_items.Count)
                    throw VisionException.OutOfRange(nameof(index), index, 0, _items.Count - 1);
                return _items[index];
            }
        }

        IconicObject IObjectArray.ItemAt(int index) => this[index];

        /// <summary>항목 추가 (AddRef).</summary>
        public void Add(T item)
        {
            ThrowIfDisposed();
            if (item == null) throw VisionException.NullArgument(nameof(item));
            item.AddRef();
            _items.Add(item);
        }

        /// <summary>항목을 소유권 이전으로 추가 (AddRef 하지 않음). 새로 만든 객체를 바로 넣을 때 사용.</summary>
        public void AddOwned(T item)
        {
            ThrowIfDisposed();
            if (item == null) throw VisionException.NullArgument(nameof(item));
            _items.Add(item);
        }

        /// <summary>인덱스 목록으로 부분 배열 생성 (Halcon select_obj 에 대응).</summary>
        public ObjectArray<T> SelectIndices(IEnumerable<int> indices)
        {
            ThrowIfDisposed();
            var result = new ObjectArray<T>();
            foreach (var i in indices)
                result.Add(this[i]);
            return result;
        }

        public T[] ToArray()
        {
            ThrowIfDisposed();
            return _items.ToArray();
        }

        public IEnumerator<T> GetEnumerator()
        {
            ThrowIfDisposed();
            return _items.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        protected override void ReleaseResources(bool disposing)
        {
            if (!disposing)
                return;

            foreach (var it in _items)
                it.Release();
            _items.Clear();
        }

        public override string ToString() => $"{TypeName} count={Count}";
    }
}
