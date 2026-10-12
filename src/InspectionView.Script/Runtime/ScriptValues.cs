using System;
using System.Collections.Generic;
using System.Globalization;
using InspectionView.Core.Memory;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;
using InspectionView.Script.Ast;

namespace InspectionView.Script.Runtime
{
    /// <summary>
    /// 스크립트 값 규칙. 값은 다음 중 하나이다:
    /// <list type="bullet">
    /// <item><see cref="TupleValue"/> (숫자/문자열/불리언은 항상 튜플)</item>
    /// <item><see cref="IconicObject"/> (Image, Region, XLD, ObjectArray)</item>
    /// <item><see cref="ResultObject"/> (출력이 여러 개인 연산자의 결과)</item>
    /// </list>
    /// 산술/비교는 원소별로 수행되며, 길이 1 튜플은 브로드캐스트된다.
    /// </summary>
    public static class ScriptValues
    {
        // ------------------------------------------------------------------
        // 참조 카운트
        // ------------------------------------------------------------------

        /// <summary>변수에 저장할 때 호출: 아이코닉 객체(및 결과 객체의 멤버)를 AddRef.</summary>
        public static void Retain(object? value)
        {
            switch (value)
            {
                case IconicObject io:
                    io.AddRef();
                    break;
                case ResultObject ro:
                    foreach (var v in ro.Values)
                        Retain(v);
                    break;
            }
        }

        /// <summary>변수에서 제거할 때 호출.</summary>
        public static void Release(object? value)
        {
            switch (value)
            {
                case IconicObject io:
                    io.Release();
                    break;
                case ResultObject ro:
                    foreach (var v in ro.Values)
                        Release(v);
                    break;
            }
        }

        // ------------------------------------------------------------------
        // 리터럴 ↔ 문자열 (Job 파라미터 저장)
        // ------------------------------------------------------------------

        /// <summary>스크립트 리터럴 문자열("[1, 2.5, 'a']", "'x'", "3")을 Tuple 로 평가한다. 변수/호출은 허용하지 않는다.</summary>
        public static TupleValue ParseLiteral(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return TupleValue.Empty;

            Expression expr;
            try
            {
                expr = Parsing.Parser.ParseExpressionText(text);
            }
            catch (Parsing.ParseException ex)
            {
                throw new ScriptRuntimeException(ex.Line, $"Invalid literal '{text}': {ex.Message}");
            }
            return ToTuple(EvaluateLiteral(expr), 0);
        }

        private static object EvaluateLiteral(Expression e)
        {
            switch (e)
            {
                case LiteralExpression lit:
                    return new TupleValue(lit.Value);
                case TupleExpression t:
                {
                    var items = new List<object>();
                    foreach (var item in t.Items)
                        foreach (var o in ToTuple(EvaluateLiteral(item), e.Line))
                            items.Add(o);
                    return new TupleValue(items.ToArray());
                }
                case UnaryExpression u:
                    return Unary(u.Operator, EvaluateLiteral(u.Operand), e.Line);
                case BinaryExpression b:
                    return Binary(b.Operator, EvaluateLiteral(b.Left), EvaluateLiteral(b.Right), e.Line);
                default:
                    throw new ScriptRuntimeException(e.Line, "Only literal values are allowed here (no variables or calls)");
            }
        }

        /// <summary>Tuple 을 다시 파싱 가능한 리터럴 문자열로.</summary>
        public static string ToLiteral(TupleValue value) => value.ToString();

        // ------------------------------------------------------------------
        // 변환
        // ------------------------------------------------------------------

        public static TupleValue ToTuple(object? value, int line)
        {
            switch (value)
            {
                case TupleValue t: return t;
                case null: throw new ScriptRuntimeException(line, "Value is undefined");
                case long _:
                case int _:
                case double _:
                case float _:
                case string _:
                case bool _:
                    return new TupleValue(value);
                case IconicObject io:
                    throw new ScriptRuntimeException(line, $"Expected a tuple but got {io.TypeName}");
                case ResultObject ro:
                    throw new ScriptRuntimeException(line, $"Expected a tuple but got the result of {ro.OperatorName}; use a member like Res.Name");
                default:
                    throw new ScriptRuntimeException(line, $"Expected a tuple but got {value.GetType().Name}");
            }
        }

