using System;
using System.Collections.Generic;
using InspectionView.Core.Errors;
using InspectionView.Core.Memory;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;
using InspectionView.Script.Ast;
using InspectionView.Script.Compilation;

namespace InspectionView.Script.Runtime
{
    /// <summary>
    /// 명령 리스트를 실행하는 인터프리터. 스레드 제어(실행/정지/브레이크포인트)는 <see cref="ScriptEngine"/> 이 담당하고,
    /// 이 클래스는 PC 하나를 전진시키는 일만 한다.
    /// <para>
    /// 메모리: 연산자가 만든 아이코닉 출력은 임시 목록에 들어가고 명령이 끝나면 Release 된다.
    /// 변수에 대입되면 <see cref="VariableTable"/> 이 AddRef 하므로 변수가 소유한 참조만 남는다.
    /// </para>
    /// </summary>
    public sealed class Interpreter
    {
        private readonly List<IconicObject> _temporaries = new List<IconicObject>();
        private readonly Dictionary<int, ForState> _loops = new Dictionary<int, ForState>();

        private sealed class ForState
        {
            public double End;
            public double Step;
            public bool Integer;
        }

        public OperatorRegistry Registry { get; }
        public VariableTable Variables { get; }
        public Builtins Builtins { get; }
        public IScriptHost Host { get; }

        /// <summary>실행마다 교체되는 연산자 컨텍스트 (취소 토큰, 프로파일러).</summary>
        public OperatorContext Context { get; set; }

        public Instruction[] Code { get; private set; } = Array.Empty<Instruction>();

        /// <summary>다음에 실행할 명령 인덱스.</summary>
        public int Pc { get; set; }

        public bool IsFinished => Pc >= Code.Length;

        public Interpreter(OperatorRegistry registry, VariableTable variables, Builtins builtins, IScriptHost host, OperatorContext context)
        {
            Registry = registry;
            Variables = variables;
            Builtins = builtins;
            Host = host;
            Context = context;
        }

        public void Load(Instruction[] code)
        {
            Code = code ?? Array.Empty<Instruction>();
            Pc = 0;
            _loops.Clear();
        }

        /// <summary>PC 를 처음으로 되돌린다 (변수는 유지).</summary>
        public void Reset()
        {
            Pc = 0;
            _loops.Clear();
        }

        /// <summary>현재 명령 하나를 실행하고 PC 를 전진시킨다. 오류 시 PC 는 그대로이며 예외를 던진다.</summary>
        public void ExecuteInstruction()
        {
            if (IsFinished)
                return;

            var ins = Code[Pc];
            try
            {
                int next = Execute(ins);
                Pc = next;
            }
            catch (ScriptRuntimeException)
            {
                throw;
            }
            catch (VisionException ex)
            {
                throw new ScriptRuntimeException(ins.Line, ex.Message, ex.Code, ex);
            }
            catch (OperationCanceledException ex)
            {
                throw new ScriptRuntimeException(ins.Line, "Execution was cancelled.", ErrorCode.Cancelled, ex);
            }
            finally
            {
                ReleaseTemporaries();
            }
        }

        private int Execute(Instruction ins)
        {
            switch (ins.Op)
            {
                case OpCode.Assign:
                {
                    var value = Evaluate(ins.Expr!);
                    if (value == null)
                        throw new ScriptRuntimeException(ins.Line, $"'{ins.Name}' cannot be assigned: the expression has no value");
                    Variables.Set(ins.Name!, value);
                    return Pc + 1;
                }

                case OpCode.Eval:
                    Evaluate(ins.Expr!);
                    return Pc + 1;

                case OpCode.JumpIfFalse:
                    return ScriptValues.IsTruthy(Evaluate(ins.Expr!), ins.Line) ? Pc + 1 : ins.Target;

                case OpCode.Jump:
                    return ins.Target;

                case OpCode.ForInit:
                {
                    var start = Scalar(Evaluate(ins.Expr!), ins.Line, "for start");
                    var end = Scalar(Evaluate(ins.End!), ins.Line, "for end");
                    object step = ins.Step != null ? Scalar(Evaluate(ins.Step), ins.Line, "for step") : 1L;

                    var state = new ForState
                    {
                        End = ToDouble(end, ins.Line),
                        Step = ToDouble(step, ins.Line),
                        Integer = start is long && step is long,
                    };
                    if (state.Step == 0)
                        throw new ScriptRuntimeException(ins.Line, "for step must not be 0");
                    _loops[ins.LoopIndex] = state;

                    Variables.Set(ins.Name!, new TupleValue(start));
                    return InRange(ToDouble(start, ins.Line), state) ? Pc + 1 : ins.Target;
                }

                case OpCode.ForNext:
                {
                    if (!_loops.TryGetValue(ins.LoopIndex, out var state))
                        throw new ScriptRuntimeException(ins.Line, $"Loop variable '{ins.Name}' was not initialized (the loop was entered without executing its 'for' line)");

                    var cur = ScriptValues.ToTuple(Variables.Get(ins.Name!), ins.Line);
                    if (!cur.IsScalar)
                        throw new ScriptRuntimeException(ins.Line, $"Loop variable '{ins.Name}' is not a scalar");

                    object next = state.Integer && cur[0] is long l
                        ? (object)(l + (long)state.Step)
                        : ToDouble(cur[0], ins.Line) + state.Step;

                    Variables.Set(ins.Name!, new TupleValue(next));
                    return InRange(ToDouble(next, ins.Line), state) ? ins.Target : Pc + 1;
                }

                default:
                    throw new ScriptRuntimeException(ins.Line, $"Unknown instruction {ins.Op}");
            }
        }

