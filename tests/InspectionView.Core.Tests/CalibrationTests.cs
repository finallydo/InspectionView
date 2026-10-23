using System;
using System.Collections.Generic;
using System.Linq;
using InspectionView.Core.Backends;
using InspectionView.Core.Interop;
using InspectionView.Core.Operators.Calibration;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;
using OpenCvSharp;
using Xunit;

namespace InspectionView.Core.Tests
{
    /// <summary>
    /// 카메라 캘리브레이션: 알려진 카메라(초점거리, 주점, 렌즈 왜곡)로 체커보드 판을 여러 자세에서 렌더링하고
    /// 캘리브레이션/포즈/좌표 변환/보정 영상이 정답을 되찾는지 확인한다.
    /// </summary>
    public class CalibrationTests
    {
        private static OperatorResult Exec(string op, params object?[] args)
        {
            var ctx = new OperatorContext(new BackendSettings { DefaultBackend = BackendKind.Cpu }, new ExecutionProfiler());
            var result = OperatorInvoker.Invoke(OperatorRegistry.Default, op, args, ctx);
            Assert.True(result.Success, $"{op} failed: {result.Code} {result.Message}");
            return result;
        }

        private static T Out<T>(OperatorResult r, string name) => Assert.IsType<T>(r.Outputs[name]);
        private static double[] D(OperatorResult r, string name) => Out<TupleValue>(r, name).ToDoubleArray();

        // 정답 카메라 (OpenCV 픽셀 좌표)
        private const int W = 640, H = 480;
        private const double Fx = 800, Fy = 805, Cx = 319.5, Cy = 239.5, K1 = -0.25, K2 = 0.08, P1 = 0.001, P2 = -0.0008;
        private static readonly TupleValue TrueCamera = new double[] { Fx, Fy, Cx, Cy, K1, K2, P1, P2, 0, W, H };

        // 판: 안쪽 코너 9x6, 간격 10 mm (정사각형 10x7 칸 + 흰 테두리 10 mm)
        private const int Cols = 9, Rows = 6;
        private const double Spacing = 10;

        private static Mat? _texture;

        /// <summary>판 텍스처: 10 px/mm. 안쪽 코너 (i, j) = 판 좌표 (10i, 10j) mm = 텍스처 (199.5 + 100 i, 199.5 + 100 j) (픽셀 중심 = 정수).</summary>
        private static Mat Texture()
        {
            if (_texture != null) return _texture;
            var t = new Mat(900, 1200, MatType.CV_8UC1, Scalar.All(255));
            for (int r = 0; r < 7; r++)
                for (int c = 0; c < 10; c++)
                    if ((r + c) % 2 == 0)
                        Cv2.Rectangle(t, new Rect(100 + c * 100, 100 + r * 100, 100, 100), Scalar.All(0), -1);
            var blurred = new Mat();
            Cv2.GaussianBlur(t, blurred, new Size(0, 0), 2.0);
            t.Dispose();
            return _texture = blurred;
        }

        /// <summary>자세: 판 중심 (40, 25) mm 를 카메라 앞 (ox, oy, z) 에 두고 회전 (rx, ry, rz) 라디안.</summary>
        private static PoseModel Pose(double rx, double ry, double rz, double ox, double oy, double z)
        {
            var rot = new PoseModel(0, 0, 0, rx, ry, rz);
            rot.Apply(40, 25, 0, out double a, out double b, out double c);
            return new PoseModel(ox - a, oy - b, z - c, rx, ry, rz);
        }

        /// <summary>정답 카메라로 판을 렌더링한다 (각 픽셀: 역왜곡 → 시선 → 판 평면 교점 → 텍스처).</summary>
        private static Image Render(PoseModel pose, double k1 = K1, double k2 = K2, double p1 = P1, double p2 = P2)
        {
            var mapX = new float[W * H];
            var mapY = new float[W * H];
            for (int v = 0; v < H; v++)
                for (int u = 0; u < W; u++)
                {
                    double xd = (u - Cx) / Fx, yd = (v - Cy) / Fy, x = xd, y = yd;
                    for (int it = 0; it < 30; it++)
                    {
                        double r2 = x * x + y * y, rad = 1 + k1 * r2 + k2 * r2 * r2;
                        x = (xd - (2 * p1 * x * y + p2 * (r2 + 2 * x * x))) / rad;
                        y = (yd - (p1 * (r2 + 2 * y * y) + 2 * p2 * x * y)) / rad;
                    }
                    double c3x = pose.R(0, 2), c3y = pose.R(1, 2), c3z = pose.R(2, 2);
                    double lambda = (c3x * pose.Tx + c3y * pose.Ty + c3z * pose.Tz) / (c3x * x + c3y * y + c3z);
                    double px = lambda * x - pose.Tx, py = lambda * y - pose.Ty, pz = lambda - pose.Tz;
                    double X = pose.R(0, 0) * px + pose.R(1, 0) * py + pose.R(2, 0) * pz;
                    double Y = pose.R(0, 1) * px + pose.R(1, 1) * py + pose.R(2, 1) * pz;
                    mapX[v * W + u] = (float)(199.5 + 10 * X);
                    mapY[v * W + u] = (float)(199.5 + 10 * Y);
                }
            using var mx = new Mat(H, W, MatType.CV_32FC1);
            using var my = new Mat(H, W, MatType.CV_32FC1);
            mx.SetArray(mapX);
            my.SetArray(mapY);
            using var dst = new Mat();
            Cv2.Remap(Texture(), dst, mx, my, InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(255));
            return MatInterop.ToImage(dst);
        }

