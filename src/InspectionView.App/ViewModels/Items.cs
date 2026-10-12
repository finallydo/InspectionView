using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media.Imaging;
using InspectionView.App.Imaging;
using InspectionView.Core.Memory;
using InspectionView.Core.Registry;
using InspectionView.Script.Parsing;
using InspectionView.Script.Runtime;

namespace InspectionView.App.ViewModels
{
    /// <summary>변수창 한 줄.</summary>
    public sealed class VariableItem : ObservableObject
    {
        private string _type = "";
        private string _value = "";
        private object? _raw;
        private BitmapSource? _thumbnail;
        private bool _thumbnailDirty;

        public string Name { get; }

        public string Type
        {
            get => _type;
            set => Set(ref _type, value);
        }

        public string Value
        {
            get => _value;
            set => Set(ref _value, value);
        }

        /// <summary>실제 값 (미리보기용). 참조 카운트는 VariableTable 이 소유하므로 여기서는 AddRef 하지 않는다.</summary>
        public object? Raw
        {
            get => _raw;
            set => Set(ref _raw, value);
        }

        public BitmapSource? Thumbnail
        {
            get => _thumbnail;
            private set => Set(ref _thumbnail, value);
        }

        /// <summary>썸네일을 다시 만들어야 하는지 (실행 중에는 미룬다).</summary>
        public bool ThumbnailDirty => _thumbnailDirty;

        public VariableItem(string name)
        {
            Name = name;
        }

        public void Update(object? value)
        {
            Raw = value;
            Type = ScriptValues.TypeName(value);
            Value = ScriptValues.Format(value);
            _thumbnailDirty = value is IconicObject;
            if (!_thumbnailDirty)
                Thumbnail = null;
        }

        /// <summary>썸네일 생성 (UI 스레드, 엔진이 멈춰 있을 때 호출).</summary>
        public void RefreshThumbnail()
        {
            _thumbnailDirty = false;
            Thumbnail = ThumbnailFactory.Create(Raw);
        }
    }

    /// <summary>연산자 트리 노드 (카테고리 또는 연산자).</summary>
    public sealed class OperatorNode
    {
        public string Name { get; }
        public string? Description { get; }
        public string? Signature { get; }
        public OperatorDescriptor? Descriptor { get; }
        public ObservableCollection<OperatorNode> Children { get; } = new ObservableCollection<OperatorNode>();

        public bool IsCategory => Descriptor == null;
        public string ToolTip => Descriptor == null ? Name : $"{Signature}\n{Description}\nBackends: {string.Join(", ", Descriptor.Backends.Select(b => b.Kind))}";

        public OperatorNode(string category)
        {
            Name = category;
        }

        public OperatorNode(OperatorDescriptor descriptor)
        {
            Name = descriptor.Name;
            Description = descriptor.Description;
            Signature = descriptor.Signature;
            Descriptor = descriptor;
        }

        /// <summary>에디터에 삽입할 호출 템플릿: "Out = Name(In1, In2)".</summary>
        public string InsertText
        {
            get
            {
                if (Descriptor == null)
                    return Name;

                var args = string.Join(", ", Descriptor.Inputs.Select(p =>
                    p.DefaultValue is string s ? $"'{s}'" :
                    p.DefaultValue != null ? ScriptValues.Str(p.DefaultValue) :
                    p.Name));
                var call = $"{Name}({args})";
                if (Descriptor.Outputs.Count == 1)
                    return $"{Descriptor.Outputs[0].Name} = {call}";
                if (Descriptor.Outputs.Count > 1)
                    return $"Res = {call}";
                return call;
            }
        }
    }

    /// <summary>오류 목록 한 줄.</summary>
    public sealed class DiagnosticItem
    {
        public string Severity { get; }
        public int Line { get; }
        public int Column { get; }
        public int Length { get; }
        public string Message { get; }

        public DiagnosticItem(Diagnostic d)
        {
            Severity = d.Severity.ToString();
            Line = d.Line;
            Column = d.Column;
            Length = d.Length;
            Message = d.Message;
        }

        public DiagnosticItem(string severity, int line, int column, int length, string message)
        {
            Severity = severity;
            Line = line;
            Column = column;
            Length = length;
            Message = message;
        }
    }

    /// <summary>프로파일 창 한 줄.</summary>
    public sealed class ProfileItem
    {
        public int Index { get; }
        public string Operator { get; }
        public string Backend { get; }
        public double Milliseconds { get; }
        public string Result { get; }

        public ProfileItem(int index, ExecutionRecord r)
        {
            Index = index;
            Operator = r.OperatorName;
            Backend = r.FellBack ? $"{r.Used} (requested {r.Requested})" : r.Used.ToString();
            Milliseconds = r.Elapsed.TotalMilliseconds;
            Result = r.Code.ToString();
        }
    }
}

namespace InspectionView.App.ViewModels
{
    /// <summary>Job 터미널 편집 행 (DataGrid).</summary>
    public sealed class TerminalItem : ObservableObject
    {
        private string _name = "";
        private string _type = InspectionView.Runtime.TerminalType.Tuple;
        private string _default = "";
        private string _description = "";

        public string Name { get => _name; set => Set(ref _name, (value ?? "").Trim()); }
        public string Type { get => _type; set => Set(ref _type, string.IsNullOrEmpty(value) ? InspectionView.Runtime.TerminalType.Tuple : value); }
        public string Default { get => _default; set => Set(ref _default, value ?? ""); }
        public string Description { get => _description; set => Set(ref _description, value ?? ""); }

        public TerminalItem()
        {
        }

        public TerminalItem(InspectionView.Runtime.JobTerminal t)
        {
            _name = t.Name;
            _type = t.Type;
            _default = t.Default;
            _description = t.Description;
        }

        public InspectionView.Runtime.JobTerminal ToTerminal()
            => new InspectionView.Runtime.JobTerminal(Name, Type, Description, Default);
    }

    /// <summary>Job 패키지 리소스 행.</summary>
    public sealed class ResourceItem
    {
        public string Name { get; }
        public string Type { get; }
        public string Summary { get; }

        public ResourceItem(string name, string type, string summary)
        {
            Name = name;
            Type = type;
            Summary = summary;
        }
    }
}
