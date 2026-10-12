using System.Collections.Generic;

namespace InspectionView.Script.Ast
{
    // ---------------------------------------------------------------------
    // 스크립트 AST. 파서가 만들고, 컴파일러(명령 리스트), 노드 UI 변환, Job 저장이 공유한다.
    //
    // 문법 요약 (1차):
    //   Name = Expression                  대입 ( := 도 허용)
    //   Op(Arg, Name = Arg)                연산자 호출문
    //   if Cond ... elseif Cond ... else ... endif
    //   for I = Start to End by Step ... endfor
    //   while Cond ... endwhile
    //   break / continue
    //   // 주석
    // ---------------------------------------------------------------------

    /// <summary>소스 위치 (1 기반 줄/열). 브레이크포인트, 실행 줄 하이라이트, 오류 표시에 사용.</summary>
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
        public int Line => Span.Line;
    }

    // ---- 식 ----

    public abstract class Expression : AstNode
    {
    }

    /// <summary>리터럴: long, double, string, bool.</summary>
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

    /// <summary>연산자/내장 함수 호출: ReadImage('a.png'), Threshold(Image, MinGray = 100, MaxGray = 255)</summary>
    public sealed class CallExpression : Expression
    {
        public string Name { get; }
        public List<Expression> Arguments { get; } = new List<Expression>();
        public List<KeyValuePair<string, Expression>> NamedArguments { get; } = new List<KeyValuePair<string, Expression>>();

        public CallExpression(string name) => Name = name;
    }

    /// <summary>멤버 접근: Res.Area, Image.Width</summary>
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

    public sealed class ElseIfClause
    {
        public SourceSpan Span { get; set; }
        public Expression Condition { get; }
        public BlockStatement Body { get; }

        public ElseIfClause(Expression condition, BlockStatement body)
        {
            Condition = condition;
            Body = body;
        }
    }

    public sealed class IfStatement : Statement
    {
        public Expression Condition { get; }
        public BlockStatement Then { get; }
        public List<ElseIfClause> ElseIfs { get; } = new List<ElseIfClause>();
        public BlockStatement? Else { get; set; }

        /// <summary>'else' 키워드 위치 (있을 때).</summary>
        public SourceSpan ElseSpan { get; set; }

        /// <summary>'endif' 키워드 위치.</summary>
        public SourceSpan EndSpan { get; set; }

        public IfStatement(Expression condition, BlockStatement then)
        {
            Condition = condition;
            Then = then;
        }
    }

    /// <summary>for Index = Start to End by Step ... endfor (End 와 Step 은 루프 시작 시 한 번 평가)</summary>
    public sealed class ForStatement : Statement
    {
        public string Variable { get; }
        public Expression Start { get; }
        public Expression End { get; }
        public Expression? Step { get; set; }
        public BlockStatement Body { get; }
        public SourceSpan EndSpan { get; set; }

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
        public SourceSpan EndSpan { get; set; }

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
