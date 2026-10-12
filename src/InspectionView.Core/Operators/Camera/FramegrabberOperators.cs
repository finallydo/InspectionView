using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using InspectionView.Core.Backends;
using InspectionView.Core.Cameras;
using InspectionView.Core.Errors;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;

namespace InspectionView.Core.Operators.Camera
{
    /// <summary>
    /// 스크립트: <c>Info = InfoFramegrabber()</c> → Providers / Available / Devices. Provider 를 주면 그 공급자의 장치만.
    /// </summary>
    [Operator("InfoFramegrabber", Category = "Camera", Description = "Lists camera providers (with availability) and the devices they enumerate.")]
    public sealed class InfoFramegrabber : OperatorBase
    {
        [Input(Description = "Provider name, or empty for all.", Default = "")] public string Provider { get; set; } = "";

        [Output(Description = "Provider names.")] public TupleValue? Providers { get; private set; }
        [Output(Description = "Whether each provider's SDK is available.")] public TupleValue? Available { get; private set; }
        [Output(Description = "Devices as 'Provider:DeviceId'.")] public TupleValue? Devices { get; private set; }
        [Output(Description = "Display name of each device.")] public TupleValue? Names { get; private set; }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var registry = CameraRegistry.Default;
            var providers = string.IsNullOrEmpty(Provider) ? registry.Providers : new[] { registry.Require(Provider) };
            Providers = providers.Select(p => p.Name).ToArray();
            Available = new TupleValue(providers.Select(p => (object)p.IsAvailable).ToArray());

