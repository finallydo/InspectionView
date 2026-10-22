using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using InspectionView.Core.Errors;

namespace InspectionView.Cameras.GenTL.GenICam
{
    /// <summary>GenApi 의 주 인터페이스 (사용자에게 보이는 값의 종류).</summary>
    public enum NodeInterface { Category, Integer, Float, Boolean, Command, Enumeration, EnumEntry, String, Register, Port }

    public enum AccessMode { NA, RO, WO, RW }

    /// <summary>
    /// GenICam 노드 공통: 이름/설명/가시성, pIsImplemented/pIsAvailable/pIsLocked, ImposedAccessMode.
    /// 값은 캐시하지 않고 매번 장치 레지스터를 읽는다 (GigE 왕복 ~1ms, 파라미터 수십 개 수준이라 충분).
    /// </summary>
    public abstract class Node
    {
        private readonly XElement? _fallback;

        internal Node(NodeMap map, XElement element, XElement? fallback = null)
        {
            Map = map;
            Element = element;
            _fallback = fallback;
            Name = (string?)element.Attribute("Name") ?? "";
        }

        public NodeMap Map { get; }
        public string Name { get; }
        internal XElement Element { get; }

        public abstract NodeInterface Interface { get; }

        public string Description => Text("Description") ?? Text("ToolTip") ?? "";
        public string DisplayName => Text("DisplayName") ?? Name;
        /// <summary>Beginner / Expert / Guru / Invisible.</summary>
        public string Visibility => Text("Visibility") ?? "Beginner";

        // ------------------------------------------------------------------ XML 도우미

        /// <summary>자식 요소 텍스트 (StructEntry 는 없으면 부모 StructReg 에서 찾는다).</summary>
        internal string? Text(string localName)
        {
            var e = First(Element, localName) ?? (_fallback != null ? First(_fallback, localName) : null);
            return e?.Value.Trim();
        }

        internal IEnumerable<XElement> All(string localName)
        {
            var own = Element.Elements().Where(e => e.Name.LocalName == localName).ToList();
            if (own.Count > 0 || _fallback == null) return own;
            return _fallback.Elements().Where(e => e.Name.LocalName == localName).ToList();
        }

        internal bool Has(string localName) => Text(localName) != null;

        private static XElement? First(XElement parent, string localName)
        {
            foreach (var e in parent.Elements())
                if (e.Name.LocalName == localName) return e;
            return null;
        }

        /// <summary>pX(노드 참조) 가 있으면 그 값, 없으면 X(리터럴), 둘 다 없으면 null.</summary>
        internal Num? PointerOrLiteral(string pointerName, string literalName, bool asFloat)
        {
            string? p = Text(pointerName);
            if (p != null) return Map.GetNum(p);
            string? lit = Text(literalName);
            if (lit == null) return null;
            return asFloat ? Num.Real(ParseDouble(lit)) : Num.Int(ParseLong(lit));
        }

        internal static long ParseLong(string s)
        {
            s = s.Trim();
            bool neg = s.StartsWith("-", StringComparison.Ordinal);
            if (neg || s.StartsWith("+", StringComparison.Ordinal)) s = s.Substring(1);
            ulong u = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? ulong.Parse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : ulong.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
            long v = unchecked((long)u);
            return neg ? -v : v;
        }

        internal static double ParseDouble(string s)
        {
            s = s.Trim();
            switch (s.ToUpperInvariant())
            {
                case "INF": case "+INF": return double.PositiveInfinity;
                case "-INF": return double.NegativeInfinity;
                case "NAN": return double.NaN;
            }
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || s.StartsWith("-0x", StringComparison.OrdinalIgnoreCase))
                return ParseLong(s);
            return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        internal static AccessMode ParseAccess(string? s)
        {
            switch (s)
            {
                case "RO": return AccessMode.RO;
                case "WO": return AccessMode.WO;
                case "NA": return AccessMode.NA;
                default: return AccessMode.RW;
            }
        }

        /// <summary>두 접근 모드의 교집합 (RW∧RO=RO, RW∧WO=WO, RO∧WO=NA).</summary>
        internal static AccessMode Combine(AccessMode a, AccessMode b)
        {
            if (a == b) return a;
            if (a == AccessMode.RW) return b;
            if (b == AccessMode.RW) return a;
            return AccessMode.NA;
        }

        // ------------------------------------------------------------------ 상태

        /// <summary>pIsImplemented/pIsAvailable 은 모두 참이어야 참, pIsLocked 는 하나라도 참이면 참.</summary>
        private bool Flags(string pointerName, bool whenMissing)
        {
            int count = 0;
            bool any = false, all = true;
            foreach (var e in All(pointerName))
            {
                bool v = Map.GetNum(e.Value.Trim()).AsBool;
                any |= v;
                all &= v;
                count++;
            }
            if (count == 0) return whenMissing;
            return whenMissing ? all : any;
        }

