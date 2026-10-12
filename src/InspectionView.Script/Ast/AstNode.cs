using System.Collections.Generic;

namespace InspectionView.Script.Ast
{
    // ---------------------------------------------------------------------
    // 1차 문법 AST: 대입, 수식, 연산자 호출, if / for / while
    // 파서와 인터프리터는 다음 단계에서 추가된다. 노드 UI 변환과 Job 저장도 이 AST 를 사용한다.
    // ---------------------------------------------------------------------

    /// <summary>소스 위치 (브레이크포인트, 실행 줄 하이라이트, 오류 표시에 사용).</summary>
    public readonly struct SourceSpan
    {
        public int Line { get; }
        public int Column { get; }
        public int Length { get; }

        public SourceSpan(int line, int column, int length)
        {
            Line = line;
            Column = column;
            Length = length;
        }

        public override string ToString() => $"({Line}:{Column})";
    }

    public abstract class AstNode
    {
        public SourceSpan Span { get; set; }
    }

    // ---- 식 ----

    public abstract class Expression : AstNode
    {
    }

    /// <summary>리터럴: 정수, 실수, 문자열, 불리언.</summary>
    public sealed class LiteralExpression : Expression
    {
        public object Value { get; }
        public LiteralExpression(object value) => Value = value;
    }

    /// <summary>변수 참조.</summary>
    public sealed class IdentifierExpression : Expression
    {
        public string Name { get; }
        public IdentifierExpression(string name) => Name = name;
    }

    /// <summary>튜플 리터럴 [a, b, c].</summary>
    public sealed class TupleExpression : Expression
    {
        public List<Expression> Items { get; } = new List<Expression>();
    }

    public enum BinaryOperator
    {
        Add, Subtract, Multiply, Divide, Modulo,
        Equal, NotEqual, Less, LessEqual, Greater, GreaterEqual,
        And, Or,
    }

    public sealed class BinaryExpression : Expression
    {
        public BinaryOperator Operator { get; }
        public Expression Left { get; }
        public Expression Right { get; }

        public BinaryExpression(BinaryOperator op, Expression left, Expression right)
        {
            Operator = op;
            Left = left;
            Right = right;
        }
    }

    public enum UnaryOperator
    {
        Negate,
        Not,
    }

    public sealed class UnaryExpression : Expression
    {
        public UnaryOperator Operator { get; }
        public Expression Operand { get; }

        public UnaryExpression(UnaryOperator op, Expression operand)
        {
            Operator = op;
            Operand = operand;
        }
    }

    /// <summary>연산자 호출: ReadImage('a.png'), Threshold(Image, 128, 255)</summary>
    public sealed class CallExpression : Expression
    {
        public string OperatorName { get; }
        public List<Expression> Arguments { get; } = new List<Expression>();
        public Dictionary<string, Expression> NamedArguments { get; } = new Dictionary<string, Expression>();

        public CallExpression(string operatorName) => OperatorName = operatorName;
    }

    /// <summary>결과 객체 멤버 접근: Res.Area</summary>
    public sealed class MemberExpression : Expression
    {
        public Expression Target { get; }
        public string Member { get; }

        public MemberExpression(Expression target, string member)
        {
            Target = target;
            Member = member;
        }
    }

    /// <summary>인덱스 접근: Regions[0], Tuple[i]</summary>
    public sealed class IndexExpression : Expression
    {
        public Expression Target { get; }
        public Expression Index { get; }

        public IndexExpression(Expression target, Expression index)
        {
            Target = target;
            Index = index;
        }
    }

    // ---- 문 ----

    public abstract class Statement : AstNode
    {
    }

    /// <summary>대입: Name = Expression</summary>
    public sealed class AssignmentStatement : Statement
    {
        public string Target { get; }
        public Expression Value { get; }

        public AssignmentStatement(string target, Expression value)
        {
            Target = target;
            Value = value;
        }
    }

    /// <summary>반환값을 쓰지 않는 호출: WriteImage(Image, 'out.png')</summary>
    public sealed class ExpressionStatement : Statement
    {
        public Expression Expression { get; }
        public ExpressionStatement(Expression expression) => Expression = expression;
    }

    public sealed class BlockStatement : Statement
    {
        public List<Statement> Statements { get; } = new List<Statement>();
    }

    public sealed class IfStatement : Statement
    {
        public Expression Condition { get; }
        public BlockStatement Then { get; }
        public BlockStatement? Else { get; set; }

        public IfStatement(Expression condition, BlockStatement then)
        {
            Condition = condition;
            Then = then;
        }
    }

    /// <summary>for Index := Start to End by Step</summary>
    public sealed class ForStatement : Statement
    {
        public string Variable { get; }
        public Expression Start { get; }
        public Expression End { get; }
        public Expression? Step { get; set; }
        public BlockStatement Body { get; }

        public ForStatement(string variable, Expression start, Expression end, BlockStatement body)
        {
            Variable = variable;
            Start = start;
            End = end;
            Body = body;
        }
    }

    public sealed class WhileStatement : Statement
    {
        public Expression Condition { get; }
        public BlockStatement Body { get; }

        public WhileStatement(Expression condition, BlockStatement body)
        {
            Condition = condition;
            Body = body;
        }
    }

    public sealed class BreakStatement : Statement
    {
    }

    public sealed class ContinueStatement : Statement
    {
    }

    /// <summary>스크립트 전체.</summary>
    public sealed class ProgramNode : AstNode
    {
        public BlockStatement Body { get; } = new BlockStatement();
    }
}
