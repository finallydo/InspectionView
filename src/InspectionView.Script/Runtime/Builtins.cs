using System;
using System.Collections.Generic;
using System.Linq;
using InspectionView.Core.Types;

namespace InspectionView.Script.Runtime
{
    /// <summary>
    /// 레지스트리 연산자가 아닌 내장 함수 (튜플 연산). 이름은 PascalCase.
    /// 연산자 이름과 겹치면 내장 함수가 우선한다.
    /// </summary>
    public sealed class Builtins
    {
        public delegate object? BuiltinFunction(object?[] args, int line, IScriptHost host);

        private readonly Dictionary<string, BuiltinFunction> _functions = new Dictionary<string, BuiltinFunction>(StringComparer.Ordinal);
        private readonly Dictionary<string, string[]> _parameterNames = new Dictionary<string, string[]>(StringComparer.Ordinal);

        public Builtins()
        {
            Register("Len", (a, l, h) => Len(Arg(a, 0, l), l));
            Register("Abs", (a, l, h) => Map(a, l, 1, v => v is long x ? (object)Math.Abs(x) : Math.Abs(ToD(v, l))));
            Register("Sqrt", (a, l, h) => Map(a, l, 1, v => Math.Sqrt(ToD(v, l))));
            Register("Round", (a, l, h) => Map(a, l, 1, v => (long)Math.Round(ToD(v, l), MidpointRounding.AwayFromZero)));
            Register("Floor", (a, l, h) => Map(a, l, 1, v => (long)Math.Floor(ToD(v, l))));
            Register("Ceil", (a, l, h) => Map(a, l, 1, v => (long)Math.Ceiling(ToD(v, l))));
            Register("Int", (a, l, h) => Map(a, l, 1, v => v is string s ? long.Parse(s, System.Globalization.CultureInfo.InvariantCulture) : (long)ToD(v, l)));
            Register("Real", (a, l, h) => Map(a, l, 1, v => v is string s ? double.Parse(s, System.Globalization.CultureInfo.InvariantCulture) : ToD(v, l)));
            Register("Str", (a, l, h) => Map(a, l, 1, v => ScriptValues.Str(v)));
            Register("Min", (a, l, h) => MinMax(a, l, true));
            Register("Max", (a, l, h) => MinMax(a, l, false));
            Register("Sum", (a, l, h) => Aggregate(a, l, (x, y) => x + y, 0));
            Register("Mean", (a, l, h) =>
            {
                var t = ScriptValues.ToTuple(Arg(a, 0, l), l);
                if (t.IsEmpty) throw new ScriptRuntimeException(l, "Mean of an empty tuple");
                return new TupleValue(t.ToDoubleArray().Average());
            });
            Register("Print", (a, l, h) =>
            {
                h.Print(string.Join(" ", a.Select(v => v is TupleValue t && t.IsScalar ? t.S(0) : ScriptValues.Format(v))));
                return null;
            });
            Register("TypeOf", (a, l, h) => new TupleValue(ScriptValues.TypeName(Arg(a, 0, l))));
            Register("Display", (a, l, h) =>
            {
                var value = Arg(a, 0, l);
                int window = a.Length > 1 && a[1] != null ? (int)ScriptValues.ToTuple(a[1], l).L() : 0;
                if (window < 0) throw new ScriptRuntimeException(l, "Window index must be >= 0");
                h.Display(value, window);
                return null;
            }, "Value", "Window");
            Register("ClearWindow", (a, l, h) =>
            {
                int window = a.Length > 0 && a[0] != null ? (int)ScriptValues.ToTuple(a[0], l).L() : 0;
                if (window < 0) throw new ScriptRuntimeException(l, "Window index must be >= 0");
                h.ClearWindow(window);
                return null;
            }, "Window");
        }

        /// <param name="parameterNames">이름 인자(<c>Display(X, Window = 1)</c>)를 받으려면 위치 순서대로의 파라미터 이름.</param>
        public void Register(string name, BuiltinFunction fn, params string[] parameterNames)
        {
            _functions[name] = fn;
            if (parameterNames.Length > 0)
                _parameterNames[name] = parameterNames;
            else
                _parameterNames.Remove(name);
        }

