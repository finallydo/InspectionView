using System;
using System.Globalization;
using System.Linq;
using System.Text;
using InspectionView.Script.Ast;

namespace InspectionView.Script.Graph
{
    /// <summary>AST 식을 다시 스크립트 텍스트로 출력한다 (노드 편집기의 리터럴/식 보존용).</summary>
    public static class AstPrinter
    {
        public static string Print(Expression e)
        {
            var sb = new StringBuilder();
            Write(sb, e, 0);
            return sb.ToString();
        }

        public static string Literal(object? value)
        {
            switch (value)
            {
                case null: return "[]";
                case string s: return "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
                case bool b: return b ? "true" : "false";
                case double d: return d.ToString("R", CultureInfo.InvariantCulture);
                case float f: return ((double)f).ToString("R", CultureInfo.InvariantCulture);
                case IFormattable fm: return fm.ToString(null, CultureInfo.InvariantCulture);
                default: return value.ToString() ?? "";
            }
        }

        private static int Precedence(BinaryOperator op)
        {
            switch (op)
            {
                case BinaryOperator.Or: return 1;
                case BinaryOperator.And: return 2;
                case BinaryOperator.Equal:
                case BinaryOperator.NotEqual:
                case BinaryOperator.Less:
                case BinaryOperator.LessEqual:
                case BinaryOperator.Greater:
                case BinaryOperator.GreaterEqual: return 3;
                case BinaryOperator.Add:
                case BinaryOperator.Subtract: return 4;
                default: return 5;
            }
        }

        private static string Symbol(BinaryOperator op)
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

        private static void Write(StringBuilder sb, Expression e, int parentPrecedence)
        {
            switch (e)
            {
                case LiteralExpression lit:
                    sb.Append(Literal(lit.Value));
                    break;
                case IdentifierExpression id:
                    sb.Append(id.Name);
                    break;
                case TupleExpression t:
                    sb.Append('[');
                    for (int i = 0; i < t.Items.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        Write(sb, t.Items[i], 0);
                    }
                    sb.Append(']');
                    break;
                case UnaryExpression u:
                    sb.Append(u.Operator == UnaryOperator.Not ? "not " : "-");
                    Write(sb, u.Operand, 6);
                    break;
                case BinaryExpression b:
                {
                    int p = Precedence(b.Operator);
                    bool paren = p < parentPrecedence;
                    if (paren) sb.Append('(');
                    Write(sb, b.Left, p);
                    sb.Append(' ').Append(Symbol(b.Operator)).Append(' ');
                    Write(sb, b.Right, p + 1);
                    if (paren) sb.Append(')');
                    break;
                }
                case CallExpression c:
                    sb.Append(c.Name).Append('(');
                    bool first = true;
                    foreach (var a in c.Arguments)
                    {
                        if (!first) sb.Append(", ");
                        first = false;
                        Write(sb, a, 0);
                    }
                    foreach (var kv in c.NamedArguments)
                    {
                        if (!first) sb.Append(", ");
                        first = false;
                        sb.Append(kv.Key).Append(" = ");
                        Write(sb, kv.Value, 0);
                    }
                    sb.Append(')');
                    break;
                case MemberExpression m:
                    Write(sb, m.Target, 7);
                    sb.Append('.').Append(m.Member);
                    break;
                case IndexExpression ix:
                    Write(sb, ix.Target, 7);
                    sb.Append('[');
                    Write(sb, ix.Index, 0);
                    sb.Append(']');
                    break;
                default:
                    throw new NotSupportedException($"Cannot print {e.GetType().Name}");
            }
        }
    }
}
