namespace InspectionView.Script.Lexing
{
    public enum TokenKind
    {
        EndOfFile,
        Newline,
        Error,

        Number,
        String,
        Identifier,

        // 키워드
        If, ElseIf, Else, EndIf,
        For, To, By, EndFor,
        While, EndWhile,
        Break, Continue,
        And, Or, Not,
        True, False,

        // 연산자 / 구분자
        Assign,         // =  또는 :=
        Plus, Minus, Star, Slash, Percent,
        Equal, NotEqual, Less, LessEqual, Greater, GreaterEqual,
        LParen, RParen, LBracket, RBracket,
        Comma, Dot,
    }

    public readonly struct Token
    {
        public TokenKind Kind { get; }
        public string Text { get; }
        public int Line { get; }
        public int Column { get; }

        /// <summary>Number: long 또는 double, String: 이스케이프 처리된 문자열, Error: 오류 메시지.</summary>
        public object? Value { get; }

        public Token(TokenKind kind, string text, int line, int column, object? value = null)
        {
            Kind = kind;
            Text = text;
            Line = line;
            Column = column;
            Value = value;
        }

        public int Length => Text.Length;

        public override string ToString() => $"{Kind} '{Text}' @{Line}:{Column}";
    }
}
