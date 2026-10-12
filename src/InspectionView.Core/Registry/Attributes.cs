using System;
using InspectionView.Core.Backends;

namespace InspectionView.Core.Registry
{
    /// <summary>
    /// 연산자 클래스 표시. 레지스트리가 어셈블리를 스캔해 자동 등록한다.
    /// 이름은 PascalCase 로 스크립트에서 호출되는 이름과 동일해야 한다.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class OperatorAttribute : Attribute
    {
        public string Name { get; }
        public string Category { get; set; } = "General";
        public string Description { get; set; } = "";

        public OperatorAttribute(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Operator name must not be empty.", nameof(name));
            Name = name;
        }
    }

    /// <summary>
    /// 입력 파라미터 속성. 선언 순서(또는 <see cref="Order"/>)가 스크립트의 위치 인자 순서가 된다.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
    public sealed class InputAttribute : Attribute
    {
        /// <summary>스크립트/노드에 표시되는 이름. null 이면 프로퍼티 이름.</summary>
        public string? Name { get; set; }
        public string Description { get; set; } = "";

        /// <summary>기본값. 지정하면 생략 가능한 파라미터가 된다.</summary>
        public object? Default { get; set; }

        /// <summary>기본값 없이도 생략 가능 (null 허용).</summary>
        public bool Optional { get; set; }

        /// <summary>정렬 순서 (작을수록 앞). 지정하지 않으면 선언 순서.</summary>
        public int Order { get; set; } = int.MaxValue;

        /// <summary>숫자 파라미터 허용 범위 (NaN = 제한 없음).</summary>
        public double Min { get; set; } = double.NaN;
        public double Max { get; set; } = double.NaN;

        /// <summary>파라미터 창에서 보여줄 선택지 (예: "light", "dark").</summary>
        public string[]? Choices { get; set; }
    }

    /// <summary>출력(결과) 파라미터 속성.</summary>
    [AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
    public sealed class OutputAttribute : Attribute
    {
        public string? Name { get; set; }
        public string Description { get; set; } = "";
        public int Order { get; set; } = int.MaxValue;
    }

    /// <summary>
    /// 백엔드 구현 메서드 표시. 시그니처는 <c>void Method(OperatorContext ctx)</c> 여야 하며
    /// 하나의 연산자에 Cpu / Parallel / Gpu 구현을 각각 붙일 수 있다.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = true, AllowMultiple = true)]
    public sealed class BackendAttribute : Attribute
    {
        public BackendKind Kind { get; }

        /// <summary>같은 종류의 백엔드가 여럿일 때 우선순위 (클수록 우선).</summary>
        public int Priority { get; set; }

        public BackendAttribute(BackendKind kind)
        {
            Kind = kind;
        }
    }
}
