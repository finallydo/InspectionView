using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AvalonDock.Themes;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Rendering;
using InspectionView.App.Editor;
using InspectionView.App.Viewer;
using InspectionView.App.ViewModels;
using InspectionView.Core.Types;
using InspectionView.Script.Runtime;
using Microsoft.Win32;
using Image = InspectionView.Core.Types.Image;

namespace InspectionView.App
{
    public partial class MainWindow : Window
    {
        private const string FileFilter = "InspectionView script (*.ivs)|*.ivs|All files (*.*)|*.*";

        private readonly MainViewModel _vm;
        private readonly BreakpointMargin _breakpointMargin = new BreakpointMargin();
        private readonly ExecutionLineRenderer _lineRenderer = new ExecutionLineRenderer();
        private readonly DiagnosticRenderer _diagnosticRenderer = new DiagnosticRenderer();
        private readonly DispatcherTimer _uiTimer;
        private readonly DispatcherTimer _parseTimer;
        private readonly List<ImageViewer> _viewers = new List<ImageViewer>();
        private ImageViewer _activeViewer = null!;
        private CompletionWindow? _completion;
        private OverloadInsightWindow? _insight;
        private bool _suppressDirty;

        public ICommand RunCommand { get; }
        public ICommand StepCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand ResetCommand { get; }
        public ICommand ToggleBreakpointCommand { get; }
        public ICommand SetNextStatementCommand { get; }
        public ICommand NewCommand { get; }
        public ICommand OpenCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand SaveAsCommand { get; }

        public MainWindow()
        {
            // 명령과 뷰모델은 XAML 의 ElementName 바인딩이 읽기 전에 만들어져 있어야 한다.
            _vm = new MainViewModel(a => Dispatcher.BeginInvoke(a));
            RunCommand = new RelayCommand(() => _vm.Run(Editor.Text), () => _vm.CanRun);
            StepCommand = new RelayCommand(() => _vm.Step(Editor.Text), () => _vm.CanRun);
            StopCommand = new RelayCommand(() => _vm.Stop(), () => _vm.CanStop);
            ResetCommand = new RelayCommand(() => _vm.Reset(), () => _vm.CanRun);
            ToggleBreakpointCommand = new RelayCommand(ToggleBreakpointAtCaret);
            SetNextStatementCommand = new RelayCommand(SetNextStatementAtCaret, () => _vm.CanRun);
            NewCommand = new RelayCommand(NewScript, () => _vm.CanRun);
            OpenCommand = new RelayCommand(OpenScript, () => _vm.CanRun);
            SaveCommand = new RelayCommand(() => SaveScript(false));
            SaveAsCommand = new RelayCommand(() => SaveScript(true));

            InitializeComponent();
            DataContext = _vm;
            Dock.Theme = new Vs2013LightTheme();

            SetupEditor();

            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.GoToLineRequested += GoToLine;
            _vm.DisplayRequested += ShowVariable;
            _vm.ScriptDisplayRequested += OnScriptDisplay;
            _vm.ScriptClearWindowRequested += OnScriptClearWindow;
            RegisterViewer(Viewer);
            _activeViewer = Viewer;

            _uiTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
            _uiTimer.Tick += (s, e) => _vm.Tick();
            _uiTimer.Start();

            _parseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _parseTimer.Tick += (s, e) =>
            {
                _parseTimer.Stop();
                _vm.UpdateSyntaxDiagnostics(Editor.Text);
                RefreshDiagnosticRenderer();
            };

            SetEditorText(_vm.NewScript() + SampleScript());
            Loaded += (s, e) => Editor.Focus();
            Closing += OnClosing;
        }

        private static string SampleScript() =>
            "Image = ReadImage('C:/images/sample.png')\n" +
            "Region = Threshold(Image, 128, 255)\n" +
            "Parts = Connection(Region)\n" +
            "Big = SelectShape(Parts, 'Area', 100, 100000)\n" +
            "Print('parts:', Len(Parts), 'big:', Len(Big))\n";

        // ------------------------------------------------------------------
        // 에디터 설정
        // ------------------------------------------------------------------

