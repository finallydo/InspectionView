using System;
using System.Collections.Generic;
using System.Globalization;
using InspectionView.Core.Types;

namespace InspectionView.Core.Cameras
{
    /// <summary>
    /// 시뮬레이션 카메라: 합성 프레임(움직이는 패턴)을 만든다. 장치 'default' 하나. 옵션/파라미터: Width, Height, Pattern(Bars|Checker|Noise|Gradient), Fps, Noise.
    /// </summary>
    public sealed class SimulationCameraProvider : ICameraProvider
    {
        public const string ProviderName = "Simulation";

        public string Name => ProviderName;
        public bool IsAvailable => true;
        public string? UnavailableReason => null;

        public IReadOnlyList<CameraInfo> Enumerate()
            => new[] { new CameraInfo(ProviderName, "default", "Simulated camera", "SIM-0001") };

        public ICamera Open(string deviceId, IReadOnlyDictionary<string, string> options)
            => new SimulationCamera(options);
    }

    public sealed class SimulationCamera : CameraBase
    {
        public static readonly string[] Patterns = { "Bars", "Checker", "Noise", "Gradient" };

        private int _width, _height;
        private string _pattern;
        private double _fps;
        private double _noise;
        private long _counter;
        private readonly Random _random = new Random(12345);

        public SimulationCamera(IReadOnlyDictionary<string, string> options)
            : base(new CameraInfo(SimulationCameraProvider.ProviderName, "default", "Simulated camera", "SIM-0001"))
        {
            _width = (int)Option(options, "Width", 640);
            _height = (int)Option(options, "Height", 480);
            _fps = Option(options, "Fps", 30);
            _noise = Option(options, "Noise", 0);
            _pattern = options.TryGetValue("Pattern", out var p) && Array.IndexOf(Patterns, p) >= 0 ? p : "Bars";
            ApplyFps();

            RegisterParameter(new CameraParameterInfo("Width", CameraParameterType.Integer, "Frame width.", min: 1, max: 16384), () => (long)_width, v => _width = (int)(long)v!);
            RegisterParameter(new CameraParameterInfo("Height", CameraParameterType.Integer, "Frame height.", min: 1, max: 16384), () => (long)_height, v => _height = (int)(long)v!);
            RegisterParameter(new CameraParameterInfo("Pattern", CameraParameterType.Enum, "Synthetic pattern.", choices: Patterns), () => _pattern, v => _pattern = (string)v!);
            RegisterParameter(new CameraParameterInfo("Fps", CameraParameterType.Float, "Frame rate of continuous acquisition.", min: 0, max: 10000), () => _fps, v => { _fps = (double)v!; ApplyFps(); });
            RegisterParameter(new CameraParameterInfo("Noise", CameraParameterType.Float, "Additive noise amplitude (gray values).", min: 0, max: 255), () => _noise, v => _noise = (double)v!);
            RegisterParameter(new CameraParameterInfo("ExposureTime", CameraParameterType.Float, "Dummy exposure time (us).", min: 1, max: 1e7), () => _exposure, v => _exposure = (double)v!);
            RegisterParameter(new CameraParameterInfo("TriggerSoftware", CameraParameterType.Command, "No-op software trigger."), () => null, v => { });
            RegisterParameter(new CameraParameterInfo("FocusPosition", CameraParameterType.Float, "Simulated lens position; frames are blurred by |FocusPosition - TrueFocus| / 10 px.", min: 0, max: 100), () => _focus, v => _focus = (double)v!);
            RegisterParameter(new CameraParameterInfo("TrueFocus", CameraParameterType.Float, "Lens position that gives a sharp image.", min: 0, max: 100), () => _trueFocus, v => _trueFocus = (double)v!);
        }

        private double _exposure = 10000;
        private double _focus = 50, _trueFocus = 50;

        private void ApplyFps() => FrameIntervalMs = _fps > 0 ? (int)Math.Round(1000.0 / _fps) : 0;

        private static double Option(IReadOnlyDictionary<string, string> o, string key, double def)
            => o.TryGetValue(key, out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : def;

        protected override Image GrabCore(int timeoutMs)
        {
            int w = _width, h = _height;
            long n = _counter++;
            var img = Image.Create(w, h, PixelType.UInt8, 1, clear: false);
            int shift = (int)(n * 4);
            for (int y = 0; y < h; y++)
            {
                var row = img.GetRow<byte>(y);
                for (int x = 0; x < w; x++)
                {
                    double v;
                    switch (_pattern)
                    {
                        case "Checker": v = (((x + shift) / 32 + y / 32) % 2 == 0) ? 200 : 50; break;
                        case "Noise": v = _random.Next(256); break;
                        case "Gradient": v = 255.0 * ((x + shift) % w) / Math.Max(1, w - 1); break;
                        default: v = ((x + shift) / 16) % 2 == 0 ? 220 : 40; break;   // Bars
                    }
                    if (_noise > 0) v += (_random.NextDouble() * 2 - 1) * _noise;
                    row[x] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
                }
            }
            double sigma = Math.Abs(_focus - _trueFocus) / 10.0;
            if (sigma > 0.05)
            {
                var blurred = new Image(w, h, PixelType.UInt8, 1);
                try
                {
                    using (var src = InspectionView.Core.Interop.MatInterop.AsMat(img))
                    using (var dst = InspectionView.Core.Interop.MatInterop.AsMat(blurred, forWrite: true))
                        OpenCvSharp.Cv2.GaussianBlur(src, dst, new OpenCvSharp.Size(0, 0), sigma, sigma, OpenCvSharp.BorderTypes.Reflect101);
                }
                catch
                {
                    blurred.Release();
                    throw;
                }
                img.Release();
                img = blurred;
            }
            img.Metadata["FrameId"] = n;
            img.Metadata["Timestamp"] = DateTime.UtcNow;
            return img;
        }

        protected override void CloseCore()
        {
        }
    }
}