        public static bool IsTruthy(object? value, int line)
        {
            var t = ToTuple(value, line);
            if (!t.IsScalar)
                throw new ScriptRuntimeException(line, $"Condition must be a single value but got a tuple of length {t.Length}");
            var v = t[0];
            switch (v)
            {
                case bool b: return b;
                case long l: return l != 0;
                case double d: return d != 0;
                case string s: return s.Length != 0;
                default: return false;
            }
        }

        public static string TypeName(object? value)
        {
            switch (value)
            {
                case null: return "undefined";
                case TupleValue t:
                    if (t.IsEmpty) return "Tuple (empty)";
                    if (t.IsScalar) return t[0] is string ? "string" : t[0] is bool ? "bool" : t[0] is long ? "integer" : "real";
                    return $"Tuple[{t.Length}]";
                case IconicObject io: return io.TypeName;
                case ResultObject ro: return $"Result of {ro.OperatorName}";
                default: return value.GetType().Name;
            }
        }

        /// <summary>변수창 표시용 짧은 문자열.</summary>
        public static string Format(object? value)
        {
            switch (value)
            {
                case null: return "<undefined>";
                case TupleValue t: return t.ToString();
                case IconicObject io: return io.ToString() ?? io.TypeName;
                case ResultObject ro: return ro.ToString();
                default: return value.ToString() ?? "";
            }
        }

        // ------------------------------------------------------------------
        // 연산
        // ------------------------------------------------------------------

        public static object Unary(UnaryOperator op, object? operand, int line)
        {
            var t = ToTuple(operand, line);
            var items = new object[t.Length];
            for (int i = 0; i < t.Length; i++)
            {
                var v = t[i];
                switch (op)
                {
                    case UnaryOperator.Negate:
                        if (v is long l) items[i] = -l;
                        else if (v is double d) items[i] = -d;
                        else throw new ScriptRuntimeException(line, $"Cannot negate '{v}'");
                        break;
                    case UnaryOperator.Not:
                        items[i] = !IsTruthy(new TupleValue(v), line);
                        break;
                }
            }
            return new TupleValue(items);
        }

        public static object Binary(BinaryOperator op, object? left, object? right, int line)
        {
            // 아이코닉 객체 비교는 참조 동일성
            if (left is IconicObject || right is IconicObject)
            {
                if (op == BinaryOperator.Equal) return new TupleValue(ReferenceEquals(left, right));
                if (op == BinaryOperator.NotEqual) return new TupleValue(!ReferenceEquals(left, right));
                throw new ScriptRuntimeException(line, $"Operator '{Symbol(op)}' is not defined for {TypeName(left)} and {TypeName(right)}");
            }

            var a = ToTuple(left, line);
            var b = ToTuple(right, line);

            if (a.IsEmpty || b.IsEmpty)
                return TupleValue.Empty;

            int n;
            if (a.Length == b.Length) n = a.Length;
            else if (a.Length == 1) n = b.Length;
            else if (b.Length == 1) n = a.Length;
            else throw new ScriptRuntimeException(line, $"Tuple lengths do not match ({a.Length} vs {b.Length})");

            var items = new object[n];
            for (int i = 0; i < n; i++)
            {
                var x = a[a.Length == 1 ? 0 : i];
                var y = b[b.Length == 1 ? 0 : i];
                items[i] = BinaryElement(op, x, y, line);
            }
            return new TupleValue(items);
        }

        private static object BinaryElement(BinaryOperator op, object x, object y, int line)
        {
            // 논리
            if (op == BinaryOperator.And) return IsTruthy(new TupleValue(x), line) && IsTruthy(new TupleValue(y), line);
            if (op == BinaryOperator.Or) return IsTruthy(new TupleValue(x), line) || IsTruthy(new TupleValue(y), line);