        /// <summary>판 네 모서리(테두리 포함)가 이미지 안에 있는지 진단 문자열.</summary>
        private static string Extent(PoseModel pose)
        {
            var cam = new CameraModel(Fx, Fy, Cx, Cy, K1, K2, P1, P2, 0, W, H);
            var corners = new[] { (-20.0, -20.0), (100.0, -20.0), (100.0, 70.0), (-20.0, 70.0) };
            return string.Join(" ", corners.Select(c =>
            {
                pose.Apply(c.Item1, c.Item2, 0, out double x, out double y, out double z);
                cam.Project(x, y, z, out double u, out double v);
                return $"({u:F0},{v:F0})";
            }));
        }

        private static readonly PoseModel[] Views =
        {
            Pose(0.00, 0.00, 0.00, 0, 0, 260),
            Pose(0.35, 0.00, 0.10, -30, -20, 250),
            Pose(-0.35, 0.10, -0.10, 30, 20, 270),
            Pose(0.10, 0.40, 0.20, -40, 25, 280),
            Pose(0.15, -0.40, -0.20, 40, -25, 260),
            Pose(-0.30, -0.25, 0.30, 35, 30, 300),
            Pose(0.30, 0.30, -0.30, -35, -30, 290),
            Pose(0.00, 0.20, 0.50, 0, 12, 280),
            Pose(0.25, -0.15, -0.50, 0, -12, 280),
            Pose(-0.20, 0.35, 0.00, -45, 0, 275),
            Pose(0.20, -0.30, 0.15, 45, 0, 265),
            Pose(0.40, 0.25, 0.05, 20, -10, 240),
        };

        private static (TupleValue cam, double rms, CalibData data) Calibrate(string distortion = "radial_tangential")
        {
            var data = Out<CalibData>(Exec("CreateCalibData", "chessboard", Cols, Rows, Spacing), "CalibData");
            for (int v = 0; v < Views.Length; v++)
            {
                using var img = Render(Views[v]);
                var a = Exec("AddCalibImage", data, img);
                Assert.True(Out<TupleValue>(a, "Found").B(), $"pattern not found in rendered view {v} ({Extent(Views[v])})");
            }
            var c = Exec("CalibrateCamera", data, distortion);
            return (Out<TupleValue>(c, "CameraParam"), D(c, "Error")[0], data);
        }

        [Fact]
        public void CalibrateCamera_RecoversIntrinsicsAndDistortion()
        {
            var (cam, rms, data) = Calibrate();
            using (data)
            {
                Assert.Equal(Views.Length, data.Count);
                var p = cam.ToDoubleArray();
                Assert.True(rms < 0.2, $"rms {rms:F3} px");
                Assert.InRange(p[0], Fx - 8, Fx + 8);
                Assert.InRange(p[1], Fy - 8, Fy + 8);
                Assert.InRange(p[2], Cx - 4, Cx + 4);
                Assert.InRange(p[3], Cy - 4, Cy + 4);
                Assert.InRange(p[4], K1 - 0.03, K1 + 0.03);
                Assert.InRange(p[5], K2 - 0.06, K2 + 0.06);
                Assert.Equal(W, p[9]);
                Assert.Equal(H, p[10]);
            }
        }

        [Fact]
        public void ImagePointsToWorldPlane_MeasuresInMillimeters()
        {
            var (cam, _, data) = Calibrate();
            data.Dispose();
            // 새 자세의 판: 포즈를 구하고 검출 코너를 판 좌표(mm)로 되돌린다
            using var img = Render(Pose(0.2, -0.25, 0.4, 10, -5, 270));
            var pose = Out<TupleValue>(Exec("FindPatternPose", img, cam, "chessboard", Cols, Rows, Spacing), "Pose");
            var found = Exec("FindCalibPattern", img, "chessboard", Cols, Rows);
            var w = Exec("ImagePointsToWorldPlane", cam, pose, found.Outputs["Row"], found.Outputs["Column"]);
            var xs = D(w, "X");
            var ys = D(w, "Y");
            double maxErr = 0;
            for (int j = 0; j < Rows; j++)
                for (int i = 0; i < Cols; i++)
                {
                    int k = j * Cols + i;
                    maxErr = Math.Max(maxErr, Math.Sqrt(Math.Pow(xs[k] - i * Spacing, 2) + Math.Pow(ys[k] - j * Spacing, 2)));
                }
            Assert.True(maxErr < 0.1, $"max world error {maxErr:F3} mm");

            // 역변환과 왕복
            var back = Exec("WorldPlaneToImagePoints", cam, pose, w.Outputs["X"], w.Outputs["Y"]);
            var r0 = Out<TupleValue>(found, "Row").ToDoubleArray();
            var r1 = D(back, "Row");
            for (int k = 0; k < r0.Length; k++)
                Assert.InRange(r1[k] - r0[k], -1e-6, 1e-6);
        }

