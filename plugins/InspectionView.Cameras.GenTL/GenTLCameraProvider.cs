using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using InspectionView.Core.Cameras;
using InspectionView.Core.Errors;

namespace InspectionView.Cameras.GenTL
{
    /// <summary>
    /// GenTL 공급자: GENICAM_GENTL64_PATH 의 모든 프로듀서(.cti)로 장치를 찾는다. GigE Vision / USB3 Vision / CoaXPress 등
    /// 프로듀서가 설치된 모든 카메라를 벤더와 관계없이 연다 (Basler pylon, Teledyne Spinnaker, Hikrobot MVS, Vieworks 등의 .cti).
    /// <para>
    /// 같은 카메라가 여러 프로듀서에 보이면(예: GigE 카메라는 모든 GEV 프로듀서가 찾는다) 장치 제조사의 프로듀서를 우선하고,
    /// 그것이 없으면 경로 순서상 첫 프로듀서를 쓴다. 열기에 실패하면 다음 프로듀서로 재시도한다.
    /// 특정 프로듀서를 쓰려면 옵션 <c>Producer=이름일부</c> (예: 'Producer=pylon').
    /// </para>
    /// </summary>
    public sealed class GenTLCameraProvider : ICameraProvider
    {
        public const string ProviderName = "GenTL";

        private static readonly object Sync = new object();
        private static List<GenTLProducer>? _producers;
        private static readonly List<string> LoadErrors = new List<string>();

        public string Name => ProviderName;

        public bool IsAvailable => GenTLProducer.FindProducerFiles().Count > 0;

        public string? UnavailableReason => IsAvailable ? null
            : "No GenTL producer (.cti) found. Install a camera SDK/viewer that registers one in GENICAM_GENTL64_PATH (pylon, Spinnaker, MVS, Vieworks VZViewer ...), or set IV_GENTL_PATH to a folder or .cti file.";

        /// <summary>로드된 프로듀서 (처음 호출 때 로드, 실패한 .cti 는 <see cref="ProducerErrors"/>).</summary>
        public static IReadOnlyList<GenTLProducer> Producers
        {
            get
            {
                lock (Sync)
                {
                    if (_producers != null) return _producers;
                    _producers = new List<GenTLProducer>();
                    foreach (var path in GenTLProducer.FindProducerFiles())
                    {
                        try
                        {
                            _producers.Add(GenTLProducer.Load(path));
                        }
                        catch (Exception ex)
                        {
                            LoadErrors.Add($"{Path.GetFileName(path)}: {ex.Message}");
                        }
                    }
                    return _producers;
                }
            }
        }

        public static IReadOnlyList<string> ProducerErrors
        {
            get { lock (Sync) return LoadErrors.ToList(); }
        }

        /// <summary>모든 프로듀서의 장치 (중복 포함). 프로듀서는 병렬로 조회한다 (GigE 탐색이 각각 수백 ms).</summary>
        public static IReadOnlyList<GenTLDeviceEntry> EnumerateAllEntries(int timeoutMs = 500)
        {
            var producers = Producers;
            var perProducer = new List<GenTLDeviceEntry>[producers.Count];
            Parallel.For(0, producers.Count, i =>
            {
                try { perProducer[i] = producers[i].EnumerateDevices(timeoutMs).ToList(); }
                catch { perProducer[i] = new List<GenTLDeviceEntry>(); }
            });
            return perProducer.SelectMany(l => l).ToList();
        }