            // 문자열
            if (x is string || y is string)
            {
                string sx = Str(x), sy = Str(y);
                switch (op)
                {
                    case BinaryOperator.Add: return sx + sy;
                    case BinaryOperator.Equal: return string.Equals(sx, sy, StringComparison.Ordinal);
                    case BinaryOperator.NotEqual: return !string.Equals(sx, sy, StringComparison.Ordinal);
                    case BinaryOperator.Less: return string.CompareOrdinal(sx, sy) < 0;
                    case BinaryOperator.LessEqual: return string.CompareOrdinal(sx, sy) <= 0;
                    case BinaryOperator.Greater: return string.CompareOrdinal(sx, sy) > 0;
                    case BinaryOperator.GreaterEqual: return string.CompareOrdinal(sx, sy) >= 0;
                    default:
                        throw new ScriptRuntimeException(line, $"Operator '{Symbol(op)}' is not defined for strings");
                }
            }

            // 불리언은 정수로
            if (x is bool bx) x = bx ? 1L : 0L;
            if (y is bool by) y = by ? 1L : 0L;

            if (x is long lx && y is long ly)
            {
                switch (op)
                {
                    case BinaryOperator.Add: return unchecked(lx + ly);
                    case BinaryOperator.Subtract: return unchecked(lx - ly);
                    case BinaryOperator.Multiply: return unchecked(lx * ly);
                    case BinaryOperator.Divide:
                        if (ly == 0) throw new ScriptRuntimeException(line, "Division by zero");
                        return (double)lx / ly;
                    case BinaryOperator.Modulo:
                        if (ly == 0) throw new ScriptRuntimeException(line, "Division by zero");
                        return lx % ly;
                    case BinaryOperator.Equal: return lx == ly;
                    case BinaryOperator.NotEqual: return lx != ly;
                    case BinaryOperator.Less: return lx < ly;
                    case BinaryOperator.LessEqual: return lx <= ly;
                    case BinaryOperator.Greater: return lx > ly;
                    case BinaryOperator.GreaterEqual: return lx >= ly;
                }
            }

            double dx = ToDouble(x, line), dy = ToDouble(y, line);
            switch (op)
            {
                case BinaryOperator.Add: return dx + dy;
                case BinaryOperator.Subtract: return dx - dy;
                case BinaryOperator.Multiply: return dx * dy;
                case BinaryOperator.Divide:
                    if (dy == 0) throw new ScriptRuntimeException(line, "Division by zero");
                    return dx / dy;
                case BinaryOperator.Modulo:
                    if (dy == 0) throw new ScriptRuntimeException(line, "Division by zero");
                    return dx % dy;
                case BinaryOperator.Equal: return dx == dy;
                case BinaryOperator.NotEqual: return dx != dy;
                case BinaryOperator.Less: return dx < dy;
                case BinaryOperator.LessEqual: return dx <= dy;
                case BinaryOperator.Greater: return dx > dy;
                case BinaryOperator.GreaterEqual: return dx >= dy;
                default:
                    throw new ScriptRuntimeException(line, $"Unknown operator {op}");
            }
        }

        private static double ToDouble(object v, int line)
        {
            switch (v)
            {
                case long l: return l;
                case double d: return d;
                case bool b: return b ? 1 : 0;
                default: throw new ScriptRuntimeException(line, $"'{v}' is not a number");
            }
        }

        public static string Str(object v)
        {
            switch (v)
            {
                case string s: return s;
                case double d: return d.ToString("R", CultureInfo.InvariantCulture);
                case long l: return l.ToString(CultureInfo.InvariantCulture);
                case bool b: return b ? "true" : "false";
                default: return v?.ToString() ?? "";
            }
        }

        public static string Symbol(BinaryOperator op)
        {
            switch (op)
            {
                case BinaryOperator.Add: return "+";
                case BinaryOperator.Subtract: return "-";
                case BinaryOperator.Multiply: return "*";
                case BinaryOperator.Divide: return "/";
                case BinaryOperator.Modulo: return "%";
                case BinaryOperator.Equal: return "==";
                case BinaryOperator.NotEqual: return "!=";
                case BinaryOperator.Less: return "<";
                case BinaryOperator.LessEqual: return "<=";
                case BinaryOperator.Greater: return ">";
                case BinaryOperator.GreaterEqual: return ">=";
                case BinaryOperator.And: return "and";
                case BinaryOperator.Or: return "or";
                default: return op.ToString();
            }
        }