        public virtual bool IsImplemented => Flags("pIsImplemented", true);
        public virtual bool IsAvailable => Flags("pIsAvailable", true);
        public virtual bool IsLocked => Flags("pIsLocked", false);

        /// <summary>값 원본(레지스터, pValue 대상)의 접근 모드.</summary>
        protected virtual AccessMode BaseAccess => AccessMode.RW;

        public AccessMode Access
        {
            get
            {
                if (!IsImplemented || !IsAvailable) return AccessMode.NA;
                var mode = BaseAccess;
                string? imposed = Text("ImposedAccessMode");
                if (imposed != null) mode = Combine(mode, ParseAccess(imposed));
                if (IsLocked)
                    mode = mode == AccessMode.RW ? AccessMode.RO : mode == AccessMode.WO ? AccessMode.NA : mode;
                return mode;
            }
        }

        public bool IsReadable => Access == AccessMode.RO || Access == AccessMode.RW;
        public bool IsWritable => Access == AccessMode.WO || Access == AccessMode.RW;

        internal void EnsureWritable()
        {
            if (!IsImplemented) throw new VisionException(ErrorCode.CameraParameterInvalid, $"Feature '{Name}' is not implemented.");
            if (!IsAvailable) throw new VisionException(ErrorCode.CameraParameterInvalid, $"Feature '{Name}' is not available in the current state.");
            if (IsLocked) throw new VisionException(ErrorCode.CameraParameterInvalid, $"Feature '{Name}' is locked (stop acquisition first).");
            if (!IsWritable) throw new VisionException(ErrorCode.CameraParameterInvalid, $"Feature '{Name}' is read-only.");
        }

        public override string ToString() => $"{Interface} {Name}";
    }

    /// <summary>숫자 값을 가진 노드 (Integer/Float/Boolean/Enumeration/레지스터/SwissKnife/Converter).</summary>
    public abstract class ValueNode : Node
    {
        internal ValueNode(NodeMap map, XElement element, XElement? fallback = null) : base(map, element, fallback) { }

        /// <summary>실수 값인지 (IFloat).</summary>
        public virtual bool IsFloat => false;

        public abstract Num GetValue();
        public abstract void SetValue(Num value);

        /// <summary>다른 노드가 pValue 로 참조해 쓸 때 (사용자 노드에서 이미 검사했으므로 범위/잠금 검사 없음).</summary>
        internal virtual void SetValueInternal(Num value) => SetValue(value);

        public virtual Num GetMin() => IsFloat ? Num.Real(double.NegativeInfinity) : Num.Int(long.MinValue);
        public virtual Num GetMax() => IsFloat ? Num.Real(double.PositiveInfinity) : Num.Int(long.MaxValue);
        public virtual Num GetInc() => IsFloat ? Num.Real(0) : Num.Int(1);

        public long GetInt() => GetValue().AsLong;
        public double GetFloat() => GetValue().AsDouble;

        internal static long ToLong(Num v) => v.IsInt ? v.L : (long)Math.Round(v.D, MidpointRounding.AwayFromZero);

        internal void CheckRange(Num v)
        {
            if (IsFloat)
            {
                double x = v.AsDouble, min = GetMin().AsDouble, max = GetMax().AsDouble;
                double tol = 1e-9 * Math.Max(1.0, Math.Max(Math.Abs(min), Math.Abs(max)));
                if (x < min - tol) throw new VisionException(ErrorCode.CameraParameterInvalid, $"'{Name}' = {x.ToString(CultureInfo.InvariantCulture)} is below the minimum {min.ToString(CultureInfo.InvariantCulture)}.");
                if (x > max + tol) throw new VisionException(ErrorCode.CameraParameterInvalid, $"'{Name}' = {x.ToString(CultureInfo.InvariantCulture)} is above the maximum {max.ToString(CultureInfo.InvariantCulture)}.");
            }
            else
            {
                long x = ToLong(v), min = GetMin().AsLong, max = GetMax().AsLong, inc = GetInc().AsLong;
                if (x < min) throw new VisionException(ErrorCode.CameraParameterInvalid, $"'{Name}' = {x} is below the minimum {min}.");
                if (x > max) throw new VisionException(ErrorCode.CameraParameterInvalid, $"'{Name}' = {x} is above the maximum {max}.");
                if (inc > 1 && (x - min) % inc != 0) throw new VisionException(ErrorCode.CameraParameterInvalid, $"'{Name}' = {x} must be {min} + n * {inc}.");
            }
        }
    }

    /// <summary>문자열 값을 가진 노드 (String, StringReg).</summary>
    public interface IStringValue
    {
        string GetString();
        void SetString(string value);
    }

    // ====================================================================== 사용자 노드

