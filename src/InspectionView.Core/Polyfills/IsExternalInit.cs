// net48 에서 init 접근자 / record 를 사용하기 위한 폴리필
#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
#endif
