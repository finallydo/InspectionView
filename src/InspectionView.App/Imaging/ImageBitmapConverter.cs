using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using InspectionView.Core.Types;

namespace InspectionView.App.Imaging
{
    /// <summary>
    /// Image → WPF BitmapSource (임시 미리보기용; OpenGL 뷰어가 들어오면 썸네일 생성에만 사용).
    /// 8bit 는 그대로, 16bit 는 min-max, 그 외 (실수 등) 는 양 끝 0.5 % 를 버린 범위로 8bit 스케일링한다. 컬러는 BGR 순서를 유지한다.
    /// </summary>
    public static unsafe class ImageBitmapConverter
    {
        public static BitmapSource? ToBitmap(Image? image, int maxSize = 0)
        {
            if (image == null || image.IsDisposed)
                return null;

            int step = 1;
            if (maxSize > 0)
                step = Math.Max(1, (int)Math.Ceiling(Math.Max(image.Width, image.Height) / (double)maxSize));

            int w = image.Width / step, h = image.Height / step;
            if (w <= 0 || h <= 0)
                return null;

            int ch = image.Channels;
            bool color = ch >= 3;
            var format = color ? PixelFormats.Bgr24 : PixelFormats.Gray8;
            int outCh = color ? 3 : 1;
            int stride = (w * outCh + 3) & ~3;
            var buffer = new byte[stride * h];

            double scale = 1, offset = 0;
            if (image.PixelType != PixelType.UInt8)
                ComputeRange(image, step, out scale, out offset);

            byte* src = (byte*)image.GetHostPointer(forWrite: false);
            for (int y = 0; y < h; y++)
            {
                byte* row = src + image.Stride * (long)(y * step);
                int o = y * stride;
                for (int x = 0; x < w; x++)
                {
                    long px = (long)x * step * ch;
                    for (int c = 0; c < outCh; c++)
                    {
                        int srcC = color ? Math.Min(c, ch - 1) : 0;
                        buffer[o + x * outCh + c] = ToByte(Read(image.PixelType, row, px + srcC), scale, offset);
                    }
                }
            }

            var bmp = BitmapSource.Create(w, h, 96, 96, format, null, buffer, stride);
            bmp.Freeze();
            return bmp;
        }

        private static double Read(PixelType type, byte* row, long index)
        {
            switch (type)
            {
                case PixelType.UInt8: return row[index];
                case PixelType.Int16: return ((short*)row)[index];
                case PixelType.UInt16: return ((ushort*)row)[index];
                case PixelType.Int32: return ((int*)row)[index];
                case PixelType.Float32: return ((float*)row)[index];
                case PixelType.Float64: return ((double*)row)[index];
                default: return 0;
            }
        }

        private static byte ToByte(double v, double scale, double offset)
        {
            double r = (v - offset) * scale;
            if (r <= 0) return 0;
            if (r >= 255) return 255;
            return (byte)r;
        }

        private static void ComputeRange(Image image, int step, out double scale, out double offset)
        {
            // 실수 영상은 튀는 값을 버린 백분위 범위
            if (image.PixelType != PixelType.UInt16
                && DisplayRange.TryRobust(image, 65536, image.Channels >= 3 ? 3 : 1, out double lo, out double hi))
            {
                offset = lo;
                scale = 255.0 / (hi - lo);
                return;
            }
            double min = double.MaxValue, max = double.MinValue;
            byte* src = (byte*)image.GetHostPointer(forWrite: false);
            int ch = image.Channels;
            for (int y = 0; y < image.Height; y += step)
            {
                byte* row = src + image.Stride * y;
                for (int x = 0; x < image.Width; x += step)
                {
                    double v = Read(image.PixelType, row, (long)x * ch);
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
            }
            if (max <= min)
            {
                scale = 1;
                offset = min;
                return;
            }
            offset = min;
            scale = 255.0 / (max - min);
        }
    }
}