    /// <summary>
    /// Integer / Float 의 값 원본: <c>Value</c>(메모리), <c>pValue</c>(노드), 또는 <c>pIndex</c> + <c>pValueIndexed</c>/<c>ValueIndexed</c>(선택자별 값).
    /// </summary>
    public abstract class NumericFeatureNode : ValueNode
    {
        private Num _stored;

        internal NumericFeatureNode(NodeMap map, XElement element) : base(map, element)
        {
            string? lit = Text("Value");
            _stored = lit == null ? Num.Int(0) : (IsFloatLiteral ? Num.Real(ParseDouble(lit)) : Num.Int(ParseLong(lit)));
        }

        protected abstract bool IsFloatLiteral { get; }

        /// <summary>현재 값을 가리키는 노드 이름 (없으면 null = 리터럴/메모리 값).</summary>
        private string? CurrentPointer(out Num? indexedLiteral)
        {
            indexedLiteral = null;
            string? index = Text("pIndex");
            if (index == null) return Text("pValue");

            long idx = Map.GetNum(index).AsLong;
            foreach (var e in Element.Elements())
            {
                string ln = e.Name.LocalName;
                if (ln != "pValueIndexed" && ln != "ValueIndexed") continue;
                string? at = (string?)e.Attribute("Index");
                if (at == null || ParseLong(at) != idx) continue;
                if (ln == "pValueIndexed") return e.Value.Trim();
                indexedLiteral = IsFloatLiteral ? Num.Real(ParseDouble(e.Value)) : Num.Int(ParseLong(e.Value));
                return null;
            }
            string? def = Text("pValueDefault");
            if (def != null) return def;
            string? defLit = Text("ValueDefault");
            if (defLit != null) indexedLiteral = IsFloatLiteral ? Num.Real(ParseDouble(defLit)) : Num.Int(ParseLong(defLit));
            return null;
        }

        protected Num RawGet()
        {
            string? p = CurrentPointer(out var lit);
            if (p != null) return Map.GetNum(p);
            return lit ?? _stored;
        }

        protected void RawSet(Num v)
        {
            string? p = CurrentPointer(out var lit);
            if (p != null) { Map.SetNum(p, v); return; }
            if (lit != null) throw new VisionException(ErrorCode.CameraParameterInvalid, $"Feature '{Name}' has a constant value for the current selector.");
            _stored = v;
        }

        protected ValueNode? PointerNode => CurrentPointer(out _) is string p ? Map.Find(p) as ValueNode : null;

        protected override AccessMode BaseAccess
        {
            get
            {
                string? p = CurrentPointer(out var lit);
                if (p != null) return Map.Find(p)?.Access ?? AccessMode.NA;
                return lit != null ? AccessMode.RO : AccessMode.RW;
            }
        }
    }

    public sealed class IntegerNode : NumericFeatureNode
    {
        internal IntegerNode(NodeMap map, XElement element) : base(map, element) { }

        public override NodeInterface Interface => NodeInterface.Integer;
        protected override bool IsFloatLiteral => false;

        public override Num GetValue() => Num.Int(ToLong(RawGet()));

        public override void SetValue(Num value)
        {
            EnsureWritable();
            var v = Num.Int(ToLong(value));
            CheckRange(v);
            RawSet(v);
        }

        internal override void SetValueInternal(Num value) => RawSet(Num.Int(ToLong(value)));

        public override Num GetMin() => Num.Int((PointerOrLiteral("pMin", "Min", false) ?? PointerNode?.GetMin() ?? Num.Int(long.MinValue)).AsLong);
        public override Num GetMax() => Num.Int((PointerOrLiteral("pMax", "Max", false) ?? PointerNode?.GetMax() ?? Num.Int(long.MaxValue)).AsLong);
        public override Num GetInc()
        {
            long inc = (PointerOrLiteral("pInc", "Inc", false) ?? PointerNode?.GetInc() ?? Num.Int(1)).AsLong;
            return Num.Int(inc <= 0 ? 1 : inc);
        }
    }

    public sealed class FloatNode : NumericFeatureNode
    {
        internal FloatNode(NodeMap map, XElement element) : base(map, element) { }

        public override NodeInterface Interface => NodeInterface.Float;
        public override bool IsFloat => true;
        protected override bool IsFloatLiteral => true;

        public string Unit => Text("Unit") ?? "";

        public override Num GetValue() => Num.Real(RawGet().AsDouble);

        public override void SetValue(Num value)
        {
            EnsureWritable();
            var v = Num.Real(value.AsDouble);
            CheckRange(v);
            RawSet(v);
        }

        internal override void SetValueInternal(Num value) => RawSet(Num.Real(value.AsDouble));

        public override Num GetMin() => Num.Real((PointerOrLiteral("pMin", "Min", true) ?? PointerNode?.GetMin() ?? Num.Real(double.NegativeInfinity)).AsDouble);
        public override Num GetMax() => Num.Real((PointerOrLiteral("pMax", "Max", true) ?? PointerNode?.GetMax() ?? Num.Real(double.PositiveInfinity)).AsDouble);
    }

