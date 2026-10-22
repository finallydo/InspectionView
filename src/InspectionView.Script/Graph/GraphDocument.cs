using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using InspectionView.Core.Registry;
using InspectionView.Script.Ast;
using InspectionView.Script.Parsing;

namespace InspectionView.Script.Graph
{
    /// <summary>노드 하나 = 연산자 호출 한 줄 (<c>Var = Op(...)</c>).</summary>
    public sealed class GraphNode
    {
        /// <summary>결과 변수 이름 (노드 식별자이기도 하다).</summary>
        public string Variable { get; set; } = "";
        public string Operator { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        /// <summary>연결되지 않은 입력의 식 텍스트 (리터럴 또는 임의의 식).</summary>
        public Dictionary<string, string> Literals { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>원본 스크립트 줄 (FromScript 로 만든 경우, 1부터; 없으면 0).</summary>
        public int Line { get; set; }

        public override string ToString() => $"{Variable} = {Operator}(...)";
    }

    /// <summary>출력 포트 → 입력 포트 연결. 출력이 하나뿐인 연산자는 FromOutput 이 그 출력 이름이다.</summary>
    public sealed class GraphConnection
    {
        public string FromNode { get; set; } = "";
        public string FromOutput { get; set; } = "";
        public string ToNode { get; set; } = "";
        public string ToInput { get; set; } = "";

        public override string ToString() => $"{FromNode}.{FromOutput} -> {ToNode}.{ToInput}";
    }

    /// <summary>
    /// 노드 그래프 문서. 스크립트와 상호 변환된다: 각 노드는 <c>Var = Op(args) // @node x,y</c> 한 줄이고,
    /// 연결은 인자 자리의 변수 참조(<c>Var</c> 또는 <c>Var.Output</c>)다. 그래프는 스크립트의 뷰이며 스크립트가 원본이다.
    /// </summary>
    public sealed class GraphDocument
    {
        private static readonly Regex NodeMeta = new Regex(@"//\s*@node\s+(-?[0-9.]+)\s*,\s*(-?[0-9.]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public List<GraphNode> Nodes { get; } = new List<GraphNode>();
        public List<GraphConnection> Connections { get; } = new List<GraphConnection>();

        public GraphNode? FindNode(string variable) => Nodes.FirstOrDefault(n => n.Variable == variable);

        private static OperatorDescriptor? Find(OperatorRegistry registry, string name)
            => registry.TryGet(name, out var d) ? d : null;

        /// <summary>연산자 이름으로 겹치지 않는 변수 이름을 만든다 (Threshold1, Threshold2 ...).</summary>
        public string NewVariableName(string operatorName)
        {
            for (int i = 1; ; i++)
            {
                string name = operatorName + i.ToString(CultureInfo.InvariantCulture);
                if (FindNode(name) == null) return name;
            }
        }

        public GraphNode AddNode(string operatorName, double x, double y, string? variable = null)
        {
            var node = new GraphNode { Operator = operatorName, X = x, Y = y, Variable = variable ?? NewVariableName(operatorName) };
            Nodes.Add(node);
            return node;
        }

        public void RemoveNode(GraphNode node)
        {
            Nodes.Remove(node);
            Connections.RemoveAll(c => c.FromNode == node.Variable || c.ToNode == node.Variable);
        }

        /// <summary>입력 하나에는 연결이 하나만. 기존 연결은 교체된다. 순환이 생기면 false.</summary>
        public bool Connect(string fromNode, string fromOutput, string toNode, string toInput)
        {
            if (fromNode == toNode) return false;
            if (ReachesFrom(toNode, fromNode)) return false;      // toNode → ... → fromNode 면 순환
            Connections.RemoveAll(c => c.ToNode == toNode && c.ToInput == toInput);
            Connections.Add(new GraphConnection { FromNode = fromNode, FromOutput = fromOutput, ToNode = toNode, ToInput = toInput });
            return true;
        }

        public void Disconnect(string toNode, string toInput)
            => Connections.RemoveAll(c => c.ToNode == toNode && c.ToInput == toInput);

        private bool ReachesFrom(string start, string target)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (n == target) return true;
                if (!seen.Add(n)) continue;
                foreach (var c in Connections.Where(c => c.FromNode == n))
                    stack.Push(c.ToNode);
            }
            return false;
        }

        /// <summary>위상 정렬된 노드 순서 (연결이 없으면 추가 순서 유지). 순환이 있으면 예외.</summary>
        public IReadOnlyList<GraphNode> TopologicalOrder()
        {
            var result = new List<GraphNode>();
            var done = new HashSet<string>(StringComparer.Ordinal);
            var visiting = new HashSet<string>(StringComparer.Ordinal);

            void Visit(GraphNode n)
            {
                if (done.Contains(n.Variable)) return;
                if (!visiting.Add(n.Variable))
                    throw new InvalidOperationException($"Cycle through node '{n.Variable}'.");
                foreach (var c in Connections.Where(c => c.ToNode == n.Variable))
                {
                    var from = FindNode(c.FromNode);
                    if (from != null) Visit(from);
                }
                visiting.Remove(n.Variable);
                done.Add(n.Variable);
                result.Add(n);
            }

            foreach (var n in Nodes) Visit(n);
            return result;
        }

        // ------------------------------------------------------------------ 그래프 → 스크립트

        public string ToScript(OperatorRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            var sb = new StringBuilder();
            int line = 0;
            foreach (var node in TopologicalOrder())
            {
                node.Line = ++line;          // 쓴 스크립트의 줄 번호 (실행 중 노드 강조에 쓴다)
                sb.Append(NodeToLine(node, registry)).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>노드 한 줄: <c>Var = Op(a, b, Name = c) // @node x,y</c>.</summary>
        public string NodeToLine(GraphNode node, OperatorRegistry registry)
        {
            var desc = Find(registry, node.Operator);
            var args = new List<string>();
            bool positional = true;
            if (desc != null)
            {
                foreach (var input in desc.Inputs)
                {
                    string? text = ArgumentText(node, input.Name, registry);
                    if (text == null)
                    {
                        positional = false;              // 생략된 입력 이후는 이름 인자
                        continue;
                    }
                    args.Add(positional ? text : $"{input.Name} = {text}");
                }
            }
            else
            {
                foreach (var kv in node.Literals)
                    args.Add($"{kv.Key} = {kv.Value}");
            }
            string meta = string.Format(CultureInfo.InvariantCulture, "// @node {0},{1}", Math.Round(node.X), Math.Round(node.Y));
            return $"{node.Variable} = {node.Operator}({string.Join(", ", args)})  {meta}";
        }

        private string? ArgumentText(GraphNode node, string inputName, OperatorRegistry registry)
        {
            var conn = Connections.FirstOrDefault(c => c.ToNode == node.Variable && c.ToInput == inputName);
            if (conn != null)
            {
                var from = FindNode(conn.FromNode);
                var fromDesc = from != null ? Find(registry, from.Operator) : null;
                bool single = fromDesc == null || fromDesc.Outputs.Count <= 1;
                return single ? conn.FromNode : $"{conn.FromNode}.{conn.FromOutput}";
            }
            if (node.Literals.TryGetValue(inputName, out var lit) && !string.IsNullOrWhiteSpace(lit))
                return lit;
            return null;
        }

        // ------------------------------------------------------------------ 스크립트 → 그래프

        /// <summary>
        /// 스크립트를 그래프로 읽는다. 연산자 호출 대입문만 노드가 된다. 그 외 문장(if/for/수식 대입 등)이 있으면
        /// <paramref name="problems"/> 에 줄 번호와 함께 기록되고 null 을 돌려준다 (스크립트가 원본이므로 덮어쓰지 않기 위해).
        /// </summary>
        public static GraphDocument? FromScript(string source, OperatorRegistry registry, out IReadOnlyList<string> problems)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            var list = new List<string>();
            problems = list;
            var program = Parser.Parse(source ?? "", out var diagnostics);
            foreach (var d in diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                list.Add($"line {d.Line}: {d.Message}");
            if (list.Count > 0) return null;

            var lines = (source ?? "").Replace("\r\n", "\n").Split('\n');
            var doc = new GraphDocument();
            double autoX = 40, autoY = 40;

            foreach (var st in program.Body.Statements)
            {
                if (!(st is AssignmentStatement asg) || !(asg.Value is CallExpression call) || Find(registry, call.Name) == null)
                {
                    list.Add($"line {st.Line}: not a single operator call assignment");
                    continue;
                }
                if (doc.FindNode(asg.Target) != null)
                {
                    list.Add($"line {st.Line}: variable '{asg.Target}' is assigned twice");
                    continue;
                }
                var desc = Find(registry, call.Name)!;
                var node = new GraphNode { Variable = asg.Target, Operator = call.Name, Line = st.Line };

                // 위치 메타데이터
                string lineText = st.Line >= 1 && st.Line <= lines.Length ? lines[st.Line - 1] : "";
                var m = NodeMeta.Match(lineText);
                if (m.Success)
                {
                    node.X = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    node.Y = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    autoY = Math.Max(autoY, node.Y + 120);      // 배치 정보가 없는 다음 노드는 그 아래에
                }
                else
                {
                    node.X = autoX; node.Y = autoY;
                    autoY += 120;
                }
                doc.Nodes.Add(node);

                for (int i = 0; i < call.Arguments.Count; i++)
                {
                    if (i >= desc.Inputs.Count)
                    {
                        list.Add($"line {st.Line}: too many arguments for {call.Name}");
                        break;
                    }
                    Bind(doc, node, desc.Inputs[i].Name, call.Arguments[i], registry);
                }
                foreach (var kv in call.NamedArguments)
                {
                    if (desc.FindInput(kv.Key) == null)
                        list.Add($"line {st.Line}: {call.Name} has no input '{kv.Key}'");
                    else
                        Bind(doc, node, kv.Key, kv.Value, registry);
                }
            }
            return list.Count > 0 ? null : doc;
        }

        private static void Bind(GraphDocument doc, GraphNode node, string inputName, Expression arg, OperatorRegistry registry)
        {
            if (arg is IdentifierExpression id && doc.FindNode(id.Name) is GraphNode from)
            {
                var fromDesc = Find(registry, from.Operator);
                string output = fromDesc != null && fromDesc.Outputs.Count > 0 ? fromDesc.Outputs[0].Name : "";
                doc.Connections.Add(new GraphConnection { FromNode = from.Variable, FromOutput = output, ToNode = node.Variable, ToInput = inputName });
                return;
            }
            if (arg is MemberExpression mem && mem.Target is IdentifierExpression tid && doc.FindNode(tid.Name) is GraphNode from2)
            {
                var fromDesc = Find(registry, from2.Operator);
                if (fromDesc != null && fromDesc.FindOutput(mem.Member) != null)
                {
                    doc.Connections.Add(new GraphConnection { FromNode = from2.Variable, FromOutput = mem.Member, ToNode = node.Variable, ToInput = inputName });
                    return;
                }
            }
            node.Literals[inputName] = AstPrinter.Print(arg);
        }
    }
}