        /// <summary>장치별로 묶어 선호 순서(제조사 프로듀서 → 경로 순서)로 정렬한다.</summary>
        internal static List<List<GenTLDeviceEntry>> Group(IReadOnlyList<GenTLDeviceEntry> entries)
        {
            var groups = new List<List<GenTLDeviceEntry>>();
            var byKey = new Dictionary<string, List<GenTLDeviceEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries)
            {
                if (!byKey.TryGetValue(e.Key, out var list))
                {
                    list = new List<GenTLDeviceEntry>();
                    byKey[e.Key] = list;
                    groups.Add(list);
                }
                list.Add(e);
            }
            foreach (var g in groups)
            {
                var ordered = g.Select((e, i) => (e, i)).OrderBy(x => x.e.IsVendorProducer ? 0 : 1).ThenBy(x => x.i).Select(x => x.e).ToList();
                g.Clear();
                g.AddRange(ordered);
            }
            return groups;
        }

        /// <summary>CameraInfo.DeviceId: 시리얼 (없으면 모델/장치 ID).</summary>
        internal static string IdOf(GenTLDeviceEntry e) => !string.IsNullOrEmpty(e.Serial) ? e.Serial : e.DeviceId;

        internal static string DisplayNameOf(GenTLDeviceEntry e)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(e.Vendor)) sb.Append(e.Vendor).Append(' ');
            sb.Append(string.IsNullOrEmpty(e.Model) ? e.DeviceId : e.Model);
            sb.Append(" (").Append(string.IsNullOrEmpty(e.Serial) ? e.DeviceId : e.Serial).Append(')');
            if (!string.IsNullOrEmpty(e.UserName)) sb.Append(" '").Append(e.UserName).Append('\'');
            if (!string.IsNullOrEmpty(e.TLType)) sb.Append(' ').Append(e.TLType);
            sb.Append(" [").Append(e.ProducerName).Append(']');
            return sb.ToString();
        }

        public IReadOnlyList<CameraInfo> Enumerate()
        {
            var list = new List<CameraInfo>();
            foreach (var g in Group(EnumerateAllEntries()))
            {
                var e = g[0];
                list.Add(new CameraInfo(ProviderName, IdOf(e), e.Model, e.Serial, DisplayNameOf(e)));
            }
            return list;
        }

        private static bool Matches(GenTLDeviceEntry e, string id)
            => string.Equals(e.Serial, id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(e.UserName, id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(e.DeviceId, id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(e.DisplayName, id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(DisplayNameOf(e), id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(e.Model, id, StringComparison.OrdinalIgnoreCase);

        public ICamera Open(string deviceId, IReadOnlyDictionary<string, string> options)
        {
            if (Producers.Count == 0)
            {
                var errs = ProducerErrors;
                throw new VisionException(ErrorCode.CameraSdkNotAvailable, UnavailableReason ?? $"No GenTL producer could be loaded: {string.Join("; ", errs)}");
            }

            var entries = EnumerateAllEntries();
            if (options.TryGetValue("Producer", out var producer) && !string.IsNullOrWhiteSpace(producer))
                entries = entries.Where(e => e.Producer.Path.IndexOf(producer, StringComparison.OrdinalIgnoreCase) >= 0
                    || e.Producer.Vendor.IndexOf(producer, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            var groups = Group(entries);
            List<GenTLDeviceEntry>? target = string.IsNullOrEmpty(deviceId)
                ? groups.FirstOrDefault()
                : groups.FirstOrDefault(g => g.Any(e => Matches(e, deviceId)));
            if (target == null)
            {
                string known = groups.Count == 0 ? "none" : string.Join(", ", groups.Select(g => DisplayNameOf(g[0])));
                throw new VisionException(ErrorCode.CameraNotFound,
                    string.IsNullOrEmpty(deviceId) ? $"No GenTL camera found (devices: {known})." : $"GenTL camera '{deviceId}' not found (devices: {known}).");
            }

            var errors = new List<string>();
            foreach (var entry in target)
            {
                try
                {
                    return new GenTLCamera(entry, options);
                }
                catch (Exception ex)
                {
                    errors.Add($"[{entry.ProducerName}] {ex.Message}");
                }
            }
            throw new VisionException(ErrorCode.CameraOpenFailed, $"Cannot open '{DisplayNameOf(target[0])}': {string.Join(" | ", errors)}");
        }
    }
}
