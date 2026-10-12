namespace InspectionView.Core.Backends
{
    /// <summary>연산자 구현 백엔드.</summary>
    public enum BackendKind
    {
        /// <summary>설정과 가용성에 따라 자동 선택 (Gpu → Parallel → Cpu 순).</summary>
        Auto = 0,

        /// <summary>단일 스레드 CPU (OpenCV 또는 직접 구현).</summary>
        Cpu = 1,

        /// <summary>Parallel.For 등 멀티스레드 CPU 구현.</summary>
        Parallel = 2,

        /// <summary>자체 C++/CUDA DLL (P/Invoke).</summary>
        Gpu = 3,
    }
}
