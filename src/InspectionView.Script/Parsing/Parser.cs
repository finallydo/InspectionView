using System;
using System.Collections.Generic;
using InspectionView.Script.Ast;
using InspectionView.Script.Lexing;

namespace InspectionView.Script.Parsing
{
    /// <summary>
    /// 재귀 하강 파서. 줄 단위 문법이며, 오류가 나면 해당 줄을 건너뛰고 계속 파싱해 모든 오류를 모은다.
    /// </summary>
    public sealed class Parser
    {
        private readonly List<Token> _tokens;
        private readonly List<Diagnostic> _diagnostics = new List<Diagnostic>();
        private int _pos;

        public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

        private Parser(List<Token> tokens)
        {
            _tokens = tokens;
        }

        /// <summary>식 하나를 파싱한다 (Job 파라미터 값 등). 구문 오류면 <see cref="ParseException"/>.</summary>
        public static Expression ParseExpressionText(string text)
        {
            var parser = new Parser(Lexer.Tokenize(text ?? ""));
            parser.SkipNewlines();
            var expr = parser.ParseExpression();
            parser.SkipNewlines();
            if (!parser.AtEnd)
                throw parser.Error(parser.Cur, $"Unexpected {Describe(parser.Cur)} after expression");
            return expr;
        }

        /// <summary>소스를 파싱한다. 오류가 있어도 가능한 부분까지의 AST 를 돌려준다.</summary>
        public static ProgramNode Parse(string source, out IReadOnlyList<Diagnostic> diagnostics)
        {
            var parser = new Parser(Lexer.Tokenize(source));
            var program = parser.ParseProgram();
            diagnostics = parser._diagnostics;
            return program;
        }

        // ------------------------------------------------------------------
        // 토큰 유틸
        // ------------------------------------------------------------------

        private Token Cur => _tokens[_pos];
        private Token PeekToken(int n = 1) => _tokens[Math.Min(_pos + n, _tokens.Count - 1)];
        private bool At(TokenKind kind) => Cur.Kind == kind;
        private bool AtEnd => Cur.Kind == TokenKind.EndOfFile;

        private Token AdvanceToken()
        {
            var t = Cur;
            if (!AtEnd) _pos++;
            return t;
        }

        private bool Match(TokenKind kind)
        {
            if (!At(kind)) return false;
            _pos++;
            return true;
        }

        private Token Expect(TokenKind kind, string what)
        {
            if (At(kind))
                return AdvanceToken();
            throw Error(Cur, $"Expected {what} but found {Describe(Cur)}");
        }

        private static string Describe(Token t)
        {
            switch (t.Kind)
            {
                case TokenKind.EndOfFile: return "end of file";
                case TokenKind.Newline: return "end of line";
                case TokenKind.Error: return $"'{t.Text}'";
                default: return $"'{t.Text}'";
            }
        }

        private ParseException Error(Token at, string message)
            => new ParseException(at.Line, at.Column, Math.Max(1, at.Length), message);

