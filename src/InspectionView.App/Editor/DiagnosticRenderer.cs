using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using InspectionView.App.ViewModels;

namespace InspectionView.App.Editor
{
    /// <summary>구문 오류 위치에 빨간 밑줄을 그린다.</summary>
    public sealed class DiagnosticRenderer : IBackgroundRenderer
    {
        private static readonly Pen UnderlinePen = new Pen(new SolidColorBrush(Color.FromRgb(0xE5, 0x14, 0x00)), 1.5);

        static DiagnosticRenderer()
        {
            UnderlinePen.Freeze();
        }

        public IReadOnlyList<DiagnosticItem> Items { get; set; } = Array.Empty<DiagnosticItem>();

        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext dc)
        {
            var doc = textView.Document;
            if (doc == null || Items.Count == 0)
                return;
            textView.EnsureVisualLines();

            foreach (var d in Items)
            {
                if (d.Line < 1 || d.Line > doc.LineCount)
                    continue;

                var line = doc.GetLineByNumber(d.Line);
                int start = line.Offset + Math.Max(0, d.Column - 1);
                int length = Math.Max(1, d.Length);
                if (start >= line.EndOffset)
                {
                    start = Math.Max(line.Offset, line.EndOffset - 1);
                    length = Math.Max(1, line.EndOffset - start);
                }
                if (start + length > line.EndOffset)
                    length = Math.Max(1, line.EndOffset - start);
                if (line.Length == 0)
                    continue;

                var segment = new TextSegment { StartOffset = start, Length = length };
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                {
                    double y = rect.Bottom - 1;
                    dc.DrawLine(UnderlinePen, new Point(rect.Left, y), new Point(rect.Right, y));
                }
            }
        }
    }
}
