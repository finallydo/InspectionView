using System;
using System.Collections.Generic;
using System.Globalization;
using InspectionView.Core.Errors;

namespace InspectionView.Cameras.GenTL.GenICam
{
    /// <summary>수식 값: IntSwissKnife 는 64bit 정수 연산, SwissKnife/Converter 는 실수 연산.</summary>
    public readonly struct Num
    {
        public readonly bool IsInt;
        public readonly long L;
        public readonly double D;

        private Num(bool isInt, long l, double d)
        {
            IsInt = isInt;
            L = l;
            D = d;
        }

        public static Num Int(long v) => new Num(true, v, v);
        public static Num Real(double v) => new Num(false, 0, v);
        public static Num Bool(bool v) => Int(v ? 1 : 0);

        public double AsDouble => IsInt ? L : D;
        /// <summary>실수는 버림(0 방향)으로 정수화한다.</summary>
        public long AsLong => IsInt ? L : (long)D;
        public bool AsBool => IsInt ? L != 0 : D != 0;

        public override string ToString() => IsInt ? L.ToString(CultureInfo.InvariantCulture) : D.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// GenICam SwissKnife 수식 (Formula / FormulaTo / FormulaFrom / Expression).
    /// 연산자 우선순위(낮은 것부터): <c>?:</c>, <c>||</c>, <c>&amp;&amp;</c>, <c>|</c>, <c>^</c>, <c>&amp;</c>, <c>= &lt;&gt;</c>,
    /// <c>&lt; &gt; &lt;= &gt;=</c>, <c>&lt;&lt; &gt;&gt;</c>, <c>+ -</c>, <c>* / %</c>, <c>**</c>, 단항 <c>- + ~ !</c>.
    /// 함수: SGN NEG ATAN COS SIN TAN ABS EXP LN LG SQRT TRUNC FLOOR CEIL ROUND ASIN ACOS, 상수 PI E.
    /// </summary>
    public sealed class Formula
    {
        private readonly Expr _root;

        public string Text { get; }

        private Formula(string text, Expr root)
        {
            Text = text;
            _root = root;
        }

        public static Formula Parse(string text)
        {
            var parser = new Parser(text ?? "");
            var root = parser.ParseAll();
            return new Formula(text ?? "", root);
        }

        /// <summary>
        /// 수식을 평가한다. <paramref name="resolve"/> 는 변수 이름 → 값 (모르는 이름이면 null).
        /// <paramref name="floatMode"/> 가 true 면 모든 값을 실수로 계산한다 (SwissKnife/Converter).
        /// </summary>
        public Num Evaluate(Func<string, Num?> resolve, bool floatMode)
            => _root.Eval(new Ctx(resolve, floatMode));

        public override string ToString() => Text;

        // ------------------------------------------------------------------ AST

        private sealed class Ctx
        {
            public readonly Func<string, Num?> Resolve;
            public readonly bool FloatMode;

            public Ctx(Func<string, Num?> resolve, bool floatMode)
            {
                Resolve = resolve;
                FloatMode = floatMode;
            }
        }

        private abstract class Expr
        {
            public abstract Num Eval(Ctx c);
        }

        private sealed class ConstExpr : Expr
        {
            private readonly Num _v;
            public ConstExpr(Num v) { _v = v; }
            public override Num Eval(Ctx c) => c.FloatMode ? Num.Real(_v.AsDouble) : _v;
        }

        private sealed class VarExpr : Expr
        {
            private readonly string _name;
            public VarExpr(string name) { _name = name; }

            public override Num Eval(Ctx c)
            {
                Num? v = c.Resolve(_name);
                if (v == null)
                {
                    // 변수가 우선, 없으면 상수
                    if (_name == "PI") v = Num.Real(Math.PI);
                    else if (_name == "E") v = Num.Real(Math.E);
                    else throw new VisionException(ErrorCode.CameraParameterInvalid, $"Unknown variable '{_name}' in formula.");
                }
                return c.FloatMode ? Num.Real(v.Value.AsDouble) : v.Value;
            }
        }

        private sealed class UnaryExpr : Expr
        {
            private readonly string _op;
            private readonly Expr _a;
            public UnaryExpr(string op, Expr a) { _op = op; _a = a; }

            public override Num Eval(Ctx c)
            {
                var a = _a.Eval(c);
                switch (_op)
                {
                    case "-": return a.IsInt ? Num.Int(unchecked(-a.L)) : Num.Real(-a.D);
                    case "+": return a;
                    case "~": return Num.Int(~a.AsLong);
                    default: return Num.Bool(!a.AsBool);   // "!"
                }
            }
        }

        private sealed class BinaryExpr : Expr
        {
            private readonly string _op;
            private readonly Expr _a, _b;
            public BinaryExpr(string op, Expr a, Expr b) { _op = op; _a = a; _b = b; }

            public override Num Eval(Ctx c)
            {
                // 논리 연산은 단락 평가
                if (_op == "&&") return Num.Bool(_a.Eval(c).AsBool && _b.Eval(c).AsBool);
                if (_op == "||") return Num.Bool(_a.Eval(c).AsBool || _b.Eval(c).AsBool);

                var a = _a.Eval(c);
                var b = _b.Eval(c);
                bool ints = a.IsInt && b.IsInt;
                switch (_op)
                {
                    case "+": return ints ? Num.Int(unchecked(a.L + b.L)) : Num.Real(a.AsDouble + b.AsDouble);
                    case "-": return ints ? Num.Int(unchecked(a.L - b.L)) : Num.Real(a.AsDouble - b.AsDouble);
                    case "*": return ints ? Num.Int(unchecked(a.L * b.L)) : Num.Real(a.AsDouble * b.AsDouble);
                    case "/":
                        if (ints)
                        {
                            if (b.L == 0) throw new VisionException(ErrorCode.CameraParameterInvalid, "Division by zero in formula.");
                            return Num.Int(a.L / b.L);
                        }
                        return Num.Real(a.AsDouble / b.AsDouble);
                    case "%":
                        if (ints)
                        {
                            if (b.L == 0) throw new VisionException(ErrorCode.CameraParameterInvalid, "Division by zero in formula.");
                            return Num.Int(a.L % b.L);
                        }
                        return Num.Real(a.AsDouble % b.AsDouble);
                    case "**":
                    {
                        double p = Math.Pow(a.AsDouble, b.AsDouble);
                        return ints ? Num.Int((long)p) : Num.Real(p);
                    }
                    case "&": return Num.Int(a.AsLong & b.AsLong);
                    case "|": return Num.Int(a.AsLong | b.AsLong);
                    case "^": return Num.Int(a.AsLong ^ b.AsLong);
                    case "<<": return Num.Int(a.AsLong << (int)b.AsLong);
                    case ">>": return Num.Int(a.AsLong >> (int)b.AsLong);
                    case "=": return Num.Bool(ints ? a.L == b.L : a.AsDouble == b.AsDouble);
                    case "<>": return Num.Bool(ints ? a.L != b.L : a.AsDouble != b.AsDouble);
                    case "<": return Num.Bool(ints ? a.L < b.L : a.AsDouble < b.AsDouble);
                    case ">": return Num.Bool(ints ? a.L > b.L : a.AsDouble > b.AsDouble);
                    case "<=": return Num.Bool(ints ? a.L <= b.L : a.AsDouble <= b.AsDouble);
                    case ">=": return Num.Bool(ints ? a.L >= b.L : a.AsDouble >= b.AsDouble);
                    default: throw new VisionException(ErrorCode.CameraParameterInvalid, $"Unknown operator '{_op}' in formula.");
                }
            }
        }

        private sealed class TernaryExpr : Expr
        {
            private readonly Expr _cond, _a, _b;
            public TernaryExpr(Expr cond, Expr a, Expr b) { _cond = cond; _a = a; _b = b; }
            public override Num Eval(Ctx c) => _cond.Eval(c).AsBool ? _a.Eval(c) : _b.Eval(c);
        }

        private sealed class CallExpr : Expr
        {
            private readonly string _fn;
            private readonly Expr[] _args;
            public CallExpr(string fn, Expr[] args) { _fn = fn; _args = args; }

            public override Num Eval(Ctx c)
            {
                var a = _args[0].Eval(c);
                double x = a.AsDouble;
                switch (_fn)
                {
                    case "SGN": return a.IsInt ? Num.Int(Math.Sign(a.L)) : Num.Real(Math.Sign(x));
                    case "NEG": return a.IsInt ? Num.Int(unchecked(-a.L)) : Num.Real(-x);
                    case "ABS": return a.IsInt ? Num.Int(Math.Abs(a.L)) : Num.Real(Math.Abs(x));
                    case "ATAN": return Num.Real(Math.Atan(x));
                    case "COS": return Num.Real(Math.Cos(x));
                    case "SIN": return Num.Real(Math.Sin(x));
                    case "TAN": return Num.Real(Math.Tan(x));
                    case "ASIN": return Num.Real(Math.Asin(x));
                    case "ACOS": return Num.Real(Math.Acos(x));
                    case "EXP": return Num.Real(Math.Exp(x));
                    case "LN": return Num.Real(Math.Log(x));
                    case "LG": return Num.Real(Math.Log10(x));
                    case "SQRT": return Num.Real(Math.Sqrt(x));
                    case "TRUNC": return a.IsInt ? a : Num.Real(Math.Truncate(x));
                    case "FLOOR": return a.IsInt ? a : Num.Real(Math.Floor(x));
                    case "CEIL": return a.IsInt ? a : Num.Real(Math.Ceiling(x));
                    case "ROUND":
                    {
                        if (a.IsInt && _args.Length == 1) return a;
                        int digits = _args.Length > 1 ? (int)_args[1].Eval(c).AsLong : 0;
                        double scale = Math.Pow(10, digits);
                        return Num.Real(Math.Round(x * scale, MidpointRounding.AwayFromZero) / scale);
                    }
                    default: throw new VisionException(ErrorCode.CameraParameterInvalid, $"Unknown function '{_fn}' in formula.");
                }
            }
        }

        // ------------------------------------------------------------------ 파서

        private static readonly HashSet<string> Functions = new HashSet<string>(StringComparer.Ordinal)
        {
            "SGN", "NEG", "ATAN", "COS", "SIN", "TAN", "ABS", "EXP", "LN", "LG", "SQRT", "TRUNC", "FLOOR", "CEIL", "ROUND", "ASIN", "ACOS",
        };

        // 이항 연산자 우선순위 (높을수록 먼저). "?:" 는 별도 처리.
        private static readonly Dictionary<string, int> Precedence = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["||"] = 1, ["&&"] = 2, ["|"] = 3, ["^"] = 4, ["&"] = 5,
            ["="] = 6, ["<>"] = 6,
            ["<"] = 7, [">"] = 7, ["<="] = 7, [">="] = 7,
            ["<<"] = 8, [">>"] = 8,
            ["+"] = 9, ["-"] = 9,
            ["*"] = 10, ["/"] = 10, ["%"] = 10,
            ["**"] = 11,
        };

        private enum TokKind { Number, Ident, Op, End }

        private struct Token
        {
            public TokKind Kind;
            public string Text;
            public Num Value;
        }

        private sealed class Parser
        {
            private readonly string _s;
            private int _pos;
            private Token _tok;

            public Parser(string s)
            {
                _s = s;
                Next();
            }

            public Expr ParseAll()
            {
                var e = ParseTernary();
                if (_tok.Kind != TokKind.End)
                    throw Error($"unexpected '{_tok.Text}'");
                return e;
            }

            private Exception Error(string what)
                => new VisionException(ErrorCode.CameraParameterInvalid, $"Invalid formula '{_s}': {what}.");

            private Expr ParseTernary()
            {
                var cond = ParseBinary(1);
                if (IsOp("?"))
                {
                    Next();
                    var a = ParseTernary();
                    if (!IsOp(":")) throw Error("expected ':'");
                    Next();
                    var b = ParseTernary();
                    return new TernaryExpr(cond, a, b);
                }
                return cond;
            }

            private Expr ParseBinary(int minPrec)
            {
                var left = ParseUnary();
                while (_tok.Kind == TokKind.Op && Precedence.TryGetValue(_tok.Text, out int prec) && prec >= minPrec)
                {
                    string op = _tok.Text;
                    Next();
                    // "**" 는 오른쪽 결합
                    var right = ParseBinary(op == "**" ? prec : prec + 1);
                    left = new BinaryExpr(op, left, right);
                }
                return left;
            }

            private Expr ParseUnary()
            {
                if (IsOp("-") || IsOp("+") || IsOp("~") || IsOp("!"))
                {
                    string op = _tok.Text;
                    Next();
                    return new UnaryExpr(op, ParseUnary());
                }
                return ParsePrimary();
            }

            private Expr ParsePrimary()
            {
                switch (_tok.Kind)
                {
                    case TokKind.Number:
                    {
                        var v = _tok.Value;
                        Next();
                        return new ConstExpr(v);
                    }
                    case TokKind.Ident:
                    {
                        string name = _tok.Text;
                        Next();
                        string upper = name.ToUpperInvariant();
                        if (IsOp("(") && Functions.Contains(upper))
                        {
                            Next();
                            var args = new List<Expr> { ParseTernary() };
                            while (IsOp(","))
                            {
                                Next();
                                args.Add(ParseTernary());
                            }
                            Expect(")");
                            return new CallExpr(upper, args.ToArray());
                        }
                        return new VarExpr(name);
                    }
                    case TokKind.Op when _tok.Text == "(":
                    {
                        Next();
                        var e = ParseTernary();
                        Expect(")");
                        return e;
                    }
                    default:
                        throw Error(_tok.Kind == TokKind.End ? "unexpected end" : $"unexpected '{_tok.Text}'");
                }
            }

            private bool IsOp(string op) => _tok.Kind == TokKind.Op && _tok.Text == op;

            private void Expect(string op)
            {
                if (!IsOp(op)) throw Error($"expected '{op}'");
                Next();
            }

            private void Next()
            {
                while (_pos < _s.Length && char.IsWhiteSpace(_s[_pos])) _pos++;
                if (_pos >= _s.Length)
                {
                    _tok = new Token { Kind = TokKind.End, Text = "" };
                    return;
                }

                char ch = _s[_pos];
                if (char.IsDigit(ch) || (ch == '.' && _pos + 1 < _s.Length && char.IsDigit(_s[_pos + 1])))
                {
                    _tok = ReadNumber();
                    return;
                }
                if (char.IsLetter(ch) || ch == '_')
                {
                    int start = _pos;
                    while (_pos < _s.Length)
                    {
                        char c = _s[_pos];
                        if (char.IsLetterOrDigit(c) || c == '_' || c == '.') { _pos++; continue; }
                        // "Std::Name" 같은 이름공간 구분자
                        if (c == ':' && _pos + 1 < _s.Length && _s[_pos + 1] == ':') { _pos += 2; continue; }
                        break;
                    }
                    _tok = new Token { Kind = TokKind.Ident, Text = _s.Substring(start, _pos - start) };
                    return;
                }

                foreach (var op in TwoCharOps)
                {
                    if (string.CompareOrdinal(_s, _pos, op, 0, 2) == 0)
                    {
                        _pos += 2;
                        _tok = new Token { Kind = TokKind.Op, Text = Normalize(op) };
                        return;
                    }
                }
                if ("+-*/%&|^~!=<>?:(),".IndexOf(ch) >= 0)
                {
                    _pos++;
                    _tok = new Token { Kind = TokKind.Op, Text = ch.ToString() };
                    return;
                }
                throw Error($"unexpected character '{ch}'");
            }

            private static readonly string[] TwoCharOps = { "**", "<<", ">>", "<=", ">=", "<>", "!=", "==", "&&", "||" };

            private static string Normalize(string op) => op == "!=" ? "<>" : op == "==" ? "=" : op;

            private Token ReadNumber()
            {
                int start = _pos;
                if (_s[_pos] == '0' && _pos + 1 < _s.Length && (_s[_pos + 1] == 'x' || _s[_pos + 1] == 'X'))
                {
                    _pos += 2;
                    int hs = _pos;
                    while (_pos < _s.Length && Uri.IsHexDigit(_s[_pos])) _pos++;
                    ulong u = ulong.Parse(_s.Substring(hs, _pos - hs), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    return new Token { Kind = TokKind.Number, Text = _s.Substring(start, _pos - start), Value = Num.Int(unchecked((long)u)) };
                }

                bool real = false;
                while (_pos < _s.Length && char.IsDigit(_s[_pos])) _pos++;
                if (_pos < _s.Length && _s[_pos] == '.')
                {
                    real = true;
                    _pos++;
                    while (_pos < _s.Length && char.IsDigit(_s[_pos])) _pos++;
                }
                if (_pos < _s.Length && (_s[_pos] == 'e' || _s[_pos] == 'E'))
                {
                    int save = _pos;
                    _pos++;
                    if (_pos < _s.Length && (_s[_pos] == '+' || _s[_pos] == '-')) _pos++;
                    if (_pos < _s.Length && char.IsDigit(_s[_pos]))
                    {
                        real = true;
                        while (_pos < _s.Length && char.IsDigit(_s[_pos])) _pos++;
                    }
                    else
                    {
                        _pos = save;
                    }
                }
                string text = _s.Substring(start, _pos - start);
                var value = real
                    ? Num.Real(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture))
                    : Num.Int(unchecked((long)ulong.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture)));
                return new Token { Kind = TokKind.Number, Text = text, Value = value };
            }
        }
    }
}
