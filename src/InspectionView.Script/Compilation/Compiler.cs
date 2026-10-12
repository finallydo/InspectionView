using System.Collections.Generic;
using InspectionView.Script.Ast;
using InspectionView.Script.Parsing;

namespace InspectionView.Script.Compilation
{
    /// <summary>AST → 명령 리스트. break/continue 의 점프 대상은 루프 스택으로 해결한다.</summary>
    public sealed class Compiler
    {
        private readonly List<Instruction> _code = new List<Instruction>();
        private readonly List<Diagnostic> _diagnostics = new List<Diagnostic>();
        private readonly Stack<LoopContext> _loops = new Stack<LoopContext>();
        private int _nextStatementId;

        private sealed class LoopContext
        {
            public List<Instruction> Breaks { get; } = new List<Instruction>();
            public List<Instruction> Continues { get; } = new List<Instruction>();
        }

        public static Instruction[] Compile(ProgramNode program, out IReadOnlyList<Diagnostic> diagnostics)
        {
            var c = new Compiler();
            c.EmitBlock(program.Body);
            diagnostics = c._diagnostics;
            return c._code.ToArray();
        }

        private int NewStatementId() => _nextStatementId++;

        private Instruction Emit(Instruction ins)
        {
            _code.Add(ins);
            return ins;
        }

        private void EmitBlock(BlockStatement block)
        {
            foreach (var s in block.Statements)
                EmitStatement(s);
        }

        private void EmitStatement(Statement s)
        {
            switch (s)
            {
                case AssignmentStatement a:
                    Emit(new Instruction(OpCode.Assign, a.Line, NewStatementId(), a, a.Target, a.Value));
                    break;

                case ExpressionStatement e:
                    Emit(new Instruction(OpCode.Eval, e.Line, NewStatementId(), e, expr: e.Expression));
                    break;

                case IfStatement i:
                    EmitIf(i);
                    break;

                case ForStatement f:
                    EmitFor(f);
                    break;

                case WhileStatement w:
                    EmitWhile(w);
                    break;

                case BreakStatement b:
                    if (_loops.Count == 0)
                    {
                        _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, b.Line, b.Span.Column, b.Span.Length, "'break' outside of a loop"));
                        break;
                    }
                    _loops.Peek().Breaks.Add(Emit(new Instruction(OpCode.Jump, b.Line, NewStatementId(), b)));
                    break;

                case ContinueStatement c:
                    if (_loops.Count == 0)
                    {
                        _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, c.Line, c.Span.Column, c.Span.Length, "'continue' outside of a loop"));
                        break;
                    }
                    _loops.Peek().Continues.Add(Emit(new Instruction(OpCode.Jump, c.Line, NewStatementId(), c)));
                    break;

                case BlockStatement blk:
                    EmitBlock(blk);
                    break;
            }
        }

        private void EmitIf(IfStatement i)
        {
            var jumpsToEnd = new List<Instruction>();

            // if
            var test = Emit(new Instruction(OpCode.JumpIfFalse, i.Line, NewStatementId(), i, expr: i.Condition));
            EmitBlock(i.Then);
            bool hasMore = i.ElseIfs.Count > 0 || i.Else != null;
            if (hasMore)
                jumpsToEnd.Add(Emit(new Instruction(OpCode.Jump, i.EndSpan.Line, NewStatementId(), i)));
            test.Target = _code.Count;

            // elseif ...
            for (int k = 0; k < i.ElseIfs.Count; k++)
            {
                var clause = i.ElseIfs[k];
                var t = Emit(new Instruction(OpCode.JumpIfFalse, clause.Span.Line, NewStatementId(), i, expr: clause.Condition));
                EmitBlock(clause.Body);
                bool more = k < i.ElseIfs.Count - 1 || i.Else != null;
                if (more)
                    jumpsToEnd.Add(Emit(new Instruction(OpCode.Jump, i.EndSpan.Line, NewStatementId(), i)));
                t.Target = _code.Count;
            }

            // else
            if (i.Else != null)
                EmitBlock(i.Else);

            foreach (var j in jumpsToEnd)
                j.Target = _code.Count;
        }

        private void EmitFor(ForStatement f)
        {
            var ctx = new LoopContext();
            _loops.Push(ctx);

            int initIndex = _code.Count;
            var init = Emit(new Instruction(OpCode.ForInit, f.Line, NewStatementId(), f, f.Variable, f.Start, f.End, f.Step));
            int bodyStart = _code.Count;
            EmitBlock(f.Body);

            int nextIndex = _code.Count;
            var next = Emit(new Instruction(OpCode.ForNext, f.EndSpan.Line, NewStatementId(), f, f.Variable));
            next.Target = bodyStart;
            next.LoopIndex = initIndex;

            int exit = _code.Count;
            init.Target = exit;
            init.LoopIndex = initIndex;

            _loops.Pop();
            foreach (var b in ctx.Breaks) b.Target = exit;
            foreach (var c in ctx.Continues) c.Target = nextIndex;
        }

        private void EmitWhile(WhileStatement w)
        {
            var ctx = new LoopContext();
            _loops.Push(ctx);

            int testIndex = _code.Count;
            var test = Emit(new Instruction(OpCode.JumpIfFalse, w.Line, NewStatementId(), w, expr: w.Condition));
            EmitBlock(w.Body);
            var back = Emit(new Instruction(OpCode.Jump, w.EndSpan.Line, NewStatementId(), w));
            back.Target = testIndex;

            int exit = _code.Count;
            test.Target = exit;

            _loops.Pop();
            foreach (var b in ctx.Breaks) b.Target = exit;
            foreach (var c in ctx.Continues) c.Target = testIndex;
        }
    }
}
