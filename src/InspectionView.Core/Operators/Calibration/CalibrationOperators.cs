using System;
using System.Collections.Generic;
using System.Linq;
using InspectionView.Core.Backends;
using InspectionView.Core.Errors;
using InspectionView.Core.Interop;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;
using OpenCvSharp;
using ErrorCode = InspectionView.Core.Errors.ErrorCode;

namespace InspectionView.Core.Operators.Calibration
{
    // 규약
    // - 이미지 좌표 Row/Column 은 매칭/측정 연산자와 같은 픽셀 경계 좌표 (픽셀 (x, y) 의 중심 = (x + 0.5, y + 0.5)).
    // - CameraParam = [Fx, Fy, Cx, Cy, K1, K2, P1, P2, K3, Width, Height] (OpenCV 핀홀 + 브라운-콘래디 왜곡, Cx/Cy 는 픽셀 중심 = 정수인 OpenCV 좌표).
    // - Pose = [Tx, Ty, Tz, Rx, Ry, Rz]: 판(월드) 좌표 → 카메라 좌표 (X_cam = R X + T), R 은 로드리게스 벡터(라디안), T 는 판 간격 단위(mm).
    // - 판(월드) 좌표: 첫 점 = 원점, X = 열 방향, Y = 행 방향, Z = 0 평면 (판 안쪽이 +Z).

    [Operator("FindCalibPattern", Category = "Calibration", Description = "Finds a calibration pattern (chessboard inner corners or circle grid) in the image: subpixel point positions in row-major order.")]
    public sealed class FindCalibPattern : OperatorBase
    {
        [Input] public Image? Image { get; set; }
        [Input(Description = "chessboard, circles or asymmetric_circles.", Default = "chessboard", Choices = new[] { "chessboard", "circles", "asymmetric_circles" })] public string PatternType { get; set; } = "chessboard";
        [Input(Description = "Points per row (chessboard: inner corners).", Default = 9, Min = 2)] public int Columns { get; set; } = 9;
        [Input(Description = "Points per column (chessboard: inner corners).", Default = 6, Min = 2)] public int Rows { get; set; } = 6;
        [Output] public TupleValue? Found { get; private set; }
        [Output] public TupleValue? Row { get; private set; }
        [Output] public TupleValue? Column { get; private set; }

        protected override void Validate()
        {
            Require(Image, nameof(Image));
            CalibOps.CheckPattern(PatternType);
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var pts = CalibOps.Detect(Image!, PatternType, Columns, Rows);
            Found = pts != null;
            Row = (pts ?? Array.Empty<PointD>()).Select(p => p.Y + 0.5).ToArray();
            Column = (pts ?? Array.Empty<PointD>()).Select(p => p.X + 0.5).ToArray();
        }
    }

    [Operator("CreateCalibData", Category = "Calibration", Description = "Creates a calibration data object for a plate with the given pattern; add views with AddCalibImage, then CalibrateCamera.")]
    public sealed class CreateCalibData : OperatorBase
    {
        [Input(Description = "chessboard, circles or asymmetric_circles.", Default = "chessboard", Choices = new[] { "chessboard", "circles", "asymmetric_circles" })] public string PatternType { get; set; } = "chessboard";
        [Input(Description = "Points per row (chessboard: inner corners).", Default = 9, Min = 2)] public int Columns { get; set; } = 9;
        [Input(Description = "Points per column (chessboard: inner corners).", Default = 6, Min = 2)] public int Rows { get; set; } = 6;
        [Input(Description = "Distance between neighboring points (e.g. mm). Results are in this unit.", Default = 10.0, Min = 0)] public double Spacing { get; set; } = 10;
        [Output] public CalibData? CalibData { get; private set; }

        protected override void Validate()
        {
            CalibOps.CheckPattern(PatternType);
            if (Spacing <= 0) throw VisionException.InvalidArgument(nameof(Spacing), "must be > 0");
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx) => CalibData = new CalibData(PatternType.ToLowerInvariant(), Columns, Rows, Spacing);
    }

