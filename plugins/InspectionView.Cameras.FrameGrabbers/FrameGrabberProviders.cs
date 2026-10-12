using System;
using System.Collections.Generic;
using InspectionView.Core.Cameras;
using InspectionView.Core.Errors;

namespace InspectionView.Cameras.FrameGrabbers
{
    /// <summary>
    /// 프레임그래버 공급자 뼈대. SDK .NET 바인딩이 통합될 때까지 '사용 불가' 로 보고한다.
    /// 통합 시: 이 클래스를 ICameraProvider 실제 구현으로 바꾸고 CameraBase 를 상속한 카메라 클래스를 추가한다
    /// (GrabCore 에서 SDK 버퍼 → CameraFrames.FromPixelFormat / FromBuffer).
    /// </summary>
    public abstract class UnavailableProviderBase : ICameraProvider
    {
        protected UnavailableProviderBase(string name, string reason)
        {
            Name = name;
            UnavailableReason = reason;
        }

        public string Name { get; }
        public bool IsAvailable => false;
        public string? UnavailableReason { get; }
        public IReadOnlyList<CameraInfo> Enumerate() => Array.Empty<CameraInfo>();
        public ICamera Open(string deviceId, IReadOnlyDictionary<string, string> options)
            => throw new VisionException(ErrorCode.CameraSdkNotAvailable, $"{Name}: {UnavailableReason}");
    }

    /// <summary>Euresys Coaxlink/Grablink (eGrabber). TODO: Euresys.EGrabber .NET 바인딩 연동.</summary>
    public sealed class EuresysCameraProvider : UnavailableProviderBase
    {
        public EuresysCameraProvider() : base("Euresys", "eGrabber .NET binding is not integrated yet (plugins/InspectionView.Cameras.FrameGrabbers).") { }
    }

    /// <summary>Matrox MIL. TODO: Matrox.MatroxImagingLibrary (MIL .NET) 연동.</summary>
    public sealed class MatroxCameraProvider : UnavailableProviderBase
    {
        public MatroxCameraProvider() : base("Matrox", "MIL .NET binding is not integrated yet (plugins/InspectionView.Cameras.FrameGrabbers).") { }
    }

    /// <summary>Teledyne Dalsa Sapera LT. TODO: DALSA.SaperaLT.SapClassBasic 연동.</summary>
    public sealed class DalsaCameraProvider : UnavailableProviderBase
    {
        public DalsaCameraProvider() : base("Dalsa", "Sapera LT .NET binding is not integrated yet (plugins/InspectionView.Cameras.FrameGrabbers).") { }
    }
}
