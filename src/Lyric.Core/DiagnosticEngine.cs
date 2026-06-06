using System.Text;

namespace Lyric.Core;

public sealed class DiagnosticEngine(SourceManager sourceManager)
{
    private readonly SourceManager _sourceManager = sourceManager ?? throw new ArgumentNullException(nameof(sourceManager));
    
    private readonly List<Diagnostic> _diagnostics = new();
    
    public int Count => Diagnostics.Count;
    public int ErrorCount => Diagnostics.Count(d => d.Severity == Severity.Error);
    public bool HasErrors => ErrorCount > 0;
    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    public void Report(string code, Severity severity, Span span, string message)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(message);
        Report(new Diagnostic(code, severity, span, message));
    }

    public void Report(Diagnostic diagnostic)
    {
        _diagnostics.Add(diagnostic);
    }

    public IReadOnlyList<Diagnostic> SortedSnapshot()
    {
        var copy = new List<Diagnostic>(Diagnostics);
        copy.Sort(new DiagnosticsComparer());
        return copy;
    }

    public void RenderText(TextWriter output)
    {
        if (output == null) throw new ArgumentNullException(nameof(output));
        
        foreach (var diagnostic in SortedSnapshot())
        {
            if (!diagnostic.Span.File.IsValid)
            {
                output.Write($"{diagnostic.Severity.ToDisplayString()}[{diagnostic.Code}]: {diagnostic.Message}");
                output.Write("\n\n");
                continue;
            }

            LinePosition diagPos = _sourceManager.LocateStart(diagnostic.Span);
            output.WriteLine
            (
                $"{_sourceManager.GetPath(diagnostic.Span.File)}:" +
                $"{diagPos.Line}:{diagPos.Column}: " +
                $"{diagnostic.Severity.ToDisplayString()}[{diagnostic.Code}]: {diagnostic.Message}"
            );
            output.WriteLine(_sourceManager.GetLineText(diagnostic.Span.File, diagPos.Line));
            for (int i = 0; i < diagPos.Column - 1; i++)
            {
                output.Write(" ");
            }

            var endPos = _sourceManager.LocateEnd(diagnostic.Span);
            for (int i = 0;
                 i < (diagPos.Line == endPos.Line ? Math.Max(diagnostic.Span.Length, 1) : 1); // Single-line with Length 0 gets one Caret
                 i++)
            {
                output.Write("^");
            }
            output.Write("\n\n");
        }
    }

    public void RenderJson(TextWriter output)
    {
        if (output == null) throw new ArgumentNullException(nameof(output));
        
        output.Write("{\"diagnostics\":[");
        bool first = true;
        foreach (var diagnostic in SortedSnapshot())
        {
            if (first) first = false;
            else output.Write(",");
            string optionalFilePart = "";
            if (diagnostic.Span.File.IsValid)
            {
                var startPos = _sourceManager.LocateStart(diagnostic.Span);
                var endPos = _sourceManager.LocateEnd(diagnostic.Span);
                optionalFilePart = $"\"file\":\"{JsonEscapedStringHelper(_sourceManager.GetPath(diagnostic.Span.File))}\","
                                   + $"\"start\":{{\"line\":{startPos.Line},\"column\":{startPos.Column},\"offset\":{diagnostic.Span.Start}}},"
                                   + $"\"end\":{{\"line\":{endPos.Line},\"column\":{endPos.Column},\"offset\":{diagnostic.Span.End}}},";
            }
            
            output.Write
            (
                $"{{\"code\":\"{JsonEscapedStringHelper(diagnostic.Code)}\",\"severity\":\"{diagnostic.Severity.ToDisplayString()}\","
                + optionalFilePart
                + $"\"message\":\"{JsonEscapedStringHelper(diagnostic.Message)}\"}}"
            );
        }
        output.Write("]}");
    }

    private string JsonEscapedStringHelper(string text)
    {
        StringBuilder sb = new();
        
        foreach (var c in text)
        {
            if (c == '"')
                sb.Append("\\\"");
            else if (c == '\\')
                sb.Append("\\\\");
            else if (c == '\n')
                sb.Append("\\n");
            else if (c == '\r')
                sb.Append("\\r");
            else if (c == '\t')
                sb.Append("\\t");
            else if (c == '\b')
                sb.Append("\\b");
            else if (c == '\f') 
                sb.Append("\\f");
            else if (c < 0x20)
                sb.Append($"\\u{(int)c:x4}");
            else sb.Append(c);
        }
        return sb.ToString();
    }
}