        private static bool InRange(double value, ForState s)
            => s.Step > 0 ? value <= s.End : value >= s.End;

        private static object Scalar(object? value, int line, string what)
        {
            var t = ScriptValues.ToTuple(value, line);
            if (!t.IsScalar)
                throw new ScriptRuntimeException(line, $"{what} must be a single number");
            var v = t[0];
            if (!(v is long) && !(v is double))
                throw new ScriptRuntimeException(line, $"{what} must be a number");
            return v;
        }

        private static double ToDouble(object v, int line)
        {
            switch (v)
            {
                case long l: return l;
                case double d: return d;
                default: throw new ScriptRuntimeException(line, $"'{v}' is not a number");
            }
        }

        private void ReleaseTemporaries()
        {
            foreach (var t in _temporaries)
                t.Release();
            _temporaries.Clear();
        }

        // ------------------------------------------------------------------
        // 식 평가
        // ------------------------------------------------------------------

        public object? Evaluate(Expression e)
        {
            switch (e)
            {
                case LiteralExpression lit:
                    return new TupleValue(lit.Value);

                case IdentifierExpression id:
                    if (Variables.TryGet(id.Name, out var v))
                        return v;
                    throw new ScriptRuntimeException(id.Line, $"Variable '{id.Name}' is not defined");

                case TupleExpression tuple:
                {
                    var items = new List<object>();
                    foreach (var item in tuple.Items)
                    {
                        var t = ScriptValues.ToTuple(Evaluate(item), item.Line);
                        foreach (var o in t) items.Add(o);
                    }
                    return new TupleValue(items.ToArray());
                }

                case UnaryExpression u:
                    return ScriptValues.Unary(u.Operator, Evaluate(u.Operand), u.Line);

                case BinaryExpression b:
                {
                    // 논리 연산의 단락 평가 (스칼라일 때)
                    if (b.Operator == BinaryOperator.And || b.Operator == BinaryOperator.Or)
                    {
                        var left = Evaluate(b.Left);
                        if (left is TupleValue lt && lt.IsScalar)
                        {
                            bool lv = ScriptValues.IsTruthy(lt, b.Line);
                            if (b.Operator == BinaryOperator.And && !lv) return new TupleValue(false);
                            if (b.Operator == BinaryOperator.Or && lv) return new TupleValue(true);
                        }
                        return ScriptValues.Binary(b.Operator, left, Evaluate(b.Right), b.Line);
                    }
                    return ScriptValues.Binary(b.Operator, Evaluate(b.Left), Evaluate(b.Right), b.Line);
                }

                case MemberExpression m:
                    return ScriptValues.Member(Evaluate(m.Target), m.Member, m.Line);

                case IndexExpression ix:
                {
                    var target = Evaluate(ix.Target);
                    var index = Evaluate(ix.Index);
                    var result = ScriptValues.Index(target, index, ix.Line);
                    // 튜플 인덱스로 만든 부분 배열은 새 객체이므로 임시로 등록 (단일 원소는 배열이 소유)
                    if (result is IconicObject io && target is IObjectArray && !ScriptValues.ToTuple(index, ix.Line).IsScalar)
                        _temporaries.Add(io);
                    return result;
                }

                case CallExpression call:
                    return Call(call);

                default:
                    throw new ScriptRuntimeException(e.Line, $"Unsupported expression {e.GetType().Name}");
            }
        }

        private object? Call(CallExpression call)
        {
            var args = new object?[call.Arguments.Count];
            for (int i = 0; i < args.Length; i++)
                args[i] = Evaluate(call.Arguments[i]);

            // 내장 함수
            if (Builtins.TryGet(call.Name, out var fn))
            {
                if (call.NamedArguments.Count > 0)
                    throw new ScriptRuntimeException(call.Line, $"Built-in function '{call.Name}' does not take named arguments");
                return fn(args, call.Line, Host);
            }

            // 레지스트리 연산자
            if (!Registry.TryGet(call.Name, out var descriptor))
                throw new ScriptRuntimeException(call.Line, $"Unknown operator or function '{call.Name}'", ErrorCode.OperatorNotFound);

            Dictionary<string, object?>? named = null;
            if (call.NamedArguments.Count > 0)
            {
                named = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var kv in call.NamedArguments)
                    named[kv.Key] = Evaluate(kv.Value);
            }

            OperatorResult result;
            try
            {
                Context.Line = call.Line;
                result = OperatorInvoker.InvokeOrThrow(Registry, descriptor.Name, args, named, Context);
            }
            catch (VisionException ex)
            {
                throw new ScriptRuntimeException(call.Line, $"{call.Name}: {ex.Message}", ex.Code, ex);
            }

            // 연산자가 만든 출력은 명령이 끝나면 해제 (변수가 받으면 그쪽이 AddRef)
            foreach (var o in result.Outputs.Values)
            {
                if (o is IconicObject io)
                    _temporaries.Add(io);
            }

            return result.ReturnValue;
        }
    }
}
