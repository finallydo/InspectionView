using System.Linq;
using InspectionView.Script.Ast;
using InspectionView.Script.Compilation;
using InspectionView.Script.Lexing;
using InspectionView.Script.Parsing;
using InspectionView.Script.Runtime;
using Xunit;

namespace InspectionView.Script.Tests
{
    public class ParserTests
    {
        [Fact]
        public void Lexer_TokenizesNumbersStringsAndOperators()
        {
            var tokens = Lexer.Tokenize("A = 12 + 3.5e1 // comment\nB = 'it\\'s' != \"x\"");
            var kinds = tokens.Select(t => t.Kind).ToArray();

            Assert.Equal(new[]
            {
                TokenKind.Identifier, TokenKind.Assign, TokenKind.Number, TokenKind.Plus, TokenKind.Number, TokenKind.Newline,
                TokenKind.Identifier, TokenKind.Assign, TokenKind.String, TokenKind.NotEqual, TokenKind.String, TokenKind.EndOfFile,
            }, kinds);

            Assert.Equal(12L, tokens[2].Value);
            Assert.Equal(35.0, tokens[4].Value);
            Assert.Equal("it's", tokens[8].Value);
            Assert.Equal(2, tokens[6].Line);
        }

        [Fact]
        public void Parser_BuildsStatements()
        {
            var src = @"
// 주석
Image = ReadImage('a.png')
Region = Threshold(Image, 128, MaxGray = 255)
if Region.Area > 10 and not Done
    N = N + 1
elseif Region.Area == 0
    N = 0
else
    N = -1
endif
for I = 0 to 9 by 2
    WriteImage(Image, 'out' + I + '.png')
endfor
while N < 3
    N = N + 1
endwhile
";
            var program = Parser.Parse(src, out var diags);
            Assert.Empty(diags);
            Assert.Equal(5, program.Body.Statements.Count);

            var assign = Assert.IsType<AssignmentStatement>(program.Body.Statements[0]);
            Assert.Equal("Image", assign.Target);
            Assert.Equal(3, assign.Line);

            var call = Assert.IsType<CallExpression>(((AssignmentStatement)program.Body.Statements[1]).Value);
            Assert.Equal("Threshold", call.Name);
            Assert.Equal(2, call.Arguments.Count);
            Assert.Single(call.NamedArguments);
            Assert.Equal("MaxGray", call.NamedArguments[0].Key);

            var ifs = Assert.IsType<IfStatement>(program.Body.Statements[2]);
            Assert.Single(ifs.ElseIfs);
            Assert.NotNull(ifs.Else);
            Assert.Equal(11, ifs.EndSpan.Line);

            var fors = Assert.IsType<ForStatement>(program.Body.Statements[3]);
            Assert.Equal("I", fors.Variable);
            Assert.NotNull(fors.Step);

            Assert.IsType<WhileStatement>(program.Body.Statements[4]);
        }

        [Fact]
        public void Parser_ReportsAllErrorsWithLines()
        {
            var src = "A = 1\nB = (2 + \nC = 3\nif A\nD = 4\n";
            Parser.Parse(src, out var diags);

            Assert.Equal(2, diags.Count);
            Assert.Equal(2, diags[0].Line);
            Assert.Contains("expression", diags[0].Message);
            Assert.Equal(6, diags[1].Line);          // endif 없음 → 파일 끝에서 보고
            Assert.Contains("endif", diags[1].Message);
        }

        [Fact]
        public void Parser_RejectsStrayKeywordsAndBadTokens()
        {
            Parser.Parse("endif\nA = 1 @ 2\n", out var diags);
            Assert.Equal(2, diags.Count);
            Assert.Contains("without matching", diags[0].Message);
            Assert.Contains("@", diags[1].Message);
        }

        [Fact]
        public void Compiler_ResolvesLoopJumpsAndReportsBreakOutsideLoop()
        {
            var program = Parser.Parse("for I = 1 to 3\n  if I == 2\n    continue\n  endif\n  A = I\nendfor\nbreak\n", out var pd);
            Assert.Empty(pd);
            var code = Compiler.Compile(program, out var cd);

            Assert.Single(cd);
            Assert.Equal(7, cd[0].Line);

            Assert.Equal(OpCode.ForInit, code[0].Op);
            var next = code.Last(i => i.Op == OpCode.ForNext);
            Assert.Equal(6, next.Line);
            Assert.Equal(1, next.Target);                       // 본문 시작
            Assert.Equal(code.Length, code[0].Target);          // 루프 끝 (break 는 오류라 생성되지 않음)
            var cont = code.First(i => i.Op == OpCode.Jump && i.Line == 3);
            Assert.Equal(code.Length - 1, cont.Target);         // continue → ForNext
        }

        [Fact]
        public void Literal_RoundTrip()
        {
            var t = ScriptValues.ParseLiteral("[1, -2.5, 'a b', true]");
            Assert.Equal(4, t.Length);
            Assert.Equal(-2.5, t.D(1));
            Assert.Equal("a b", t.S(2));
            Assert.Equal(t, ScriptValues.ParseLiteral(ScriptValues.ToLiteral(t)));

            Assert.Equal(42L, ScriptValues.ParseLiteral("42").L());
            Assert.Equal("x", ScriptValues.ParseLiteral("'x'").S());
            Assert.True(ScriptValues.ParseLiteral("").IsEmpty);
            Assert.Throws<ScriptRuntimeException>(() => ScriptValues.ParseLiteral("Foo"));
            Assert.Throws<ScriptRuntimeException>(() => ScriptValues.ParseLiteral("ReadImage('a')"));
            Assert.Throws<ScriptRuntimeException>(() => ScriptValues.ParseLiteral("[1,"));
        }

        [Fact]
        public void Engine_Check_ReturnsDiagnosticsWithoutRunning()
        {
            Assert.Empty(ScriptEngine.Check("A = 1\n"));
            Assert.NotEmpty(ScriptEngine.Check("A = \n"));
            Assert.NotEmpty(ScriptEngine.Check("continue\n"));
        }
    }
}