            var ids = new List<string>();
            var names = new List<string>();
            foreach (var p in providers)
            {
                if (!p.IsAvailable) continue;
                IReadOnlyList<CameraInfo> devices;
                try { devices = p.Enumerate(); }
                catch (Exception ex) { ctx.Info($"{p.Name}: enumeration failed: {ex.Message}"); continue; }
                foreach (var d in devices)
                {
                    ids.Add($"{d.Provider}:{d.DeviceId}");
                    names.Add(d.DisplayName);
                }
            }
            Devices = ids.ToArray();
            Names = names.ToArray();
        }
    }

    /// <summary>
    /// 스크립트: <c>Cam = OpenFramegrabber('Simulation')</c>, <c>Cam = OpenFramegrabber('File', 'C:/images/*.png', 'Loop=true')</c>.
    /// </summary>
    [Operator("OpenFramegrabber", Category = "Camera", Description = "Opens a camera: Provider (File, Simulation or a vendor plugin), Device (id/serial/path, empty = first) and 'Key=Value;...' options.")]
    public sealed class OpenFramegrabber : OperatorBase
    {
        [Input(Description = "Provider name (see InfoFramegrabber).")] public string Provider { get; set; } = "";
        [Input(Description = "Device id, serial, or path. Empty = first device.", Default = "")] public string Device { get; set; } = "";
        [Input(Description = "Provider options as 'Key=Value;Key2=Value2'.", Default = "")] public string Options { get; set; } = "";

        [Output(Description = "Camera handle.")] public CameraHandle? Camera { get; private set; }

        protected override void Validate()
        {
            RequireNotEmpty(Provider, nameof(Provider));
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var cam = CameraRegistry.Default.Open(Provider, Device, Options);
            Camera = new CameraHandle(cam);
            ctx.Info($"OpenFramegrabber: {cam}");
        }
    }

    [Operator("CloseFramegrabber", Category = "Camera", Description = "Closes the camera (the handle variable becomes unusable).")]
    public sealed class CloseFramegrabber : OperatorBase
    {
        [Input] public CameraHandle? Camera { get; set; }

        protected override void Validate() => Require(Camera, nameof(Camera));

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx) => Camera!.Close();
    }

    /// <summary>스크립트: <c>Img = GrabImage(Cam)</c>. 연속 취득 중이면 다음 프레임을 기다린다.</summary>
    [Operator("GrabImage", Category = "Camera", Description = "Grabs one frame synchronously (waits for the next frame while acquiring continuously).")]
    public sealed class GrabImage : OperatorBase
    {
        [Input] public CameraHandle? Camera { get; set; }
        [Input(Description = "Timeout in milliseconds.", Default = 5000, Min = 0)] public int Timeout { get; set; }

        [Output] public Image? Image { get; private set; }

        protected override void Validate() => Require(Camera, nameof(Camera));

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            Image = Camera!.Camera.Grab(Timeout);
        }
    }

    [Operator("GrabImageStart", Category = "Camera", Description = "Starts continuous acquisition in the background (use GrabImageAsync / GrabImage to fetch frames).")]
    public sealed class GrabImageStart : OperatorBase
    {
        [Input] public CameraHandle? Camera { get; set; }

        protected override void Validate() => Require(Camera, nameof(Camera));

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx) => Camera!.Camera.StartAcquisition();
    }

    [Operator("GrabImageStop", Category = "Camera", Description = "Stops continuous acquisition.")]
    public sealed class GrabImageStop : OperatorBase
    {
        [Input] public CameraHandle? Camera { get; set; }

        protected override void Validate() => Require(Camera, nameof(Camera));

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx) => Camera!.Camera.StopAcquisition();
    }

    /// <summary>스크립트: <c>Img = GrabImageAsync(Cam)</c>. 연속 취득의 가장 최근 프레임 (없으면 Timeout 까지 기다린 뒤 다음 프레임).</summary>
    [Operator("GrabImageAsync", Category = "Camera", Description = "Returns the latest frame of the continuous acquisition (starts it if needed); waits up to Timeout for the first frame.")]
    public sealed class GrabImageAsync : OperatorBase
    {
        [Input] public CameraHandle? Camera { get; set; }
        [Input(Description = "Timeout in milliseconds for the first frame.", Default = 5000, Min = 0)] public int Timeout { get; set; }

        [Output] public Image? Image { get; private set; }

        protected override void Validate() => Require(Camera, nameof(Camera));

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var cam = Camera!.Camera;
            if (!cam.IsAcquiring)
                cam.StartAcquisition();
            var latest = cam.GetLatestFrame();
            Image = latest ?? cam.Grab(Timeout);
        }
    }

    /// <summary>스크립트: <c>SetFramegrabberParam(Cam, 'ExposureTime', 2000)</c>.</summary>
    [Operator("SetFramegrabberParam", Category = "Camera", Description = "Sets a camera parameter (use GetFramegrabberParam(Cam, 'AvailableParamNames') to list them).")]
    public sealed class SetFramegrabberParam : OperatorBase
    {
        [Input] public CameraHandle? Camera { get; set; }
        [Input(Name = "Name", Description = "Parameter name.")] public string ParamName { get; set; } = "";
        [Input(Description = "New value (omit for command parameters).", Optional = true)] public TupleValue? Value { get; set; }

        protected override void Validate()
        {
            Require(Camera, nameof(Camera));
            RequireNotEmpty(ParamName, "Name");
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            Camera!.Camera.SetParameter(ParamName, Value);
        }
    }

    /// <summary>
    /// 스크립트: <c>Exp = GetFramegrabberParam(Cam, 'ExposureTime')</c>.
    /// 특수 이름: 'AvailableParamNames' (모든 파라미터 이름), 'Provider', 'Device', 'Model', 'Serial', 'IsAcquiring'.
    /// </summary>
    [Operator("GetFramegrabberParam", Category = "Camera", Description = "Reads a camera parameter. Special names: AvailableParamNames, Provider, Device, Model, Serial, IsAcquiring.")]
    public sealed class GetFramegrabberParam : OperatorBase
    {
        [Input] public CameraHandle? Camera { get; set; }
        [Input(Name = "Name", Description = "Parameter name.")] public string ParamName { get; set; } = "";

        [Output] public TupleValue? Value { get; private set; }

        protected override void Validate()
        {
            Require(Camera, nameof(Camera));
            RequireNotEmpty(ParamName, "Name");
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var cam = Camera!.Camera;
            switch (ParamName.ToLowerInvariant())
            {
                case "availableparamnames": Value = cam.Parameters.Select(p => p.Name).ToArray(); return;
                case "provider": Value = cam.Info.Provider; return;
                case "device": Value = cam.Info.DeviceId; return;
                case "model": Value = cam.Info.Model; return;
                case "serial": Value = cam.Info.Serial; return;
                case "isacquiring": Value = cam.IsAcquiring; return;
            }
            Value = ToTuple(cam.GetParameter(ParamName));
        }

        internal static TupleValue ToTuple(object? v)
        {
            switch (v)
            {
                case null: return TupleValue.Empty;
                case TupleValue t: return t;
                case bool b: return b;
                case string s: return s;
                case double d: return d;
                case float f: return (double)f;
                case long l: return l;
                case int i: return (long)i;
                case short sh: return (long)sh;
                case byte by: return (long)by;
                case uint ui: return (long)ui;
                case ulong ul: return (double)ul;
                case Enum e: return e.ToString();
                case IEnumerable<string> ss: return ss.ToArray();
                default: return Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
            }
        }
    }
}
