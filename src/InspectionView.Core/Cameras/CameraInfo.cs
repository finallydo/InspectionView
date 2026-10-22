using System;

namespace InspectionView.Core.Cameras
{
    /// <summary>열거된 카메라 장치 하나의 설명.</summary>
    public sealed class CameraInfo
    {
        /// <summary>공급자 이름 (File, Simulation, GenTL, Euresys ...).</summary>
        public string Provider { get; }
        /// <summary>공급자 안에서 장치를 여는 데 쓰는 식별자 (시리얼, 경로 등).</summary>
        public string DeviceId { get; }
        public string Model { get; }
        public string Serial { get; }
        /// <summary>사용자에게 보여줄 이름.</summary>
        public string DisplayName { get; }

        public CameraInfo(string provider, string deviceId, string model = "", string serial = "", string? displayName = null)
        {
            Provider = provider ?? throw new ArgumentNullException(nameof(provider));
            DeviceId = deviceId ?? "";
            Model = model ?? "";
            Serial = serial ?? "";
            DisplayName = displayName ?? (string.IsNullOrEmpty(Model) ? DeviceId : $"{Model} ({(string.IsNullOrEmpty(Serial) ? DeviceId : Serial)})");
        }

        public override string ToString() => $"{Provider}:{DeviceId}";
    }

    public enum CameraParameterType
    {
        Integer,
        Float,
        String,
        Boolean,
        /// <summary>문자열 선택지 (<see cref="CameraParameterInfo.Choices"/>).</summary>
        Enum,
        /// <summary>값 없이 실행되는 명령 (예: 소프트웨어 트리거). SetParameter(name, null) 로 실행.</summary>
        Command,
    }

    /// <summary>카메라 파라미터 메타데이터.</summary>
    public sealed class CameraParameterInfo
    {
        public string Name { get; }
        public CameraParameterType Type { get; }
        public string Description { get; }
        public bool ReadOnly { get; }
        public double Min { get; }
        public double Max { get; }
        public string[] Choices { get; }
        /// <summary>장치 왕복 없이 바로 읽히는 상태 값 (프레임 수, 오류 등). UI 는 라이브 중에 이것만 주기적으로 갱신한다.</summary>
        public bool IsStatus { get; }

        public CameraParameterInfo(string name, CameraParameterType type, string description = "", bool readOnly = false,
            double min = double.NaN, double max = double.NaN, string[]? choices = null, bool status = false)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Type = type;
            Description = description ?? "";
            ReadOnly = readOnly;
            Min = min;
            Max = max;
            Choices = choices ?? Array.Empty<string>();
            IsStatus = status;
        }

        public override string ToString() => $"{Name} ({Type}{(ReadOnly ? ", ro" : "")})";
    }
}
