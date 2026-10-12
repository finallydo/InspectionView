using System;
using System.ComponentModel;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using InspectionView.Core.Registry;

namespace InspectionView.App.Editor
{
    /// <summary>자동완성 항목 (연산자 / 내장 함수 / 키워드 / 변수).</summary>
    public sealed class CompletionItem : ICompletionData
    {
        public CompletionItem(string text, string? description, double priority, string kind)
        {
            Text = text;
            Description = description ?? "";
            Priority = priority;
            Kind = kind;
        }

        public string Kind { get; }
        public ImageSource? Image => null;
        public string Text { get; }
        public object Content => Text;
        public object Description { get; }
        public double Priority { get; }

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            textArea.Document.Replace(completionSegment, Text);
        }
    }

    /// <summary>'(' 입력 시 연산자 시그니처를 보여주는 파라미터 힌트.</summary>
    public sealed class OperatorInsightProvider : IOverloadProvider
    {
        private readonly OperatorDescriptor _descriptor;

#pragma warning disable 67
        public event PropertyChangedEventHandler? PropertyChanged;
#pragma warning restore 67

        public OperatorInsightProvider(OperatorDescriptor descriptor)
        {
            _descriptor = descriptor;
        }

        public int SelectedIndex
        {
            get => 0;
            set { }
        }

        public int Count => 1;
        public string CurrentIndexText => "";
        public object CurrentHeader => _descriptor.Signature;

        public object CurrentContent
        {
            get
            {
                var lines = new System.Text.StringBuilder();
                if (!string.IsNullOrEmpty(_descriptor.Description))
                    lines.AppendLine(_descriptor.Description);
                foreach (var p in _descriptor.Inputs)
                {
                    lines.Append("  ").Append(p.TypeDisplayName).Append(' ').Append(p.Name);
                    if (p.DefaultValue != null) lines.Append(" = ").Append(p.DefaultValue);
                    if (!string.IsNullOrEmpty(p.Description)) lines.Append("  — ").Append(p.Description);
                    lines.AppendLine();
                }
                foreach (var p in _descriptor.Outputs)
                    lines.Append("  → ").Append(p.TypeDisplayName).Append(' ').Append(p.Name).AppendLine();
                return lines.ToString().TrimEnd();
            }
        }
    }
}
