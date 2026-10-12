using System;

namespace InspectionView.Core.Errors
{
    /// <summary>
    /// 연산자 실행 중 발생하는 모든 오류의 기본 예외.
    /// <see cref="Code"/> 로 런타임용 에러 코드를 함께 전달한다.
    /// </summary>
    public class VisionException : Exception
    {
        public ErrorCode Code { get; }
        public string? OperatorName { get; set; }

        public VisionException(ErrorCode code, string message)
            : base(message)
        {
            Code = code;
        }

        public VisionException(ErrorCode code, string message, Exception? inner)
            : base(message, inner)
        {
            Code = code;
        }

        public override string ToString()
        {
            var op = string.IsNullOrEmpty(OperatorName) ? "" : $"[{OperatorName}] ";
            return $"{op}{Code}: {Message}";
        }

        // ---- 자주 쓰는 팩토리 ----

        public static VisionException InvalidArgument(string paramName, string reason)
            => new VisionException(ErrorCode.InvalidArgument, $"Invalid argument '{paramName}': {reason}");

        public static VisionException NullArgument(string paramName)
            => new VisionException(ErrorCode.NullArgument, $"Argument '{paramName}' must not be null.");

        public static VisionException OutOfRange(string paramName, object value, object min, object max)
            => new VisionException(ErrorCode.OutOfRange, $"Argument '{paramName}' = {value} is out of range [{min}, {max}].");

        public static VisionException TypeMismatch(string paramName, Type expected, Type? actual)
            => new VisionException(ErrorCode.TypeMismatch,
                $"Argument '{paramName}' expects {expected.Name} but got {(actual == null ? "null" : actual.Name)}.");

        public static VisionException FileNotFound(string path)
            => new VisionException(ErrorCode.FileNotFound, $"File not found: {path}");

        public static VisionException Disposed(string typeName)
            => new VisionException(ErrorCode.ObjectDisposed, $"{typeName} has already been released.");
    }
}