        /// <summary>
        /// 이름 인자를 위치에 넣은 인자 배열. 건너뛴 위치는 null (기본값). 이름 인자를 받지 않는 함수거나 이름이 틀리면 예외.
        /// </summary>
        public object?[] BindNamed(string name, object?[] positional, IReadOnlyList<KeyValuePair<string, object?>> named, int line)
        {
            if (named.Count == 0)
                return positional;
            if (!_parameterNames.TryGetValue(name, out var names))
                throw new ScriptRuntimeException(line, $"Built-in function '{name}' does not take named arguments");
            var args = new List<object?>(positional);
            foreach (var kv in named)
            {
                int index = Array.IndexOf(names, kv.Key);
                if (index < 0)
                    throw new ScriptRuntimeException(line, $"'{name}' has no parameter '{kv.Key}' (parameters: {string.Join(", ", names)})");
                if (index < positional.Length)
                    throw new ScriptRuntimeException(line, $"'{name}': parameter '{kv.Key}' is already given by position");
                while (args.Count <= index) args.Add(null);
                args[index] = kv.Value;
            }
            return args.ToArray();
        }

        public bool Contains(string name) => _functions.ContainsKey(name);

        public bool TryGet(string name, out BuiltinFunction fn) => _functions.TryGetValue(name, out fn!);

        public IEnumerable<string> Names => _functions.Keys.OrderBy(n => n, StringComparer.Ordinal);

        // ------------------------------------------------------------------

        private static object? Arg(object?[] args, int i, int line)
        {
            if (i >= args.Length)
                throw new ScriptRuntimeException(line, $"Missing argument {i + 1}");
            return args[i];
        }

        private static double ToD(object v, int line)
        {
            switch (v)
            {
                case long l: return l;
                case double d: return d;
                case bool b: return b ? 1 : 0;
                default: throw new ScriptRuntimeException(line, $"'{v}' is not a number");
            }
        }

        private static object Len(object? v, int line)
        {
            switch (v)
            {
                case TupleValue t: return new TupleValue((long)t.Length);
                case IObjectArray arr: return new TupleValue((long)arr.Count);
                case null: return new TupleValue(0L);
                default: return new TupleValue(1L);
            }
        }

        private static object Map(object?[] args, int line, int expected, Func<object, object> f)
        {
            if (args.Length != expected)
                throw new ScriptRuntimeException(line, $"Expected {expected} argument(s) but got {args.Length}");
            var t = ScriptValues.ToTuple(args[0], line);
            var items = new object[t.Length];
            for (int i = 0; i < t.Length; i++)
                items[i] = f(t[i]);
            return new TupleValue(items);
        }

        private static object MinMax(object?[] args, int line, bool min)
        {
            if (args.Length == 1)
            {
                var t = ScriptValues.ToTuple(args[0], line);
                if (t.IsEmpty) throw new ScriptRuntimeException(line, "Min/Max of an empty tuple");
                object best = t[0];
                for (int i = 1; i < t.Length; i++)
                {
                    double a = ToD(t[i], line), b = ToD(best, line);
                    if (min ? a < b : a > b) best = t[i];
                }
                return new TupleValue(best);
            }

            if (args.Length == 2)
            {
                var a = ScriptValues.ToTuple(args[0], line);
                var b = ScriptValues.ToTuple(args[1], line);
                int n = Math.Max(a.Length, b.Length);
                if (a.Length != b.Length && a.Length != 1 && b.Length != 1)
                    throw new ScriptRuntimeException(line, $"Tuple lengths do not match ({a.Length} vs {b.Length})");
                var items = new object[n];
                for (int i = 0; i < n; i++)
                {
                    var x = a[a.Length == 1 ? 0 : i];
                    var y = b[b.Length == 1 ? 0 : i];
                    double dx = ToD(x, line), dy = ToD(y, line);
                    items[i] = (min ? dx <= dy : dx >= dy) ? x : y;
                }
                return new TupleValue(items);
            }

            throw new ScriptRuntimeException(line, "Min/Max takes 1 or 2 arguments");
        }

        private static object Aggregate(object?[] args, int line, Func<double, double, double> f, double seed)
        {
            var t = ScriptValues.ToTuple(Arg(args, 0, line), line);
            if (t.IsInteger)
            {
                long acc = 0;
                foreach (var v in t) acc += (long)v;
                return new TupleValue(acc);
            }
            double d = seed;
            foreach (var v in t) d = f(d, ToD(v, line));
            return new TupleValue(d);
        }
    }

    /// <summary>내장 함수가 엔진에 접근하는 통로 (출력 창, 그래픽 창).</summary>
    public interface IScriptHost
    {
        void Print(string text);

        /// <summary>Display(Value, Window): 값을 그래픽 창에 표시해 달라는 요청. 실행 스레드에서 호출된다.</summary>
        void Display(object? value, int window);

        /// <summary>ClearWindow(Window).</summary>
        void ClearWindow(int window);
    }
}
