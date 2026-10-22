using System;
using System.Collections.Generic;
using System.Xml.Linq;
using InspectionView.Core.Errors;

namespace InspectionView.Cameras.GenTL.GenICam
{
    /// <summary>GenICam 레지스터 포트 (원격 장치 메모리). 읽기/쓰기 길이는 버퍼 길이.</summary>
    public interface IRegisterPort
    {
        void Read(long address, byte[] buffer);
        void Write(long address, byte[] buffer);
    }

    /// <summary>
    /// 장치 설명 XML(GenApi 스키마 1.0/1.1)로 만든 노드맵. GenApi 참조 구현의 필요한 부분만 구현한다:
    /// Category, Integer, Float, Boolean, Command, Enumeration, String, IntReg, MaskedIntReg, StructReg, FloatReg, StringReg,
    /// IntSwissKnife, SwissKnife, IntConverter, Converter, Port. 캐시/무효화, 청크, 이벤트 노드는 없다.
    /// 스레드 안전하지 않으므로 호출자가 <see cref="SyncRoot"/> 로 직렬화한다.
    /// </summary>
    public sealed class NodeMap
    {
        private const int MaxDepth = 64;

        private readonly Dictionary<string, Node> _nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
        private readonly IRegisterPort _port;
        [ThreadStatic] private static int _depth;

        public NodeMap(XDocument document, IRegisterPort port)
        {
            _port = port ?? throw new ArgumentNullException(nameof(port));
            var root = document.Root ?? throw new VisionException(ErrorCode.CameraOpenFailed, "Empty GenICam device description.");
            VendorName = (string?)root.Attribute("VendorName") ?? "";
            ModelName = (string?)root.Attribute("ModelName") ?? "";
            AddChildren(root);
        }

        public static NodeMap Parse(string xml, IRegisterPort port) => new NodeMap(XDocument.Parse(xml), port);

        public object SyncRoot { get; } = new object();
        public string VendorName { get; }
        public string ModelName { get; }
        public IEnumerable<Node> Nodes => _nodes.Values;
        public int Count => _nodes.Count;

        private void AddChildren(XElement parent)
        {
            foreach (var e in parent.Elements())
            {
                string kind = e.Name.LocalName;
                if (kind == "Group")
                {
                    AddChildren(e);
                    continue;
                }
                if (kind == "StructReg")
                {
                    foreach (var entry in e.Elements())
                        if (entry.Name.LocalName == "StructEntry")
                            Add(new MaskedIntRegNode(this, entry, e));
                    continue;
                }
                var node = Create(kind, e);
                if (node != null) Add(node);
            }
        }

        private Node? Create(string kind, XElement e)
        {
            switch (kind)
            {
                case "Category": return new CategoryNode(this, e);
                case "Integer": return new IntegerNode(this, e);
                case "Float": return new FloatNode(this, e);
                case "Boolean": return new BooleanNode(this, e);
                case "Command": return new CommandNode(this, e);
                case "Enumeration": return new EnumerationNode(this, e);
                case "String": return new StringNode(this, e);
                case "IntReg": return new IntRegNode(this, e);
                case "MaskedIntReg": return new MaskedIntRegNode(this, e);
                case "FloatReg": return new FloatRegNode(this, e);
                case "StringReg": return new StringRegNode(this, e);
                case "Register": return new RawRegisterNode(this, e);
                case "IntSwissKnife": return new IntSwissKnifeNode(this, e);
                case "SwissKnife": return new SwissKnifeNode(this, e);
                case "IntConverter": return new ConverterNode(this, e, isFloat: false);
                case "Converter": return new ConverterNode(this, e, isFloat: true);
                case "Port": return new PortNode(this, e);
                default: return null;   // ConfRom, TextDesc, IntKey, AdvFeatureLock, SmartFeature 등 구형/미지원
            }
        }

        private void Add(Node node)
        {
            if (node.Name.Length > 0 && !_nodes.ContainsKey(node.Name))
                _nodes.Add(node.Name, node);
        }

        /// <summary>이름으로 찾는다 (정확히 일치 우선, 없으면 대소문자 무시).</summary>
        public Node? Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_nodes.TryGetValue(name, out var n)) return n;
            foreach (var kv in _nodes)
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }

        public T Require<T>(string name) where T : class
        {
            var n = Find(name);
            if (n == null) throw new VisionException(ErrorCode.CameraParameterInvalid, $"Unknown GenICam feature '{name}'.");
            return n as T ?? throw new VisionException(ErrorCode.CameraParameterInvalid, $"GenICam feature '{name}' is a {n.Interface}, not {typeof(T).Name}.");
        }

        internal Num GetNum(string name)
        {
            var node = Require<ValueNode>(name);
            if (++_depth > MaxDepth)
            {
                _depth = 0;
                throw new VisionException(ErrorCode.CameraParameterInvalid, $"GenICam node references are too deep (cycle?) at '{name}'.");
            }
            try { return node.GetValue(); }
            finally { if (_depth > 0) _depth--; }
        }

        internal void SetNum(string name, Num value)
        {
            var node = Require<ValueNode>(name);
            if (++_depth > MaxDepth)
            {
                _depth = 0;
                throw new VisionException(ErrorCode.CameraParameterInvalid, $"GenICam node references are too deep (cycle?) at '{name}'.");
            }
            try
            {
                // 내부 참조(pValue 대상)는 범위/잠금 검사 없이 쓴다: 사용자 노드에서 이미 검사했다
                node.SetValueInternal(value);
            }
            finally { if (_depth > 0) _depth--; }
        }

        internal IRegisterPort PortFor(string? portName)
        {
            if (portName != null && Find(portName) is PortNode p && p.IsChunkPort)
                throw new VisionException(ErrorCode.CameraParameterInvalid, $"Chunk data port '{portName}' is not supported.");
            return _port;
        }
    }
}
