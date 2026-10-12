using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using InspectionView.Core.Errors;

namespace InspectionView.Core.Types
{
    /// <summary>
    /// 스크립트의 Tuple (Halcon HTuple 에 대응). 원소는 long / double / string / bool 중 하나이며 혼합 가능.
    /// 불변 객체이다. (System.Tuple 과의 이름 충돌을 피하기 위해 TupleValue 로 명명)
    /// </summary>
    public sealed class TupleValue : IReadOnlyList<object>, IEquatable<TupleValue>
    {
        private readonly object[] _items;

        public static TupleValue Empty { get; } = new TupleValue(Array.Empty<object>());

        public TupleValue(params object[] items)
        {
            if (items == null)
            {
                _items = Array.Empty<object>();
                return;
            }

            _items = new object[items.Length];
            for (int i = 0; i < items.Length; i++)
                _items[i] = NormalizeElement(items[i]);
        }

        public TupleValue(IEnumerable<object> items)
            : this(items?.ToArray() ?? Array.Empty<object>())
        {
        }

        private static object NormalizeElement(object? v)
        {
            switch (v)
            {
                case null: throw VisionException.InvalidArgument("item", "Tuple elements must not be null");
                case long l: return l;
                case int i: return (long)i;
                case short s: return (long)s;
                case byte b: return (long)b;
                case uint ui: return (long)ui;
                case ushort us: return (long)us;
                case sbyte sb: return (long)sb;
                case ulong ul: return (long)ul;
                case double d: return d;
                case float f: return (double)f;
                case decimal m: return (double)m;
                case string str: return str;
                case bool bo: return bo;
                case char c: return c.ToString();
                case Enum e: return e.ToString();
                default:
                    throw new VisionException(ErrorCode.TypeMismatch,
                        $"Type {v.GetType().Name} cannot be stored in a Tuple (long/double/string/bool only).");
            }
        }

        public int Length => _items.Length;
        public int Count => _items.Length;
        public object this[int index] => _items[index];

        public bool IsEmpty => _items.Length == 0;
        public bool IsScalar => _items.Length == 1;

        /// <summary>모든 원소가 숫자(long/double)인지.</summary>
        public bool IsNumeric => _items.All(o => o is long || o is double);

        /// <summary>모든 원소가 정수인지.</summary>
        public bool IsInteger => _items.All(o => o is long);

        public bool IsString => _items.All(o => o is string);

        // ---- 원소 접근 ----

        public long L(int index = 0)
        {
            var v = _items[index];
            switch (v)
            {
                case long l: return l;
                case double d: return (long)Math.Round(d);
                case bool b: return b ? 1 : 0;
                case string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p): return p;
                default:
                    throw new VisionException(ErrorCode.TypeMismatch, $"Tuple[{index}] = '{v}' is not an integer.");
            }
        }

        public double D(int index = 0)
        {
            var v = _items[index];
            switch (v)
            {
                case double d: return d;
                case long l: return l;
                case bool b: return b ? 1 : 0;
                case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p): return p;
                default:
                    throw new VisionException(ErrorCode.TypeMismatch, $"Tuple[{index}] = '{v}' is not a number.");
            }
        }

        public string S(int index = 0) => FormatElement(_items[index]);

        public bool B(int index = 0)
        {
            var v = _items[index];
            switch (v)
            {
                case bool b: return b;
                case long l: return l != 0;
                case double d: return d != 0;
                case string s: return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
                default: return false;
            }
        }

        public double[] ToDoubleArray()
        {
            var r = new double[_items.Length];
            for (int i = 0; i < r.Length; i++) r[i] = D(i);
            return r;
        }

        public long[] ToLongArray()
        {
            var r = new long[_items.Length];
            for (int i = 0; i < r.Length; i++) r[i] = L(i);
            return r;
        }

        public string[] ToStringArray()
        {
            var r = new string[_items.Length];
            for (int i = 0; i < r.Length; i++) r[i] = S(i);
            return r;
        }

        public TupleValue Concat(TupleValue other)
        {
            if (other == null || other.IsEmpty) return this;
            if (IsEmpty) return other;
            var all = new object[_items.Length + other._items.Length];
            Array.Copy(_items, 0, all, 0, _items.Length);
            Array.Copy(other._items, 0, all, _items.Length, other._items.Length);
            return new TupleValue(all);
        }

        public TupleValue Slice(int start, int count)
        {
            if (start < 0 || count < 0 || start + count > _items.Length)
                throw VisionException.OutOfRange("start/count", $"{start}+{count}", 0, _items.Length);
            var r = new object[count];
            Array.Copy(_items, start, r, 0, count);
            return new TupleValue(r);
        }

        // ---- 변환 ----

        public static implicit operator TupleValue(long v) => new TupleValue(v);
        public static implicit operator TupleValue(int v) => new TupleValue((long)v);
        public static implicit operator TupleValue(double v) => new TupleValue(v);
        public static implicit operator TupleValue(string v) => new TupleValue(v);
        public static implicit operator TupleValue(bool v) => new TupleValue(v);
        public static implicit operator TupleValue(long[] v) => new TupleValue(v.Cast<object>().ToArray());
        public static implicit operator TupleValue(int[] v) => new TupleValue(v.Select(i => (object)(long)i).ToArray());
        public static implicit operator TupleValue(double[] v) => new TupleValue(v.Cast<object>().ToArray());
        public static implicit operator TupleValue(string[] v) => new TupleValue(v.Cast<object>().ToArray());

        /// <summary>임의 CLR 값을 Tuple 로 변환한다 (배열/리스트/스칼라 모두 허용).</summary>
        public static TupleValue From(object? value)
        {
            switch (value)
            {
                case null: return Empty;
                case TupleValue t: return t;
                case string s: return new TupleValue(s);
                case IEnumerable e: return new TupleValue(e.Cast<object>().ToArray());
                default: return new TupleValue(value);
            }
        }

        private static string FormatElement(object v)
        {
            switch (v)
            {
                case double d: return d.ToString("R", CultureInfo.InvariantCulture);
                case long l: return l.ToString(CultureInfo.InvariantCulture);
                case bool b: return b ? "true" : "false";
                default: return v.ToString() ?? "";
            }
        }

        public override string ToString()
        {
            if (_items.Length == 1)
                return _items[0] is string s ? $"'{s}'" : FormatElement(_items[0]);

            var sb = new StringBuilder("[");
            for (int i = 0; i < _items.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(_items[i] is string s ? $"'{s}'" : FormatElement(_items[i]));
            }
            return sb.Append(']').ToString();
        }

        public bool Equals(TupleValue? other)
        {
            if (other is null || other._items.Length != _items.Length)
                return false;
            for (int i = 0; i < _items.Length; i++)
            {
                if (!Equals(_items[i], other._items[i]))
                    return false;
            }
            return true;
        }

        public override bool Equals(object? obj) => obj is TupleValue t && Equals(t);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                foreach (var it in _items) h = h * 31 + it.GetHashCode();
                return h;
            }
        }

        public IEnumerator<object> GetEnumerator() => ((IEnumerable<object>)_items).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
    }
}