    [Operator("AddCalibImage", Category = "Calibration", Description = "Finds the plate in the image and, if found, adds the view to the calibration data. Use 10 or more views with the plate tilted and covering the whole field of view.")]
    public sealed class AddCalibImage : OperatorBase
    {
        [Input] public CalibData? CalibData { get; set; }
        [Input] public Image? Image { get; set; }
        [Output(Description = "Whether the plate was found (and the view added).")] public TupleValue? Found { get; private set; }
        [Output] public TupleValue? Row { get; private set; }
        [Output] public TupleValue? Column { get; private set; }
        [Output(Description = "Number of views in the calibration data.")] public TupleValue? Count { get; private set; }

        protected override void Validate()
        {
            Require(CalibData, nameof(CalibData));
            Require(Image, nameof(Image));
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var data = CalibData!;
            var img = Image!;
            var pts = CalibOps.Detect(img, data.PatternType, data.Columns, data.Rows);
            if (pts != null)
                data.Add(pts, img.Width, img.Height);
            Found = pts != null;
            Row = (pts ?? Array.Empty<PointD>()).Select(p => p.Y + 0.5).ToArray();
            Column = (pts ?? Array.Empty<PointD>()).Select(p => p.X + 0.5).ToArray();
            Count = (long)data.Count;
        }
    }

    [Operator("CalibrateCamera", Category = "Calibration", Description = "Computes the camera parameters (focal lengths, principal point, lens distortion) and the plate pose of every view from the calibration data. Error is the RMS reprojection error in pixels.")]
    public sealed class CalibrateCamera : OperatorBase
    {
        [Input] public CalibData? CalibData { get; set; }
        [Input(Description = "Distortion terms to estimate: none, radial (K1, K2), radial_tangential (K1, K2, P1, P2), full (K1, K2, P1, P2, K3).",
            Default = "radial_tangential", Choices = new[] { "none", "radial", "radial_tangential", "full" })] public string Distortion { get; set; } = "radial_tangential";
        [Output(Description = "[Fx, Fy, Cx, Cy, K1, K2, P1, P2, K3, Width, Height].")] public TupleValue? CameraParam { get; private set; }
        [Output(Description = "Plate pose of every view: 6 values per view [Tx, Ty, Tz, Rx, Ry, Rz].")] public TupleValue? Poses { get; private set; }
        [Output(Description = "RMS reprojection error in pixels.")] public TupleValue? Error { get; private set; }

        protected override void Validate()
        {
            var data = Require(CalibData, nameof(CalibData));
            if (data.Count < 3)
                throw new VisionException(ErrorCode.InvalidArgument, $"At least 3 views are needed (have {data.Count}); 10 or more tilted views give a stable result.");
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var data = CalibData!;
            var obj = data.ObjectPoints().Select(p => new Point3f((float)p.x, (float)p.y, 0)).ToArray();
            var objectPoints = Enumerable.Repeat(obj, data.Count).ToList();
            var imagePoints = data.Observations.Select(o => o.Select(p => new Point2f((float)p.X, (float)p.Y)).ToArray()).ToList();

            var flags = CalibrationFlags.None;
            switch ((Distortion ?? "").ToLowerInvariant())
            {
                case "none": flags |= CalibrationFlags.FixK1 | CalibrationFlags.FixK2 | CalibrationFlags.FixK3 | CalibrationFlags.ZeroTangentDist; break;
                case "radial": flags |= CalibrationFlags.FixK3 | CalibrationFlags.ZeroTangentDist; break;
                case "radial_tangential": flags |= CalibrationFlags.FixK3; break;
                case "full": break;
                default: throw VisionException.InvalidArgument(nameof(Distortion), "must be none, radial, radial_tangential or full");
            }

            var k = new double[3, 3];
            var dist = new double[5];
            double rms = Cv2.CalibrateCamera(objectPoints, imagePoints, new Size(data.ImageWidth, data.ImageHeight), k, dist,
                out var rvecs, out var tvecs, flags, new TermCriteria(CriteriaTypes.Count | CriteriaTypes.Eps, 100, 1e-9));

            var cam = new CameraModel(k[0, 0], k[1, 1], k[0, 2], k[1, 2], dist[0], dist[1], dist[2], dist[3], dist[4], data.ImageWidth, data.ImageHeight);
            CameraParam = cam.ToTuple();
            var poses = new List<double>();
            for (int i = 0; i < rvecs.Length; i++)
                poses.AddRange(new[] { tvecs[i].Item0, tvecs[i].Item1, tvecs[i].Item2, rvecs[i].Item0, rvecs[i].Item1, rvecs[i].Item2 });
            Poses = poses.ToArray();
            Error = rms;
        }
    }

