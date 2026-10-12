using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace InspectionView.App.Editor
{
    /// <summary>줄 번호 왼쪽의 브레이크포인트 여백. 클릭으로 토글한다.</summary>
    public sealed class BreakpointMargin : AbstractMargin
    {
        private const double MarginWidth = 18;
        private static readonly Brush Fill = new SolidColorBrush(Color.FromRgb(0xE5, 0x14, 0x00));
        private static readonly Brush Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));

        public Func<int, bool> HasBreakpoint { get; set; } = _ => false;
        public Func<int, bool> IsExecutableLine { get; set; } = _ => true;

        /// <summary>클릭된 줄 번호를 알린다. 호출자가 토글한다.</summary>
        public event Action<int>? LineClicked;

        static BreakpointMargin()
        {
            Fill.Freeze();
            Background.Freeze();
        }

        protected override Size MeasureOverride(Size availableSize) => new Size(MarginWidth, 0);

        protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
        {
            if (oldTextView != null)
                oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
            base.OnTextViewChanged(oldTextView, newTextView);
            if (newTextView != null)
                newTextView.VisualLinesChanged += OnVisualLinesChanged;
            InvalidateVisual();
        }

        private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

        public void Refresh() => InvalidateVisual();

        protected override void OnRender(DrawingContext dc)
        {
            var tv = TextView;
            dc.DrawRectangle(Background, null, new Rect(0, 0, RenderSize.Width, RenderSize.Height));
            if (tv == null || !tv.VisualLinesValid)
                return;

            foreach (var vl in tv.VisualLines)
            {
                int line = vl.FirstDocumentLine.LineNumber;
                if (!HasBreakpoint(line))
                    continue;

                double y = vl.VisualTop - tv.VerticalOffset;
                double h = vl.Height;
                double r = Math.Min(h, MarginWidth) / 2 - 2;
                dc.DrawEllipse(Fill, null, new Point(MarginWidth / 2, y + h / 2), r, r);
            }
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            var tv = TextView;
            if (tv == null || e.ChangedButton != MouseButton.Left)
                return;

            var pos = e.GetPosition(this);
            var vl = tv.GetVisualLineFromVisualTop(pos.Y + tv.VerticalOffset);
            if (vl == null)
                return;

            int line = vl.FirstDocumentLine.LineNumber;
            if (!IsExecutableLine(line))
                return;

            LineClicked?.Invoke(line);
            InvalidateVisual();
            e.Handled = true;
        }
    }
}
