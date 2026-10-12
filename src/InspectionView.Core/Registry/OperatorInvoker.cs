using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using InspectionView.Core.Errors;

namespace InspectionView.Core.Registry
{
    /// <summary>
    /// 출력이 여러 개인 연산자의 반환 객체. 스크립트에서 <c>Res = AreaCenter(Region)</c> 후 <c>Res.Area</c> 로 접근한다.
    /// </summary>
    public sealed class ResultObject : IReadOnlyDictionary<string, object?>
    {
        private readonly Dictionary<string, object?> _values;

        public string OperatorName { get; }

        public ResultObject(string operatorName, IReadOnlyDictionary<string, object?> values)
        {
            OperatorName = operatorName;
            _values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var kv in values)
                _values[kv.Key] = kv.Value;
        }

        public object? this[string key]
            => _values.TryGetValue(key, out var v)
                ? v
                : throw new VisionException(ErrorCode.InvalidArgument, $"Result of '{OperatorName}' has no member '{key}'.");

        public IEnumerable<string> Keys => _values.Keys;
        public IEnumerable<object?> Values => _values.Values;
        public int Count => _values.Count;
        public bool ContainsKey(string key) => _values.ContainsKey(key);
        public bool TryGetValue(string key, out object? value) => _values.TryGetValue(key, out value);
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _values.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString()
            => $"{OperatorName} {{ {string.Join(", ", _values.Select(kv => $"{kv.Key} = {kv.Value}"))} }}";
    }

    /// <summary>연산자 호출 결과 (런타임용: 예외 대신 코드).</summary>
    public sealed class OperatorResult
    {
        public string OperatorName { get; }
        public ErrorCode Code { get; }
        public string? Message { get; }
        public VisionException? Error { get; }
        public ExecutionRecord? Record { get; }
        public IReadOnlyDictionary<string, object?> Outputs { get; }

        public bool Success => Code == ErrorCode.Ok;

        /// <summary>
        /// 스크립트 대입문의 우변 값: 출력이 하나면 그 값, 여럿이면 <see cref="ResultObject"/>, 없으면 null.
        /// </summary>
        public object? ReturnValue
        {
            get
            {
                if (Outputs.Count == 0) return null;
                if (Outputs.Count == 1) return Outputs.Values.First();
                return new ResultObject(OperatorName, Outputs);
            }
        }

        internal OperatorResult(string operatorName, ErrorCode code, string? message, VisionException? error,
            ExecutionRecord? record, IReadOnlyDictionary<string, object?> outputs)
        {
            OperatorName = operatorName;
            Code = code;
            Message = message;
            Error = error;
            Record = record;
            Outputs = outputs;
        }

        public override string ToString() => Success ? $"{OperatorName}: OK" : $"{OperatorName}: {Code} {Message}";
    }

    /// <summary>
    /// 스크립트 스타일 호출 (<c>Image = ReadImage('a.png')</c>) 을 레지스트리의 연산자로 연결한다.
    /// 위치 인자 → [Input] 선언 순서, 이름 인자 → 입력 이름.
    /// </summary>
    public static class OperatorInvoker
    {
        private static readonly IReadOnlyDictionary<string, object?> NoOutputs = new Dictionary<string, object?>();

        /// <summary>위치 인자로 호출. 실패해도 예외를 던지지 않고 결과 코드로 돌려준다.</summary>
        public static OperatorResult Invoke(OperatorRegistry registry, string operatorName, IReadOnlyList<object?> args, OperatorContext? ctx = null)
            => Invoke(registry, operatorName, args, null, ctx);

        /// <summary>위치 인자 + 이름 인자로 호출.</summary>
        public static OperatorResult Invoke(OperatorRegistry registry, string operatorName, IReadOnlyList<object?>? args,
            IReadOnlyDictionary<string, object?>? namedArgs, OperatorContext? ctx = null)
        {
            if (registry == null) throw VisionException.NullArgument(nameof(registry));

            try
            {
                var op = Bind(registry, operatorName, args, namedArgs);
                var record = op.Execute(ctx);
                return new OperatorResult(operatorName, ErrorCode.Ok, null, null, record, op.GetOutputs());
            }
            catch (VisionException ex)
            {
                return new OperatorResult(operatorName, ex.Code, ex.Message, ex, null, NoOutputs);
            }
        }

        /// <summary>IDE 용: 실패 시 예외를 던진다 (해당 줄에서 정지).</summary>
        public static OperatorResult InvokeOrThrow(OperatorRegistry registry, string operatorName, IReadOnlyList<object?>? args,
            IReadOnlyDictionary<string, object?>? namedArgs = null, OperatorContext? ctx = null)
        {
            var op = Bind(registry, operatorName, args, namedArgs);
            var record = op.Execute(ctx);
            return new OperatorResult(operatorName, ErrorCode.Ok, null, null, record, op.GetOutputs());
        }

        /// <summary>인자를 바인딩한 연산자 인스턴스를 만든다 (실행은 하지 않음).</summary>
        public static OperatorBase Bind(OperatorRegistry registry, string operatorName, IReadOnlyList<object?>? args,
            IReadOnlyDictionary<string, object?>? namedArgs = null)
        {
            var descriptor = registry.Get(operatorName);
            var op = descriptor.CreateInstance();
            var inputs = descriptor.Inputs;
            var assigned = new HashSet<string>(StringComparer.Ordinal);

            int positional = args?.Count ?? 0;
            if (positional > inputs.Count)
                throw new VisionException(ErrorCode.InvalidArgument,
                    $"'{operatorName}' takes at most {inputs.Count} argument(s) but {positional} were given. Signature: {descriptor.Signature}");

            for (int i = 0; i < positional; i++)
            {
                inputs[i].SetValue(op, args![i]);
                assigned.Add(inputs[i].Name);
            }

            if (namedArgs != null)
            {
                foreach (var kv in namedArgs)
                {
                    var p = descriptor.GetInput(kv.Key);
                    if (!assigned.Add(p.Name))
                        throw new VisionException(ErrorCode.InvalidArgument, $"Argument '{p.Name}' of '{operatorName}' was given twice.");
                    p.SetValue(op, kv.Value);
                }
            }

            foreach (var p in inputs)
            {
                if (assigned.Contains(p.Name))
                    continue;
                if (p.ApplyDefault(op) || p.IsOptional)
                    continue;
                throw new VisionException(ErrorCode.MissingArgument,
                    $"Missing argument '{p.Name}' for '{operatorName}'. Signature: {descriptor.Signature}");
            }

            return op;
        }
    }
}