        private void SetupEditor()
        {
            Editor.SyntaxHighlighting = ScriptHighlighting.Create(_vm.Registry, _vm.Engine.Builtins);
            Editor.Options.ConvertTabsToSpaces = true;
            Editor.Options.IndentationSize = 4;
            Editor.Options.EnableHyperlinks = false;
            Editor.Options.EnableEmailHyperlinks = false;

            _breakpointMargin.HasBreakpoint = _vm.Engine.HasBreakpoint;
            _breakpointMargin.LineClicked += line =>
            {
                _vm.ToggleBreakpoint(line);
                _breakpointMargin.Refresh();
            };
            Editor.TextArea.LeftMargins.Insert(0, _breakpointMargin);

            Editor.TextArea.TextView.BackgroundRenderers.Add(_lineRenderer);
            Editor.TextArea.TextView.BackgroundRenderers.Add(_diagnosticRenderer);

            Editor.TextChanged += (s, e) =>
            {
                if (!_suppressDirty)
                    _vm.IsDirty = true;
                _parseTimer.Stop();
                _parseTimer.Start();
            };
            Editor.TextArea.Caret.PositionChanged += (s, e) =>
                CaretText.Text = $"Ln {Editor.TextArea.Caret.Line}, Col {Editor.TextArea.Caret.Column}";

            Editor.TextArea.TextEntering += OnTextEntering;
            Editor.TextArea.TextEntered += OnTextEntered;
        }

        private void SetEditorText(string text)
        {
            _suppressDirty = true;
            Editor.Text = text;
            _suppressDirty = false;
            _vm.IsDirty = false;
            _vm.UpdateSyntaxDiagnostics(text);
            RefreshDiagnosticRenderer();
            _breakpointMargin.Refresh();
        }

        private void RefreshDiagnosticRenderer()
        {
            _diagnosticRenderer.Items = _vm.Diagnostics.Where(d => d.Severity != "Runtime").ToList();
            Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.CurrentLine):
                    _lineRenderer.CurrentLine = _vm.CurrentLine;
                    Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
                    if (_vm.CurrentLine > 0 && _vm.State != EngineState.Running)
                        Editor.ScrollToLine(_vm.CurrentLine);
                    break;

                case nameof(MainViewModel.ErrorLine):
                    _lineRenderer.ErrorLine = _vm.ErrorLine;
                    Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
                    if (_vm.ErrorLine > 0)
                        Editor.ScrollToLine(_vm.ErrorLine);
                    break;

                case nameof(MainViewModel.SelectedVariable):
                    if (_vm.SelectedVariable != null)
                        ShowVariable(_vm.SelectedVariable);
                    break;