    public sealed class BooleanNode : ValueNode
    {
        private long _stored;

        internal BooleanNode(NodeMap map, XElement element) : base(map, element)
        {
            string? lit = Text("Value");
            if (lit != null)
                _stored = lit.Equals("true", StringComparison.OrdinalIgnoreCase) ? OnValue : lit.Equals("false", StringComparison.OrdinalIgnoreCase) ? OffValue : ParseLong(lit);
        }

        public override NodeInterface Interface => NodeInterface.Boolean;

        private long OnValue => Text("OnValue") is string s ? ParseLong(s) : 1;
        private long OffValue => Text("OffValue") is string s ? ParseLong(s) : 0;

        public bool GetBool()
        {
            long raw = Text("pValue") is string p ? Map.GetNum(p).AsLong : _stored;
            return raw == OnValue;
        }

        public void SetBool(bool value)
        {
            EnsureWritable();
            long raw = value ? OnValue : OffValue;
            if (Text("pValue") is string p) Map.SetNum(p, Num.Int(raw));
            else _stored = raw;
        }

        public override Num GetValue() => Num.Bool(GetBool());
        public override void SetValue(Num value) => SetBool(value.AsBool);
        public override Num GetMin() => Num.Int(0);
        public override Num GetMax() => Num.Int(1);

        protected override AccessMode BaseAccess => Text("pValue") is string p ? Map.Find(p)?.Access ?? AccessMode.NA : AccessMode.RW;
    }

    public sealed class CommandNode : ValueNode
    {
        internal CommandNode(NodeMap map, XElement element) : base(map, element) { }

        public override NodeInterface Interface => NodeInterface.Command;

        private long CommandValue => (PointerOrLiteral("pCommandValue", "CommandValue", false) ?? Num.Int(1)).AsLong;

        public void Execute()
        {
            EnsureWritable();
            string? p = Text("pValue");
            if (p == null) throw new VisionException(ErrorCode.CameraParameterInvalid, $"Command '{Name}' has no target register.");
            Map.SetNum(p, Num.Int(CommandValue));
        }

        /// <summary>명령 레지스터가 아직 명령 값을 갖고 있으면 실행 중.</summary>
        public bool IsDone
        {
            get
            {
                try { return Text("pValue") is string p ? Map.GetNum(p).AsLong != CommandValue : true; }
                catch { return true; }
            }
        }

        public override Num GetValue() => Num.Bool(!IsDone);
        public override void SetValue(Num value) => Execute();

        protected override AccessMode BaseAccess
        {
            get
            {
                string? p = Text("pValue");
                if (p == null) return AccessMode.NA;
                var a = Map.Find(p)?.Access ?? AccessMode.NA;
                return a == AccessMode.RO ? AccessMode.NA : a;
            }
        }
    }

    public sealed class EnumEntryNode : Node
    {
        private readonly string _enumName;

        internal EnumEntryNode(NodeMap map, XElement element, string enumName) : base(map, element)
        {
            _enumName = enumName;
            Value = Text("Value") is string v ? ParseLong(v) : 0;
        }

        public override NodeInterface Interface => NodeInterface.EnumEntry;
        public long Value { get; }

        /// <summary>
        /// 사용자에게 보이는 이름: <c>Symbolic</c> 요소, 없으면 Name (생성기 접두사 <c>EnumEntry_&lt;Enum&gt;_</c> 제거).
        /// </summary>
        public string Symbol
        {
            get
            {
                string? sym = Text("Symbolic");
                if (!string.IsNullOrEmpty(sym)) return sym!;
                string prefix = "EnumEntry_" + _enumName + "_";
                return Name.StartsWith(prefix, StringComparison.Ordinal) ? Name.Substring(prefix.Length) : Name;
            }
        }
    }

    public sealed class EnumerationNode : ValueNode
    {
        private long _stored;

        internal EnumerationNode(NodeMap map, XElement element) : base(map, element)
        {
            Entries = element.Elements().Where(e => e.Name.LocalName == "EnumEntry")
                .Select(e => new EnumEntryNode(map, e, Name)).ToList();
            if (Text("Value") is string lit) _stored = ParseLong(lit);
        }

        public override NodeInterface Interface => NodeInterface.Enumeration;
        public IReadOnlyList<EnumEntryNode> Entries { get; }

        public override Num GetValue() => Num.Int(Text("pValue") is string p ? Map.GetNum(p).AsLong : _stored);

        public override void SetValue(Num value)
        {
            EnsureWritable();
            long v = ToLong(value);
            if (Text("pValue") is string p) Map.SetNum(p, Num.Int(v));
            else _stored = v;
        }

        public EnumEntryNode? CurrentEntry
        {
            get
            {
                long v = GetValue().AsLong;
                foreach (var e in Entries)
                    if (e.Value == v) return e;
                return null;
            }
        }

