using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InspectionView.Core.Errors;
using InspectionView.Core.Interop;
using InspectionView.Core.Types;
using OpenCvSharp;
using ErrorCode = InspectionView.Core.Errors.ErrorCode;

namespace InspectionView.Core.Cameras
{
    /// <summary>
    /// 파일 카메라: 폴더/패턴/단일 파일의 이미지를 순서대로 프레임으로 돌려준다 (오프라인 개발, 재생).
    /// DeviceId = 폴더 경로, 'folder/*.png' 패턴, 또는 파일 경로. 옵션: Loop(true), Recursive(false), AsGray(false), Fps(0).
    /// </summary>
    public sealed class FileCameraProvider : ICameraProvider
    {
        public const string ProviderName = "File";

        public string Name => ProviderName;
        public bool IsAvailable => true;
        public string? UnavailableReason => null;

        /// <summary>파일 카메라는 열거할 장치가 없다 (경로를 직접 지정).</summary>
        public IReadOnlyList<CameraInfo> Enumerate() => Array.Empty<CameraInfo>();

        public ICamera Open(string deviceId, IReadOnlyDictionary<string, string> options)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                throw new VisionException(ErrorCode.CameraNotFound, "File camera needs a folder, a file pattern or a file path as device.");
            return new FileCamera(deviceId, options);
        }
    }

    public sealed class FileCamera : CameraBase
    {
        private static readonly string[] DefaultExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".pgm", ".ppm", ".exr" };

        private readonly List<string> _files;
        private int _index;
        private bool _loop;
        private bool _asGray;
        private double _fps;
        private string _lastFile = "";

        public FileCamera(string device, IReadOnlyDictionary<string, string> options)
            : base(new CameraInfo(FileCameraProvider.ProviderName, device, "File camera", "", device))
        {
            bool recursive = Option(options, "Recursive", false);
            _loop = Option(options, "Loop", true);
            _asGray = Option(options, "AsGray", false);
            _fps = Option(options, "Fps", 0.0);
            _files = Resolve(device, recursive);
            if (_files.Count == 0)
                throw new VisionException(ErrorCode.CameraNotFound, $"No image files found for '{device}'.");
            ApplyFps();

            RegisterParameter(new CameraParameterInfo("Count", CameraParameterType.Integer, "Number of files.", readOnly: true), () => (long)_files.Count);
            RegisterParameter(new CameraParameterInfo("Index", CameraParameterType.Integer, "Index of the next file to grab.", min: 0), () => (long)_index,
                v => { lock (_files) _index = (int)Math.Min((long)v!, _files.Count - 1); });
            RegisterParameter(new CameraParameterInfo("Loop", CameraParameterType.Boolean, "Restart from the first file after the last."), () => _loop, v => _loop = (bool)v!);
            RegisterParameter(new CameraParameterInfo("AsGray", CameraParameterType.Boolean, "Convert to 8-bit gray on load."), () => _asGray, v => _asGray = (bool)v!);
            RegisterParameter(new CameraParameterInfo("Fps", CameraParameterType.Float, "Playback rate for continuous acquisition (0 = as fast as possible).", min: 0), () => _fps,
                v => { _fps = (double)v!; ApplyFps(); });
            RegisterParameter(new CameraParameterInfo("FileName", CameraParameterType.String, "File of the last grabbed frame.", readOnly: true), () => _lastFile);
            RegisterParameter(new CameraParameterInfo("Files", CameraParameterType.String, "All files, separated by ';'.", readOnly: true), () => string.Join(";", _files));
        }

        public IReadOnlyList<string> Files => _files;

        private void ApplyFps() => FrameIntervalMs = _fps > 0 ? (int)Math.Round(1000.0 / _fps) : 0;

        private static List<string> Resolve(string device, bool recursive)
        {
            var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            if (File.Exists(device))
                return new List<string> { Path.GetFullPath(device) };
            if (Directory.Exists(device))
            {
                return Directory.GetFiles(device, "*.*", opt)
                    .Where(f => DefaultExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            var dir = Path.GetDirectoryName(device);
            var pattern = Path.GetFileName(device);
            if (!string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(pattern) && Directory.Exists(dir))
            {
                return Directory.GetFiles(dir, pattern, opt)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            return new List<string>();
        }

        private static bool Option(IReadOnlyDictionary<string, string> o, string key, bool def)
            => o.TryGetValue(key, out var s) ? (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1") : def;

        private static double Option(IReadOnlyDictionary<string, string> o, string key, double def)
            => o.TryGetValue(key, out var s) && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : def;

        protected override Image GrabCore(int timeoutMs)
        {
            string file;
            lock (_files)
            {
                if (_index >= _files.Count)
                {
                    if (!_loop)
                        throw new VisionException(ErrorCode.CameraTimeout, "File camera reached the end of the sequence (Loop = false).");
                    _index = 0;
                }
                file = _files[_index++];
            }

            using (var mat = Cv2.ImRead(file, _asGray ? ImreadModes.Grayscale : ImreadModes.Unchanged))
            {
                if (mat == null || mat.Empty())
                    throw new VisionException(ErrorCode.FileReadFailed, $"Could not decode image file: {file}");
                var img = MatInterop.ToImage(mat);
                img.Metadata["FileName"] = file;
                _lastFile = file;
                return img;
            }
        }

        protected override void CloseCore()
        {
        }
    }
}
