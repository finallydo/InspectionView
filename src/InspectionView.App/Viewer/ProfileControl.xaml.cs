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
    /// <summary>선분을 따라 샘플링한 그레이 값 프로파일 (컬러는 채널별 선).</summary>
    public partial class ProfileControl : UserControl
    {
        private static readonly Brush[] ChannelBrushes =
        {
            new SolidColorBrush(Color.FromRgb(0x60, 0x90, 0xFF)),
            new SolidColorBrush(Color.FromRgb(0x50, 0xE0, 0x50)),
            new SolidColorBrush(Color.FromRgb(0xFF, 0x60, 0x60)),
            new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
        };
        private static readonly Brush GrayBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8));
        private static readonly Brush GridBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x45));

        private double[][]? _samples;
        private double _min, _max;
        private string _summary = "";

        public ProfileControl()
        {
            InitializeComponent();
        }

        public void Clear()
        {
            _samples = null;
            Plot.Children.Clear();
            Info.Text = "Use the 'Profile' tool in the graphics window and drag a line over the image";
        }

        /// <summary>a→b 선분을 1픽셀 간격으로 샘플링한다 (최근접 픽셀).</summary>
        public void Update(Image? image, PointD a, PointD b)
        {
            if (image == null || image.IsDisposed)
            {
                Clear();
                return;
            }

            double len = a.DistanceTo(b);
            int n = Math.Max(2, (int)Math.Round(len) + 1);
            int channels = Math.Min(image.Channels, 4);
            var samples = new double[channels][];
            for (int c = 0; c < channels; c++) samples[c] = new double[n];

            double min = double.MaxValue, max = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                double t = n == 1 ? 0 : i / (double)(n - 1);
                int x = (int)Math.Floor(a.X + (b.X - a.X) * t);
                int y = (int)Math.Floor(a.Y + (b.Y - a.Y) * t);
                bool inside = x >= 0 && y >= 0 && x < image.Width && y < image.Height;
                for (int c = 0; c < channels; c++)
                {
                    double v = inside ? image.GetPixel(x, y, c) : double.NaN;
                    samples[c][i] = v;
                    if (!double.IsNaN(v))
                    {
                        if (v < min) min = v;
                        if (v > max) max = v;
                    }
                }
            }
            if (min == double.MaxValue) { min = 0; max = 1; }
            if (max <= min) max = min + 1;

            _samples = samples;
            _min = min;
            _max = max;
            _summary = $"({Fmt(a.X)}, {Fmt(a.Y)}) → ({Fmt(b.X)}, {Fmt(b.Y)})  length {len:F1} px  {n} samples  min {Fmt(min)}  max {Fmt(max)}";
            Redraw();
        }

        private static string Fmt(double v) => v == Math.Floor(v) ? ((long)v).ToString(CultureInfo.InvariantCulture) : v.ToString("F1", CultureInfo.InvariantCulture);

        private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

        private void Redraw()
        {
            Plot.Children.Clear();
            if (_samples == null)
                return;

            double w = Plot.ActualWidth, h = Plot.ActualHeight;
            if (w < 10 || h < 10)
                return;

            // 격자 (4등분)
            for (int g = 1; g < 4; g++)
            {
                var gl = new Line { X1 = 0, X2 = w, Y1 = h * g / 4, Y2 = h * g / 4, Stroke = GridBrush, StrokeThickness = 1 };
                Plot.Children.Add(gl);
            }

            int n = _samples[0].Length;
            double dx = n > 1 ? w / (n - 1) : 0;
            double range = _max - _min;
            for (int c = 0; c < _samples.Length; c++)
            {
                var line = new Polyline { Stroke = _samples.Length == 1 ? GrayBrush : ChannelBrushes[c], StrokeThickness = 1.5 };
                for (int i = 0; i < n; i++)
                {
                    double v = _samples[c][i];
                    if (double.IsNaN(v)) continue;
                    double y = h - 1 - (v - _min) / range * (h - 2);
                    line.Points.Add(new Point(i * dx, y));
                }
                Plot.Children.Add(line);
            }

            Info.Text = _summary;
        }
    }
}
