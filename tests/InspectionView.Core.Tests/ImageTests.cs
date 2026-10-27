using System;
using InspectionView.Core.Interop;
using InspectionView.Core.Types;
using OpenCvSharp;
using Xunit;

namespace InspectionView.Core.Tests
{
    public class ImageTests
    {
        [Fact]
        public void Create_StrideIsAlignedAndPixelsRoundTrip()
        {
            using var img = Image.Create(13, 7, PixelType.UInt16, channels: 1, clear: true);

            Assert.Equal(0, img.Stride % 64);
            Assert.True(img.Stride >= 13 * 2);

            img.SetPixel(3, 4, 1234);
            Assert.Equal(1234, img.GetPixel(3, 4));

            var row = img.GetRowReadOnly<ushort>(4);
            Assert.Equal(1234, row[3]);
        }

        [Fact]
        public void ReduceDomain_SharesStorage()
        {
            using var img = Image.Create(100, 100, PixelType.UInt8, clear: true);
            using var roi = Region.FromRectangle(new RectI(10, 10, 20, 20));
            using var reduced = img.ReduceDomain(roi);

            Assert.Same(img.Storage, reduced.Storage);
            Assert.Equal(new RectI(10, 10, 20, 20), reduced.DomainBounds);

            img.SetPixel(15, 15, 77);
            Assert.Equal(77, reduced.GetPixel(15, 15));
        }

        [Fact]
        public void MatInterop_WrapsWithoutCopy()
        {
            using var img = Image.Create(32, 8, PixelType.UInt8, channels: 3, clear: true);
            using (var mat = MatInterop.AsMat(img, forWrite: true))
            {
                mat.Set(2, 5, new Vec3b(1, 2, 3));
            }

            Assert.Equal(1, img.GetPixel(5, 2, 0));
            Assert.Equal(2, img.GetPixel(5, 2, 1));
            Assert.Equal(3, img.GetPixel(5, 2, 2));
        }

        [Fact]
        public void MatInterop_ToImage_CopiesFloatData()
        {
            using var mat = new Mat(4, 6, MatType.CV_32FC1, Scalar.All(0));
            mat.Set(1, 2, 3.5f);

            using var img = MatInterop.ToImage(mat);
            Assert.Equal(PixelType.Float32, img.PixelType);
            Assert.Equal(6, img.Width);
            Assert.Equal(4, img.Height);
            Assert.Equal(3.5, img.GetPixel(2, 1), 6);
        }

        [Fact]
        public unsafe void FromBuffer_And_CopyTo_RoundTrip()
        {
            // 행 간격이 넓은 외부 버퍼 (5x3, 1채널, stride 8)
            var src = new byte[8 * 3];
            for (int y = 0; y < 3; y++)
                for (int x = 0; x < 5; x++)
                    src[y * 8 + x] = (byte)(y * 10 + x);

            fixed (byte* p = src)
            {
                using var img = Image.FromBuffer((IntPtr)p, 5, 3, 8, PixelType.UInt8);
                Assert.Equal(21, img.GetPixel(1, 2));

                var dst = new byte[5 * 3];
                fixed (byte* d = dst)
                    img.CopyTo((IntPtr)d);
                Assert.Equal(21, dst[2 * 5 + 1]);
                Assert.Equal(4, dst[4]);
            }
        }

        [Fact]
        public void ObjectArray_ReleasesItems()
        {
            var a = Image.Create(2, 2, PixelType.UInt8);
            var b = Image.Create(2, 2, PixelType.UInt8);

            var arr = new ObjectArray<Image>(new[] { a, b });
            Assert.Equal(2, arr.Count);
            Assert.Equal(2, a.RefCount);

            arr.Release();
            Assert.Equal(1, a.RefCount);
            Assert.False(a.IsDisposed);

            a.Release();
            b.Release();
            Assert.True(a.IsDisposed);
        }

        [Fact]
        public void DisplayRange_IgnoresOutliersAndNaN()
        {
            // 0..1 사이 값 + 튀는 값 몇 개 (그림자 쪽 기울기 폭주) + NaN
            using var img = Image.Create(200, 200, PixelType.Float32, 1, clear: true);
            for (int y = 0; y < 200; y++)
            {
                var row = img.GetRow<float>(y);
                for (int x = 0; x < 200; x++)
                    row[x] = (float)(x / 199.0);
            }
            img.GetRow<float>(5)[5] = 10000f;
            img.GetRow<float>(6)[6] = -10000f;
            img.GetRow<float>(7)[7] = float.NaN;
            img.GetRow<float>(8)[8] = float.PositiveInfinity;

            Assert.True(DisplayRange.TryRobust(img, 1_000_000, 1, out double lo, out double hi));
            Assert.InRange(lo, 0, 0.02);
            Assert.InRange(hi, 0.98, 1.0);

            // 대부분 같은 값이면 전체 범위, 전부 같으면 폭 1
            using var flat = Image.Create(50, 50, PixelType.Float32, 1, clear: true);
            flat.GetRow<float>(0)[0] = 3f;
            Assert.True(DisplayRange.TryRobust(flat, 10_000, 1, out lo, out hi));
            Assert.Equal(0, lo);
            Assert.Equal(3, hi);
            flat.GetRow<float>(0)[0] = 0f;
            Assert.True(DisplayRange.TryRobust(flat, 10_000, 1, out lo, out hi));
            Assert.Equal(1, hi - lo);
        }
    }
}
