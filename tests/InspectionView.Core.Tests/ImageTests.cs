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
    }
}
