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
    /// 이미지를 파일로 저장한다. 포맷은 확장자로 결정된다 (png, jpg, bmp, tif ...).
    /// 8/16bit 는 PNG/TIFF, Float 는 TIFF/EXR 로 저장할 수 있다. Domain 과 무관하게 전체 이미지를 기록한다.
    /// 스크립트: <c>WriteImage(Image, 'C:/out/result.png')</c>
    /// </summary>
    [Operator("WriteImage", Category = "IO", Description = "Writes an image to a file; the format is chosen by the file extension.")]
    public sealed class WriteImage : OperatorBase
    {
        [Input(Description = "Image to write.")]
        public Image? Image { get; set; }

        [Input(Description = "Output file path (extension selects the format).")]
        public string FileName { get; set; } = "";

        protected override void Validate()
        {
            Require(Image, nameof(Image));
            RequireNotEmpty(FileName, nameof(FileName));

            var dir = Path.GetDirectoryName(Path.GetFullPath(FileName));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                throw new VisionException(ErrorCode.FileWriteFailed, $"Directory does not exist: {dir}");
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var img = Image!;
            bool ok;
            using (var mat = MatInterop.AsMat(img))
            {
                try
                {
                    ok = Cv2.ImWrite(FileName, mat);
                }
                catch (OpenCVException ex)
                {
                    throw new VisionException(ErrorCode.FileWriteFailed, $"Could not encode '{FileName}' ({img.PixelType.ShortName()}x{img.Channels}): {ex.Message}", ex);
                }
            }

            if (!ok)
                throw new VisionException(ErrorCode.FileWriteFailed, $"Could not write image file: {FileName}");

            ctx.Info($"WriteImage: {FileName}");
        }
    }
}
