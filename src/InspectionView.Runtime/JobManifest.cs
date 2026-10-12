using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace InspectionView.Runtime
{
    /// <summary>
    /// Job 패키지(zip) 의 manifest.json.
    /// 패키지 구성: manifest.json, script.ivs, params/values.json, roi/*.rgn, models/*.png|tif
    /// </summary>
    [DataContract]
    public sealed class JobManifest
    {
        public const int CurrentFormatVersion = 1;

        [DataMember(Name = "formatVersion", Order = 0)]
        public int FormatVersion { get; set; } = CurrentFormatVersion;

        [DataMember(Name = "name", Order = 1)]
        public string Name { get; set; } = "";

        [DataMember(Name = "description", Order = 2)]
        public string Description { get; set; } = "";

        [DataMember(Name = "author", Order = 3)]
        public string Author { get; set; } = "";

        /// <summary>ISO 8601 (DataContractJsonSerializer 의 날짜 형식을 피하기 위해 문자열).</summary>
        [DataMember(Name = "createdAt", Order = 4)]
        public string CreatedAt { get; set; } = DateTime.Now.ToString("o");

        [DataMember(Name = "modifiedAt", Order = 5)]
        public string ModifiedAt { get; set; } = DateTime.Now.ToString("o");

        /// <summary>작성에 사용된 InspectionView 버전.</summary>
        [DataMember(Name = "toolVersion", Order = 6)]
        public string ToolVersion { get; set; } = "";

        [DataMember(Name = "scriptFile", Order = 7)]
        public string ScriptFile { get; set; } = "script.ivs";

        /// <summary>검사 프로그램이 값을 넣는 입력 터미널 (ToolBlock 방식).</summary>
        [DataMember(Name = "inputs", Order = 8)]
        public List<JobTerminal> Inputs { get; set; } = new List<JobTerminal>();

        /// <summary>검사 프로그램이 읽어가는 출력 터미널.</summary>
        [DataMember(Name = "outputs", Order = 9)]
        public List<JobTerminal> Outputs { get; set; } = new List<JobTerminal>();

        /// <summary>패키지에 저장된 리소스(ROI, 모델 이미지, 파라미터) 목록. 실행 시 같은 이름의 변수로 미리 로드된다.</summary>
        [DataMember(Name = "resources", Order = 10)]
        public List<JobResource> Resources { get; set; } = new List<JobResource>();

        [OnDeserialized]
        private void OnDeserialized(StreamingContext _)
        {
            Inputs = Inputs ?? new List<JobTerminal>();
            Outputs = Outputs ?? new List<JobTerminal>();
            Resources = Resources ?? new List<JobResource>();
            Name = Name ?? "";
            Description = Description ?? "";
            Author = Author ?? "";
            ScriptFile = string.IsNullOrEmpty(ScriptFile) ? "script.ivs" : ScriptFile;
        }

        public JobTerminal? FindInput(string name) => Inputs.Find(t => t.Name == name);
        public JobTerminal? FindOutput(string name) => Outputs.Find(t => t.Name == name);
    }

    /// <summary>Job 입출력 터미널 정의.</summary>
    [DataContract]
    public sealed class JobTerminal
    {
        /// <summary>스크립트 변수 이름.</summary>
        [DataMember(Name = "name", Order = 0)]
        public string Name { get; set; } = "";

        /// <summary>타입 이름: Image, Region, XLD, Tuple, Image[], Region[], XLD[], Any.</summary>
        [DataMember(Name = "type", Order = 1)]
        public string Type { get; set; } = TerminalType.Tuple;

        [DataMember(Name = "description", Order = 2)]
        public string Description { get; set; } = "";

        /// <summary>입력 터미널의 기본값 (스크립트 리터럴). 비어 있으면 필수 입력.</summary>
        [DataMember(Name = "default", Order = 3)]
        public string Default { get; set; } = "";

        public JobTerminal()
        {
        }

        public JobTerminal(string name, string type, string description = "", string defaultValue = "")
        {
            Name = name;
            Type = type;
            Description = description;
            Default = defaultValue;
        }

        public override string ToString() => $"{Type} {Name}" + (Default.Length > 0 ? $" = {Default}" : "");
    }

    /// <summary>패키지 리소스 항목.</summary>
    [DataContract]
    public sealed class JobResource
    {
        /// <summary>변수 이름 (= 파일 이름).</summary>
        [DataMember(Name = "name", Order = 0)]
        public string Name { get; set; } = "";

        /// <summary>Region, Image, Tuple.</summary>
        [DataMember(Name = "type", Order = 1)]
        public string Type { get; set; } = "";

        /// <summary>패키지 내 경로 (roi/Name.rgn, models/Name.png, params 는 values.json).</summary>
        [DataMember(Name = "path", Order = 2)]
        public string Path { get; set; } = "";

        public JobResource()
        {
        }

        public JobResource(string name, string type, string path)
        {
            Name = name;
            Type = type;
            Path = path;
        }
    }

    /// <summary>터미널 타입 이름과 값 검사.</summary>
    public static class TerminalType
    {
        public const string Image = "Image";
        public const string Region = "Region";
        public const string Xld = "XLD";
        public const string Tuple = "Tuple";
        public const string ImageArray = "Image[]";
        public const string RegionArray = "Region[]";
        public const string XldArray = "XLD[]";
        public const string Any = "Any";

        public static readonly string[] All = { Image, Region, Xld, Tuple, ImageArray, RegionArray, XldArray, Any };

        /// <summary>값이 타입에 맞는지.</summary>
        public static bool Matches(string type, object? value)
        {
            switch (type)
            {
                case Any: return value != null;
                case Image: return value is Core.Types.Image;
                case Region: return value is Core.Types.Region;
                case Xld: return value is Core.Types.XldContour;
                case Tuple: return value is Core.Types.TupleValue || value is long || value is int || value is double || value is string || value is bool;
                case ImageArray: return value is Core.Types.ObjectArray<Core.Types.Image>;
                case RegionArray: return value is Core.Types.ObjectArray<Core.Types.Region>;
                case XldArray: return value is Core.Types.ObjectArray<Core.Types.XldContour>;
                default: return false;
            }
        }

        /// <summary>값의 터미널 타입 이름.</summary>
        public static string Of(object? value)
        {
            switch (value)
            {
                case Core.Types.Image _: return Image;
                case Core.Types.Region _: return Region;
                case Core.Types.XldContour _: return Xld;
                case Core.Types.ObjectArray<Core.Types.Image> _: return ImageArray;
                case Core.Types.ObjectArray<Core.Types.Region> _: return RegionArray;
                case Core.Types.ObjectArray<Core.Types.XldContour> _: return XldArray;
                case null: return "";
                default: return Tuple;
            }
        }
    }
}
