using System;
using InspectionView.Core.Errors;

namespace InspectionView.Script.Runtime
{
    /// <summary>스크립트 실행 중 오류. 줄 번호를 가지며, 연산자 오류는 <see cref="Code"/> 에 에러 코드를 담는다.</summary>
    public class ScriptRuntimeException : Exception
    {
        public int Line { get; }
        public ErrorCode Code { get; }

        public bool IsCancelled => Code == ErrorCode.Cancelled;

        public ScriptRuntimeException(int line, string message, ErrorCode code = ErrorCode.Unknown, Exception? inner = null)
            : base(message, inner)
        {
            Line = line;
            Code = code;
        }

        public override string ToString() => $"Line {Line}: {Message}";
    }
}
