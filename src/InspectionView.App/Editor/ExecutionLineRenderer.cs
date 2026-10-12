using System;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;

namespace InspectionView.App.Editor
{
    /// <summary>실행 줄(노랑)과 오류 줄(빨강) 배경을 그린다.</summary>
    public sealed class ExecutionLineRenderer : IBackgroundRenderer
    {
        private static readonly Brush CurrentBrush = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xE0, 0x40));
        private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0x50, 0x50));
        private static readonly Pen CurrentPen = new Pen(new SolidColorBrush(Color.FromRgb(0xE0, 0xB0, 0x00)), 1);

        static ExecutionLineRenderer()
        {
            CurrentBrush.Freeze();
            ErrorBrush.Freeze();
            CurrentPen.Freeze();
        }

        public int CurrentLine { get; set; } = -1;
        public int ErrorLine { get; set; } = -1;

        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext dc)
        {
            if (textView.Document == null)
                return;
            textView.EnsureVisualLines();

            if (ErrorLine > 0)
                DrawLine(textView, dc, ErrorLine, ErrorBrush, null);
            if (CurrentLine > 0)
                DrawLine(textView, dc, CurrentLine, CurrentBrush, CurrentPen);
        }

        private static void DrawLine(TextView textView, DrawingContext dc, int line, Brush brush, Pen? pen)
        {
            if (line > textView.Document.LineCount)
                return;
            var vl = textView.GetVisualLine(line);
            if (vl == null)
                return;

            double y = vl.VisualTop - textView.VerticalOffset;
            var rect = new Rect(0, y, Math.Max(textView.ActualWidth, 1), vl.Height);
            dc.DrawRectangle(brush, pen, rect);
        }
    }
}
