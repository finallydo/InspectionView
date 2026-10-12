using System.Linq;
using InspectionView.Core.Registry;
using InspectionView.Script.Graph;
using InspectionView.Script.Parsing;
using Xunit;

namespace InspectionView.Script.Tests
{
    public class GraphTests
    {
        private static readonly OperatorRegistry Registry = OperatorRegistry.Default;

        [Fact]
        public void AstPrinter_RoundTripsExpressions()
        {
            string[] samples =
            {
                "1", "2.5", "'a\\'b'", "true", "[1, 2, 'x']", "A + B * 2", "(A + B) * 2", "-A", "not A", "A.Area", "T[3]",
                "Max(A, 1) == 3 and B < 2", "Op(1, Name = 'v')", "A - (B - C)",
            };
            foreach (var s in samples)
            {
                var expr = Parser.ParseExpressionText(s);
                string printed = AstPrinter.Print(expr);
                Assert.Equal(s, printed);
            }
        }

        [Fact]
        public void FromScript_BuildsNodesAndConnections_AndBackToScript()
        {
            string script =
                "Image = ReadImage('C:/images/part.png')  // @node 40,40\n" +
                "Region = Threshold(Image, 128, 255) // @node 300,40\n" +
                "Blobs = Connection(Region)\n" +
                "Sel = SelectShape(Blobs, 'Area', 100, 99999)\n" +
                "Feat = AreaCenter(Sel)\n" +
                "Part = CropPart(Image, Feat.Row, Feat.Column, 10, 10)\n";

            var doc = GraphDocument.FromScript(script, Registry, out var problems);
            Assert.True(doc != null, string.Join("; ", problems));
            Assert.Equal(6, doc!.Nodes.Count);
            Assert.Equal(40, doc.FindNode("Image")!.X);
            Assert.Equal(300, doc.FindNode("Region")!.X);
            Assert.True(doc.FindNode("Blobs")!.Y > doc.FindNode("Image")!.Y);     // 자동 배치

            var th = doc.FindNode("Region")!;
            Assert.Equal("128", th.Literals["MinGray"]);
            Assert.Equal("255", th.Literals["MaxGray"]);
            Assert.Contains(doc.Connections, c => c.FromNode == "Image" && c.FromOutput == "Image" && c.ToNode == "Region" && c.ToInput == "Image");
            Assert.Contains(doc.Connections, c => c.FromNode == "Feat" && c.FromOutput == "Row" && c.ToNode == "Part" && c.ToInput == "Row");
            Assert.Contains(doc.Connections, c => c.FromNode == "Image" && c.ToNode == "Part" && c.ToInput == "Image");
            Assert.Equal("'Area'", doc.FindNode("Sel")!.Literals["Feature"]);

            string back = doc.ToScript(Registry);
            var again = GraphDocument.FromScript(back, Registry, out var problems2);
            Assert.True(again != null, string.Join("; ", problems2));
            Assert.Equal(doc.Nodes.Count, again!.Nodes.Count);
            Assert.Equal(doc.Connections.Count, again.Connections.Count);
            Assert.Contains("Part = CropPart(Image, Feat.Row, Feat.Column, 10, 10)", back);
            Assert.Contains("// @node 40,40", back);

            // 원래 스크립트와 같은 결과를 내는지: 파싱 가능 + 같은 줄 수
            Parser.Parse(back, out var diags);
            Assert.Empty(diags);
        }

        [Fact]
        public void FromScript_RejectsControlFlow_AndReportsLine()
        {
            string script = "Image = ReadImage('a.png')\nfor I = 1 to 3\n  Print(I)\nendfor\n";
            var doc = GraphDocument.FromScript(script, Registry, out var problems);
            Assert.Null(doc);
            Assert.Contains(problems, p => p.StartsWith("line 2"));

            Assert.Null(GraphDocument.FromScript("X = 1 + 2", Registry, out var p2));
            Assert.Single(p2);
            Assert.Null(GraphDocument.FromScript("X = ReadImage(", Registry, out var p3));
            Assert.NotEmpty(p3);
        }

        [Fact]
        public void Editing_AddConnectRemove_AndNamedArgumentsForSkippedInputs()
        {
            var doc = new GraphDocument();
            var read = doc.AddNode("ReadImage", 0, 0);
            read.Literals["FileName"] = "'x.png'";
            var th = doc.AddNode("Threshold", 200, 0);
            th.Literals["MaxGray"] = "200";                                   // MinGray 생략 → 이름 인자
            Assert.Equal("ReadImage1", read.Variable);
            Assert.Equal("Threshold1", th.Variable);
            Assert.Equal("Threshold2", doc.NewVariableName("Threshold"));

            Assert.True(doc.Connect("ReadImage1", "Image", "Threshold1", "Image"));
            Assert.False(doc.Connect("Threshold1", "Region", "ReadImage1", "FileName"));   // 순환 금지
            Assert.False(doc.Connect("Threshold1", "Region", "Threshold1", "Image"));

            string script = doc.ToScript(Registry);
            Assert.Contains("ReadImage1 = ReadImage('x.png')", script);
            Assert.Contains("Threshold1 = Threshold(ReadImage1, MaxGray = 200)", script);

            var blobs = doc.AddNode("Connection", 400, 0);
            Assert.True(doc.Connect("Threshold1", "Region", "Connection1", "Region"));
            var order = doc.TopologicalOrder().Select(n => n.Variable).ToList();
            Assert.Equal(new[] { "ReadImage1", "Threshold1", "Connection1" }, order);

            doc.RemoveNode(th);
            Assert.Equal(2, doc.Nodes.Count);
            Assert.Empty(doc.Connections);
            Assert.NotNull(blobs);
        }
    }
}
