using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using InspectionView.Core.Backends;
using InspectionView.Core.Errors;

namespace InspectionView.Core.Registry
{
    /// <summary>하나의 백엔드 구현(메서드).</summary>
    public sealed class BackendDescriptor
    {
        public BackendKind Kind { get; }
        public int Priority { get; }
        public MethodInfo Method { get; }

        internal BackendDescriptor(BackendKind kind, int priority, MethodInfo method)
        {
            Kind = kind;
            Priority = priority;
            Method = method;
        }

        /// <summary>구현 메서드를 호출한다. 내부 예외는 그대로 풀어서 던진다.</summary>
        public void Invoke(OperatorBase op, OperatorContext ctx)
        {
            try
            {
                Method.Invoke(op, new object[] { ctx });
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            }
        }

        public override string ToString() => $"{Kind} ({Method.Name}, priority {Priority})";
    }

    /// <summary>
    /// 연산자 메타데이터. Attribute 리플렉션으로 생성되며 에디터, 노드 UI, Job 직렬화의 단일 정보원이다.
    /// </summary>
    public sealed class OperatorDescriptor
    {
        private static readonly ConcurrentDictionary<Type, OperatorDescriptor> _cache =
            new ConcurrentDictionary<Type, OperatorDescriptor>();

        public string Name { get; }
        public string Category { get; }
        public string Description { get; }
        public Type OperatorType { get; }
        public IReadOnlyList<ParameterDescriptor> Inputs { get; }
        public IReadOnlyList<ParameterDescriptor> Outputs { get; }
        public IReadOnlyList<BackendDescriptor> Backends { get; }

        /// <summary>플러그인 DLL 에서 로드된 연산자인지 (레지스트리가 설정).</summary>
        public bool IsPlugin { get; internal set; }

        public string AssemblyName => OperatorType.Assembly.GetName().Name ?? "";

        private OperatorDescriptor(Type type, OperatorAttribute attr)
        {
            OperatorType = type;
            Name = attr.Name;
            Category = attr.Category;
            Description = attr.Description;

            var props = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                            .OrderBy(p => p.MetadataToken)
                            .ToArray();

            var inputs = new List<ParameterDescriptor>();
            var outputs = new List<ParameterDescriptor>();
            for (int i = 0; i < props.Length; i++)
            {
                var inAttr = props[i].GetCustomAttribute<InputAttribute>();
                if (inAttr != null)
                    inputs.Add(new ParameterDescriptor(props[i], inAttr, i));

                var outAttr = props[i].GetCustomAttribute<OutputAttribute>();
                if (outAttr != null)
                    outputs.Add(new ParameterDescriptor(props[i], outAttr, i));
            }

            Inputs = inputs.OrderBy(p => p.Order).ToArray();
            Outputs = outputs.OrderBy(p => p.Order).ToArray();

            var backends = new List<BackendDescriptor>();
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                foreach (var b in m.GetCustomAttributes<BackendAttribute>())
                {
                    var ps = m.GetParameters();
                    if (m.ReturnType != typeof(void) || ps.Length != 1 || ps[0].ParameterType != typeof(OperatorContext))
                        throw new VisionException(ErrorCode.InvalidOperatorDefinition,
                            $"Backend method {type.Name}.{m.Name} must have signature 'void {m.Name}(OperatorContext ctx)'.");
                    backends.Add(new BackendDescriptor(b.Kind, b.Priority, m));
                }
            }

            if (backends.Count == 0)
                throw new VisionException(ErrorCode.InvalidOperatorDefinition,
                    $"Operator '{Name}' ({type.FullName}) has no [Backend] method.");

            Backends = backends.OrderByDescending(b => b.Priority).ToArray();
        }

        /// <summary>타입에서 디스크립터를 만든다 (캐시됨). 연산자 타입이 아니면 예외.</summary>
        public static OperatorDescriptor Of(Type type)
        {
            if (type == null) throw VisionException.NullArgument(nameof(type));
            return _cache.GetOrAdd(type, Create);
        }

        /// <summary>연산자 타입이면 디스크립터를 돌려주고 아니면 null.</summary>
        public static OperatorDescriptor? TryCreate(Type type)
        {
            if (type == null || type.IsAbstract || !typeof(OperatorBase).IsAssignableFrom(type))
                return null;
            if (type.GetCustomAttribute<OperatorAttribute>() == null)
                return null;
            return Of(type);
        }

        private static OperatorDescriptor Create(Type type)
        {
            var attr = type.GetCustomAttribute<OperatorAttribute>()
                       ?? throw new VisionException(ErrorCode.InvalidOperatorDefinition, $"{type.FullName} has no [Operator] attribute.");
            if (!typeof(OperatorBase).IsAssignableFrom(type) || type.IsAbstract)
                throw new VisionException(ErrorCode.InvalidOperatorDefinition, $"{type.FullName} must be a concrete subclass of OperatorBase.");
            if (type.GetConstructor(Type.EmptyTypes) == null)
                throw new VisionException(ErrorCode.InvalidOperatorDefinition, $"{type.FullName} must have a public parameterless constructor.");

            return new OperatorDescriptor(type, attr);
        }

        /// <summary>연산자 인스턴스 생성.</summary>
        public OperatorBase CreateInstance()
        {
            var op = (OperatorBase)Activator.CreateInstance(OperatorType)!;
            op.Descriptor = this;
            return op;
        }

        public ParameterDescriptor? FindInput(string name)
            => Inputs.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

        public ParameterDescriptor? FindOutput(string name)
            => Outputs.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

        public ParameterDescriptor GetInput(string name)
            => FindInput(name) ?? throw new VisionException(ErrorCode.InvalidArgument, $"Operator '{Name}' has no input '{name}'.");

        public ParameterDescriptor GetOutput(string name)
            => FindOutput(name) ?? throw new VisionException(ErrorCode.InvalidArgument, $"Operator '{Name}' has no output '{name}'.");

        public bool HasBackend(BackendKind kind) => Backends.Any(b => b.Kind == kind);

        /// <summary>
        /// 설정에 따라 실제 사용할 백엔드를 고른다. 요청 백엔드가 없으면 선호 순서대로 폴백한다.
        /// </summary>
        public BackendDescriptor SelectBackend(BackendSettings settings, out BackendKind requested)
        {
            if (settings == null) throw VisionException.NullArgument(nameof(settings));
            requested = settings.Resolve(Name);

            foreach (var kind in settings.PreferenceOrder(requested))
            {
                var found = Backends.FirstOrDefault(b => b.Kind == kind);
                if (found != null)
                    return found;
            }

            if (Backends.Count > 0)
                return Backends[0];

            throw new VisionException(ErrorCode.BackendNotAvailable, $"Operator '{Name}' has no usable backend.");
        }

        /// <summary>에디터 파라미터 힌트용 시그니처 (예: "ReadImage(string FileName) -> Image Image").</summary>
        public string Signature
        {
            get
            {
                var ins = string.Join(", ", Inputs.Select(p => $"{p.TypeDisplayName} {p.Name}" + (p.DefaultValue != null ? $" = {p.DefaultValue}" : "")));
                var outs = string.Join(", ", Outputs.Select(p => $"{p.TypeDisplayName} {p.Name}"));
                return $"{Name}({ins})" + (outs.Length > 0 ? $" -> {outs}" : "");
            }
        }

        public override string ToString() => Signature;
    }
}
