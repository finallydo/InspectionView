using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using InspectionView.Core.Backends;
using InspectionView.Core.Errors;
using InspectionView.Core.Memory;

namespace InspectionView.Core.Registry
{
    /// <summary>연산자 한 번 실행의 기록. 변수창/프로파일러에 "실행 시간 + 실제 사용된 백엔드" 로 표시된다.</summary>
    public sealed class ExecutionRecord
    {
        public string OperatorName { get; }
        public DateTime StartedAt { get; } = DateTime.Now;
        public BackendKind Requested { get; internal set; } = BackendKind.Auto;
        public BackendKind Used { get; internal set; } = BackendKind.Auto;
        public TimeSpan Elapsed { get; internal set; }
        public ErrorCode Code { get; internal set; } = ErrorCode.Unknown;
        public string? Message { get; internal set; }

        /// <summary>요청과 다른 백엔드로 폴백했는지.</summary>
        public bool FellBack => Requested != BackendKind.Auto && Requested != Used;

        public ExecutionRecord(string operatorName)
        {
            OperatorName = operatorName;
        }

        public override string ToString()
            => $"{OperatorName} [{Used}{(FellBack ? $" (requested {Requested})" : "")}] {Elapsed.TotalMilliseconds:F2} ms {Code}";
    }

    /// <summary>실행 기록 수집기.</summary>
    public sealed class ExecutionProfiler
    {
        private readonly object _lock = new object();
        private readonly List<ExecutionRecord> _records = new List<ExecutionRecord>();

        /// <summary>false 이면 기록하지 않는다 (고속 모드).</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>기록이 추가될 때 발생 (UI 갱신용).</summary>
        public event Action<ExecutionRecord>? Recorded;

        public void Add(ExecutionRecord record)
        {
            if (!Enabled || record == null)
                return;

            lock (_lock)
                _records.Add(record);

            Recorded?.Invoke(record);
        }

        public IReadOnlyList<ExecutionRecord> Records
        {
            get
            {
                lock (_lock)
                    return _records.ToArray();
            }
        }

        public TimeSpan TotalElapsed
        {
            get
            {
                lock (_lock)
                {
                    var total = TimeSpan.Zero;
                    foreach (var r in _records) total += r.Elapsed;
                    return total;
                }
            }
        }

        public void Clear()
        {
            lock (_lock)
                _records.Clear();
        }
    }

    /// <summary>
    /// 연산자 실행 컨텍스트: 백엔드 설정, 취소 토큰, 프로파일러, 로그.
    /// 스크립트 실행 세션(IDE) 또는 Job 실행(런타임)마다 하나씩 만든다.
    /// </summary>
    public sealed class OperatorContext
    {
        /// <summary>전역 설정을 사용하는 기본 컨텍스트.</summary>
        public static OperatorContext Default { get; } = new OperatorContext();

        public BackendSettings Settings { get; }
        public ExecutionProfiler Profiler { get; }
        public CancellationToken Cancellation { get; }

        /// <summary>진단 메시지 출력 (null 이면 무시).</summary>
        public Action<string>? Log { get; set; }

        public IDeviceAllocator? Gpu => Settings.Gpu;
        public int ParallelDegree => Settings.ParallelDegree;

        public OperatorContext(BackendSettings? settings = null, ExecutionProfiler? profiler = null, CancellationToken cancellation = default)
        {
            Settings = settings ?? BackendSettings.Global;
            Profiler = profiler ?? new ExecutionProfiler();
            Cancellation = cancellation;
        }

        /// <summary>Parallel 백엔드용 옵션.</summary>
        public ParallelOptions CreateParallelOptions()
            => new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, ParallelDegree),
                CancellationToken = Cancellation,
            };

        /// <summary>취소 요청이 있으면 VisionException(Cancelled) 을 던진다.</summary>
        public void ThrowIfCancelled()
        {
            if (Cancellation.IsCancellationRequested)
                throw new VisionException(ErrorCode.Cancelled, "Execution was cancelled.");
        }

        /// <summary>GPU 가 필요한 백엔드에서 호출. 없으면 예외.</summary>
        public IDeviceAllocator RequireGpu()
            => Gpu != null && Gpu.IsAvailable
                ? Gpu
                : throw new VisionException(ErrorCode.DeviceNotAvailable, "GPU device is not available.");

        public void Info(string message) => Log?.Invoke(message);
    }
}