        // ------------------------------------------------------------------
        // 인덱스 / 멤버
        // ------------------------------------------------------------------

        public static object Index(object? target, object? index, int line)
        {
            var idx = ToTuple(index, line);

            switch (target)
            {
                case TupleValue t:
                {
                    if (idx.IsScalar)
                    {
                        int i = CheckIndex(idx.L(0), t.Length, line);
                        return new TupleValue(t[i]);
                    }
                    var items = new object[idx.Length];
                    for (int k = 0; k < idx.Length; k++)
                        items[k] = t[CheckIndex(idx.L(k), t.Length, line)];
                    return new TupleValue(items);
                }

                case IObjectArray arr:
                {
                    if (idx.IsScalar)
                        return arr.ItemAt(CheckIndex(idx.L(0), arr.Count, line));
                    var indices = new List<int>(idx.Length);
                    for (int k = 0; k < idx.Length; k++)
                        indices.Add(CheckIndex(idx.L(k), arr.Count, line));
                    return SelectIndices(arr, indices);
                }

                case null:
                    throw new ScriptRuntimeException(line, "Cannot index an undefined value");
                default:
                    throw new ScriptRuntimeException(line, $"{TypeName(target)} cannot be indexed");
            }
        }

        private static int CheckIndex(long i, int count, int line)
        {
            if (i < 0 || i >= count)
                throw new ScriptRuntimeException(line, $"Index {i} is out of range [0, {count - 1}]");
            return (int)i;
        }

        private static object SelectIndices(IObjectArray arr, List<int> indices)
        {
            switch (arr)
            {
                case ObjectArray<Region> r: return r.SelectIndices(indices);
                case ObjectArray<Image> im: return im.SelectIndices(indices);
                case ObjectArray<XldContour> x: return x.SelectIndices(indices);
                default:
                    throw new InvalidOperationException($"Unsupported object array {arr.GetType().Name}");
            }
        }

        public static object Member(object? target, string member, int line)
        {
            switch (target)
            {
                case ResultObject ro:
                    if (ro.TryGetValue(member, out var v))
                        return v ?? TupleValue.Empty;
                    throw new ScriptRuntimeException(line, $"Result of {ro.OperatorName} has no member '{member}' (available: {string.Join(", ", ro.Keys)})");

                case Image img:
                    switch (member)
                    {
                        case "Width": return new TupleValue((long)img.Width);
                        case "Height": return new TupleValue((long)img.Height);
                        case "Channels": return new TupleValue((long)img.Channels);
                        case "Type": return new TupleValue(img.PixelType.ShortName());
                    }
                    break;

                case Region reg:
                    switch (member)
                    {
                        case "Area": return new TupleValue(reg.Area);
                        case "Width": return new TupleValue((long)reg.BoundingBox.Width);
                        case "Height": return new TupleValue((long)reg.BoundingBox.Height);
                        case "Row": return new TupleValue(reg.Centroid().Y);
                        case "Column": return new TupleValue(reg.Centroid().X);
                        case "Row1": return new TupleValue((long)reg.BoundingBox.Y);
                        case "Column1": return new TupleValue((long)reg.BoundingBox.X);
                        case "Row2": return new TupleValue((long)(reg.BoundingBox.Bottom - 1));
                        case "Column2": return new TupleValue((long)(reg.BoundingBox.Right - 1));
                        case "IsEmpty": return new TupleValue(reg.IsEmpty);
                    }
                    break;

                case IObjectArray arr:
                    switch (member)
                    {
                        case "Count":
                        case "Length":
                            return new TupleValue((long)arr.Count);
                    }
                    break;

                case TupleValue t:
                    switch (member)
                    {
                        case "Length": return new TupleValue((long)t.Length);
                    }
                    break;

                case null:
                    throw new ScriptRuntimeException(line, $"Cannot read member '{member}' of an undefined value");
            }

            throw new ScriptRuntimeException(line, $"{TypeName(target)} has no member '{member}'");
        }
    }
}
