using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using InspectionView.Core.Memory;

namespace InspectionView.Core.Backends
{
    /// <summary>
    /// 백엔드 선택 정책: 전역 기본값 + 연산자별 오버라이드.
    /// 실행 시 <see cref="Resolve"/> 가 연산자 이름에 대한 "요청 백엔드" 를 결정하고,
    /// 실제 사용 백엔드는 연산자가 가진 구현과 디바이스 가용성에 따라 <c>OperatorDescriptor.SelectBackend</c> 가 확정한다.
    /// </summary>
    public sealed class BackendSettings
    {
        private readonly ConcurrentDictionary<string, BackendKind> _overrides =
            new ConcurrentDictionary<string, BackendKind>(StringComparer.Ordinal);

        /// <summary>프로세스 전역 설정.</summary>
        public static BackendSettings Global { get; } = new BackendSettings();

        /// <summary>전역 기본 백엔드.</summary>
        public BackendKind DefaultBackend { get; set; } = BackendKind.Auto;

        /// <summary>Parallel 백엔드가 사용할 최대 스레드 수.</summary>
        public int ParallelDegree { get; set; } = Environment.ProcessorCount;

        /// <summary>GPU 할당기. null 이면 GPU 백엔드는 선택되지 않는다.</summary>
        public IDeviceAllocator? Gpu { get; set; }

        /// <summary>OpenCL(OpenCV T-API/UMat) GPU 백엔드 허용 여부. Intel/AMD 내장 GPU 도 사용한다.</summary>
        public bool UseOpenCl { get; set; } = true;

        /// <summary>GPU 사용 가능 여부 (CUDA 할당기 또는 OpenCL 디바이스).</summary>
        public bool IsGpuAvailable => (Gpu != null && Gpu.IsAvailable) || (UseOpenCl && OpenClRuntime.IsAvailable);

        /// <summary>표시용 GPU 설명 (예: "OpenCL: Intel(R) UHD Graphics 630").</summary>
        public string GpuDescription
        {
            get
            {
                if (Gpu != null && Gpu.IsAvailable) return Gpu.Name;
                if (UseOpenCl && OpenClRuntime.IsAvailable) return "OpenCL: " + OpenClRuntime.DeviceName;
                return "not available";
            }
        }

        /// <summary>Parallel 백엔드 허용 여부 (스레드가 2개 이상일 때).</summary>
        public bool IsParallelAvailable => ParallelDegree > 1;

        /// <summary>연산자별 오버라이드 설정. <see cref="BackendKind.Auto"/> 를 주면 오버라이드가 제거된다.</summary>
        public void SetOverride(string operatorName, BackendKind kind)
        {
            if (string.IsNullOrEmpty(operatorName))
                throw new ArgumentException("operatorName must not be empty", nameof(operatorName));

            if (kind == BackendKind.Auto)
                _overrides.TryRemove(operatorName, out _);
            else
                _overrides[operatorName] = kind;
        }

        public bool TryGetOverride(string operatorName, out BackendKind kind)
            => _overrides.TryGetValue(operatorName, out kind);

        public IReadOnlyDictionary<string, BackendKind> Overrides => _overrides;

        public void ClearOverrides() => _overrides.Clear();

        /// <summary>연산자에 대해 요청된 백엔드 (오버라이드 → 전역 기본값).</summary>
        public BackendKind Resolve(string operatorName)
            => _overrides.TryGetValue(operatorName, out var k) ? k : DefaultBackend;

        /// <summary>
        /// 요청 백엔드와 연산자가 제공하는 백엔드 목록으로부터 실제 사용할 백엔드 우선순위를 돌려준다.
        /// 첫 번째 원소가 가장 선호되는 백엔드이며, 사용할 수 없으면 다음으로 폴백한다.
        /// </summary>
        public IEnumerable<BackendKind> PreferenceOrder(BackendKind requested)
        {
            switch (requested)
            {
                case BackendKind.Gpu:
                    if (IsGpuAvailable) yield return BackendKind.Gpu;
                    if (IsParallelAvailable) yield return BackendKind.Parallel;
                    yield return BackendKind.Cpu;
                    break;

                case BackendKind.Parallel:
                    if (IsParallelAvailable) yield return BackendKind.Parallel;
                    yield return BackendKind.Cpu;
                    if (IsGpuAvailable) yield return BackendKind.Gpu;
                    break;

                case BackendKind.Cpu:
                    yield return BackendKind.Cpu;
                    if (IsParallelAvailable) yield return BackendKind.Parallel;
                    if (IsGpuAvailable) yield return BackendKind.Gpu;
                    break;

                case BackendKind.Auto:
                default:
                    if (IsGpuAvailable) yield return BackendKind.Gpu;
                    if (IsParallelAvailable) yield return BackendKind.Parallel;
                    yield return BackendKind.Cpu;
                    break;
            }
        }
    }
}
