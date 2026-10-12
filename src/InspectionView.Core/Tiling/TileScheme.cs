using System;
using System.Collections.Generic;
using InspectionView.Core.Errors;
using InspectionView.Core.Types;

namespace InspectionView.Core.Tiling
{
    /// <summary>타일 하나. <see cref="Outer"/> 는 오버랩을 포함한 읽기 영역, <see cref="Inner"/> 는 결과를 기록할 유효 영역.</summary>
    public readonly struct Tile
    {
        public int Index { get; }
        public int Row { get; }
        public int Col { get; }
        public RectI Outer { get; }
        public RectI Inner { get; }

        public Tile(int index, int row, int col, RectI outer, RectI inner)
        {
            Index = index;
            Row = row;
            Col = col;
            Outer = outer;
            Inner = inner;
        }

        public override string ToString() => $"Tile #{Index} ({Row},{Col}) outer={Outer} inner={Inner}";
    }

    /// <summary>
    /// 대형 이미지 / 라인스캔 이미지를 엔진이 자동으로 나누어 처리하기 위한 타일 분할 계획.
    /// 필터 반경만큼 오버랩을 두어 경계 아티팩트를 막고, Inner 영역만 결과에 병합한다.
    /// </summary>
    public sealed class TileScheme
    {
        /// <summary>타일 처리를 시작하는 기본 픽셀 수 (약 64M 픽셀).</summary>
        public const long DefaultTileThreshold = 64L * 1024 * 1024;

        public int ImageWidth { get; }
        public int ImageHeight { get; }
        public int TileWidth { get; }
        public int TileHeight { get; }
        public int Overlap { get; }
        public int Rows { get; }
        public int Cols { get; }
        public IReadOnlyList<Tile> Tiles { get; }

        public TileScheme(int imageWidth, int imageHeight, int tileWidth, int tileHeight, int overlap)
        {
            if (imageWidth <= 0 || imageHeight <= 0)
                throw VisionException.InvalidArgument("imageSize", "must be > 0");
            if (tileWidth <= 0 || tileHeight <= 0)
                throw VisionException.InvalidArgument("tileSize", "must be > 0");
            if (overlap < 0)
                throw VisionException.InvalidArgument(nameof(overlap), "must be >= 0");

            ImageWidth = imageWidth;
            ImageHeight = imageHeight;
            TileWidth = Math.Min(tileWidth, imageWidth);
            TileHeight = Math.Min(tileHeight, imageHeight);
            Overlap = overlap;
            Cols = (imageWidth + TileWidth - 1) / TileWidth;
            Rows = (imageHeight + TileHeight - 1) / TileHeight;

            var tiles = new List<Tile>(Rows * Cols);
            var bounds = new RectI(0, 0, imageWidth, imageHeight);
            int index = 0;
            for (int r = 0; r < Rows; r++)
            {
                for (int c = 0; c < Cols; c++)
                {
                    var inner = new RectI(c * TileWidth, r * TileHeight, TileWidth, TileHeight).Intersect(bounds);
                    var outer = inner.Inflate(overlap, overlap).Intersect(bounds);
                    tiles.Add(new Tile(index++, r, c, outer, inner));
                }
            }
            Tiles = tiles;
        }

        /// <summary>이미지가 타일 처리 대상인지 (픽셀 수 기준).</summary>
        public static bool ShouldTile(Image image, long thresholdPixels = DefaultTileThreshold)
            => (long)image.Width * image.Height > thresholdPixels;

        /// <summary>라인스캔(세로로 긴) 이미지는 가로 전체 폭의 가로 스트립으로 나눈다.</summary>
        public static TileScheme ForLineScan(Image image, int stripHeight, int overlap)
            => new TileScheme(image.Width, image.Height, image.Width, stripHeight, overlap);

        /// <summary>정사각형에 가까운 기본 타일 분할.</summary>
        public static TileScheme ForArea(Image image, int tileSize, int overlap)
            => new TileScheme(image.Width, image.Height, tileSize, tileSize, overlap);
    }
}
