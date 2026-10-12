using System;
using System.Collections.Generic;
using System.Linq;

namespace InspectionView.Script.Runtime
{
    /// <summary>
    /// 스크립트 변수 저장소. 아이코닉 값은 저장 시 AddRef, 교체/삭제 시 Release 한다.
    /// 변수창은 <see cref="Changed"/> 이벤트로 갱신한다 (인터프리터 스레드에서 발생).
    /// </summary>
    public sealed class VariableTable : IDisposable
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, object?> _values = new Dictionary<string, object?>(StringComparer.Ordinal);
        private readonly List<string> _order = new List<string>();

        /// <summary>변수가 설정/삭제될 때 (이름, 새 값; 삭제 시 null).</summary>
        public event Action<string, object?>? Changed;

        /// <summary>false 이면 Changed 이벤트를 발생시키지 않는다 (고속 모드).</summary>
        public bool NotifyChanges { get; set; } = true;

        public int Count
        {
            get { lock (_lock) return _values.Count; }
        }

        /// <summary>정의된 순서대로의 변수 이름.</summary>
        public IReadOnlyList<string> Names
        {
            get { lock (_lock) return _order.ToArray(); }
        }

        public bool TryGet(string name, out object? value)
        {
            lock (_lock)
                return _values.TryGetValue(name, out value);
        }

        public object? Get(string name)
        {
            lock (_lock)
                return _values.TryGetValue(name, out var v) ? v : null;
        }

        public bool Contains(string name)
        {
            lock (_lock) return _values.ContainsKey(name);
        }

        /// <summary>값을 저장한다 (새 값 AddRef, 이전 값 Release).</summary>
        public void Set(string name, object? value)
        {
            object? old;
            lock (_lock)
            {
                ScriptValues.Retain(value);
                if (_values.TryGetValue(name, out old))
                {
                    if (ReferenceEquals(old, value))
                    {
                        ScriptValues.Release(value);
                        old = null;
                    }
                }
                else
                {
                    _order.Add(name);
                }
                _values[name] = value;
            }
            ScriptValues.Release(old);

            if (NotifyChanges)
                Changed?.Invoke(name, value);
        }

        public bool Remove(string name)
        {
            object? old;
            lock (_lock)
            {
                if (!_values.TryGetValue(name, out old))
                    return false;
                _values.Remove(name);
                _order.Remove(name);
            }
            ScriptValues.Release(old);
            if (NotifyChanges)
                Changed?.Invoke(name, null);
            return true;
        }

        /// <summary>모든 변수를 해제한다.</summary>
        public void Clear()
        {
            KeyValuePair<string, object?>[] all;
            lock (_lock)
            {
                all = _values.ToArray();
                _values.Clear();
                _order.Clear();
            }
            foreach (var kv in all)
            {
                ScriptValues.Release(kv.Value);
                if (NotifyChanges)
                    Changed?.Invoke(kv.Key, null);
            }
        }

        /// <summary>현재 값 스냅샷 (변수창 초기화용).</summary>
        public IReadOnlyList<KeyValuePair<string, object?>> Snapshot()
        {
            lock (_lock)
                return _order.Select(n => new KeyValuePair<string, object?>(n, _values[n])).ToArray();
        }

        public void Dispose() => Clear();
    }
}
