using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using InspectionView.Core.Types;
using Image = InspectionView.Core.Types.Image;

namespace InspectionView.App.Viewer
{
    /// <summary>표시 중인 이미지(또는 ROI 안)의 히스토그램. 컬러는 채널별 선, 그레이는 막대.</summary>
    public unsafe partial class HistogramControl : UserControl
    {
        private const int Bins = 256;
        private static readonly Brush[] ChannelBrushes =
        {
            new SolidColorBrush(Color.FromRgb(0x60, 0x90, 0xFF)),   // B
            new SolidColorBrush(Color.FromRgb(0x50, 0xE0, 0x50)),   // G
            new SolidColorBrush(Color.FromRgb(0xFF, 0x60, 0x60)),   // R
            new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),   // A / 4ch
        };
        private static readonly Brush GrayBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8));

        private long[][]? _hist;
        private double _min, _max;
        private string _summary = "";

        public HistogramControl()
        {
            InitializeComponent();
        }

        public void Clear()
        {
            _hist = null;
            Info.Text = "No image";
            Plot.Children.Clear();
        }

        /// <summary>이미지(ROI 가 있으면 그 안)의 히스토그램을 계산해 그린다. 큰 이미지는 서브샘플링.</summary>
        public void Update(Image? image, RectI? roi)
        {
            if (image == null || image.IsDisposed)
            {
                Clear();
                return;
            }

            var area = roi.HasValue ? roi.Value.Intersect(image.Bounds) : image.Bounds;
            if (area.IsEmpty)
            {
                Clear();
                return;
            }

            int channels = Math.Min(image.Channels, 4);
            // 약 30만 샘플이면 분포 모양은 같다 (라이브 중에도 UI 스레드에서 몇 ms 안에 끝나도록)
            int step = Math.Max(1, (int)Math.Sqrt(area.Area / 300_000.0));

            // 값 범위 (8bit 은 0..255 고정)
            double min = 0, max = 255;
            if (image.PixelType != PixelType.UInt8)
            {
                min = double.MaxValue;
                max = double.MinValue;
                for (int y = area.Y; y < area.Bottom; y += step)
                    for (int x = area.X; x < area.Right; x += step)
                        for (int c = 0; c < channels; c++)
                        {
                            double v = image.GetPixel(x, y, c);
                            if (v < min) min = v;
                            if (v > max) max = v;
                        }
                if (max <= min) max = min + 1;
            }

            var hist = new long[channels][];
            for (int c = 0; c < channels; c++) hist[c] = new long[Bins];
            double scale = (Bins - 1) / (max - min);
            long count = 0;
            double sum = 0;
            if (image.PixelType == PixelType.UInt8)
            {
                // 8bit: 값 = 구간 번호, 행 포인터로 바로 센다
                byte* basePtr = (byte*)image.GetHostPointer(forWrite: false);
                int ch = image.Channels;
                for (int y = area.Y; y < area.Bottom; y += step)
                {
                    byte* row = basePtr + image.Stride * y;
                    for (int x = area.X; x < area.Right; x += step)
                    {
                        byte* px = row + (long)x * ch;
                        for (int c = 0; c < channels; c++)
                            hist[c][px[c]]++;
                        sum += px[0];
                        count++;
                    }
                }
            }
            else
            {
                for (int y = area.Y; y < area.Bottom; y += step)
                {
                    for (int x = area.X; x < area.Right; x += step)
                    {
                        for (int c = 0; c < channels; c++)
                        {
                            double v = image.GetPixel(x, y, c);
                            int bin = (int)((v - min) * scale);
                            if (bin < 0) bin = 0; else if (bin >= Bins) bin = Bins - 1;
                            hist[c][bin]++;
                            if (c == 0) { sum += v; count++; }
                        }
                    }
                }
            }

            _hist = hist;
            _min = min;
            _max = max;
            string range = image.PixelType == PixelType.UInt8 ? "0..255" : $"{Fmt(min)}..{Fmt(max)}";
            string region = roi.HasValue ? $"ROI {area.Width}x{area.Height}" : $"{area.Width}x{area.Height}";
            _summary = $"{region}  range {range}  mean {(count > 0 ? sum / count : 0):F2}" + (step > 1 ? $"  (sampled 1/{step})" : "");
            Redraw();
        }

        private static string Fmt(double v) => v == Math.Floor(v) ? ((long)v).ToString(CultureInfo.InvariantCulture) : v.ToString("F3", CultureInfo.InvariantCulture);

        private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

        private void Redraw()
        {
            Plot.Children.Clear();
            if (_hist == null)
                return;

            double w = Plot.ActualWidth, h = Plot.ActualHeight;
            if (w < 10 || h < 10)
                return;

            long peak = 1;
            foreach (var ch in _hist)
                foreach (var v in ch)
                    if (v > peak) peak = v;

            double binW = w / Bins;
            if (_hist.Length == 1)
            {
                // 막대 256개를 요소 하나(경로)로 그린다 (요소를 매번 만들면 라이브 중 레이아웃 비용이 크다)
                var ch = _hist[0];
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    for (int i = 0; i < Bins; i++)
                    {
                        if (ch[i] == 0) continue;
                        double bh = ch[i] / (double)peak * (h - 2);
                        double x0 = i * binW, x1 = x0 + Math.Max(1, binW);
                        ctx.BeginFigure(new Point(x0, h), true, true);
                        ctx.LineTo(new Point(x0, h - bh), false, false);
                        ctx.LineTo(new Point(x1, h - bh), false, false);
                        ctx.LineTo(new Point(x1, h), false, false);
                    }
                }
                geo.Freeze();
                Plot.Children.Add(new Path { Data = geo, Fill = GrayBrush });
            }
            else
            {
                for (int c = 0; c < _hist.Length; c++)
                {
                    var line = new Polyline { Stroke = ChannelBrushes[c], StrokeThickness = 1.2 };
                    for (int i = 0; i < Bins; i++)
                    {
                        double bh = _hist[c][i] / (double)peak * (h - 2);
                        line.Points.Add(new Point(i * binW + binW / 2, h - bh));
                    }
                    Plot.Children.Add(line);
                }
            }

            Info.Text = _summary;
        }
    }
}
