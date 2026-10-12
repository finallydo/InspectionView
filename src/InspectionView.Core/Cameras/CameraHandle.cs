using InspectionView.Core.Errors;
using InspectionView.Core.Memory;

namespace InspectionView.Core.Cameras
{
    /// <summary>
    /// 스크립트/변수 테이블에서 카메라를 가리키는 핸들 (참조 카운트 객체). 마지막 참조가 해제되거나
    /// <see cref="Close"/> 가 호출되면 장치를 닫는다.
    /// </summary>
    public sealed class CameraHandle : IconicObject
    {
        private ICamera? _camera;

        public override string TypeName => "Camera";

        public CameraHandle(ICamera camera)
        {
            _camera = camera ?? throw VisionException.NullArgument(nameof(camera));
        }

        /// <summary>열린 카메라. 닫혔으면 <see cref="ErrorCode.CameraClosed"/> 예외.</summary>
        public ICamera Camera
        {
            get
            {
                var c = _camera;
                if (c == null || !c.IsOpen)
                    throw new VisionException(ErrorCode.CameraClosed, "The camera has been closed.");
                return c;
            }
        }

        public bool IsOpen => _camera != null && _camera.IsOpen;
        public CameraInfo? Info => _camera?.Info;

        /// <summary>장치를 즉시 닫는다 (핸들 변수는 남아 있어도 됨).</summary>
        public void Close()
        {
            var c = _camera;
            _camera = null;
            c?.Dispose();
        }

        protected override void ReleaseResources(bool disposing)
        {
            if (disposing)
                Close();
        }

        public override string ToString() => IsOpen ? $"Camera {_camera!.Info.Provider}:{_camera.Info.DisplayName}" : "Camera (closed)";
    }
}
