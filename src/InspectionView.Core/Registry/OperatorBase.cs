using System;
using System.Collections.Generic;
using System.Diagnostics;
using InspectionView.Core.Errors;

namespace InspectionView.Core.Registry
{
    /// <summary>
    /// 모든 연산자의 기반 클래스.
    /// <para>
    /// 연산자는 [Operator] 를 붙인 클래스로 정의하고, 입력은 [Input] 프로퍼티, 출력은 [Output] 프로퍼티,
    /// 구현은 [Backend(...)] 메서드로 선언한다. 예:
    /// <code>
    /// [Operator("Threshold", Category = "Segmentation")]
    /// public sealed class Threshold : OperatorBase
    /// {
    ///     [Input] public Image? Image { get; set; }
    ///     [Input(Default = 128.0)] public double MinGray { get; set; }
    ///     [Input(Default = 255.0)] public double MaxGray { get; set; }
    ///     [Output] public Region? Region { get; private set; }
    ///
    ///     [Backend(BackendKind.Cpu)] void RunCpu(OperatorContext ctx) { ... }
    ///     [Backend(BackendKind.Gpu)] void RunGpu(OperatorContext ctx) { ... }
    /// }
    /// </code>
    /// </para>
    /// </summary>
    public abstract class OperatorBase
    {
        private OperatorDescriptor? _descriptor;

        /// <summary>이 연산자의 메타데이터.</summary>
        public OperatorDescriptor Descriptor
        {
            get => _descriptor ?? (_descriptor = OperatorDescriptor.Of(GetType()));
            internal set => _descriptor = value;
        }

        public string Name => Descriptor.Name;

        /// <summary>마지막 실행 기록.</summary>
        public ExecutionRecord? LastExecution { get; private set; }

        /// <summary>입력 검증. 잘못된 입력은 <see cref="VisionException"/> 으로 던진다.</summary>
        protected virtual void Validate()
        {
        }

        /// <summary>
        /// 연산자를 실행한다. 백엔드 선택 → 검증 → 실행 → 시간 기록.
        /// 오류는 <see cref="VisionException"/> 으로 던지며(IDE 는 해당 줄에서 정지),
        /// 런타임은 <see cref="TryExecute"/> 로 에러 코드를 받는다.
        /// </summary>
        public ExecutionRecord Execute(OperatorContext? ctx = null)
        {
            ctx = ctx ?? OperatorContext.Default;
            var record = new ExecutionRecord(Name);
            var sw = Stopwatch.StartNew();

            try
            {
                ctx.ThrowIfCancelled();
                Validate();

                var backend = Descriptor.SelectBackend(ctx.Settings, out var requested);
                record.Requested = requested;
                record.Used = backend.Kind;

                backend.Invoke(this, ctx);

                record.Code = ErrorCode.Ok;
                return record;
            }
            catch (OperationCanceledException ex)
            {
                record.Code = ErrorCode.Cancelled;
                record.Message = ex.Message;
                throw new VisionException(ErrorCode.Cancelled, "Execution was cancelled.", ex) { OperatorName = Name };
            }
            catch (VisionException ex)
            {
                if (string.IsNullOrEmpty(ex.OperatorName))
                    ex.OperatorName = Name;
                record.Code = ex.Code;
                record.Message = ex.Message;
                throw;
            }
            catch (Exception ex)
            {
                record.Code = ErrorCode.BackendExecutionFailed;
                record.Message = ex.Message;
                throw new VisionException(ErrorCode.BackendExecutionFailed, $"{Name} failed: {ex.Message}", ex) { OperatorName = Name };
            }
            finally
            {
                sw.Stop();
                record.Elapsed = sw.Elapsed;
                LastExecution = record;
                ctx.Profiler.Add(record);
            }
        }

        /// <summary>예외 대신 에러 코드를 반환하는 실행 (검사 프로그램 런타임용).</summary>
        public ErrorCode TryExecute(OperatorContext? ctx, out VisionException? error)
        {
            try
            {
                Execute(ctx);
                error = null;
                return ErrorCode.Ok;
            }
            catch (VisionException ex)
            {
                error = ex;
                return ex.Code;
            }
        }

        /// <summary>이름으로 입력 설정 (타입 변환 포함).</summary>
        public void SetInput(string name, object? value)
            => Descriptor.GetInput(name).SetValue(this, value);

        /// <summary>이름으로 출력 읽기.</summary>
        public object? GetOutput(string name)
            => Descriptor.GetOutput(name).GetValue(this);

        /// <summary>모든 출력을 이름 → 값 으로 돌려준다.</summary>
        public IReadOnlyDictionary<string, object?> GetOutputs()
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var o in Descriptor.Outputs)
                dict[o.Name] = o.GetValue(this);
            return dict;
        }

        /// <summary>기본값이 있는 입력에 기본값을 채운다.</summary>
        public void ApplyDefaults()
        {
            foreach (var p in Descriptor.Inputs)
                p.ApplyDefault(this);
        }

        // ---- 구현에서 자주 쓰는 검증 헬퍼 ----

        protected static T Require<T>(T? value, string paramName) where T : class
            => value ?? throw VisionException.NullArgument(paramName);

        protected static void RequireNotEmpty(string? value, string paramName)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw VisionException.InvalidArgument(paramName, "must not be empty");
        }

        public override string ToString() => Descriptor.Signature;
    }
}