    [Operator("FindPatternPose", Category = "Calibration", Description = "Finds the calibration plate in the image and computes its pose for the calibrated camera. Place the plate on the measuring plane to define the world coordinate system for ImagePointsToWorldPlane.")]
    public sealed class FindPatternPose : OperatorBase
    {
        [Input] public Image? Image { get; set; }
        [Input(Description = "Camera parameters from CalibrateCamera.")] public TupleValue? CameraParam { get; set; }
        [Input(Description = "chessboard, circles or asymmetric_circles.", Default = "chessboard", Choices = new[] { "chessboard", "circles", "asymmetric_circles" })] public string PatternType { get; set; } = "chessboard";
        [Input(Default = 9, Min = 2)] public int Columns { get; set; } = 9;
        [Input(Default = 6, Min = 2)] public int Rows { get; set; } = 6;
        [Input(Default = 10.0, Min = 0)] public double Spacing { get; set; } = 10;
        [Output(Description = "[Tx, Ty, Tz, Rx, Ry, Rz] (plate → camera).")] public TupleValue? Pose { get; private set; }
        [Output(Description = "RMS reprojection error in pixels.")] public TupleValue? Error { get; private set; }

        protected override void Validate()
        {
            Require(Image, nameof(Image));
            CameraModel.FromTuple(Require(CameraParam, nameof(CameraParam)));
            CalibOps.CheckPattern(PatternType);
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var cam = CameraModel.FromTuple(CameraParam!);
            var pts = CalibOps.Detect(Image!, PatternType, Columns, Rows)
                ?? throw new VisionException(ErrorCode.EmptyObject, "Calibration pattern not found in the image.");
            var obj = CalibData.PatternPoints(PatternType, Columns, Rows, Spacing);
            var pose = CalibOps.SolvePose(cam, obj, pts);
            Pose = pose.ToTuple();
            Error = CalibOps.ReprojectionError(cam, pose, obj, pts);
        }
    }

    [Operator("ImagePointsToWorldPlane", Category = "Calibration", Description = "Transforms image points (row, column) into world coordinates on the plane Z = 0 of the pose (e.g. mm on the plate plane), correcting the lens distortion.")]
    public sealed class ImagePointsToWorldPlane : OperatorBase
    {
        [Input] public TupleValue? CameraParam { get; set; }
        [Input] public TupleValue? Pose { get; set; }
        [Input] public TupleValue? Row { get; set; }
        [Input] public TupleValue? Column { get; set; }
        [Output] public TupleValue? X { get; private set; }
        [Output] public TupleValue? Y { get; private set; }

        protected override void Validate()
        {
            CameraModel.FromTuple(Require(CameraParam, nameof(CameraParam)));
            PoseModel.FromTuple(Require(Pose, nameof(Pose)));
            if (Require(Row, nameof(Row)).Count != Require(Column, nameof(Column)).Count)
                throw new VisionException(ErrorCode.SizeMismatch, "Row and Column must have the same length.");
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var cam = CameraModel.FromTuple(CameraParam!);
            var pose = PoseModel.FromTuple(Pose!);
            var rows = Row!.ToDoubleArray();
            var cols = Column!.ToDoubleArray();
            var xs = new double[rows.Length];
            var ys = new double[rows.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                if (!CalibOps.PixelToPlane(cam, pose, cols[i] - 0.5, rows[i] - 0.5, out xs[i], out ys[i]))
                    throw new VisionException(ErrorCode.InvalidArgument, $"Point {i} does not see the plane (ray parallel to it).");
            }
            X = xs;
            Y = ys;
        }
    }

    [Operator("WorldPlaneToImagePoints", Category = "Calibration", Description = "Projects world points (X, Y on the plane Z = 0 of the pose) into the image (row, column), including the lens distortion.")]
    public sealed class WorldPlaneToImagePoints : OperatorBase
    {
        [Input] public TupleValue? CameraParam { get; set; }
        [Input] public TupleValue? Pose { get; set; }
        [Input] public TupleValue? X { get; set; }
        [Input] public TupleValue? Y { get; set; }
        [Output] public TupleValue? Row { get; private set; }
        [Output] public TupleValue? Column { get; private set; }

