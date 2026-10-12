using System;
using System.Collections.Generic;

namespace InspectionView.Runtime
{
    /// <summary>
    /// Job 패키지(zip) 의 manifest.json 모델.
    /// 패키지 구성: manifest.json, script.ivs, params/, roi/, calib/, models/.
    /// 실제 zip 입출력과 실행기는 다음 단계에서 구현한다.
    /// </summary>
    public sealed class JobManifest
    {
        /// <summary>패키지 포맷 버전.</summary>
        public int FormatVersion { get; set; } = 1;

        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Author { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime ModifiedAt { get; set; } = DateTime.Now;

        /// <summary>작성에 사용된 InspectionView 버전.</summary>
        public string ToolVersion { get; set; } = "";

        /// <summary>패키지 내 스크립트 경로.</summary>
        public string ScriptFile { get; set; } = "script.ivs";

        /// <summary>검사 프로그램이 값을 넣는 입력 터미널 (ToolBlock 방식).</summary>
        public List<JobTerminal> Inputs { get; } = new List<JobTerminal>();

        /// <summary>검사 프로그램이 읽어가는 출력 터미널.</summary>
        public List<JobTerminal> Outputs { get; } = new List<JobTerminal>();
    }

    /// <summary>Job 입출력 터미널 정의.</summary>
    public sealed class JobTerminal
    {
        /// <summary>스크립트 변수 이름.</summary>
        public string Name { get; set; } = "";

        /// <summary>타입 이름: Image, Region, XLD, Tuple, Image[] ...</summary>
        public string Type { get; set; } = "";

        public string Description { get; set; } = "";

        /// <summary>입력 터미널의 기본값 (Tuple 형식 문자열).</summary>
        public string? Default { get; set; }
    }
}