        [Fact]
        public void UndistortImage_StraightensLines()
        {
            // 판이 화면을 크게 채우는 자세 (가장자리 왜곡이 크다)
            using var img = Render(Pose(0, 0, 0, 0, 0, 175));
            double before = MaxRowCurvature(img);
            using var und = Out<Image>(Exec("UndistortImage", img, TrueCamera), "ImageResult");
            double after = MaxRowCurvature(und);
            Assert.True(before > 0.5, $"distorted rows should be curved ({before:F2} px)");
            Assert.True(after < 0.1, $"undistorted rows should be straight ({after:F3} px)");
        }

        /// <summary>판 각 행의 코너들이 직선에서 벗어난 최대 거리 (px).</summary>
        private static double MaxRowCurvature(Image img)
        {
            var f = Exec("FindCalibPattern", img, "chessboard", Cols, Rows);
            Assert.True(Out<TupleValue>(f, "Found").B());
            var rows = D(f, "Row");
            var cols = D(f, "Column");
            double worst = 0;
            for (int j = 0; j < Rows; j++)
            {
                var px = Enumerable.Range(0, Cols).Select(i => cols[j * Cols + i]).ToArray();
                var py = Enumerable.Range(0, Cols).Select(i => rows[j * Cols + i]).ToArray();
                // 양 끝점을 잇는 직선과의 거리
                double x0 = px[0], y0 = py[0], x1 = px[Cols - 1], y1 = py[Cols - 1];
                double len = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
                for (int i = 1; i < Cols - 1; i++)
                    worst = Math.Max(worst, Math.Abs((x1 - x0) * (y0 - py[i]) - (x0 - px[i]) * (y1 - y0)) / len);
            }
            return worst;
        }

        [Fact]
        public void ImageToWorldPlane_RectifiesToMetricTopView()
        {
            var pose = Pose(0.3, -0.2, 0.25, 5, 10, 260);
            using var img = Render(pose);
            const double scale = 0.25, ox = -15, oy = -15;
            int w = (int)((Cols - 1) * Spacing + 30) * 4, h = (int)((Rows - 1) * Spacing + 30) * 4;
            using var top = Out<Image>(Exec("ImageToWorldPlane", img, TrueCamera, pose.ToTuple(), w, h, scale, ox, oy), "ImageResult");
            Assert.Equal(w, top.Width);
            var f = Exec("FindCalibPattern", top, "chessboard", Cols, Rows);
            Assert.True(Out<TupleValue>(f, "Found").B());
            var rows = D(f, "Row");
            var cols = D(f, "Column");
            double maxErr = 0;
            for (int j = 0; j < Rows; j++)
                for (int i = 0; i < Cols; i++)
                {
                    int k = j * Cols + i;
                    // 출력 픽셀 경계 좌표 c ↔ 월드 OriginX + c * Scale
                    maxErr = Math.Max(maxErr, Math.Abs(cols[k] - (i * Spacing - ox) / scale));
                    maxErr = Math.Max(maxErr, Math.Abs(rows[k] - (j * Spacing - oy) / scale));
                }
            Assert.True(maxErr < 0.25, $"max rectified error {maxErr:F3} px ({maxErr * scale:F3} mm)");
        }

        [Fact]
        public void AddCalibImage_SkipsImagesWithoutPattern()
        {
            using var data = Out<CalibData>(Exec("CreateCalibData", "chessboard", Cols, Rows, Spacing), "CalibData");
            using var blank = Image.Create(W, H, PixelType.UInt8, 1, clear: true);
            var a = Exec("AddCalibImage", data, blank);
            Assert.False(Out<TupleValue>(a, "Found").B());
            Assert.Equal(0, data.Count);
            Assert.Equal(0L, Out<TupleValue>(a, "Count").L());

            // 3장 미만이면 캘리브레이션을 거부
            var ctx = new OperatorContext(new BackendSettings { DefaultBackend = BackendKind.Cpu }, new ExecutionProfiler());
            var r = OperatorInvoker.Invoke(OperatorRegistry.Default, "CalibrateCamera", new object?[] { data }, ctx);
            Assert.False(r.Success);
        }
    }
}
