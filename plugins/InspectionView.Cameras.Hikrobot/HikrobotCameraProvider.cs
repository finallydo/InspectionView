using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using InspectionView.Core.Cameras;
using InspectionView.Core.Errors;
using InspectionView.Core.Types;
#if IV_HAS_MVS
using MvCamCtrl.NET;
#endif

namespace InspectionView.Cameras.Hikrobot
{
    /// <summary>
    /// Hikrobot MVS 공급자 (MvCameraControl.Net). SDK 가 빌드 시 있으면 실제 구현, 없으면 사용 불가 스텁.
    /// 미검증: SDK 가 설치된 PC 에서 컴파일/실행 확인 필요.
    /// </summary>
    public sealed class HikrobotCameraProvider : ICameraProvider
    {
        public const string ProviderName = "Hikrobot";
        public string Name => ProviderName;

#if IV_HAS_MVS
        public bool IsAvailable => true;
        public string? UnavailableReason => null;

        internal static List<(MyCamera.MV_CC_DEVICE_INFO info, CameraInfo desc)> EnumerateDevices()
        {
            var result = new List<(MyCamera.MV_CC_DEVICE_INFO, CameraInfo)>();
            var list = new MyCamera.MV_CC_DEVICE_INFO_LIST();
            int ret = MyCamera.MV_CC_EnumDevices_NET(MyCamera.MV_GIGE_DEVICE | MyCamera.MV_USB_DEVICE, ref list);
            if (ret != MyCamera.MV_OK)
                throw new VisionException(ErrorCode.DeviceError, $"MV_CC_EnumDevices failed: 0x{ret:X}");
            for (int i = 0; i < list.nDeviceNum; i++)
            {
                var info = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(list.pDeviceInfo[i], typeof(MyCamera.MV_CC_DEVICE_INFO))!;
                string serial = "", model = "", user = "";
                if (info.nTLayerType == MyCamera.MV_GIGE_DEVICE)
                {
                    var g = (MyCamera.MV_GIGE_DEVICE_INFO)MyCamera.ByteToStruct(info.SpecialInfo.stGigEInfo, typeof(MyCamera.MV_GIGE_DEVICE_INFO));
                    serial = g.chSerialNumber; model = g.chModelName; user = g.chUserDefinedName;
                }
                else if (info.nTLayerType == MyCamera.MV_USB_DEVICE)
                {
                    var u = (MyCamera.MV_USB3_DEVICE_INFO)MyCamera.ByteToStruct(info.SpecialInfo.stUsb3VInfo, typeof(MyCamera.MV_USB3_DEVICE_INFO));
                    serial = u.chSerialNumber; model = u.chModelName; user = u.chUserDefinedName;
                }
                result.Add((info, new CameraInfo(ProviderName, serial, model, serial, string.IsNullOrEmpty(user) ? null : $"{user} ({serial})")));
            }
            return result;
        }

        public IReadOnlyList<CameraInfo> Enumerate()
        {
            var list = new List<CameraInfo>();
            foreach (var d in EnumerateDevices()) list.Add(d.desc);
            return list;
        }

        public ICamera Open(string deviceId, IReadOnlyDictionary<string, string> options)
        {
            var devices = EnumerateDevices();
            if (devices.Count == 0)
                throw new VisionException(ErrorCode.CameraNotFound, "No Hikrobot camera found.");
            foreach (var d in devices)
            {
                if (string.IsNullOrEmpty(deviceId) || d.desc.Serial == deviceId || d.desc.DisplayName.StartsWith(deviceId + " ", StringComparison.Ordinal))
                    return new HikrobotCamera(d.info, d.desc, options);
            }
            throw new VisionException(ErrorCode.CameraNotFound, $"Hikrobot camera '{deviceId}' not found.");
        }
#else
        public bool IsAvailable => false;
        public string? UnavailableReason => "Hikrobot MVS SDK not found at build time. Install MVS (sets MVCAM_SDK_PATH) and rebuild, or pass -p:IVMvsAssembly=<path to MvCameraControl.Net.dll>.";
        public IReadOnlyList<CameraInfo> Enumerate() => Array.Empty<CameraInfo>();
        public ICamera Open(string deviceId, IReadOnlyDictionary<string, string> options)
            => throw new VisionException(ErrorCode.CameraSdkNotAvailable, UnavailableReason!);
#endif
    }

#if IV_HAS_MVS
    public sealed class HikrobotCamera : CameraBase
    {
        private readonly MyCamera _camera = new MyCamera();
        private readonly object _grabLock = new object();
        private bool _grabbing;
        private IntPtr _convertBuffer = IntPtr.Zero;
        private int _convertSize;

