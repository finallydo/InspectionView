using System.Linq;
using InspectionView.Core.Types;
using Xunit;

namespace InspectionView.Core.Tests
{
    public class RegionTests
    {
        [Fact]
        public void Rectangle_HasExpectedAreaAndBoundingBox()
        {
            using var r = Region.FromRectangle(new RectI(10, 20, 30, 40));

            Assert.Equal(30L * 40, r.Area);
            Assert.Equal(new RectI(10, 20, 30, 40), r.BoundingBox);
            Assert.Equal(40, r.RunCount);
            Assert.True(r.Contains(10, 20));
            Assert.True(r.Contains(39, 59));
            Assert.False(r.Contains(40, 59));
            Assert.False(r.Contains(10, 60));
        }

        [Fact]
        public void Constructor_MergesOverlappingAndAdjacentRuns()
        {
            using var r = new Region(new[]
            {
                new Run(0, 5, 10),
                new Run(0, 0, 4),     // 인접
                new Run(0, 8, 12),    // 겹침
                new Run(0, 20, 25),   // 분리
            });

            Assert.Equal(2, r.RunCount);
            Assert.Equal(new Run(0, 0, 12), r.Runs[0]);
            Assert.Equal(new Run(0, 20, 25), r.Runs[1]);
        }

        [Fact]
        public void SetOperations_Work()
        {
            using var a = Region.FromRectangle(new RectI(0, 0, 10, 10));
            using var b = Region.FromRectangle(new RectI(5, 5, 10, 10));

            using var inter = a.Intersection(b);
            using var union = a.Union(b);
            using var diff = a.Difference(b);

            Assert.Equal(25, inter.Area);
            Assert.Equal(new RectI(5, 5, 5, 5), inter.BoundingBox);
            Assert.Equal(100 + 100 - 25, union.Area);
            Assert.Equal(75, diff.Area);
            Assert.True(diff.Contains(0, 0));
            Assert.False(diff.Contains(7, 7));
        }

        [Fact]
        public void Mask_RoundTrip()
        {
            using var a = Region.FromRectangle(new RectI(2, 3, 4, 5));
            using var b = Region.FromRectangle(new RectI(10, 10, 1, 1));
            using var r = a.Union(b);

            var mask = r.ToMask(16, 16);
            Assert.Equal(16, mask.Rows);
            Assert.Equal(16, mask.Cols);
            Assert.Equal(255, mask.At<byte>(3, 2));
            Assert.Equal(0, mask.At<byte>(2, 2));
            Assert.Equal(255, mask.At<byte>(10, 10));

            using var back = Region.FromMask(mask);
            Assert.Equal(r.Area, back.Area);
            Assert.Equal(r.Runs.ToArray(), back.Runs.ToArray());

            // 같은 크기 요청은 캐시를 돌려준다
            Assert.Same(mask, r.ToMask(16, 16));
        }

        [Fact]
        public void ReferenceCounting_ReleasesAtZero()
        {
            var r = Region.FromRectangle(new RectI(0, 0, 2, 2));
            Assert.Equal(1, r.RefCount);

            r.AddRef();
            Assert.Equal(2, r.RefCount);

            r.Release();
            Assert.False(r.IsDisposed);

            r.Release();
            Assert.True(r.IsDisposed);
        }
    }
}
