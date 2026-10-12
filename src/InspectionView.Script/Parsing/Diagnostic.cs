namespace InspectionView.Script.Parsing
{
    public enum DiagnosticSeverity
    {
        Error,
        Warning,
    }

    /// <summary>구문 오류/경고. 에디터의 오류 표시와 오류 목록에 사용한다.</summary>
    public sealed class Diagnostic
    {
        public DiagnosticSeverity Severity { get; }
        public int Line { get; }
        public int Column { get; }
        public int Length { get; }
        public string Message { get; }

        public Diagnostic(DiagnosticSeverity severity, int line, int column, int length, string message)
        {
            Severity = severity;
            Line = line;
            Column = column;
            Length = length;
            Message = message;
        }

        public override string ToString() => $"{Severity} ({Line}:{Column}): {Message}";
    }
}