        public HikrobotCamera(MyCamera.MV_CC_DEVICE_INFO info, CameraInfo desc, IReadOnlyDictionary<string, string> options)
            : base(desc)
        {
            Check(_camera.MV_CC_CreateDevice_NET(ref info), "MV_CC_CreateDevice");
            Check(_camera.MV_CC_OpenDevice_NET(), "MV_CC_OpenDevice");
            if (info.nTLayerType == MyCamera.MV_GIGE_DEVICE)
            {
                int packet = _camera.MV_CC_GetOptimalPacketSize_NET();
                if (packet > 0) _camera.MV_CC_SetIntValueEx_NET("GevSCPSPacketSize", packet);
            }
            _camera.MV_CC_SetEnumValueByString_NET("TriggerMode", "Off");
            foreach (var kv in options)
            {
                try { SetParameter(kv.Key, kv.Value); } catch { /* best-effort */ }
            }

            RegisterNode("ExposureTime", CameraParameterType.Float, "Exposure time (us).");
            RegisterNode("Gain", CameraParameterType.Float, "Gain (dB).");
            RegisterNode("Width", CameraParameterType.Integer, "ROI width.");
            RegisterNode("Height", CameraParameterType.Integer, "ROI height.");
            RegisterNode("OffsetX", CameraParameterType.Integer, "ROI offset X.");
            RegisterNode("OffsetY", CameraParameterType.Integer, "ROI offset Y.");
            RegisterNode("PixelFormat", CameraParameterType.Enum, "Pixel format.");
            RegisterNode("TriggerMode", CameraParameterType.Enum, "On/Off.");
            RegisterNode("TriggerSource", CameraParameterType.Enum, "Software, Line0 ...");
            RegisterNode("TriggerSoftware", CameraParameterType.Command, "Software trigger.");
            RegisterNode("AcquisitionFrameRate", CameraParameterType.Float, "Frame rate (Hz).");
            RegisterNode("AcquisitionFrameRateEnable", CameraParameterType.Boolean, "Enable frame rate control.");
        }

        private static void Check(int ret, string what)
        {
            if (ret != MyCamera.MV_OK)
                throw new VisionException(ErrorCode.CameraOpenFailed, $"{what} failed: 0x{ret:X}");
        }

        private void RegisterNode(string name, CameraParameterType type, string description)
        {
            double min = double.NaN, max = double.NaN;
            switch (type)
            {
                case CameraParameterType.Float:
                {
                    var v = new MyCamera.MVCC_FLOATVALUE();
                    if (_camera.MV_CC_GetFloatValue_NET(name, ref v) != MyCamera.MV_OK) return;
                    min = v.fMin; max = v.fMax;
                    break;
                }
                case CameraParameterType.Integer:
                {
                    var v = new MyCamera.MVCC_INTVALUE_EX();
                    if (_camera.MV_CC_GetIntValueEx_NET(name, ref v) != MyCamera.MV_OK) return;
                    min = v.nMin; max = v.nMax;
                    break;
                }
                case CameraParameterType.Enum:
                {
                    var v = new MyCamera.MVCC_ENUMVALUE();
                    if (_camera.MV_CC_GetEnumValue_NET(name, ref v) != MyCamera.MV_OK) return;
                    break;
                }
                case CameraParameterType.Boolean:
                {
                    bool b = false;
                    if (_camera.MV_CC_GetBoolValue_NET(name, ref b) != MyCamera.MV_OK) return;
                    break;
                }
            }
            RegisterParameter(new CameraParameterInfo(name, type, description, false, min, max),
                () => ReadNode(name, type), v => WriteNode(name, type, v));
        }

