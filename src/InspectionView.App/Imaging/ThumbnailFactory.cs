using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using InspectionView.Core.Types;

namespace InspectionView.App.Imaging
{
    /// <summary>변수창 썸네일 생성 (Image, Region, 객체 배열의 첫 원소).</summary>
    public static class ThumbnailFactory
    {
        public const int Size = 64;

        public static BitmapSource? Create(object? value)
        {
            try
            {
                switch (value)
                {
                    case Image img when !img.IsDisposed:
                        return ImageBitmapConverter.ToBitmap(img, Size);
                    case Region reg when !reg.IsDisposed:
                        return FromRegion(reg);
                    case ObjectArray<Image> imgs when !imgs.IsDisposed && imgs.Count > 0:
                        return ImageBitmapConverter.ToBitmap(imgs[0], Size);
                    case ObjectArray<Region> regs when !regs.IsDisposed && regs.Count > 0:
                        return FromRegions(regs);
                    default:
                        return null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>바운딩 박스를 썸네일 크기로 축소해 영역 픽셀을 흰색으로 그린다.</summary>
        private static BitmapSource? FromRegion(Region region)
        {
            var bb = region.BoundingBox;
            if (bb.IsEmpty)
                return null;

            Prepare(bb, out int w, out int h, out double scale);
            var buf = new byte[w * h];
            foreach (var r in region.Runs)
            {
                int y = (int)((r.Row - bb.Y) * scale);
                if (y >= h) y = h - 1;
                int x0 = (int)((r.ColBegin - bb.X) * scale);
                int x1 = (int)((r.ColEnd - bb.X) * scale);
                if (x1 >= w) x1 = w - 1;
                for (int x = x0; x <= x1; x++)
                    buf[y * w + x] = 255;
            }
            return Finish(buf, w, h);
        }

        /// <summary>여러 영역을 서로 다른 밝기로 한 썸네일에 그린다.</summary>
        private static BitmapSource? FromRegions(ObjectArray<Region> regions)
        {
            var bb = RectI.Empty;
            foreach (var r in regions)
                bb = bb.Union(r.BoundingBox);
            if (bb.IsEmpty)
                return null;

            Prepare(bb, out int w, out int h, out double scale);
            var buf = new byte[w * h];
            for (int i = 0; i < regions.Count; i++)
            {
                byte v = (byte)(255 - (i % 6) * 30);
                foreach (var r in regions[i].Runs)
                {
                    int y = Math.Min(h - 1, (int)((r.Row - bb.Y) * scale));
                    int x0 = (int)((r.ColBegin - bb.X) * scale);
                    int x1 = Math.Min(w - 1, (int)((r.ColEnd - bb.X) * scale));
                    for (int x = x0; x <= x1; x++)
                        buf[y * w + x] = v;
                }
            }
            return Finish(buf, w, h);
        }

        private static void Prepare(RectI bb, out int w, out int h, out double scale)
        {
            scale = Math.Min(1.0, (double)Size / Math.Max(bb.Width, bb.Height));
            w = Math.Max(1, (int)Math.Ceiling(bb.Width * scale));
            h = Math.Max(1, (int)Math.Ceiling(bb.Height * scale));
        }

        private static BitmapSource Finish(byte[] buf, int w, int h)
        {
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Gray8, null, buf, w);
            bmp.Freeze();
            return bmp;
        }
    }
}
