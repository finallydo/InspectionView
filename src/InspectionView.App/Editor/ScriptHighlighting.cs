using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using InspectionView.Core.Registry;
using InspectionView.Script.Runtime;

namespace InspectionView.App.Editor
{
    /// <summary>스크립트 구문 하이라이트 정의. 연산자 이름은 레지스트리에서 가져와 XSHD 를 생성한다.</summary>
    public static class ScriptHighlighting
    {
        public static readonly string[] Keywords =
        {
            "if", "elseif", "else", "endif", "for", "to", "by", "endfor", "while", "endwhile",
            "break", "continue", "and", "or", "not", "true", "false",
        };

        public static IHighlightingDefinition Create(OperatorRegistry registry, Builtins builtins)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\"?>");
            sb.AppendLine("<SyntaxDefinition name=\"InspectionView\" extensions=\".ivs\" xmlns=\"http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008\">");
            sb.AppendLine("  <Color name=\"Comment\" foreground=\"#008000\" />");
            sb.AppendLine("  <Color name=\"String\" foreground=\"#A31515\" />");
            sb.AppendLine("  <Color name=\"Number\" foreground=\"#098658\" />");
            sb.AppendLine("  <Color name=\"Keyword\" foreground=\"#0000FF\" fontWeight=\"bold\" />");
            sb.AppendLine("  <Color name=\"Operator\" foreground=\"#795E26\" fontWeight=\"bold\" />");
            sb.AppendLine("  <Color name=\"Builtin\" foreground=\"#267F99\" />");
            sb.AppendLine("  <RuleSet ignoreCase=\"false\">");
            sb.AppendLine("    <Span color=\"Comment\" begin=\"//\" />");
            sb.AppendLine("    <Span color=\"String\" begin=\"'\" end=\"'\"><RuleSet><Span begin=\"\\\\\" end=\".\" /></RuleSet></Span>");
            sb.AppendLine("    <Span color=\"String\" begin=\"&quot;\" end=\"&quot;\"><RuleSet><Span begin=\"\\\\\" end=\".\" /></RuleSet></Span>");
            AppendKeywords(sb, "Keyword", Keywords);
            AppendKeywords(sb, "Operator", registry.All.Select(d => d.Name));
            AppendKeywords(sb, "Builtin", builtins.Names);
            sb.AppendLine("    <Rule color=\"Number\">\\b0[xX][0-9a-fA-F]+|\\b\\d+(\\.\\d+)?([eE][+-]?\\d+)?</Rule>");
            sb.AppendLine("  </RuleSet>");
            sb.AppendLine("</SyntaxDefinition>");

            using (var reader = XmlReader.Create(new StringReader(sb.ToString())))
                return HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }

        private static void AppendKeywords(StringBuilder sb, string color, IEnumerable<string> words)
        {
            var list = words.Where(w => !string.IsNullOrWhiteSpace(w)).Distinct().ToList();
            if (list.Count == 0)
                return;
            sb.Append("    <Keywords color=\"").Append(color).AppendLine("\">");
            foreach (var w in list)
                sb.Append("      <Word>").Append(System.Security.SecurityElement.Escape(w)).AppendLine("</Word>");
            sb.AppendLine("    </Keywords>");
        }
    }
}