        private object? ReadNode(string name, CameraParameterType type)
        {
            switch (type)
            {
                case CameraParameterType.Float:
                {
                    var v = new MyCamera.MVCC_FLOATVALUE();
                    Check2(_camera.MV_CC_GetFloatValue_NET(name, ref v), name);
                    return (double)v.fCurValue;
                }
                case CameraParameterType.Integer:
                {
                    var v = new MyCamera.MVCC_INTVALUE_EX();
                    Check2(_camera.MV_CC_GetIntValueEx_NET(name, ref v), name);
                    return v.nCurValue;
                }
                case CameraParameterType.Boolean:
                {
                    bool b = false;
                    Check2(_camera.MV_CC_GetBoolValue_NET(name, ref b), name);
                    return b;
                }
                case CameraParameterType.Enum:
                {
                    var v = new MyCamera.MVCC_ENUMVALUE();
                    Check2(_camera.MV_CC_GetEnumValue_NET(name, ref v), name);
                    var entry = new MyCamera.MVCC_ENUMENTRY { nValue = v.nCurValue };
                    if (_camera.MV_CC_GetEnumEntrySymbolic_NET(name, ref entry) == MyCamera.MV_OK)
                        return entry.chSymbolic;
                    return (long)v.nCurValue;
                }
                case CameraParameterType.String:
                {
                    var v = new MyCamera.MVCC_STRINGVALUE();
                    Check2(_camera.MV_CC_GetStringValue_NET(name, ref v), name);
                    return v.chCurValue;
                }
                default:
                    return null;
            }
        }

        private void WriteNode(string name, CameraParameterType type, object? value)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            string str = Convert.ToString(value, ci) ?? "";
            switch (type)
            {
                case CameraParameterType.Float: Check2(_camera.MV_CC_SetFloatValue_NET(name, (float)Convert.ToDouble(value, ci)), name); break;
                case CameraParameterType.Integer: Check2(_camera.MV_CC_SetIntValueEx_NET(name, Convert.ToInt64(value, ci)), name); break;
                case CameraParameterType.Boolean: Check2(_camera.MV_CC_SetBoolValue_NET(name, value is bool b ? b : str.Equals("true", StringComparison.OrdinalIgnoreCase) || str == "1"), name); break;
                case CameraParameterType.Enum: Check2(_camera.MV_CC_SetEnumValueByString_NET(name, str), name); break;
                case CameraParameterType.String: Check2(_camera.MV_CC_SetStringValue_NET(name, str), name); break;
                case CameraParameterType.Command: Check2(_camera.MV_CC_SetCommandValue_NET(name), name); break;
            }
        }

        private static void Check2(int ret, string name)
        {
            if (ret != MyCamera.MV_OK)
                throw new VisionException(ErrorCode.CameraParameterInvalid, $"Parameter '{name}': MVS error 0x{ret:X}");
        }

        /// <summary>등록되지 않은 이름: 타입을 모르므로 float → int → enum → bool → string 순으로 시도.</summary>
        public override object? GetParameter(string name)
        {
            if (HasRegisteredParameter(name)) return base.GetParameter(name);
            ThrowIfClosed();
            foreach (var t in new[] { CameraParameterType.Float, CameraParameterType.Integer, CameraParameterType.Enum, CameraParameterType.Boolean, CameraParameterType.String })
            {
                try { return ReadNode(name, t); } catch (VisionException) { }
            }
            throw new VisionException(ErrorCode.CameraParameterInvalid, $"Unknown MVS parameter '{name}'.");
        }

        public override void SetParameter(string name, object? value)
        {
            if (HasRegisteredParameter(name)) { base.SetParameter(name, value); return; }
            ThrowIfClosed();
            if (value is TupleValue t) value = t.IsEmpty ? null : t[0];
            if (value == null) { WriteNode(name, CameraParameterType.Command, null); return; }
            var order = value is string
                ? new[] { CameraParameterType.Enum, CameraParameterType.String, CameraParameterType.Float, CameraParameterType.Integer, CameraParameterType.Boolean }
                : new[] { CameraParameterType.Float, CameraParameterType.Integer, CameraParameterType.Boolean, CameraParameterType.Enum };
            VisionException? last = null;
            foreach (var t2 in order)
            {
                try { WriteNode(name, t2, value); return; } catch (VisionException ex) { last = ex; }
            }
            throw last ?? new VisionException(ErrorCode.CameraParameterInvalid, $"Unknown MVS parameter '{name}'.");
        }

