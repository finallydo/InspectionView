using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using InspectionView.App.ViewModels;
using InspectionView.Core.Registry;
using InspectionView.Script.Graph;

namespace InspectionView.App.Nodes
{
    /// <summary>노드의 입력/출력 포트.</summary>
    public sealed class ConnectorVm : ObservableObject
    {
        private Point _anchor;
        private bool _isConnected;
        private string _literal = "";

        public ConnectorVm(NodeVm node, ParameterDescriptor parameter, bool isInput)
        {
            Node = node;
            Parameter = parameter;
            IsInput = isInput;
        }

        public NodeVm Node { get; }
        public ParameterDescriptor Parameter { get; }
        public bool IsInput { get; }
        public string Name => Parameter.Name;
        public string Title => $"{Parameter.Name} : {Parameter.TypeDisplayName}";
        public bool IsIconic => Parameter.IsIconic;

        public Point Anchor { get => _anchor; set => Set(ref _anchor, value); }

        public bool IsConnected
        {
            get => _isConnected;
            set { if (Set(ref _isConnected, value)) OnPropertyChanged(nameof(ShowEditor)); }
        }

        /// <summary>연결되지 않은 입력의 식 텍스트 (리터럴). 편집하면 문서에 반영.</summary>
        public string Literal
        {
            get => _literal;
            set
            {
                if (!Set(ref _literal, value ?? "")) return;
                if (string.IsNullOrWhiteSpace(_literal)) Node.Model.Literals.Remove(Name);
                else Node.Model.Literals[Name] = _literal;
                Node.Graph.MarkChanged();
            }
        }

        internal void SetLiteralSilently(string value)
        {
            _literal = value ?? "";
            OnPropertyChanged(nameof(Literal));
        }

        /// <summary>리터럴 편집창 표시 여부 (입력 + 비연결 + 비아이코닉).</summary>
        public bool ShowEditor => IsInput && !IsConnected && !IsIconic;
    }

    /// <summary>노드 하나 (연산자 호출).</summary>
    public sealed class NodeVm : ObservableObject
    {
        private Point _location;
        private bool _isSelected;

        public NodeVm(NodeGraphViewModel graph, GraphNode model, OperatorDescriptor? descriptor)
        {
            Graph = graph;
            Model = model;
            Descriptor = descriptor;
            _location = new Point(model.X, model.Y);
            if (descriptor != null)
            {
                foreach (var p in descriptor.Inputs)
                {
                    var c = new ConnectorVm(this, p, true);
                    if (model.Literals.TryGetValue(p.Name, out var lit)) c.SetLiteralSilently(lit);
                    Inputs.Add(c);
                }
                foreach (var p in descriptor.Outputs)
                    Outputs.Add(new ConnectorVm(this, p, false));
            }
        }

        public NodeGraphViewModel Graph { get; }
        public GraphNode Model { get; }
        public OperatorDescriptor? Descriptor { get; }
        public ObservableCollection<ConnectorVm> Inputs { get; } = new ObservableCollection<ConnectorVm>();
        public ObservableCollection<ConnectorVm> Outputs { get; } = new ObservableCollection<ConnectorVm>();

        public string Variable => Model.Variable;
        public string Operator => Model.Operator;
        public string Title => $"{Model.Variable} = {Model.Operator}";
        public string Category => Descriptor?.Category ?? "";
        public string ToolTip => Descriptor?.Signature ?? Model.Operator;

        // ---- 실행 상태 (스크립트 실행 줄/오류 줄/실행 기록을 노드에 표시) ----
        private bool _isCurrent;
        private bool _hasError;
        private string? _timeText;

        /// <summary>지금 실행할(멈춘) 줄의 노드.</summary>
        public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }
        /// <summary>오류가 난 줄의 노드.</summary>
        public bool HasError { get => _hasError; set => Set(ref _hasError, value); }
        /// <summary>마지막 실행 시간과 백엔드 (예: "1.23 ms Gpu"). 실행 전이면 null (노드 아래 표시 없음).</summary>
        public string? TimeText { get => _timeText; set => Set(ref _timeText, value); }

