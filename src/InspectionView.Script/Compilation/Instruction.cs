using InspectionView.Script.Ast;

namespace InspectionView.Script.Compilation
{
    public enum OpCode
    {
        /// <summary>Name = Expr</summary>
        Assign,
        /// <summary>식 평가 (결과 버림)</summary>
        Eval,
        /// <summary>Expr 이 거짓이면 Target 으로</summary>
        JumpIfFalse,
        /// <summary>무조건 Target 으로</summary>
        Jump,
        /// <summary>for 시작: Name = Expr(Start), End/Step 평가해 저장. 범위 밖이면 Target(루프 끝)으로</summary>
        ForInit,
        /// <summary>for 다음 반복: Name += Step, 범위 안이면 Target(본문 시작)으로, 아니면 다음 명령</summary>
        ForNext,
    }

    /// <summary>
    /// AST 를 평탄화한 명령. 프로그램 카운터(PC) 기반이라 브레이크포인트, 한 줄 실행, PC 이동이 단순해진다.
    /// 식은 AST 그대로 들고 있어 인터프리터가 트리 평가한다.
    /// </summary>
    public sealed class Instruction
    {
        public OpCode Op { get; }

        /// <summary>소스 줄 (1 기반).</summary>
        public int Line { get; }

        /// <summary>같은 문에서 나온 명령은 같은 ID 를 가진다 (Step 단위).</summary>
        public int StatementId { get; }

        /// <summary>Assign / ForInit / ForNext 의 변수 이름.</summary>
        public string? Name { get; }

        /// <summary>Assign 값, Eval 식, JumpIfFalse 조건, ForInit 시작값.</summary>
        public Expression? Expr { get; }

        /// <summary>ForInit 종료값.</summary>
        public Expression? End { get; }

        /// <summary>ForInit 증가값 (없으면 1).</summary>
        public Expression? Step { get; }

        /// <summary>점프 대상 명령 인덱스. ForNext 는 본문 시작, ForInit 는 루프 끝.</summary>
        public int Target { get; internal set; } = -1;

        /// <summary>ForNext 가 참조하는 ForInit 의 인덱스 (루프 상태 키).</summary>
        public int LoopIndex { get; internal set; } = -1;

        /// <summary>이 명령을 만든 AST 문 (노드 UI / 변수창 연결용).</summary>
        public Statement? Source { get; }

        internal Instruction(OpCode op, int line, int statementId, Statement? source,
            string? name = null, Expression? expr = null, Expression? end = null, Expression? step = null)
        {
            Op = op;
            Line = line;
            StatementId = statementId;
            Source = source;
            Name = name;
            Expr = expr;
            End = end;
            Step = step;
        }

        public override string ToString()
        {
            switch (Op)
            {
                case OpCode.Assign: return $"L{Line}: {Name} = <expr>";
                case OpCode.Eval: return $"L{Line}: eval";
                case OpCode.JumpIfFalse: return $"L{Line}: jumpIfFalse -> {Target}";
                case OpCode.Jump: return $"L{Line}: jump -> {Target}";
                case OpCode.ForInit: return $"L{Line}: for {Name} (exit -> {Target})";
                case OpCode.ForNext: return $"L{Line}: next {Name} (body -> {Target})";
                default: return $"L{Line}: {Op}";
            }
        }
    }
}