        private void Report(ParseException ex)
            => _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, ex.Line, ex.Column, ex.Length, ex.Message));

        private void SkipLine()
        {
            while (!AtEnd && !At(TokenKind.Newline)) _pos++;
            Match(TokenKind.Newline);
        }

        private void SkipNewlines()
        {
            while (Match(TokenKind.Newline)) { }
        }

        private void EndOfStatement()
        {
            if (At(TokenKind.Newline) || AtEnd)
            {
                Match(TokenKind.Newline);
                return;
            }
            throw Error(Cur, $"Unexpected {Describe(Cur)} after statement");
        }

        private static SourceSpan SpanOf(Token t) => new SourceSpan(t.Line, t.Column, Math.Max(1, t.Length));

        // ------------------------------------------------------------------
        // 문
        // ------------------------------------------------------------------

        private ProgramNode ParseProgram()
        {
            var program = new ProgramNode { Span = new SourceSpan(1, 1, 0) };
            ParseStatementsInto(program.Body, TokenKind.EndOfFile);
            if (!AtEnd)
            {
                Report(Error(Cur, $"Unexpected {Describe(Cur)}"));
            }
            return program;
        }

        private static bool IsBlockTerminator(TokenKind k)
            => k == TokenKind.EndIf || k == TokenKind.Else || k == TokenKind.ElseIf
               || k == TokenKind.EndFor || k == TokenKind.EndWhile || k == TokenKind.EndOfFile;

        /// <summary>블록 종료 키워드 전까지 문을 읽는다.</summary>
        private void ParseStatementsInto(BlockStatement block, TokenKind terminator)
        {
            while (true)
            {
                SkipNewlines();
                if (AtEnd)
                    return;
                if (IsBlockTerminator(Cur.Kind))
                {
                    if (terminator == TokenKind.EndOfFile)
                    {
                        // 최상위에서 만난 종료 키워드는 짝이 없는 것
                        Report(Error(Cur, $"'{Cur.Text}' without matching block start"));
                        SkipLine();
                        continue;
                    }
                    return;
                }

                try
                {
                    var stmt = ParseStatement();
                    if (stmt != null)
                        block.Statements.Add(stmt);
                }
                catch (ParseException ex)
                {
                    Report(ex);
                    SkipLine();
                }
            }
        }

        private Statement? ParseStatement()
        {
            var t = Cur;
            switch (t.Kind)
            {
                case TokenKind.Error:
                    throw Error(t, (string?)t.Value ?? "Invalid token");

                case TokenKind.If: return ParseIf();
                case TokenKind.For: return ParseFor();
                case TokenKind.While: return ParseWhile();

                case TokenKind.Break:
                    AdvanceToken();
                    EndOfStatement();
                    return new BreakStatement { Span = SpanOf(t) };

                case TokenKind.Continue:
                    AdvanceToken();
                    EndOfStatement();
                    return new ContinueStatement { Span = SpanOf(t) };

                case TokenKind.Identifier:
                    if (PeekToken().Kind == TokenKind.Assign)
                    {
                        AdvanceToken();
                        AdvanceToken();
                        var value = ParseExpression();
                        EndOfStatement();
                        return new AssignmentStatement(t.Text, value) { Span = SpanOf(t) };
                    }
                    if (PeekToken().Kind == TokenKind.LParen)
                    {
                        var call = ParseExpression();
                        EndOfStatement();
                        return new ExpressionStatement(call) { Span = SpanOf(t) };
                    }
                    throw Error(PeekToken(), $"Expected '=' or '(' after '{t.Text}'");

                default:
                    throw Error(t, $"Unexpected {Describe(t)} at start of statement");
            }
        }

        private IfStatement ParseIf()
        {
            var ifTok = Expect(TokenKind.If, "'if'");
            var cond = ParseExpression();
            EndOfStatement();

            var then = new BlockStatement { Span = SpanOf(ifTok) };
            ParseStatementsInto(then, TokenKind.EndIf);
            var stmt = new IfStatement(cond, then) { Span = SpanOf(ifTok) };

            while (At(TokenKind.ElseIf))
            {
                var eiTok = AdvanceToken();
                var eiCond = ParseExpression();
                EndOfStatement();
                var body = new BlockStatement { Span = SpanOf(eiTok) };
                ParseStatementsInto(body, TokenKind.EndIf);
                stmt.ElseIfs.Add(new ElseIfClause(eiCond, body) { Span = SpanOf(eiTok) });
            }

            if (At(TokenKind.Else))
            {
                var elseTok = AdvanceToken();
                EndOfStatement();
                var body = new BlockStatement { Span = SpanOf(elseTok) };
                ParseStatementsInto(body, TokenKind.EndIf);
                stmt.Else = body;
                stmt.ElseSpan = SpanOf(elseTok);
            }

            if (!At(TokenKind.EndIf))
                throw Error(Cur, $"Expected 'endif' for 'if' at line {ifTok.Line}");
            stmt.EndSpan = SpanOf(AdvanceToken());
            EndOfStatement();
            return stmt;
        }

        private ForStatement ParseFor()
        {
            var forTok = Expect(TokenKind.For, "'for'");
            var name = Expect(TokenKind.Identifier, "loop variable");
            Expect(TokenKind.Assign, "'='");
            var start = ParseExpression();
            Expect(TokenKind.To, "'to'");
            var end = ParseExpression();
            Expression? step = null;
            if (Match(TokenKind.By))
                step = ParseExpression();
            EndOfStatement();

            var body = new BlockStatement { Span = SpanOf(forTok) };
            ParseStatementsInto(body, TokenKind.EndFor);
            if (!At(TokenKind.EndFor))
                throw Error(Cur, $"Expected 'endfor' for 'for' at line {forTok.Line}");
            var endTok = AdvanceToken();
            EndOfStatement();

            return new ForStatement(name.Text, start, end, body) { Step = step, Span = SpanOf(forTok), EndSpan = SpanOf(endTok) };
        }

        private WhileStatement ParseWhile()
        {
            var whileTok = Expect(TokenKind.While, "'while'");
            var cond = ParseExpression();
            EndOfStatement();

            var body = new BlockStatement { Span = SpanOf(whileTok) };
            ParseStatementsInto(body, TokenKind.EndWhile);
            if (!At(TokenKind.EndWhile))
                throw Error(Cur, $"Expected 'endwhile' for 'while' at line {whileTok.Line}");
            var endTok = AdvanceToken();
            EndOfStatement();

            return new WhileStatement(cond, body) { Span = SpanOf(whileTok), EndSpan = SpanOf(endTok) };
        }

        // ------------------------------------------------------------------
        // 식 (우선순위 낮은 것부터)
        // ------------------------------------------------------------------

        public Expression ParseExpression() => ParseOr();

        private Expression ParseOr()
        {
            var left = ParseAnd();
            while (At(TokenKind.Or))
            {
                var op = AdvanceToken();
                var right = ParseAnd();
                left = new BinaryExpression(BinaryOperator.Or, left, right) { Span = SpanOf(op) };
            }
            return left;
        }

        private Expression ParseAnd()
        {
            var left = ParseEquality();
            while (At(TokenKind.And))
            {
                var op = AdvanceToken();
                var right = ParseEquality();
                left = new BinaryExpression(BinaryOperator.And, left, right) { Span = SpanOf(op) };
            }
            return left;
        }

        private Expression ParseEquality()
        {
            var left = ParseComparison();
            while (At(TokenKind.Equal) || At(TokenKind.NotEqual))
            {
                var op = AdvanceToken();
                var right = ParseComparison();
                var kind = op.Kind == TokenKind.Equal ? BinaryOperator.Equal : BinaryOperator.NotEqual;
                left = new BinaryExpression(kind, left, right) { Span = SpanOf(op) };
            }
            return left;
        }

        private Expression ParseComparison()
        {
            var left = ParseAdditive();
            while (At(TokenKind.Less) || At(TokenKind.LessEqual) || At(TokenKind.Greater) || At(TokenKind.GreaterEqual))
            {
                var op = AdvanceToken();
                var right = ParseAdditive();
                BinaryOperator kind;
                switch (op.Kind)
                {
                    case TokenKind.Less: kind = BinaryOperator.Less; break;
                    case TokenKind.LessEqual: kind = BinaryOperator.LessEqual; break;
                    case TokenKind.Greater: kind = BinaryOperator.Greater; break;
                    default: kind = BinaryOperator.GreaterEqual; break;
                }
                left = new BinaryExpression(kind, left, right) { Span = SpanOf(op) };
            }
            return left;
        }

        private Expression ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (At(TokenKind.Plus) || At(TokenKind.Minus))
            {
                var op = AdvanceToken();
                var right = ParseMultiplicative();
                var kind = op.Kind == TokenKind.Plus ? BinaryOperator.Add : BinaryOperator.Subtract;
                left = new BinaryExpression(kind, left, right) { Span = SpanOf(op) };
            }
            return left;
        }

        private Expression ParseMultiplicative()
        {
            var left = ParseUnary();
            while (At(TokenKind.Star) || At(TokenKind.Slash) || At(TokenKind.Percent))
            {
                var op = AdvanceToken();
                var right = ParseUnary();
                BinaryOperator kind;
                switch (op.Kind)
                {
                    case TokenKind.Star: kind = BinaryOperator.Multiply; break;
                    case TokenKind.Slash: kind = BinaryOperator.Divide; break;
                    default: kind = BinaryOperator.Modulo; break;
                }
                left = new BinaryExpression(kind, left, right) { Span = SpanOf(op) };
            }
            return left;
        }

        private Expression ParseUnary()
        {
            if (At(TokenKind.Minus))
            {
                var op = AdvanceToken();
                return new UnaryExpression(UnaryOperator.Negate, ParseUnary()) { Span = SpanOf(op) };
            }
            if (At(TokenKind.Not))
            {
                var op = AdvanceToken();
                return new UnaryExpression(UnaryOperator.Not, ParseUnary()) { Span = SpanOf(op) };
            }
            if (At(TokenKind.Plus))
            {
                AdvanceToken();
                return ParseUnary();
            }
            return ParsePostfix();
        }

        private Expression ParsePostfix()
        {
            var expr = ParsePrimary();
            while (true)
            {
                if (At(TokenKind.Dot))
                {
                    var dot = AdvanceToken();
                    var member = Expect(TokenKind.Identifier, "member name");
                    expr = new MemberExpression(expr, member.Text) { Span = SpanOf(dot) };
                }
                else if (At(TokenKind.LBracket))
                {
                    var lb = AdvanceToken();
                    var index = ParseExpression();
                    Expect(TokenKind.RBracket, "']'");
                    expr = new IndexExpression(expr, index) { Span = SpanOf(lb) };
                }
                else
                {
                    return expr;
                }
            }
        }

        private Expression ParsePrimary()
        {
            var t = Cur;
            switch (t.Kind)
            {
                case TokenKind.Number:
                    AdvanceToken();
                    return new LiteralExpression(t.Value!) { Span = SpanOf(t) };

                case TokenKind.String:
                    AdvanceToken();
                    return new LiteralExpression((string)t.Value!) { Span = SpanOf(t) };

                case TokenKind.True:
                    AdvanceToken();
                    return new LiteralExpression(true) { Span = SpanOf(t) };

                case TokenKind.False:
                    AdvanceToken();
                    return new LiteralExpression(false) { Span = SpanOf(t) };

                case TokenKind.Identifier:
                    AdvanceToken();
                    if (At(TokenKind.LParen))
                        return ParseCallArguments(t);
                    return new IdentifierExpression(t.Text) { Span = SpanOf(t) };

                case TokenKind.LParen:
                {
                    AdvanceToken();
                    var inner = ParseExpression();
                    Expect(TokenKind.RParen, "')'");
                    return inner;
                }

                case TokenKind.LBracket:
                {
                    AdvanceToken();
                    var tuple = new TupleExpression { Span = SpanOf(t) };
                    if (!At(TokenKind.RBracket))
                    {
                        do
                        {
                            tuple.Items.Add(ParseExpression());
                        } while (Match(TokenKind.Comma));
                    }
                    Expect(TokenKind.RBracket, "']'");
                    return tuple;
                }

                case TokenKind.Error:
                    throw Error(t, (string?)t.Value ?? "Invalid token");

                default:
                    throw Error(t, $"Expected an expression but found {Describe(t)}");
            }
        }

        private CallExpression ParseCallArguments(Token nameTok)
        {
            Expect(TokenKind.LParen, "'('");
            var call = new CallExpression(nameTok.Text) { Span = SpanOf(nameTok) };

            if (!At(TokenKind.RParen))
            {
                do
                {
                    // 이름 인자: Name = expr
                    if (At(TokenKind.Identifier) && PeekToken().Kind == TokenKind.Assign)
                    {
                        var name = AdvanceToken();
                        AdvanceToken();
                        var value = ParseExpression();
                        call.NamedArguments.Add(new KeyValuePair<string, Expression>(name.Text, value));
                    }
                    else
                    {
                        if (call.NamedArguments.Count > 0)
                            throw Error(Cur, "Positional argument cannot follow a named argument");
                        call.Arguments.Add(ParseExpression());
                    }
                } while (Match(TokenKind.Comma));
            }

            Expect(TokenKind.RParen, "')'");
            return call;
        }
    }

    /// <summary>파서 내부용 구문 오류.</summary>
    public sealed class ParseException : Exception
    {
        public int Line { get; }
        public int Column { get; }
        public int Length { get; }

        public ParseException(int line, int column, int length, string message)
            : base(message)
        {
            Line = line;
            Column = column;
            Length = length;
        }
    }
}
