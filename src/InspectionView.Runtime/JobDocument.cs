using System;
using System.Collections.Generic;
using InspectionView.Core.Memory;
using InspectionView.Core.Types;

namespace InspectionView.Runtime
{
    /// <summary>
    /// 메모리상의 Job: manifest + 스크립트 + 리소스(Region/Image/Tuple).
    /// 리소스 아이코닉 객체는 문서가 소유한다 (Set 시 AddRef, 제거/Dispose 시 Release).
    /// </summary>
    public sealed class JobDocument : IDisposable
    {
        private readonly Dictionary<string, Region> _regions = new Dictionary<string, Region>(StringComparer.Ordinal);
        private readonly Dictionary<string, Image> _images = new Dictionary<string, Image>(StringComparer.Ordinal);
        private readonly Dictionary<string, TupleValue> _parameters = new Dictionary<string, TupleValue>(StringComparer.Ordinal);

        public JobManifest Manifest { get; set; } = new JobManifest();
        public string Script { get; set; } = "";

        public IReadOnlyDictionary<string, Region> Regions => _regions;
        public IReadOnlyDictionary<string, Image> Images => _images;
        public IReadOnlyDictionary<string, TupleValue> Parameters => _parameters;

        public void SetRegion(string name, Region region)
        {
            CheckName(name);
            if (region == null) throw new ArgumentNullException(nameof(region));
            region.AddRef();
            if (_regions.TryGetValue(name, out var old)) old.Release();
            _regions[name] = region;
        }

        public void SetImage(string name, Image image)
        {
            CheckName(name);
            if (image == null) throw new ArgumentNullException(nameof(image));
            image.AddRef();
            if (_images.TryGetValue(name, out var old)) old.Release();
            _images[name] = image;
        }

        public void SetParameter(string name, TupleValue value)
        {
            CheckName(name);
            _parameters[name] = value ?? TupleValue.Empty;
        }

        public bool RemoveResource(string name)
        {
            bool removed = false;
            if (_regions.TryGetValue(name, out var r)) { r.Release(); _regions.Remove(name); removed = true; }
            if (_images.TryGetValue(name, out var i)) { i.Release(); _images.Remove(name); removed = true; }
            if (_parameters.Remove(name)) removed = true;
            return removed;
        }

        /// <summary>리소스 이름 → 값 (실행 시 변수로 로드).</summary>
        public IEnumerable<KeyValuePair<string, object>> AllResources()
        {
            foreach (var kv in _regions) yield return new KeyValuePair<string, object>(kv.Key, kv.Value);
            foreach (var kv in _images) yield return new KeyValuePair<string, object>(kv.Key, kv.Value);
            foreach (var kv in _parameters) yield return new KeyValuePair<string, object>(kv.Key, kv.Value);
        }

        /// <summary>manifest.Resources 를 현재 리소스 상태로 다시 만든다 (저장 전).</summary>
        public void SyncManifestResources()
        {
            Manifest.Resources.Clear();
            foreach (var kv in _regions)
                Manifest.Resources.Add(new JobResource(kv.Key, TerminalType.Region, $"roi/{kv.Key}.rgn"));
            foreach (var kv in _images)
                Manifest.Resources.Add(new JobResource(kv.Key, TerminalType.Image, $"models/{kv.Key}{JobPackage.ImageExtension(kv.Value)}"));
            foreach (var kv in _parameters)
                Manifest.Resources.Add(new JobResource(kv.Key, TerminalType.Tuple, "params/values.json"));
        }

        private static void CheckName(string name)
        {
            if (string.IsNullOrEmpty(name) || !(char.IsLetter(name[0]) || name[0] == '_'))
                throw new ArgumentException($"'{name}' is not a valid resource name (must be an identifier).", nameof(name));
            foreach (var c in name)
            {
                if (!char.IsLetterOrDigit(c) && c != '_')
                    throw new ArgumentException($"'{name}' is not a valid resource name (must be an identifier).", nameof(name));
            }
        }

        public void Dispose()
        {
            foreach (var r in _regions.Values) r.Release();
            foreach (var i in _images.Values) i.Release();
            _regions.Clear();
            _images.Clear();
            _parameters.Clear();
        }
    }
}
