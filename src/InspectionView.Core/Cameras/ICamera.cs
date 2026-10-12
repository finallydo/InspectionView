using System;
using System.Collections.Generic;
using InspectionView.Core.Types;

namespace InspectionView.Core.Cameras
{
    /// <summary>
    /// 카메라 공통 인터페이스. 벤더 플러그인은 <see cref="ICameraProvider"/> 와 이 인터페이스(보통 <see cref="CameraBase"/> 상속)를 구현한다.
    /// <para>
    /// 이미지 소유권: <see cref="Grab"/> 이 돌려주는 Image 는 호출자가 소유한다(참조 1). 연속 취득 중의
    /// <see cref="FrameReceived"/> 이벤트 인자는 카메라가 소유하므로 보관하려면 AddRef 해야 한다.
    /// </para>
    /// </summary>
    public interface ICamera : IDisposable
    {
        CameraInfo Info { get; }
        bool IsOpen { get; }
        bool IsAcquiring { get; }

        /// <summary>프레임 하나를 동기 취득한다 (연속 취득 중이면 다음 프레임을 기다린다).</summary>
        Image Grab(int timeoutMs);

        /// <summary>연속 취득 시작 (백그라운드).</summary>
        void StartAcquisition();
        /// <summary>연속 취득 중지.</summary>
        void StopAcquisition();

        /// <summary>연속 취득 중 가장 최근 프레임 (AddRef 된 참조, 호출자가 Release). 아직 없으면 null.</summary>
        Image? GetLatestFrame();

        /// <summary>연속 취득 중 새 프레임 (실행 스레드: 카메라 스레드). 인자는 카메라 소유.</summary>
        event Action<ICamera, Image>? FrameReceived;

        IReadOnlyList<CameraParameterInfo> Parameters { get; }
        object? GetParameter(string name);
        void SetParameter(string name, object? value);
    }

    /// <summary>카메라 공급자 (벤더 SDK 하나 또는 File/Simulation 같은 가상 소스).</summary>
    public interface ICameraProvider
    {
        /// <summary>공급자 이름 (OpenFramegrabber 의 Provider 인자).</summary>
        string Name { get; }
        /// <summary>SDK 가 설치되어 사용 가능한지.</summary>
        bool IsAvailable { get; }
        /// <summary>사용 불가 시 이유 (SDK 경로 안내 등).</summary>
        string? UnavailableReason { get; }

        IReadOnlyList<CameraInfo> Enumerate();

        /// <summary>
        /// 장치를 연다. <paramref name="deviceId"/> 가 비어 있으면 첫 장치. <paramref name="options"/> 는 'Key=Value;Key=Value' 를 파싱한 것.
        /// </summary>
        ICamera Open(string deviceId, IReadOnlyDictionary<string, string> options);
    }
}