        protected override void Validate()
        {
            CameraModel.FromTuple(Require(CameraParam, nameof(CameraParam)));
            PoseModel.FromTuple(Require(Pose, nameof(Pose)));
            if (Require(X, nameof(X)).Count != Require(Y, nameof(Y)).Count)
                throw new VisionException(ErrorCode.SizeMismatch, "X and Y must have the same length.");
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var cam = CameraModel.FromTuple(CameraParam!);
            var pose = PoseModel.FromTuple(Pose!);
            var xs = X!.ToDoubleArray();
            var ys = Y!.ToDoubleArray();
            var rows = new double[xs.Length];
            var cols = new double[xs.Length];
            for (int i = 0; i < xs.Length; i++)
            {
                pose.Apply(xs[i], ys[i], 0, out double cx, out double cy, out double cz);
                cam.Project(cx, cy, cz, out double u, out double v);
                cols[i] = u + 0.5;
                rows[i] = v + 0.5;
            }
            Row = rows;
            Column = cols;
        }
    }

    [Operator("UndistortImage", Category = "Calibration", Description = "Removes the lens distortion: the result is the image an ideal pinhole camera with the same focal lengths and principal point would take.")]
    public sealed class UndistortImage : OperatorBase
    {
        [Input] public Image? Image { get; set; }
        [Input] public TupleValue? CameraParam { get; set; }
        [Output] public Image? ImageResult { get; private set; }

        protected override void Validate()
        {
            Require(Image, nameof(Image));
            CameraModel.FromTuple(Require(CameraParam, nameof(CameraParam)));
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var cam = CameraModel.FromTuple(CameraParam!);
            var img = Image!;
            int w = img.Width, h = img.Height;
            using (var mapX = new Mat(h, w, MatType.CV_32FC1))
            using (var mapY = new Mat(h, w, MatType.CV_32FC1))
            {
                var mx = new float[w * h];
                var my = new float[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        // 이상 카메라의 픽셀 → 정규 좌표 → 왜곡 → 실제 픽셀
                        double nx = (x - cam.Cx) / cam.Fx, ny = (y - cam.Cy) / cam.Fy;
                        cam.Distort(nx, ny, out double dx, out double dy);
                        mx[y * w + x] = (float)(cam.Fx * dx + cam.Cx);
                        my[y * w + x] = (float)(cam.Fy * dy + cam.Cy);
                    }
                mapX.SetArray(mx);
                mapY.SetArray(my);
                ImageResult = CalibOps.Remap(img, mapX, mapY, w, h);
            }
        }
    }

    [Operator("ImageToWorldPlane", Category = "Calibration", Description = "Rectifies the image onto the world plane of the pose (top view without perspective and lens distortion): output pixel (column, row) shows world point (OriginX + column * Scale, OriginY + row * Scale) in pixel-boundary coordinates, so measurements in the result times Scale are world units.")]
    public sealed class ImageToWorldPlane : OperatorBase
    {
        [Input] public Image? Image { get; set; }
        [Input] public TupleValue? CameraParam { get; set; }
        [Input] public TupleValue? Pose { get; set; }
        [Input(Description = "Output width in pixels.", Min = 1)] public int Width { get; set; }
        [Input(Description = "Output height in pixels.", Min = 1)] public int Height { get; set; }
        [Input(Description = "World size of one output pixel (e.g. mm/pixel).", Min = 0)] public double Scale { get; set; }
        [Input(Description = "World X of the output image's left edge.", Default = 0.0)] public double OriginX { get; set; }
        [Input(Description = "World Y of the output image's top edge.", Default = 0.0)] public double OriginY { get; set; }
        [Output] public Image? ImageResult { get; private set; }

