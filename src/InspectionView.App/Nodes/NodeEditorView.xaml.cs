using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace InspectionView.App.Nodes
{
    /// <summary>노드 편집기 뷰. 스크립트 에디터와의 동기화는 이벤트로 MainWindow 가 처리한다.</summary>
    public partial class NodeEditorView : UserControl
    {
        public NodeEditorView()
        {
            InitializeComponent();
            PreviewKeyDown += OnPreviewKeyDown;
        }

        private NodeGraphViewModel? Vm => DataContext as NodeGraphViewModel;

        /// <summary>스크립트 → 그래프 요청.</summary>
        public event Action? FromScriptRequested;
        /// <summary>그래프 → 스크립트 요청.</summary>
        public event Action? ToScriptRequested;
        /// <summary>그래프를 스크립트로 쓰고 실행.</summary>
        public event Action? RunRequested;

        private void OnFromScript(object sender, RoutedEventArgs e) => FromScriptRequested?.Invoke();
        private void OnToScript(object sender, RoutedEventArgs e) => ToScriptRequested?.Invoke();
        private void OnRun(object sender, RoutedEventArgs e) => RunRequested?.Invoke();
        private void OnAutoLayout(object sender, RoutedEventArgs e) => Vm?.AutoLayout();
        private void OnDelete(object sender, RoutedEventArgs e) => Vm?.RemoveSelected();

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && !(e.OriginalSource is TextBox))
            {
                Vm?.RemoveSelected();
                e.Handled = true;
            }
        }
    }
}