        public Point Location
        {
            get => _location;
            set
            {
                if (!Set(ref _location, value)) return;
                Model.X = value.X;
                Model.Y = value.Y;
                Graph.MarkChanged();
            }
        }

        public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

        public ConnectorVm? FindInput(string name) => Inputs.FirstOrDefault(c => c.Name == name);
        public ConnectorVm? FindOutput(string name) => Outputs.FirstOrDefault(c => c.Name == name);
    }

    /// <summary>출력 → 입력 연결 (화면용).</summary>
    public sealed class ConnectionVm
    {
        public ConnectionVm(ConnectorVm output, ConnectorVm input, Action<ConnectionVm> disconnect)
        {
            Output = output;
            Input = input;
            DisconnectCommand = new RelayCommand(() => disconnect(this));
        }

        public ConnectorVm Output { get; }
        public ConnectorVm Input { get; }
        public ICommand DisconnectCommand { get; }
    }

    /// <summary>드래그 중인 연결 (Nodify PendingConnection 의 Started/Completed 커맨드).</summary>
    public sealed class PendingConnectionVm
    {
        private readonly NodeGraphViewModel _graph;
        private ConnectorVm? _source;

        public PendingConnectionVm(NodeGraphViewModel graph)
        {
            _graph = graph;
            StartCommand = new RelayCommand(o => _source = o as ConnectorVm);
            FinishCommand = new RelayCommand(o =>
            {
                if (_source != null && o is ConnectorVm target)
                    _graph.Connect(_source, target);
                _source = null;
            });
        }

        public ICommand StartCommand { get; }
        public ICommand FinishCommand { get; }
    }

    /// <summary>
    /// 노드 편집기 뷰모델. <see cref="GraphDocument"/> 를 소유하고 화면 컬렉션과 동기화한다. 스크립트가 원본이므로
    /// <see cref="LoadFromScript"/> / <see cref="ToScript"/> 로 에디터와 주고받는다.
    /// </summary>
    public sealed class NodeGraphViewModel : ObservableObject
    {
        private readonly OperatorRegistry _registry;
        private GraphDocument _document = new GraphDocument();
        private string _status = "";
        private bool _isDirty;
        private double _nextX = 40, _nextY = 40;

        public NodeGraphViewModel(OperatorRegistry registry)
        {
            _registry = registry;
            PendingConnection = new PendingConnectionVm(this);
        }

        public ObservableCollection<NodeVm> Nodes { get; } = new ObservableCollection<NodeVm>();
        public ObservableCollection<ConnectionVm> Connections { get; } = new ObservableCollection<ConnectionVm>();
        public PendingConnectionVm PendingConnection { get; }
        public GraphDocument Document => _document;

        public string Status { get => _status; set => Set(ref _status, value); }

        /// <summary>그래프가 스크립트보다 새로운지 (To Script 필요).</summary>
        public bool IsDirty { get => _isDirty; private set => Set(ref _isDirty, value); }

        /// <summary>그래프가 바뀌었을 때 (자동 동기화용).</summary>
        public event Action? Changed;

        internal void MarkChanged()
        {
            IsDirty = true;
            Changed?.Invoke();
        }

        // ------------------------------------------------------------------ 스크립트 ↔ 그래프

        public bool LoadFromScript(string source)
        {
            var doc = GraphDocument.FromScript(source, _registry, out var problems);
            if (doc == null)
            {
                Status = problems.Count == 0 ? "Script is empty" : "Cannot show as nodes: " + string.Join("; ", problems.Take(3));
                return false;
            }
            _document = doc;
            Rebuild();
            IsDirty = false;
            Status = $"{Nodes.Count} node(s), {Connections.Count} connection(s)";
            if (source.IndexOf("@node", StringComparison.Ordinal) < 0)
                MeasuredLayoutRequested?.Invoke();
            return true;
        }

        public string ToScript()
        {
            IsDirty = false;
            return _document.ToScript(_registry);
        }

        private void Rebuild()
        {
            Nodes.Clear();
            Connections.Clear();
            foreach (var n in _document.Nodes)
            {
                _registry.TryGet(n.Operator, out var d);
                Nodes.Add(new NodeVm(this, n, d));
            }
            foreach (var c in _document.Connections)
                AddConnectionVm(c);
            if (_document.Nodes.Count > 0)
            {
                _nextX = _document.Nodes.Max(n => n.X) + 260;
                _nextY = 40;
            }
        }

        private void AddConnectionVm(GraphConnection c)
        {
            var from = Nodes.FirstOrDefault(n => n.Variable == c.FromNode);
            var to = Nodes.FirstOrDefault(n => n.Variable == c.ToNode);
            var output = from?.FindOutput(c.FromOutput) ?? from?.Outputs.FirstOrDefault();
            var input = to?.FindInput(c.ToInput);
            if (output == null || input == null) return;
            output.IsConnected = true;
            input.IsConnected = true;
            Connections.Add(new ConnectionVm(output, input, Disconnect));
        }

        // ------------------------------------------------------------------ 편집

        public NodeVm AddNode(string operatorName, Point? location = null)
        {
            var p = location ?? new Point(_nextX, _nextY);
            if (location == null)
            {
                _nextY += 140;
                if (_nextY > 600) { _nextY = 40; _nextX += 260; }
            }
            var model = _document.AddNode(operatorName, p.X, p.Y);
            _registry.TryGet(operatorName, out var d);
            if (d != null)
            {
                // 기본값이 있는 입력은 리터럴로 채워 바로 실행 가능하게
                foreach (var input in d.Inputs)
                {
                    if (input.IsIconic || input.DefaultValue == null) continue;
                    model.Literals[input.Name] = AstPrinter.Literal(input.DefaultValue is Enum e ? e.ToString() : input.DefaultValue);
                }
            }
            var vm = new NodeVm(this, model, d);
            Nodes.Add(vm);
            MarkChanged();
            Status = $"Added {vm.Title}";
            return vm;
        }

        public void RemoveSelected()
        {
            var selected = Nodes.Where(n => n.IsSelected).ToList();
            if (selected.Count == 0) return;
            foreach (var n in selected)
            {
                foreach (var c in Connections.Where(c => c.Input.Node == n || c.Output.Node == n).ToList())
                    RemoveConnectionVm(c);
                _document.RemoveNode(n.Model);
                Nodes.Remove(n);
            }
            MarkChanged();
            Status = $"Removed {selected.Count} node(s)";
        }

        public bool Connect(ConnectorVm a, ConnectorVm b)
        {
            ConnectorVm output, input;
            if (!a.IsInput && b.IsInput) { output = a; input = b; }
            else if (a.IsInput && !b.IsInput) { output = b; input = a; }
            else { Status = "Connect an output to an input"; return false; }

            if (input.IsIconic != output.IsIconic)
            {
                Status = $"Type mismatch: {output.Title} -> {input.Title}";
                return false;
            }
            if (!_document.Connect(output.Node.Variable, output.Name, input.Node.Variable, input.Name))
            {
                Status = "Connection would create a cycle";
                return false;
            }
            foreach (var old in Connections.Where(c => c.Input == input).ToList())
                RemoveConnectionVm(old);
            output.IsConnected = true;
            input.IsConnected = true;
            Connections.Add(new ConnectionVm(output, input, Disconnect));
            MarkChanged();
            Status = $"{output.Node.Variable}.{output.Name} -> {input.Node.Variable}.{input.Name}";
            return true;
        }

        private void Disconnect(ConnectionVm c)
        {
            _document.Disconnect(c.Input.Node.Variable, c.Input.Name);
            RemoveConnectionVm(c);
            MarkChanged();
        }

        private void RemoveConnectionVm(ConnectionVm c)
        {
            Connections.Remove(c);
            c.Input.IsConnected = false;
            c.Output.IsConnected = Connections.Any(x => x.Output == c.Output);
        }

        /// <summary>자동 배치: 위상 순서대로 열을 나눠 놓는다.</summary>
        /// <summary>
        /// 스크립트에 위치 주석(<c>// @node</c>)이 없어 기본 배치로 만든 경우, 화면에 그린 뒤 실제 크기로 다시 배치해 달라는 요청.
        /// 뷰가 노드 크기를 재서 <see cref="AutoLayout"/> 을 호출한다.
        /// </summary>
        public event Action? MeasuredLayoutRequested;

        /// <summary>
        /// 스크립트 실행 상태를 노드에 반영한다: <paramref name="currentLine"/> 의 노드를 강조, <paramref name="errorLine"/> 의 노드를 오류로,
        /// 실행 기록(줄 번호가 있는 것)은 그 줄 노드의 마지막 실행 시간으로 표시한다. 노드 줄 번호는 스크립트에서 읽거나 스크립트로 쓸 때 정해진다.
        /// </summary>
        public void ShowExecution(int currentLine, int errorLine, IReadOnlyList<InspectionView.Core.Registry.ExecutionRecord> records)
        {
            var last = new Dictionary<int, InspectionView.Core.Registry.ExecutionRecord>();
            foreach (var r in records)
                if (r.Line > 0) last[r.Line] = r;
            foreach (var vm in Nodes)
            {
                int line = vm.Model.Line;
                vm.IsCurrent = line > 0 && line == currentLine;
                vm.HasError = line > 0 && line == errorLine;
                vm.TimeText = line > 0 && last.TryGetValue(line, out var rec)
                    ? $"{rec.Elapsed.TotalMilliseconds:0.###} ms  {rec.Used}"
                    : null;
            }
        }

        /// <summary>
        /// 데이터 흐름 깊이별 열로 배치한다. <paramref name="measure"/> 가 노드의 실제 크기를 주면 열 폭/행 높이를 거기에 맞추고
        /// (리터럴 입력칸 때문에 노드 폭이 제각각이라 고정 간격이면 겹친다), 없으면 기본 크기로 본다.
        /// </summary>
        public void AutoLayout(Func<NodeVm, Size?>? measure = null)
        {
            var depth = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var n in _document.TopologicalOrder())
            {
                int d = 0;
                foreach (var c in _document.Connections.Where(c => c.ToNode == n.Variable))
                    if (depth.TryGetValue(c.FromNode, out var fd)) d = Math.Max(d, fd + 1);
                depth[n.Variable] = d;
            }
            const double Margin = 40, ColumnGap = 60, RowGap = 30;
            var defaultSize = new Size(260, 120);
            var columns = new SortedDictionary<int, List<(NodeVm vm, Size size)>>();
            foreach (var vm in Nodes)
            {
                int d = depth.TryGetValue(vm.Variable, out var dd) ? dd : 0;
                var size = measure?.Invoke(vm) ?? defaultSize;
                if (size.Width <= 0 || size.Height <= 0) size = defaultSize;
                if (!columns.TryGetValue(d, out var list)) columns[d] = list = new List<(NodeVm, Size)>();
                list.Add((vm, size));
            }

            double x = Margin;
            foreach (var col in columns.Values)
            {
                double y = Margin, width = 0;
                foreach (var (vm, size) in col)
                {
                    vm.Location = new Point(x, y);
                    y += size.Height + RowGap;
                    width = Math.Max(width, size.Width);
                }
                x += width + ColumnGap;
            }
            Status = "Auto layout applied";
        }
    }
}
