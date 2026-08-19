using System.Text;

namespace Lyric.Core;

public sealed class DiagnosticEngine(SourceManager sourceManager)
{
    private readonly SourceManager _sourceManager = sourceManager ?? throw new ArgumentNullException(nameof(sourceManager));
    
    private readonly List<Diagnostic> _diagnostics = new();
    
    public int Count => Diagnostics.Count;
    public int ErrorCount => Diagnostics.Count(d => d.Severity == Severity.Error);
    public int WarningCount => Diagnostics.Count(d => d.Severity == Severity.Warning);
    public bool HasErrors => ErrorCount > 0;
    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    public void Report(string code, Severity severity, Span span, string message)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(message);
        Report(new Diagnostic(code, severity, span, message));
    }

    /// <summary>Reports with secondary remarks, in telling order.</summary>
    public void Report(string code, Severity severity, Span span, string message,
        params DiagnosticNote[] notes)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(message);
        Report(new Diagnostic(code, severity, span, message, notes));
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
                output.Write("\n");
                RenderNotes(output, diagnostic);
                output.Write("\n");
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
            output.Write("\n");
            RenderNotes(output, diagnostic);
            output.Write("\n");
        }
    }

    /// <summary>
    /// The notes, indented under their diagnostic. Indented lines deliberately do not match the
    /// <c>path:line:col:</c> head format, so an editor's problem matcher sees one problem, not
    /// one per note.
    /// </summary>
    private void RenderNotes(TextWriter output, Diagnostic diagnostic)
    {
        foreach (var note in diagnostic.Notes ?? [])
        {
            if (note.Location.File.IsValid)
            {
                var position = _sourceManager.LocateStart(note.Location);
                output.WriteLine($"  note: {note.Message} — " +
                    $"{_sourceManager.GetPath(note.Location.File)}:{position.Line}:{position.Column}");
            }
            else
            {
                output.WriteLine($"  note: {note.Message}");
            }
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

            // A diagnostic without notes serializes exactly as it always did; the key appears only
            // when there is something to put under it.
            string optionalNotesPart = "";
            if (diagnostic.Notes is { Count: > 0 } notes)
            {
                var parts = notes.Select(note =>
                    $"{{{JsonPositionPart(note.Location)}\"message\":\"{JsonEscapedStringHelper(note.Message)}\"}}");
                optionalNotesPart = $"\"notes\":[{string.Join(",", parts)}],";
            }

            output.Write
            (
                $"{{\"code\":\"{JsonEscapedStringHelper(diagnostic.Code)}\",\"severity\":\"{diagnostic.Severity.ToDisplayString()}\","
                + JsonPositionPart(diagnostic.Span)
                + optionalNotesPart
                + $"\"message\":\"{JsonEscapedStringHelper(diagnostic.Message)}\"}}"
            );
        }
        output.Write("]}");
    }

    /// <summary>The <c>file</c>/<c>start</c>/<c>end</c> keys of a location, trailing comma
    /// included — empty for a span that has no file.</summary>
    private string JsonPositionPart(Span span)
    {
        if (!span.File.IsValid) return "";

        var startPos = _sourceManager.LocateStart(span);
        var endPos = _sourceManager.LocateEnd(span);
        return $"\"file\":\"{JsonEscapedStringHelper(_sourceManager.GetPath(span.File))}\","
               + $"\"start\":{{\"line\":{startPos.Line},\"column\":{startPos.Column},\"offset\":{span.Start}}},"
               + $"\"end\":{{\"line\":{endPos.Line},\"column\":{endPos.Column},\"offset\":{span.End}}},";
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