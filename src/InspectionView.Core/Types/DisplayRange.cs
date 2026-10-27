using System;

namespace InspectionView.Core.Types
{
    /// <summary>
    /// 표시용 밝기 범위. 실수 영상 (기울기, 발산 등) 은 그림자/배경에서 튀는 값 몇 개가 min/max 를 차지해 나머지가
    /// 한 가지 회색으로 뭉개지므로, 양 끝 일부 (기본 0.5 %) 를 버린 백분위 범위를 쓴다. NaN/무한대는 무시한다.
    /// </summary>
    public static class DisplayRange
    {
        public const double DefaultTail = 0.005;

        /// <summary>
        /// 격자로 최대 <paramref name="maxSamples"/> 개를 뽑아 [tail, 1 − tail] 백분위를 구한다.
        /// 앞쪽 <paramref name="channels"/> 개 채널만 본다. 유효한 값이 없으면 false.
        /// </summary>
        public static bool TryRobust(Image image, int maxSamples, int channels, out double low, out double high, double tail = DefaultTail)
        {
            low = 0;
            high = 0;
            if (image == null || image.IsDisposed || image.Width <= 0 || image.Height <= 0)
                return false;
            int take = Math.Max(1, Math.Min(channels, image.Channels));
            int step = Math.Max(1, (int)Math.Sqrt((double)image.Width * image.Height * take / Math.Max(1, maxSamples)));
            int capacity = ((image.Width + step - 1) / step) * ((image.Height + step - 1) / step) * take;
            var values = new double[capacity];
            int n = 0;
            for (int y = 0; y < image.Height; y += step)
            {
                for (int x = 0; x < image.Width; x += step)
                {
                    for (int k = 0; k < take; k++)
                    {
                        double v = image.GetPixel(x, y, k);
                        if (double.IsNaN(v) || double.IsInfinity(v))
                            continue;
                        values[n++] = v;
                    }
                }
            }
            if (n == 0)
                return false;

            Array.Sort(values, 0, n);
            int lo = (int)Math.Floor(tail * (n - 1));
            int hi = (int)Math.Ceiling((1 - tail) * (n - 1));
            low = values[lo];
            high = values[hi];
            if (high <= low)
            {
                // 대부분이 같은 값이면 전체 범위로 (그래도 같으면 폭 1)
                low = values[0];
                high = values[n - 1];
                if (high <= low)
                    high = low + 1;
            }
            return true;
        }
    }
}
