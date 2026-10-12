using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using InspectionView.Core.Errors;

namespace InspectionView.Core.Registry
{
    /// <summary>플러그인 폴더 로드 결과.</summary>
    public sealed class PluginLoadResult
    {
        public List<string> LoadedAssemblies { get; } = new List<string>();
        public List<OperatorDescriptor> Operators { get; } = new List<OperatorDescriptor>();
        public List<(string File, Exception Error)> Failures { get; } = new List<(string, Exception)>();

        public bool HasFailures => Failures.Count > 0;
    }

    /// <summary>
    /// 연산자 레지스트리. 어셈블리를 스캔해 [Operator] 클래스를 자동 등록하고,
    /// Plugins 폴더의 DLL 을 로드한다. 스크립트 인터프리터, 에디터(자동완성/연산자 트리), 노드 UI 가 모두 이것을 사용한다.
    /// </summary>
    public sealed class OperatorRegistry
    {
        private static readonly Lazy<OperatorRegistry> _default = new Lazy<OperatorRegistry>(() =>
        {
            var reg = new OperatorRegistry();
            reg.RegisterAssembly(typeof(OperatorRegistry).Assembly);
            return reg;
        });

        private readonly ConcurrentDictionary<string, OperatorDescriptor> _operators =
            new ConcurrentDictionary<string, OperatorDescriptor>(StringComparer.Ordinal);

        private readonly HashSet<Assembly> _scanned = new HashSet<Assembly>();
        private readonly object _lock = new object();

        /// <summary>Core 연산자가 등록된 기본 레지스트리.</summary>
        public static OperatorRegistry Default => _default.Value;

        /// <summary>연산자가 등록될 때 발생.</summary>
        public event Action<OperatorDescriptor>? Registered;

        public int Count => _operators.Count;

        /// <summary>등록된 모든 연산자 (카테고리, 이름 순).</summary>
        public IEnumerable<OperatorDescriptor> All
            => _operators.Values.OrderBy(d => d.Category, StringComparer.Ordinal).ThenBy(d => d.Name, StringComparer.Ordinal);

        /// <summary>카테고리 목록.</summary>
        public IEnumerable<string> Categories
            => _operators.Values.Select(d => d.Category).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal);

        public IEnumerable<OperatorDescriptor> ByCategory(string category)
            => All.Where(d => string.Equals(d.Category, category, StringComparer.Ordinal));

        public bool Contains(string name) => _operators.ContainsKey(name);

        public bool TryGet(string name, out OperatorDescriptor descriptor)
            => _operators.TryGetValue(name, out descriptor!);

        public OperatorDescriptor Get(string name)
        {
            if (_operators.TryGetValue(name, out var d))
                return d;
            throw new VisionException(ErrorCode.OperatorNotFound, $"Operator '{name}' is not registered.");
        }

        /// <summary>이름으로 연산자 인스턴스를 만든다.</summary>
        public OperatorBase Create(string name) => Get(name).CreateInstance();

        /// <summary>디스크립터를 직접 등록. 같은 이름이 있으면 덮어쓴다 (플러그인이 Core 를 오버라이드 가능).</summary>
        public void Register(OperatorDescriptor descriptor)
        {
            if (descriptor == null) throw VisionException.NullArgument(nameof(descriptor));
            _operators[descriptor.Name] = descriptor;
            Registered?.Invoke(descriptor);
        }

        /// <summary>타입 하나 등록.</summary>
        public OperatorDescriptor Register(Type operatorType)
        {
            var d = OperatorDescriptor.Of(operatorType);
            Register(d);
            return d;
        }

        /// <summary>어셈블리의 [Operator] 타입을 모두 등록하고 개수를 돌려준다.</summary>
        public int RegisterAssembly(Assembly assembly, bool isPlugin = false)
        {
            if (assembly == null) throw VisionException.NullArgument(nameof(assembly));

            lock (_lock)
            {
                if (!_scanned.Add(assembly))
                    return 0;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray()!;
            }

            int count = 0;
            foreach (var t in types)
            {
                var d = OperatorDescriptor.TryCreate(t);
                if (d == null)
                    continue;
                d.IsPlugin = isPlugin;
                Register(d);
                count++;
            }
            return count;
        }

        /// <summary>
        /// 폴더의 *.dll 을 모두 로드해 연산자를 등록한다. 실패한 파일은 결과에 기록하고 계속 진행한다.
        /// </summary>
        public PluginLoadResult LoadPlugins(string folder, string searchPattern = "*.dll")
        {
            var result = new PluginLoadResult();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return result;

            foreach (var file in Directory.GetFiles(folder, searchPattern, SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var asm = LoadAssembly(Path.GetFullPath(file));
                    var added = RegisterAssembly(asm, isPlugin: true);
                    result.LoadedAssemblies.Add(file);
                    if (added > 0)
                        result.Operators.AddRange(_operators.Values.Where(d => d.OperatorType.Assembly == asm));
                }
                catch (Exception ex)
                {
                    result.Failures.Add((file, new VisionException(ErrorCode.PluginLoadFailed, $"Failed to load plugin '{file}': {ex.Message}", ex)));
                }
            }
            return result;
        }

        private static Assembly LoadAssembly(string path)
        {
#if NET5_0_OR_GREATER
            return System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
#else
            return Assembly.LoadFrom(path);
#endif
        }
    }
}
