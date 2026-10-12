namespace InspectionView.Core.Errors
{
    /// <summary>
    /// 런타임(검사 프로그램)에는 예외 대신 이 코드를 반환하고,
    /// 개발 툴(IDE)에서는 예외로 변환해 해당 줄에서 정지한다.
    /// </summary>
    public enum ErrorCode
    {
        Ok = 0,
        Unknown = 1,

        // 인자
        InvalidArgument = 1000,
        NullArgument = 1001,
        OutOfRange = 1002,
        TypeMismatch = 1003,
        MissingArgument = 1004,

        // 파일
        FileNotFound = 2000,
        FileReadFailed = 2001,
        FileWriteFailed = 2002,
        UnsupportedFormat = 2003,

        // 연산자 / 레지스트리
        OperatorNotFound = 3000,
        BackendNotAvailable = 3001,
        BackendExecutionFailed = 3002,
        InvalidOperatorDefinition = 3003,
        PluginLoadFailed = 3004,

        // 메모리 / 객체
        ObjectDisposed = 4000,
        OutOfMemory = 4001,
        EmptyObject = 4002,
        SizeMismatch = 4003,

        // 디바이스(GPU)
        DeviceError = 5000,
        DeviceNotAvailable = 5001,

        // 실행 제어
        Cancelled = 6000,
    }
}