        protected override void Validate()
        {
            Require(Image, nameof(Image));
            CameraModel.FromTuple(Require(CameraParam, nameof(CameraParam)));
            PoseModel.FromTuple(Require(Pose, nameof(Pose)));
            if (Width <= 0 || Height <= 0) throw VisionException.InvalidArgument(nameof(Width), "Width and Height must be > 0");
            if (Scale <= 0) throw VisionException.InvalidArgument(nameof(Scale), "must be > 0");
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var cam = CameraModel.FromTuple(CameraParam!);
            var pose = PoseModel.FromTuple(Pose!);
            int w = Width, h = Height;
            using (var mapX = new Mat(h, w, MatType.CV_32FC1))
            using (var mapY = new Mat(h, w, MatType.CV_32FC1))
            {
                var mx = new float[w * h];
                var my = new float[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        // 출력 픽셀 중심 (x + 0.5, y + 0.5) 의 월드 점
                        double wx = OriginX + (x + 0.5) * Scale, wy = OriginY + (y + 0.5) * Scale;
                        pose.Apply(wx, wy, 0, out double cx, out double cy, out double cz);
                        if (cz <= 1e-9)
                        {
                            mx[y * w + x] = my[y * w + x] = -1;
                            continue;
                        }
                        cam.Project(cx, cy, cz, out double u, out double v);
                        mx[y * w + x] = (float)u;
                        my[y * w + x] = (float)v;
                    }
                mapX.SetArray(mx);
                mapY.SetArray(my);
                ImageResult = CalibOps.Remap(Image!, mapX, mapY, w, h);
            }
        }
    }

    /// <summary>핀홀 + 브라운-콘래디 왜곡 (OpenCV 와 같은 모델). 픽셀 좌표는 OpenCV 규약 (픽셀 중심 = 정수).</summary>
    public readonly struct CameraModel
    {
        public CameraModel(double fx, double fy, double cx, double cy, double k1, double k2, double p1, double p2, double k3, int width, int height)
        {
            Fx = fx; Fy = fy; Cx = cx; Cy = cy; K1 = k1; K2 = k2; P1 = p1; P2 = p2; K3 = k3; Width = width; Height = height;
        }

        public double Fx { get; }
        public double Fy { get; }
        public double Cx { get; }
        public double Cy { get; }
        public double K1 { get; }
        public double K2 { get; }
        public double P1 { get; }
        public double P2 { get; }
        public double K3 { get; }
        public int Width { get; }
        public int Height { get; }

        public static CameraModel FromTuple(TupleValue t)
        {
            if (t == null || t.Count != 11)
                throw new VisionException(ErrorCode.InvalidArgument, "CameraParam must have 11 values [Fx, Fy, Cx, Cy, K1, K2, P1, P2, K3, Width, Height] (from CalibrateCamera).");
            var v = t.ToDoubleArray();
            if (v[0] <= 0 || v[1] <= 0)
                throw new VisionException(ErrorCode.InvalidArgument, "CameraParam focal lengths must be > 0.");
            return new CameraModel(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], (int)v[9], (int)v[10]);
        }

        public TupleValue ToTuple() => new double[] { Fx, Fy, Cx, Cy, K1, K2, P1, P2, K3, Width, Height };

        /// <summary>정규 좌표 (x/z, y/z) → 왜곡된 정규 좌표.</summary>
        public void Distort(double x, double y, out double xd, out double yd)
        {
            double r2 = x * x + y * y;
            double radial = 1 + r2 * (K1 + r2 * (K2 + r2 * K3));
            xd = x * radial + 2 * P1 * x * y + P2 * (r2 + 2 * x * x);
            yd = y * radial + P1 * (r2 + 2 * y * y) + 2 * P2 * x * y;
        }

        /// <summary>왜곡된 정규 좌표 → 정규 좌표 (고정점 반복).</summary>
        public void Undistort(double xd, double yd, out double x, out double y)
        {
            x = xd;
            y = yd;
            for (int i = 0; i < 20; i++)
            {
                double r2 = x * x + y * y;
                double radial = 1 + r2 * (K1 + r2 * (K2 + r2 * K3));
                double dx = 2 * P1 * x * y + P2 * (r2 + 2 * x * x);
                double dy = P1 * (r2 + 2 * y * y) + 2 * P2 * x * y;
                double nx = (xd - dx) / radial, ny = (yd - dy) / radial;
                if (Math.Abs(nx - x) < 1e-12 && Math.Abs(ny - y) < 1e-12) { x = nx; y = ny; break; }
                x = nx;
                y = ny;
            }
        }

        /// <summary>카메라 좌표 → 픽셀 (왜곡 포함).</summary>
        public void Project(double x, double y, double z, out double u, out double v)
        {
            Distort(x / z, y / z, out double xd, out double yd);
            u = Fx * xd + Cx;
            v = Fy * yd + Cy;
        }

