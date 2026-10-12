using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using InspectionView.Core.Errors;

namespace InspectionView.Core.Cameras
{
    /// <summary>
    /// 카메라 공급자 레지스트리. 기본으로 File / Simulation 공급자가 등록되며, 플러그인 DLL(Plugins 폴더) 의
    /// <see cref="ICameraProvider"/> 구현(매개변수 없는 생성자)은 <see cref="ScanAssembly"/> 로 추가된다
    /// (<c>OperatorRegistry.LoadPlugins</c> 가 호출).
    /// </summary>
    public sealed class CameraRegistry
    {
        private static readonly Lazy<CameraRegistry> _default = new Lazy<CameraRegistry>(() =>
        {
            var r = new CameraRegistry();
            r.Register(new FileCameraProvider());
            r.Register(new SimulationCameraProvider());
            return r;
        });

        public static CameraRegistry Default => _default.Value;

        private readonly object _lock = new object();
        private readonly Dictionary<string, ICameraProvider> _providers = new Dictionary<string, ICameraProvider>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<Assembly> _scanned = new HashSet<Assembly>();

        public IReadOnlyList<ICameraProvider> Providers
        {
            get { lock (_lock) return _providers.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(); }
        }

        public void Register(ICameraProvider provider)
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            lock (_lock) _providers[provider.Name] = provider;
        }

        public ICameraProvider? Find(string name)
        {
            lock (_lock)
                return _providers.TryGetValue(name ?? "", out var p) ? p : null;
        }

        public ICameraProvider Require(string name)
        {
            var p = Find(name);
            if (p == null)
                throw new VisionException(ErrorCode.CameraProviderNotFound, $"Unknown camera provider '{name}'. Available: {string.Join(", ", Providers.Select(x => x.Name))}.");
            return p;
        }

        /// <summary>어셈블리의 <see cref="ICameraProvider"/> 구현을 등록하고 개수를 돌려준다.</summary>
        public int ScanAssembly(Assembly assembly)
        {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            lock (_lock)
            {
                if (!_scanned.Add(assembly)) return 0;
            }

            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

            int count = 0;
            foreach (var t in types)
            {
                if (t.IsAbstract || !typeof(ICameraProvider).IsAssignableFrom(t)) continue;
                if (t.GetConstructor(Type.EmptyTypes) == null) continue;
                try
                {
                    Register((ICameraProvider)Activator.CreateInstance(t)!);
                    count++;
                }
                catch
                {
                    // 생성 실패한 공급자는 건너뜀 (SDK 로드 실패 등)
                }
            }
            return count;
        }

        /// <summary>모든 공급자의 장치 목록 (사용 불가 공급자는 비움).</summary>
        public IReadOnlyList<CameraInfo> EnumerateAll()
        {
            var list = new List<CameraInfo>();
            foreach (var p in Providers)
            {
                if (!p.IsAvailable) continue;
                try { list.AddRange(p.Enumerate()); } catch { /* 열거 실패 무시 */ }
            }
            return list;
        }

        /// <summary>'Key=Value;Key2=Value2' 옵션 문자열 파싱.</summary>
        public static IReadOnlyDictionary<string, string> ParseOptions(string? options)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(options)) return dict;
            foreach (var part in options!.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq < 0) dict[part.Trim()] = "true";
                else dict[part.Substring(0, eq).Trim()] = part.Substring(eq + 1).Trim();
            }
            return dict;
        }

        /// <summary>공급자 이름과 장치 식별자로 카메라를 연다.</summary>
        public ICamera Open(string provider, string deviceId, string? options = null)
        {
            var p = Require(provider);
            if (!p.IsAvailable)
                throw new VisionException(ErrorCode.CameraSdkNotAvailable, $"Camera provider '{p.Name}' is not available: {p.UnavailableReason ?? "SDK not found"}");
            return p.Open(deviceId ?? "", ParseOptions(options));
        }
    }
}
