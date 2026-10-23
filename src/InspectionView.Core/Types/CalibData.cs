using System;
using System.Collections.Generic;
using InspectionView.Core.Memory;

namespace InspectionView.Core.Types
{
    /// <summary>
    /// 카메라 캘리브레이션 데이터 (Halcon calib data 에 해당): 캘리브 판 정의와 지금까지 찾은 관측(이미지마다 판 점들의 이미지 좌표).
    /// <c>AddCalibImage</c> 가 관측을 누적하고 <c>CalibrateCamera</c> 가 그것으로 카메라 파라미터를 구한다.
    /// </summary>
    public sealed class CalibData : IconicObject
    {
        private readonly List<PointD[]> _observations = new List<PointD[]>();

        public CalibData(string patternType, int columns, int rows, double spacing)
        {
            PatternType = patternType;
            Columns = columns;
            Rows = rows;
            Spacing = spacing;
        }

        public override string TypeName => "CalibData";

        /// <summary>chessboard / circles / asymmetric_circles.</summary>
        public string PatternType { get; }
        /// <summary>가로 점 수 (체커보드는 안쪽 코너 수).</summary>
        public int Columns { get; }
        /// <summary>세로 점 수.</summary>
        public int Rows { get; }
        /// <summary>점 간격 (mm 등 원하는 단위, 결과 좌표도 이 단위).</summary>
        public double Spacing { get; }

        /// <summary>관측된 이미지 크기 (첫 관측에서 정해진다).</summary>
        public int ImageWidth { get; private set; }
        public int ImageHeight { get; private set; }

        /// <summary>관측 (OpenCV 픽셀 좌표: 픽셀 중심 = 정수). 판 점 순서는 <see cref="ObjectPoints"/> 와 같다.</summary>
        public IReadOnlyList<PointD[]> Observations => _observations;
        public int Count => _observations.Count;

        public void Add(PointD[] imagePoints, int width, int height)
        {
            if (imagePoints.Length != Columns * Rows)
                throw new ArgumentException($"Expected {Columns * Rows} points.", nameof(imagePoints));
            if (_observations.Count == 0)
            {
                ImageWidth = width;
                ImageHeight = height;
            }
            else if (width != ImageWidth || height != ImageHeight)
            {
                throw new Errors.VisionException(Errors.ErrorCode.SizeMismatch, $"All calibration images must have the same size ({ImageWidth}x{ImageHeight}).");
            }
            _observations.Add(imagePoints);
        }

        public void Clear() => _observations.Clear();

        /// <summary>판 좌표계의 점 (Z = 0 평면, X 오른쪽, Y 아래). 행 우선 순서 (OpenCV 검출 순서와 같다).</summary>
        public (double x, double y)[] ObjectPoints() => PatternPoints(PatternType, Columns, Rows, Spacing);

        public static (double x, double y)[] PatternPoints(string patternType, int columns, int rows, double spacing)
        {
            var pts = new (double, double)[columns * rows];
            bool asym = string.Equals(patternType, "asymmetric_circles", StringComparison.OrdinalIgnoreCase);
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < columns; c++)
                    pts[r * columns + c] = asym ? ((2 * c + r % 2) * spacing, r * spacing) : (c * spacing, r * spacing);
            return pts;
        }

        protected override void ReleaseResources(bool disposing)
        {
            _observations.Clear();
        }

        public override string ToString() => $"CalibData {PatternType} {Columns}x{Rows} @ {Spacing}, {Count} image(s)";
    }
}