        public double[,] Matrix() => new double[,] { { Fx, 0, Cx }, { 0, Fy, Cy }, { 0, 0, 1 } };
        public double[] DistCoeffs() => new[] { K1, K2, P1, P2, K3 };
    }

    /// <summary>강체 변환 X_cam = R X + T (R 은 로드리게스 벡터).</summary>
    public readonly struct PoseModel
    {
        private readonly double[] _r;

        public PoseModel(double tx, double ty, double tz, double rx, double ry, double rz)
        {
            Tx = tx; Ty = ty; Tz = tz; Rx = rx; Ry = ry; Rz = rz;
            _r = Rodrigues(rx, ry, rz);
        }

        public double Tx { get; }
        public double Ty { get; }
        public double Tz { get; }
        public double Rx { get; }
        public double Ry { get; }
        public double Rz { get; }

        /// <summary>회전 행렬 (행 우선 3x3).</summary>
        public double R(int row, int col) => _r[row * 3 + col];

        public static PoseModel FromTuple(TupleValue t)
        {
            if (t == null || t.Count != 6)
                throw new VisionException(ErrorCode.InvalidArgument, "Pose must have 6 values [Tx, Ty, Tz, Rx, Ry, Rz] (from FindPatternPose or CalibrateCamera).");
            var v = t.ToDoubleArray();
            return new PoseModel(v[0], v[1], v[2], v[3], v[4], v[5]);
        }

        public TupleValue ToTuple() => new double[] { Tx, Ty, Tz, Rx, Ry, Rz };

        public void Apply(double x, double y, double z, out double cx, out double cy, out double cz)
        {
            cx = _r[0] * x + _r[1] * y + _r[2] * z + Tx;
            cy = _r[3] * x + _r[4] * y + _r[5] * z + Ty;
            cz = _r[6] * x + _r[7] * y + _r[8] * z + Tz;
        }

        private static double[] Rodrigues(double rx, double ry, double rz)
        {
            double theta = Math.Sqrt(rx * rx + ry * ry + rz * rz);
            if (theta < 1e-12) return new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
            double kx = rx / theta, ky = ry / theta, kz = rz / theta;
            double c = Math.Cos(theta), s = Math.Sin(theta), v = 1 - c;
            return new[]
            {
                c + kx * kx * v,      kx * ky * v - kz * s, kx * kz * v + ky * s,
                ky * kx * v + kz * s, c + ky * ky * v,      ky * kz * v - kx * s,
                kz * kx * v - ky * s, kz * ky * v + kx * s, c + kz * kz * v,
            };
        }
    }

    internal static class CalibOps
    {
        public static void CheckPattern(string? type)
        {
            switch ((type ?? "").ToLowerInvariant())
            {
                case "chessboard":
                case "circles":
                case "asymmetric_circles":
                    return;
                default:
                    throw VisionException.InvalidArgument("PatternType", "must be chessboard, circles or asymmetric_circles");
            }
        }

        /// <summary>검출용 8bit 그레이.</summary>
        private static Mat Gray8(Image img)
        {
            var gray = new Mat();
            using (var view = MatInterop.AsMat(img))
            {
                Mat src = view;
                Mat? tmp = null;
                try
                {
                    if (img.Channels == 3) { tmp = new Mat(); Cv2.CvtColor(view, tmp, ColorConversionCodes.BGR2GRAY); src = tmp; }
                    else if (img.Channels == 4) { tmp = new Mat(); Cv2.CvtColor(view, tmp, ColorConversionCodes.BGRA2GRAY); src = tmp; }
                    else if (img.Channels != 1) { tmp = new Mat(); Cv2.ExtractChannel(view, tmp, 0); src = tmp; }

                    if (src.Depth() == MatType.CV_8U) src.CopyTo(gray);
                    else
                    {
                        Cv2.MinMaxLoc(src, out double min, out double max);
                        double scale = max > min ? 255.0 / (max - min) : 1;
                        src.ConvertTo(gray, MatType.CV_8U, scale, -min * scale);
                    }
                }
                finally
                {
                    tmp?.Dispose();
                }
            }
            return gray;
        }

        /// <summary>판 점 검출 (OpenCV 픽셀 좌표, 행 우선). 못 찾으면 null.</summary>
        public static PointD[]? Detect(Image img, string patternType, int columns, int rows)
        {
            CheckPattern(patternType);
            using (var gray = Gray8(img))
            {
                var size = new Size(columns, rows);
                Point2f[] pts;
                switch (patternType.ToLowerInvariant())
                {
                    case "chessboard":
                        if (!Cv2.FindChessboardCorners(gray, size, out pts, ChessboardFlags.AdaptiveThresh | ChessboardFlags.NormalizeImage))
                            return null;
                        pts = Cv2.CornerSubPix(gray, pts, new Size(5, 5), new Size(-1, -1), new TermCriteria(CriteriaTypes.Count | CriteriaTypes.Eps, 50, 1e-3));
                        break;
                    case "circles":
                        if (!Cv2.FindCirclesGrid(gray, size, out pts, FindCirclesGridFlags.SymmetricGrid))
                            return null;
                        break;
                    default:
                        if (!Cv2.FindCirclesGrid(gray, size, out pts, FindCirclesGridFlags.AsymmetricGrid))
                            return null;
                        break;
                }
                if (pts.Length != columns * rows) return null;
                return pts.Select(p => new PointD(p.X, p.Y)).ToArray();
            }
        }

        public static PoseModel SolvePose(CameraModel cam, (double x, double y)[] obj, PointD[] img)
        {
            var o = obj.Select(p => new Point3f((float)p.x, (float)p.y, 0)).ToArray();
            var i = img.Select(p => new Point2f((float)p.X, (float)p.Y)).ToArray();
            var rvec = new double[3];
            var tvec = new double[3];
            Cv2.SolvePnP(o, i, cam.Matrix(), cam.DistCoeffs(), ref rvec, ref tvec);
            if (rvec.Concat(tvec).Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                throw new VisionException(ErrorCode.InvalidArgument, "Could not compute the plate pose.");
            return new PoseModel(tvec[0], tvec[1], tvec[2], rvec[0], rvec[1], rvec[2]);
        }

        public static double ReprojectionError(CameraModel cam, PoseModel pose, (double x, double y)[] obj, PointD[] img)
        {
            double sum = 0;
            for (int k = 0; k < obj.Length; k++)
            {
                pose.Apply(obj[k].x, obj[k].y, 0, out double cx, out double cy, out double cz);
                cam.Project(cx, cy, cz, out double u, out double v);
                sum += (u - img[k].X) * (u - img[k].X) + (v - img[k].Y) * (v - img[k].Y);
            }
            return Math.Sqrt(sum / Math.Max(1, obj.Length));
        }

        /// <summary>OpenCV 픽셀 (u, v) → 포즈의 Z = 0 평면 위 (x, y). 시선이 평면과 평행하면 false.</summary>
        public static bool PixelToPlane(CameraModel cam, PoseModel pose, double u, double v, out double x, out double y)
        {
            cam.Undistort((u - cam.Cx) / cam.Fx, (v - cam.Cy) / cam.Fy, out double nx, out double ny);
            // 카메라 좌표의 시선 d = (nx, ny, 1). 평면 법선(카메라 좌표) = R 의 셋째 열 c3. λ = (c3·T) / (c3·d)
            double c3x = pose.R(0, 2), c3y = pose.R(1, 2), c3z = pose.R(2, 2);
            double denom = c3x * nx + c3y * ny + c3z;
            x = y = 0;
            if (Math.Abs(denom) < 1e-12) return false;
            double lambda = (c3x * pose.Tx + c3y * pose.Ty + c3z * pose.Tz) / denom;
            double px = lambda * nx - pose.Tx, py = lambda * ny - pose.Ty, pz = lambda - pose.Tz;
            // X = Rᵀ (P - T)
            x = pose.R(0, 0) * px + pose.R(1, 0) * py + pose.R(2, 0) * pz;
            y = pose.R(0, 1) * px + pose.R(1, 1) * py + pose.R(2, 1) * pz;
            return true;
        }

        public static Image Remap(Image img, Mat mapX, Mat mapY, int w, int h)
        {
            using (var src = MatInterop.AsMat(img))
            using (var dst = new Mat())
            {
                Cv2.Remap(src, dst, mapX, mapY, InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
                return MatInterop.ToImage(dst);
            }
        }
    }
}