        public EnumEntryNode? FindEntry(string symbol)
        {
            foreach (var e in Entries)
                if (string.Equals(e.Symbol, symbol, StringComparison.Ordinal)) return e;
            foreach (var e in Entries)
                if (string.Equals(e.Symbol, symbol, StringComparison.OrdinalIgnoreCase) || string.Equals(e.Name, symbol, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        /// <summary>현재 상태에서 선택 가능한 항목 이름.</summary>
        public IReadOnlyList<string> AvailableSymbols
        {
            get
            {
                var list = new List<string>();
                foreach (var e in Entries)
                {
                    try
                    {
                        if (e.IsImplemented && e.IsAvailable) list.Add(e.Symbol);
                    }
                    catch
                    {
                        // 가용성 평가 실패 항목은 숨긴다
                    }
                }
                return list;
            }
        }

        public string GetSymbol()
        {
            var e = CurrentEntry;
            return e != null ? e.Symbol : GetValue().AsLong.ToString(CultureInfo.InvariantCulture);
        }

        public void SetSymbol(string symbol)
        {
            var e = FindEntry(symbol);
            if (e == null)
                throw new VisionException(ErrorCode.CameraParameterInvalid, $"'{symbol}' is not a value of '{Name}' (choices: {string.Join(", ", AvailableSymbols)}).");
            if (!e.IsImplemented || !e.IsAvailable)
                throw new VisionException(ErrorCode.CameraParameterInvalid, $"'{symbol}' is not available for '{Name}' in the current state.");
            SetValue(Num.Int(e.Value));
        }

        protected override AccessMode BaseAccess => Text("pValue") is string p ? Map.Find(p)?.Access ?? AccessMode.NA : AccessMode.RW;
    }

    public sealed class StringNode : Node, IStringValue
    {
        private string _stored;

        internal StringNode(NodeMap map, XElement element) : base(map, element)
        {
            _stored = Text("Value") ?? "";
        }

        public override NodeInterface Interface => NodeInterface.String;

        public string GetString() => Text("pValue") is string p ? Map.Require<IStringValue>(p).GetString() : _stored;

        public void SetString(string value)
        {
            EnsureWritable();
            if (Text("pValue") is string p) Map.Require<IStringValue>(p).SetString(value);
            else _stored = value ?? "";
        }

        protected override AccessMode BaseAccess => Text("pValue") is string p ? Map.Find(p)?.Access ?? AccessMode.NA : AccessMode.RW;
    }

    public sealed class CategoryNode : Node
    {
        internal CategoryNode(NodeMap map, XElement element) : base(map, element) { }

        public override NodeInterface Interface => NodeInterface.Category;
        public IReadOnlyList<string> Features => All("pFeature").Select(e => e.Value.Trim()).ToList();
    }

    public sealed class PortNode : Node
    {
        internal PortNode(NodeMap map, XElement element) : base(map, element) { }

        public override NodeInterface Interface => NodeInterface.Port;
        /// <summary>청크 데이터 포트 (지원 안 함).</summary>
        public bool IsChunkPort => Has("pChunkID") || Has("ChunkID");
    }

    // ====================================================================== 레지스터

    /// <summary>레지스터 노드 공통: 주소(Address/pAddress/내장 IntSwissKnife/pIndex), 길이, 포트, 엔디안, 접근 모드.</summary>
    internal sealed class RegisterAccess
    {
        private readonly Node _owner;
        private readonly List<IntSwissKnifeNode> _addressKnives = new List<IntSwissKnifeNode>();

        public RegisterAccess(Node owner)
        {
            _owner = owner;
            foreach (var e in owner.All("IntSwissKnife"))
                _addressKnives.Add(new IntSwissKnifeNode(owner.Map, e));
        }

        public long Address
        {
            get
            {
                long addr = 0;
                foreach (var e in _owner.All("Address")) addr += Node.ParseLong(e.Value);
                foreach (var e in _owner.All("pAddress")) addr += _owner.Map.GetNum(e.Value.Trim()).AsLong;
                foreach (var k in _addressKnives) addr += k.GetValue().AsLong;
                foreach (var e in _owner.All("pIndex"))
                {
                    long index = _owner.Map.GetNum(e.Value.Trim()).AsLong;
                    long offset = (string?)e.Attribute("Offset") is string o ? Node.ParseLong(o)
                        : (string?)e.Attribute("pOffset") is string po ? _owner.Map.GetNum(po).AsLong
                        : 1;
                    addr += index * offset;
                }
                return addr;
            }
        }

        public int Length => (int)(_owner.PointerOrLiteral("pLength", "Length", false) ?? Num.Int(4)).AsLong;
        public bool BigEndian => string.Equals(_owner.Text("Endianess"), "BigEndian", StringComparison.Ordinal);
        public AccessMode Mode => Node.ParseAccess(_owner.Text("AccessMode"));

        /// <summary>NoCache 가 아니면 <see cref="NodeMap.Batch"/> 안에서 같은 주소를 다시 읽지 않는다.</summary>
        private bool Cachable => !string.Equals(_owner.Text("Cachable"), "NoCache", StringComparison.Ordinal);

        public byte[] Read() => _owner.Map.ReadRegister(_owner.Text("pPort"), Address, Length, Cachable);

        public void Write(byte[] data) => _owner.Map.WriteRegister(_owner.Text("pPort"), Address, data);

        public static ulong ToUInt(byte[] b, bool bigEndian)
        {
            ulong v = 0;
            int n = Math.Min(b.Length, 8);
            if (bigEndian)
                for (int i = b.Length - n; i < b.Length; i++) v = (v << 8) | b[i];
            else
                for (int i = n - 1; i >= 0; i--) v = (v << 8) | b[i];
            return v;
        }

        public static byte[] FromUInt(ulong v, int length, bool bigEndian)
        {
            var b = new byte[length];
            for (int i = 0; i < length && i < 8; i++)
            {
                byte x = (byte)(v >> (8 * i));
                if (bigEndian) b[length - 1 - i] = x; else b[i] = x;
            }
            return b;
        }

        public static long SignExtend(ulong v, int bits)
        {
            if (bits >= 64) return unchecked((long)v);
            ulong sign = 1UL << (bits - 1);
            if ((v & sign) != 0) v |= ~0UL << bits;
            return unchecked((long)v);
        }

        public static (long min, long max) Range(int bits, bool signed)
        {
            if (bits >= 64) return signed ? (long.MinValue, long.MaxValue) : (0, long.MaxValue);
            if (signed) return (-(1L << (bits - 1)), (1L << (bits - 1)) - 1);
            return (0, bits == 63 ? long.MaxValue : (1L << bits) - 1);
        }
    }

    public sealed class IntRegNode : ValueNode
    {
        private readonly RegisterAccess _reg;

        internal IntRegNode(NodeMap map, XElement element) : base(map, element)
        {
            _reg = new RegisterAccess(this);
        }

        public override NodeInterface Interface => NodeInterface.Integer;
        private bool Signed => string.Equals(Text("Sign"), "Signed", StringComparison.Ordinal);

        public override Num GetValue()
        {
            var b = _reg.Read();
            ulong u = RegisterAccess.ToUInt(b, _reg.BigEndian);
            return Num.Int(Signed ? RegisterAccess.SignExtend(u, b.Length * 8) : unchecked((long)u));
        }

        public override void SetValue(Num value)
            => _reg.Write(RegisterAccess.FromUInt(unchecked((ulong)ToLong(value)), _reg.Length, _reg.BigEndian));

        public override Num GetMin() => Num.Int(RegisterAccess.Range(_reg.Length * 8, Signed).min);
        public override Num GetMax() => Num.Int(RegisterAccess.Range(_reg.Length * 8, Signed).max);
        protected override AccessMode BaseAccess => _reg.Mode;
    }

    /// <summary>
    /// 레지스터의 비트 필드 (MaskedIntReg, StructReg 의 StructEntry).
    /// 비트 번호: LittleEndian 은 0 = 최하위 비트, BigEndian 은 0 = 최상위 비트 (GenICam 규약).
    /// </summary>
    public sealed class MaskedIntRegNode : ValueNode
    {
        private readonly RegisterAccess _reg;

        internal MaskedIntRegNode(NodeMap map, XElement element, XElement? structReg = null) : base(map, element, structReg)
        {
            _reg = new RegisterAccess(this);
        }

        public override NodeInterface Interface => NodeInterface.Integer;
        private bool Signed => string.Equals(Text("Sign"), "Signed", StringComparison.Ordinal);

        /// <summary>최하위 비트 위치(0 = 레지스터 값의 LSB)와 비트 수.</summary>
        private (int shift, int width) Field()
        {
            int bits = _reg.Length * 8;
            bool big = _reg.BigEndian;
            int lsb, msb;
            if (Text("Bit") is string bit)
            {
                lsb = msb = (int)ParseLong(bit);
            }
            else
            {
                lsb = Text("LSB") is string l ? (int)ParseLong(l) : (big ? bits - 1 : 0);
                msb = Text("MSB") is string m ? (int)ParseLong(m) : (big ? 0 : bits - 1);
            }
            if (big)
            {
                lsb = bits - 1 - lsb;
                msb = bits - 1 - msb;
            }
            int lo = Math.Min(lsb, msb), hi = Math.Max(lsb, msb);
            return (lo, hi - lo + 1);
        }

        private static ulong Mask(int width) => width >= 64 ? ~0UL : (1UL << width) - 1;

        public override Num GetValue()
        {
            var (shift, width) = Field();
            ulong raw = RegisterAccess.ToUInt(_reg.Read(), _reg.BigEndian);
            ulong v = (raw >> shift) & Mask(width);
            return Num.Int(Signed ? RegisterAccess.SignExtend(v, width) : unchecked((long)v));
        }

        public override void SetValue(Num value)
        {
            var (shift, width) = Field();
            ulong mask = Mask(width);
            ulong raw = _reg.Mode == AccessMode.WO ? 0 : RegisterAccess.ToUInt(_reg.Read(), _reg.BigEndian);
            raw = (raw & ~(mask << shift)) | ((unchecked((ulong)ToLong(value)) & mask) << shift);
            _reg.Write(RegisterAccess.FromUInt(raw, _reg.Length, _reg.BigEndian));
        }

        public override Num GetMin() => Num.Int(RegisterAccess.Range(Field().width, Signed).min);
        public override Num GetMax() => Num.Int(RegisterAccess.Range(Field().width, Signed).max);
        protected override AccessMode BaseAccess => _reg.Mode;
    }

    public sealed class FloatRegNode : ValueNode
    {
        private readonly RegisterAccess _reg;

        internal FloatRegNode(NodeMap map, XElement element) : base(map, element)
        {
            _reg = new RegisterAccess(this);
        }

        public override NodeInterface Interface => NodeInterface.Float;
        public override bool IsFloat => true;

        public override Num GetValue()
        {
            var b = _reg.Read();
            if (_reg.BigEndian == BitConverter.IsLittleEndian) Array.Reverse(b);
            return Num.Real(b.Length == 4 ? BitConverter.ToSingle(b, 0) : BitConverter.ToDouble(b, 0));
        }

        public override void SetValue(Num value)
        {
            byte[] b = _reg.Length == 4 ? BitConverter.GetBytes((float)value.AsDouble) : BitConverter.GetBytes(value.AsDouble);
            if (_reg.BigEndian == BitConverter.IsLittleEndian) Array.Reverse(b);
            _reg.Write(b);
        }

        protected override AccessMode BaseAccess => _reg.Mode;
    }

    public sealed class StringRegNode : Node, IStringValue
    {
        private readonly RegisterAccess _reg;

        internal StringRegNode(NodeMap map, XElement element) : base(map, element)
        {
            _reg = new RegisterAccess(this);
        }

        public override NodeInterface Interface => NodeInterface.String;

        public string GetString()
        {
            var b = _reg.Read();
            int n = Array.IndexOf(b, (byte)0);
            return Encoding.ASCII.GetString(b, 0, n < 0 ? b.Length : n);
        }

        public void SetString(string value)
        {
            EnsureWritable();
            var src = Encoding.ASCII.GetBytes(value ?? "");
            int len = _reg.Length;
            if (src.Length > len)
                throw new VisionException(ErrorCode.CameraParameterInvalid, $"'{Name}' accepts at most {len} characters.");
            var b = new byte[len];
            Array.Copy(src, b, src.Length);
            _reg.Write(b);
        }

        protected override AccessMode BaseAccess => _reg.Mode;
    }

    /// <summary>원시 바이트 레지스터 (Register). 값 접근은 지원하지 않고 존재만 표시한다.</summary>
    public sealed class RawRegisterNode : Node
    {
        internal RawRegisterNode(NodeMap map, XElement element) : base(map, element) { }
        public override NodeInterface Interface => NodeInterface.Register;
    }

    // ====================================================================== 수식 노드

    /// <summary>pVariable / Constant / Expression 이름 해석 (SwissKnife, Converter 공통).</summary>
    internal sealed class FormulaScope
    {
        private readonly Node _owner;
        private readonly Dictionary<string, string> _vars = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Num> _consts = new Dictionary<string, Num>(StringComparer.Ordinal);
        private readonly Dictionary<string, Formula> _exprs = new Dictionary<string, Formula>(StringComparer.Ordinal);

        public FormulaScope(Node owner)
        {
            _owner = owner;
            foreach (var e in owner.All("pVariable"))
                if ((string?)e.Attribute("Name") is string n) _vars[n] = e.Value.Trim();
            foreach (var e in owner.All("Constant"))
                if ((string?)e.Attribute("Name") is string n)
                {
                    string t = e.Value.Trim();
                    _consts[n] = t.Contains(".") || t.IndexOf('e') >= 0 && !t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        ? Num.Real(Node.ParseDouble(t)) : Num.Int(Node.ParseLong(t));
                }
            foreach (var e in owner.All("Expression"))
                if ((string?)e.Attribute("Name") is string n) _exprs[n] = Formula.Parse(e.Value.Trim());
        }

        public Num Evaluate(Formula f, bool floatMode, string? extraName = null, Num extraValue = default)
        {
            Func<string, Num?>? resolve = null;
            resolve = name =>
            {
                if (extraName != null && name == extraName) return extraValue;
                if (_consts.TryGetValue(name, out var c)) return c;
                if (_exprs.TryGetValue(name, out var ex)) return ex.Evaluate(resolve!, floatMode);
                if (_vars.TryGetValue(name, out var node)) return _owner.Map.GetNum(node);

                // X.Entry.Sym / X.Value / X.Min / X.Max / X.Inc
                int dot = name.IndexOf('.');
                if (dot > 0 && _vars.TryGetValue(name.Substring(0, dot), out var target))
                {
                    string rest = name.Substring(dot + 1);
                    var n = _owner.Map.Find(target);
                    if (rest.StartsWith("Entry.", StringComparison.Ordinal) && n is EnumerationNode en)
                    {
                        var entry = en.FindEntry(rest.Substring("Entry.".Length));
                        if (entry != null) return Num.Int(entry.Value);
                    }
                    if (n is ValueNode vn)
                    {
                        switch (rest)
                        {
                            case "Value": return vn.GetValue();
                            case "Min": return vn.GetMin();
                            case "Max": return vn.GetMax();
                            case "Inc": return vn.GetInc();
                        }
                    }
                }
                return null;
            };
            return f.Evaluate(resolve, floatMode);
        }
    }

    /// <summary>IntSwissKnife (정수 수식) / SwissKnife (실수 수식). 읽기 전용.</summary>
    public sealed class IntSwissKnifeNode : ValueNode
    {
        private readonly FormulaScope _scope;
        private readonly Formula _formula;

        internal IntSwissKnifeNode(NodeMap map, XElement element) : base(map, element)
        {
            _scope = new FormulaScope(this);
            _formula = Formula.Parse(Text("Formula") ?? "0");
        }

        public override NodeInterface Interface => NodeInterface.Integer;
        public override Num GetValue() => Num.Int(_scope.Evaluate(_formula, false).AsLong);
        public override void SetValue(Num value) => throw new VisionException(ErrorCode.CameraParameterInvalid, $"'{Name}' is read-only.");
        protected override AccessMode BaseAccess => AccessMode.RO;
    }

    public sealed class SwissKnifeNode : ValueNode
    {
        private readonly FormulaScope _scope;
        private readonly Formula _formula;

        internal SwissKnifeNode(NodeMap map, XElement element) : base(map, element)
        {
            _scope = new FormulaScope(this);
            _formula = Formula.Parse(Text("Formula") ?? "0");
        }

        public override NodeInterface Interface => NodeInterface.Float;
        public override bool IsFloat => true;
        public override Num GetValue() => Num.Real(_scope.Evaluate(_formula, true).AsDouble);
        public override void SetValue(Num value) => throw new VisionException(ErrorCode.CameraParameterInvalid, $"'{Name}' is read-only.");
        protected override AccessMode BaseAccess => AccessMode.RO;
    }

    /// <summary>
    /// Converter (실수) / IntConverter (정수): 읽기는 FormulaFrom(TO = pValue 값), 쓰기는 FormulaTo(FROM = 쓸 값) 결과를 pValue 에 쓴다.
    /// </summary>
    public sealed class ConverterNode : ValueNode
    {
        private readonly FormulaScope _scope;
        private readonly Formula _to;
        private readonly Formula _from;
        private readonly bool _float;

        internal ConverterNode(NodeMap map, XElement element, bool isFloat) : base(map, element)
        {
            _float = isFloat;
            _scope = new FormulaScope(this);
            _to = Formula.Parse(Text("FormulaTo") ?? "FROM");
            _from = Formula.Parse(Text("FormulaFrom") ?? "TO");
        }

        public override NodeInterface Interface => _float ? NodeInterface.Float : NodeInterface.Integer;
        public override bool IsFloat => _float;

        private string Target => Text("pValue") ?? throw new VisionException(ErrorCode.CameraParameterInvalid, $"Converter '{Name}' has no pValue.");

        private Num FromRaw(Num raw)
        {
            var v = _scope.Evaluate(_from, _float, "TO", raw);
            return _float ? Num.Real(v.AsDouble) : Num.Int(ToLong(v));
        }

        public override Num GetValue() => FromRaw(Map.GetNum(Target));

        public override void SetValue(Num value)
        {
            var from = _float ? Num.Real(value.AsDouble) : Num.Int(ToLong(value));
            Map.SetNum(Target, _scope.Evaluate(_to, _float, "FROM", from));
        }

        private (Num a, Num b) Bounds()
        {
            var target = Map.Find(Target) as ValueNode;
            if (target == null) return (base.GetMin(), base.GetMax());
            try
            {
                var a = FromRaw(target.GetMin());
                var b = FromRaw(target.GetMax());
                return a.AsDouble <= b.AsDouble ? (a, b) : (b, a);
            }
            catch
            {
                return (base.GetMin(), base.GetMax());
            }
        }

        public override Num GetMin() => Bounds().a;
        public override Num GetMax() => Bounds().b;
        protected override AccessMode BaseAccess => Map.Find(Target)?.Access ?? AccessMode.NA;
    }
}
