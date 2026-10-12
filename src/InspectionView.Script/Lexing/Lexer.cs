using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace InspectionView.Script.Lexing
{
    /// <summary>
    /// 소스 텍스트를 토큰으로 나눈다. 오류는 예외 대신 <see cref="TokenKind.Error"/> 토큰으로 돌려주어
    /// 에디터가 실시간 파싱 중에도 나머지 코드를 계속 분석할 수 있게 한다.
    /// </summary>
    public sealed class Lexer
    {
        private static readonly Dictionary<string, TokenKind> Keywords = new Dictionary<string, TokenKind>(StringComparer.OrdinalIgnoreCase)
        {
            { "if", TokenKind.If }, { "elseif", TokenKind.ElseIf }, { "else", TokenKind.Else }, { "endif", TokenKind.EndIf },
            { "for", TokenKind.For }, { "to", TokenKind.To }, { "by", TokenKind.By }, { "endfor", TokenKind.EndFor },
            { "while", TokenKind.While }, { "endwhile", TokenKind.EndWhile },
            { "break", TokenKind.Break }, { "continue", TokenKind.Continue },
            { "and", TokenKind.And }, { "or", TokenKind.Or }, { "not", TokenKind.Not },
            { "true", TokenKind.True }, { "false", TokenKind.False },
        };

        private readonly string _src;
        private int _pos;
        private int _line = 1;
        private int _col = 1;

        public Lexer(string source)
        {
            _src = source ?? "";
        }

        public static List<Token> Tokenize(string source) => new Lexer(source).ReadAll();

        public List<Token> ReadAll()
        {
            var tokens = new List<Token>();
            while (true)
            {
                var t = Next();
                // 연속된 줄바꿈은 하나로
                if (t.Kind == TokenKind.Newline && tokens.Count > 0 && tokens[tokens.Count - 1].Kind == TokenKind.Newline)
                    continue;
                tokens.Add(t);
                if (t.Kind == TokenKind.EndOfFile)
                    break;
            }
            return tokens;
        }

        private char Cur => _pos < _src.Length ? _src[_pos] : '\0';
        private char Peek(int n = 1) => _pos + n < _src.Length ? _src[_pos + n] : '\0';

        private void Advance(int n = 1)
        {
            for (int i = 0; i < n && _pos < _src.Length; i++)
            {
                if (_src[_pos] == '\n')
                {
                    _line++;
                    _col = 1;
                }
                else
                {
                    _col++;
                }
                _pos++;
            }
        }

        private Token Next()
        {
            // 공백 / 주석 건너뛰기
            while (true)
            {
                char c = Cur;
                if (c == ' ' || c == '\t' || c == '\r')
                {
                    Advance();
                }
                else if (c == '/' && Peek() == '/')
                {
                    while (Cur != '\n' && Cur != '\0') Advance();
                }
                else
                {
                    break;
                }
            }

            int line = _line, col = _col, start = _pos;
            char ch = Cur;

            if (ch == '\0')
                return new Token(TokenKind.EndOfFile, "", line, col);

            if (ch == '\n')
            {
                Advance();
                return new Token(TokenKind.Newline, "\n", line, col);
            }

            if (char.IsDigit(ch) || (ch == '.' && char.IsDigit(Peek())))
                return ReadNumber(line, col);

            if (ch == '\'' || ch == '"')
                return ReadString(line, col);

            if (char.IsLetter(ch) || ch == '_')
            {
                while (char.IsLetterOrDigit(Cur) || Cur == '_') Advance();
                var text = _src.Substring(start, _pos - start);
                return Keywords.TryGetValue(text, out var kw)
                    ? new Token(kw, text, line, col)
                    : new Token(TokenKind.Identifier, text, line, col);
            }

            // 2문자 연산자
            string two = _pos + 1 < _src.Length ? _src.Substring(_pos, 2) : "";
            switch (two)
            {
                case ":=": Advance(2); return new Token(TokenKind.Assign, two, line, col);
                case "==": Advance(2); return new Token(TokenKind.Equal, two, line, col);
                case "!=": Advance(2); return new Token(TokenKind.NotEqual, two, line, col);
                case "<>": Advance(2); return new Token(TokenKind.NotEqual, two, line, col);
                case "<=": Advance(2); return new Token(TokenKind.LessEqual, two, line, col);
                case ">=": Advance(2); return new Token(TokenKind.GreaterEqual, two, line, col);
                case "&&": Advance(2); return new Token(TokenKind.And, two, line, col);
                case "||": Advance(2); return new Token(TokenKind.Or, two, line, col);
            }

            Advance();
            string one = ch.ToString();
            switch (ch)
            {
                case '=': return new Token(TokenKind.Assign, one, line, col);
                case '+': return new Token(TokenKind.Plus, one, line, col);
                case '-': return new Token(TokenKind.Minus, one, line, col);
                case '*': return new Token(TokenKind.Star, one, line, col);
                case '/': return new Token(TokenKind.Slash, one, line, col);
                case '%': return new Token(TokenKind.Percent, one, line, col);
                case '<': return new Token(TokenKind.Less, one, line, col);
                case '>': return new Token(TokenKind.Greater, one, line, col);
                case '!': return new Token(TokenKind.Not, one, line, col);
                case '(': return new Token(TokenKind.LParen, one, line, col);
                case ')': return new Token(TokenKind.RParen, one, line, col);
                case '[': return new Token(TokenKind.LBracket, one, line, col);
                case ']': return new Token(TokenKind.RBracket, one, line, col);
                case ',': return new Token(TokenKind.Comma, one, line, col);
                case '.': return new Token(TokenKind.Dot, one, line, col);
                default:
                    return new Token(TokenKind.Error, one, line, col, $"Unexpected character '{ch}'");
            }
        }

        private Token ReadNumber(int line, int col)
        {
            int start = _pos;
            bool isReal = false;

            // 16진수
            if (Cur == '0' && (Peek() == 'x' || Peek() == 'X'))
            {
                Advance(2);
                int hs = _pos;
                while (Uri.IsHexDigit(Cur)) Advance();
                var hex = _src.Substring(hs, _pos - hs);
                if (hex.Length == 0 || !long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hv))
                    return new Token(TokenKind.Error, _src.Substring(start, _pos - start), line, col, "Invalid hexadecimal number");
                return new Token(TokenKind.Number, _src.Substring(start, _pos - start), line, col, hv);
            }

            while (char.IsDigit(Cur)) Advance();
            if (Cur == '.' && char.IsDigit(Peek()))
            {
                isReal = true;
                Advance();
                while (char.IsDigit(Cur)) Advance();
            }
            if (Cur == 'e' || Cur == 'E')
            {
                int save = _pos;
                Advance();
                if (Cur == '+' || Cur == '-') Advance();
                if (char.IsDigit(Cur))
                {
                    isReal = true;
                    while (char.IsDigit(Cur)) Advance();
                }
                else
                {
                    // 'e' 는 숫자의 일부가 아님 → 되돌림
                    _col -= _pos - save;
                    _pos = save;
                }
            }

            var text = _src.Substring(start, _pos - start);
            if (isReal)
            {
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    ? new Token(TokenKind.Number, text, line, col, d)
                    : new Token(TokenKind.Error, text, line, col, "Invalid number");
            }

            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                return new Token(TokenKind.Number, text, line, col, l);
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var big))
                return new Token(TokenKind.Number, text, line, col, big);
            return new Token(TokenKind.Error, text, line, col, "Invalid number");
        }

        private Token ReadString(int line, int col)
        {
            int start = _pos;
            char quote = Cur;
            Advance();
            var sb = new StringBuilder();
            while (true)
            {
                char c = Cur;
                if (c == '\0' || c == '\n')
                    return new Token(TokenKind.Error, _src.Substring(start, _pos - start), line, col, "Unterminated string");

                if (c == quote)
                {
                    Advance();
                    break;
                }

                if (c == '\\')
                {
                    char e = Peek();
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case '\\': sb.Append('\\'); break;
                        case '\'': sb.Append('\''); break;
                        case '"': sb.Append('"'); break;
                        default:
                            // 알 수 없는 이스케이프는 그대로 (경로 'C:\img' 를 허용)
                            sb.Append('\\').Append(e);
                            break;
                    }
                    Advance(2);
                    continue;
                }

                sb.Append(c);
                Advance();
            }
            return new Token(TokenKind.String, _src.Substring(start, _pos - start), line, col, sb.ToString());
        }
    }
}