                case nameof(MainViewModel.State):
                    Editor.IsReadOnly = _vm.State == EngineState.Running;
                    if (_vm.State != EngineState.Running)
                        RefreshDiagnosticRenderer();
                    break;
            }
        }

        // ------------------------------------------------------------------
        // 자동완성 / 파라미터 힌트
        // ------------------------------------------------------------------

        private void OnTextEntering(object sender, TextCompositionEventArgs e)
        {
            if (e.Text.Length > 0 && _completion != null)
            {
                char c = e.Text[0];
                if (!char.IsLetterOrDigit(c) && c != '_')
                    _completion.CompletionList.RequestInsertion(e);
            }
        }

        private void OnTextEntered(object sender, TextCompositionEventArgs e)
        {
            if (e.Text.Length != 1)
                return;
            char c = e.Text[0];

            if (c == '(')
            {
                ShowInsight();
                return;
            }

            if ((char.IsLetter(c) || c == '_') && _completion == null)
            {
                int caret = Editor.CaretOffset;
                int start = caret - 1;
                var doc = Editor.Document;
                while (start > 0 && (char.IsLetterOrDigit(doc.GetCharAt(start - 1)) || doc.GetCharAt(start - 1) == '_'))
                    start--;

                // 멤버 접근(.) 뒤에서는 완성하지 않는다
                if (start > 0 && doc.GetCharAt(start - 1) == '.')
                    return;

                _completion = new CompletionWindow(Editor.TextArea) { StartOffset = start, Width = 360 };
                var data = _completion.CompletionList.CompletionData;

                foreach (var d in _vm.Registry.All)
                    data.Add(new CompletionItem(d.Name, d.Signature + "\n" + d.Description, 2, "operator"));
                foreach (var b in _vm.Engine.Builtins.Names)
                    data.Add(new CompletionItem(b, "built-in function", 1, "builtin"));
                foreach (var k in ScriptHighlighting.Keywords)
                    data.Add(new CompletionItem(k, "keyword", 0, "keyword"));
                foreach (var v in _vm.Engine.Variables.Names)
                    data.Add(new CompletionItem(v, ScriptValues.TypeName(_vm.Engine.Variables.Get(v)), 3, "variable"));

                _completion.Closed += (s, a) => _completion = null;
                _completion.Show();
                _completion.CompletionList.SelectItem(doc.GetText(start, caret - start));
            }
        }

        private void ShowInsight()
        {
            var doc = Editor.Document;
            int end = Editor.CaretOffset - 1;       // '(' 위치
            int start = end;
            while (start > 0 && (char.IsLetterOrDigit(doc.GetCharAt(start - 1)) || doc.GetCharAt(start - 1) == '_'))
                start--;
            if (start == end)
                return;

            var name = doc.GetText(start, end - start);
            if (!_vm.Registry.TryGet(name, out var descriptor))
                return;

            _insight?.Close();
            _insight = new OverloadInsightWindow(Editor.TextArea)
            {
                Provider = new OperatorInsightProvider(descriptor),
                StartOffset = end,
                EndOffset = end + 1,
                CloseAutomatically = true,
            };
            _insight.Closed += (s, e) => _insight = null;
            _insight.Show();
        }

        // ------------------------------------------------------------------
        // 그래픽 창 (여러 개; 마지막으로 클릭한 창이 활성 창)
        // ------------------------------------------------------------------

        private void RegisterViewer(ImageViewer viewer)
        {
            _viewers.Add(viewer);
            viewer.Activated += () => SetActiveViewer(viewer);
            viewer.InsertRoiRequested += InsertRoi;
            viewer.ImageChanged += () => { if (ReferenceEquals(viewer, _activeViewer)) RefreshAnalysis(); };
            viewer.RoiChanged += _ => { if (ReferenceEquals(viewer, _activeViewer)) RefreshHistogram(); };
            viewer.ProfileLineChanged += () => { if (ReferenceEquals(viewer, _activeViewer)) RefreshProfile(); };
        }

        private void SetActiveViewer(ImageViewer viewer)
        {
            if (ReferenceEquals(_activeViewer, viewer))
                return;
            _activeViewer = viewer;
            _vm.StatusText = $"Active graphics window: {_viewers.IndexOf(viewer) + 1}";
            RefreshAnalysis();
        }

        /// <summary>index 번 창을 돌려준다 (없으면 만든다).</summary>
        private ImageViewer GetViewer(int index)
        {
            while (index >= _viewers.Count)
                CreateGraphicsWindow();
            return _viewers[index];
        }

        private ImageViewer CreateGraphicsWindow()
        {
            var viewer = new ImageViewer();
            int number = _viewers.Count + 1;
            var anchorable = new AvalonDock.Layout.LayoutAnchorable
            {
                Title = $"Graphics {number}",
                ContentId = $"graphics{number}",
                Content = viewer,
                CanHide = false,
            };
            anchorable.Closed += (s, e) =>
            {
                _viewers.Remove(viewer);
                viewer.SetImage(null);
                if (ReferenceEquals(_activeViewer, viewer))
                    SetActiveViewer(Viewer);
            };
            GraphicsPane.Children.Add(anchorable);
            RegisterViewer(viewer);
            anchorable.IsActive = true;
            return viewer;
        }

        private void OnNewGraphicsWindow(object sender, RoutedEventArgs e)
        {
            var viewer = CreateGraphicsWindow();
            SetActiveViewer(viewer);
        }

        private void OnCloseExtraGraphicsWindows(object sender, RoutedEventArgs e)
        {
            foreach (var v in _viewers.ToArray())
            {
                if (ReferenceEquals(v, Viewer))
                    continue;
                if (v.Parent is AvalonDock.Layout.LayoutAnchorable a)
                    a.Close();
                else if (Dock.Layout.Descendents().OfType<AvalonDock.Layout.LayoutAnchorable>().FirstOrDefault(x => ReferenceEquals(x.Content, v)) is AvalonDock.Layout.LayoutAnchorable b)
                    b.Close();
            }
            SetActiveViewer(Viewer);
        }

        private void RefreshAnalysis()
        {
            RefreshHistogram();
            RefreshProfile();
        }

        private void RefreshHistogram()
        {
            var v = _activeViewer;
            Histogram.Update(v.CurrentImage, v.Roi);
        }

        private void RefreshProfile()
        {
            var v = _activeViewer;
            if (v.ProfileLine.HasValue)
                LineProfile.Update(v.CurrentImage, v.ProfileLine.Value.A, v.ProfileLine.Value.B);
            else
                LineProfile.Clear();
        }

        /// <summary>값을 지정 창에 표시: Image 는 배경(오버레이 지움), Region/XLD/배열은 오버레이.</summary>
        private static void Display(ImageViewer viewer, object? value, string name)
        {
            switch (value)
            {
                case Image img:
                    viewer.ClearOverlays();
                    viewer.SetImage(img);
                    break;
                case ObjectArray<Image> arr when arr.Count > 0:
                    viewer.ClearOverlays();
                    viewer.SetImage(arr[0]);
                    break;
                case Region _:
                case XldContour _:
                case IObjectArray _:
                    viewer.AddOverlay(value, name);
                    break;
            }
        }

        /// <summary>변수창 선택 / 자동 표시 → 활성 창.</summary>
        private void ShowVariable(VariableItem item)
        {
            if (_vm.State == EngineState.Running)
                return;
            Display(_activeViewer, item.Raw, item.Name);
        }

        private void OnScriptDisplay(object value, int window) => Display(GetViewer(window), value, "Display");

        private void OnScriptClearWindow(int window)
        {
            var v = GetViewer(window);
            v.ClearOverlays();
            v.ClearRoi();
            v.ClearProfileLine();
            v.SetImage(null);
        }

        private void InsertRoi(RectI roi)
        {
            var doc = Editor.Document;
            var line = doc.GetLineByNumber(Editor.TextArea.Caret.Line);
            string text = $"Roi = GenRectangle1({roi.Y}, {roi.X}, {roi.Bottom - 1}, {roi.Right - 1})";
            if (line.Length > 0)
                doc.Insert(line.EndOffset, "\n" + text);
            else
                doc.Insert(line.Offset, text);
            Editor.Focus();
        }

        // ------------------------------------------------------------------
        // 명령
        // ------------------------------------------------------------------

        private void ToggleBreakpointAtCaret()
        {
            int line = Editor.TextArea.Caret.Line;
            _vm.ToggleBreakpoint(line);
            _breakpointMargin.Refresh();
        }

        private void SetNextStatementAtCaret()
        {
            if (!_vm.EnsureLoaded(Editor.Text))
                return;
            if (!_vm.SetNextLine(Editor.TextArea.Caret.Line))
                _vm.StatusText = "No executable statement at that line";
        }

        private void GoToLine(int line)
        {
            if (line < 1 || line > Editor.Document.LineCount)
                return;
            Editor.TextArea.Caret.Line = line;
            Editor.TextArea.Caret.Column = 1;
            Editor.ScrollToLine(line);
            Editor.Focus();
        }

        private bool ConfirmDiscardChanges()
        {
            if (!_vm.IsDirty)
                return true;
            var r = MessageBox.Show(this, "Save changes to the script?", "InspectionView", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel)
                return false;
            if (r == MessageBoxResult.Yes)
                return SaveScript(false);
            return true;
        }

        private void NewScript()
        {
            if (!ConfirmDiscardChanges())
                return;
            _vm.Engine.ClearBreakpoints();
            SetEditorText(_vm.NewScript());
        }

        private void OpenScript()
        {
            if (!ConfirmDiscardChanges())
                return;
            var dlg = new OpenFileDialog { Filter = FileFilter, DefaultExt = ".ivs" };
            if (dlg.ShowDialog(this) != true)
                return;
            try
            {
                SetEditorText(_vm.OpenScript(dlg.FileName));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Open failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool SaveScript(bool saveAs)
        {
            var path = _vm.FilePath;
            if (saveAs || string.IsNullOrEmpty(path))
            {
                var dlg = new SaveFileDialog { Filter = FileFilter, DefaultExt = ".ivs", FileName = string.IsNullOrEmpty(path) ? "script.ivs" : path };
                if (dlg.ShowDialog(this) != true)
                    return false;
                path = dlg.FileName;
            }
            try
            {
                _vm.SaveScript(path, Editor.Text);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        // ------------------------------------------------------------------
        // 이벤트 핸들러
        // ------------------------------------------------------------------

        private void OnOperatorDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (OperatorTreeView.SelectedItem is OperatorNode node && !node.IsCategory)
            {
                var doc = Editor.Document;
                var line = doc.GetLineByNumber(Editor.TextArea.Caret.Line);
                string text = node.InsertText;
                if (line.Length > 0)
                    doc.Insert(line.EndOffset, "\n" + text);
                else
                    doc.Insert(line.Offset, text);
                Editor.Focus();
                e.Handled = true;
            }
        }

        private void OnDiagnosticDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListView lv && lv.SelectedItem is DiagnosticItem d)
                GoToLine(d.Line);
        }

        private void OnOutputChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb)
                tb.ScrollToEnd();
        }

        private void OnClearOutput(object sender, RoutedEventArgs e) => _vm.ClearOutput();
        private void OnClearProfile(object sender, RoutedEventArgs e) => _vm.ClearProfile();
        private void OnClearVariables(object sender, RoutedEventArgs e) => _vm.ClearVariables();

        private void OnClearBreakpoints(object sender, RoutedEventArgs e)
        {
            _vm.Engine.ClearBreakpoints();
            _breakpointMargin.Refresh();
        }

        private void OnExit(object sender, RoutedEventArgs e) => Close();

        private void OnClosing(object? sender, CancelEventArgs e)
        {
            if (!ConfirmDiscardChanges())
            {
                e.Cancel = true;
                return;
            }
            _uiTimer.Stop();
            _parseTimer.Stop();
            _vm.Dispose();
        }
    }
}
