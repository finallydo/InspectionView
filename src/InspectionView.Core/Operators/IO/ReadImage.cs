using System.IO;
using InspectionView.Core.Backends;
using InspectionView.Core.Errors;
using InspectionView.Core.Interop;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;
using OpenCvSharp;
using ErrorCode = InspectionView.Core.Errors.ErrorCode;

namespace InspectionView.Core.Operators.IO
{
    /// <summary>
    /// 파일에서 이미지를 읽는다. 8/16bit, Gray/Color(BGR)/Float(TIFF, EXR) 를 원본 타입 그대로 로드한다.
    /// 스크립트: <c>Image = ReadImage('C:/images/part.png')</c>
    /// </summary>
    [Operator("ReadImage", Category = "IO", Description = "Reads an image file (PNG/JPEG/BMP/TIFF/...) keeping its original pixel type and channels.")]
    public sealed class ReadImage : OperatorBase
    {
        [Input(Description = "Path of the image file.")]
        public string FileName { get; set; } = "";

        [Input(Description = "Convert to 8-bit gray on load.", Default = false)]
        public bool AsGray { get; set; }

        [Output(Description = "Loaded image.")]
        public Image? Image { get; private set; }

        protected override void Validate()
        {
            RequireNotEmpty(FileName, nameof(FileName));
            if (!File.Exists(FileName))
                throw VisionException.FileNotFound(FileName);
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var mode = AsGray ? ImreadModes.Grayscale : ImreadModes.Unchanged;

            using (var mat = Cv2.ImRead(FileName, mode))
            {
                if (mat == null || mat.Empty())
                    throw new VisionException(ErrorCode.FileReadFailed, $"Could not decode image file: {FileName}");

                Image = MatInterop.ToImage(mat);
            }

            Image.Metadata["FileName"] = Path.GetFullPath(FileName);
            ctx.Info($"ReadImage: {Image}");
        }
    }
}