        protected override Image GrabCore(int timeoutMs)
        {
            lock (_grabLock)
            {
                if (!_grabbing)
                {
                    Check(_camera.MV_CC_StartGrabbing_NET(), "MV_CC_StartGrabbing");
                    _grabbing = true;
                }
                var frame = new MyCamera.MV_FRAME_OUT();
                int ret = _camera.MV_CC_GetImageBuffer_NET(ref frame, Math.Max(1, timeoutMs));
                if (ret != MyCamera.MV_OK)
                    throw new VisionException(ErrorCode.CameraTimeout, $"No frame within {timeoutMs} ms (0x{ret:X}).");
                try
                {
                    var fi = frame.stFrameInfo;
                    int w = fi.nWidth, h = fi.nHeight;
                    Image img;
                    switch (fi.enPixelType)
                    {
                        case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8:
                            img = CameraFrames.FromBuffer(frame.pBufAddr, w, h, w, PixelType.UInt8); break;
                        case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono10:
                        case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono12:
                        case MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono16:
                            img = CameraFrames.FromBuffer(frame.pBufAddr, w, h, w * 2, PixelType.UInt16); break;
                        case MyCamera.MvGvspPixelType.PixelType_Gvsp_BGR8_Packed:
                            img = CameraFrames.FromBuffer(frame.pBufAddr, w, h, w * 3, PixelType.UInt8, 3); break;
                        case MyCamera.MvGvspPixelType.PixelType_Gvsp_RGB8_Packed:
                            img = CameraFrames.FromRgb8(frame.pBufAddr, w, h, w * 3); break;
                        case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerRG8:
                            img = CameraFrames.FromBayer(frame.pBufAddr, w, h, w, CameraFrames.BayerPattern.RG); break;
                        case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGR8:
                            img = CameraFrames.FromBayer(frame.pBufAddr, w, h, w, CameraFrames.BayerPattern.GR); break;
                        case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerBG8:
                            img = CameraFrames.FromBayer(frame.pBufAddr, w, h, w, CameraFrames.BayerPattern.BG); break;
                        case MyCamera.MvGvspPixelType.PixelType_Gvsp_BayerGB8:
                            img = CameraFrames.FromBayer(frame.pBufAddr, w, h, w, CameraFrames.BayerPattern.GB); break;
                        default:
                            img = ConvertToBgr8(ref frame); break;
                    }
                    img.Metadata["FrameId"] = (long)fi.nFrameNum;
                    img.Metadata["Timestamp"] = ((long)fi.nDevTimeStampHigh << 32) | fi.nDevTimeStampLow;
                    return img;
                }
                finally
                {
                    _camera.MV_CC_FreeImageBuffer_NET(ref frame);
                }
            }
        }

        /// <summary>기타 포맷(packed Bayer/YUV 등)은 SDK 변환으로 BGR8 로.</summary>
        private Image ConvertToBgr8(ref MyCamera.MV_FRAME_OUT frame)
        {
            var fi = frame.stFrameInfo;
            int size = fi.nWidth * fi.nHeight * 3;
            if (_convertBuffer == IntPtr.Zero || _convertSize < size)
            {
                if (_convertBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_convertBuffer);
                _convertBuffer = Marshal.AllocHGlobal(size);
                _convertSize = size;
            }
            var p = new MyCamera.MV_CC_PIXEL_CONVERT_PARAM
            {
                nWidth = fi.nWidth,
                nHeight = fi.nHeight,
                enSrcPixelType = fi.enPixelType,
                pSrcData = frame.pBufAddr,
                nSrcDataLen = fi.nFrameLen,
                enDstPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_BGR8_Packed,
                pDstBuffer = _convertBuffer,
                nDstBufferSize = (uint)size,
            };
            int ret = _camera.MV_CC_ConvertPixelType_NET(ref p);
            if (ret != MyCamera.MV_OK)
                throw new VisionException(ErrorCode.UnsupportedFormat, $"Pixel format {fi.enPixelType} conversion failed: 0x{ret:X}");
            return CameraFrames.FromBuffer(_convertBuffer, fi.nWidth, fi.nHeight, fi.nWidth * 3, PixelType.UInt8, 3);
        }

        protected override void CloseCore()
        {
            try { if (_grabbing) _camera.MV_CC_StopGrabbing_NET(); } catch { }
            _grabbing = false;
            try { _camera.MV_CC_CloseDevice_NET(); } catch { }
            try { _camera.MV_CC_DestroyDevice_NET(); } catch { }
            if (_convertBuffer != IntPtr.Zero) { Marshal.FreeHGlobal(_convertBuffer); _convertBuffer = IntPtr.Zero; }
        }
    }
#endif
}
