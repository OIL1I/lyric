using Lyric.Core;
using Lyric.Lsp.Protocol;
using Range = Lyric.Lsp.Protocol.Range;

namespace Lyric.Lsp.Analysis;

/// <summary>
/// A compiler diagnostic as the editor wants it.
///
/// <para>The one conversion that matters is the ORIGIN. <see cref="SourceManager.Locate"/> counts
/// lines and columns from one, because that is what a message on a terminal says; the protocol
/// counts both from zero. The subtraction happens here and nowhere else, so there is one place to
/// be wrong in and one place to test.</para>
///
/// <para>No conversion of the character offset itself is needed: <see cref="Span"/> counts UTF-16
/// code units and the server announces <c>utf-16</c> as its position encoding, so the two agree by
/// construction rather than by luck.</para>
/// </summary>
public static class DiagnosticMapper
{
    /// <summary>
    /// The diagnostics that belong to one file, in the order the compiler sorted them.
    ///
    /// <para>Everything reported against another file is dropped. A diagnostic inside the standard
    /// library is real, but it is not about the document the user has open, and an editor has no
    /// way to show it against a file it did not ask about.</para>
    /// </summary>
    public static List<LspDiagnostic> ForFile(
        SourceManager sources,
        IEnumerable<Diagnostic> diagnostics,
        FileId file)
    {
        var mapped = new List<LspDiagnostic>();
        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.Span.File != file) continue;
            mapped.Add(Map(sources, diagnostic));
        }
        return mapped;
    }

    /// <summary>
    /// The file a compile read a given path into, or <see cref="FileId.None"/> when it never got
    /// that far.
    ///
    /// <para>Asked by path rather than assumed to be the first file: that the entry source is
    /// registered first is true of today's pipeline and is not a promise it makes. A wrong answer
    /// here would not fail — it would attribute the standard library's diagnostics to the user's
    /// document.</para>
    /// </summary>
    public static FileId FindFile(SourceManager sources, string path)
    {
        for (var i = 1; i <= sources.FileCount; i++)
        {
            var id = new FileId(i);
            if (DocumentUriPathEquals(sources.GetPath(id), path)) return id;
        }
        return FileId.None;
    }

    private static bool DocumentUriPathEquals(string left, string right) =>
        Documents.DocumentUri.PathComparer.Equals(left, right);

    private static LspDiagnostic Map(SourceManager sources, Diagnostic diagnostic) =>
        new()
        {
            Range = ToRange(sources, diagnostic.Span),
            Severity = ToSeverity(diagnostic.Severity),
            Code = diagnostic.Code,
            Message = diagnostic.Message,
        };

    /// <summary>
    /// A span as a protocol range.
    ///
    /// <para>An EMPTY span is widened to one character. The compiler reports at a point where a
    /// token is missing rather than wrong, and an editor draws a zero-width range as nothing at
    /// all — the diagnostic would be in the list and invisible in the text.</para>
    /// </summary>
    public static Range ToRange(SourceManager sources, Span span)
    {
        var length = sources.GetText(span.File).Length;

        // Clamped rather than trusted. Locate throws past the end of the file, and a diagnostic
        // whose span is one past the last character is an ordinary way to say 'unexpected end of
        // input'.
        var start = Math.Clamp(span.Start, 0, length);
        var end = Math.Clamp(Math.Max(span.End, span.Start), 0, length);
        if (start == end && end < length) end++;

        return new Range
        {
            Start = ToPosition(sources, span.File, start),
            End = ToPosition(sources, span.File, end),
        };
    }

    private static Position ToPosition(SourceManager sources, FileId file, int offset)
    {
        var located = sources.Locate(file, offset);
        return new Position { Line = located.Line - 1, Character = located.Column - 1 };
    }

    private static LspSeverity ToSeverity(Severity severity) => severity switch
    {
        Severity.Error => LspSeverity.Error,
        Severity.Warning => LspSeverity.Warning,
        Severity.Hint => LspSeverity.Hint,

        // Total over today's severities. A new one is a decision about how an editor should draw
        // it, and a default that guessed 'Information' would make that decision silently.
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity,
            "no protocol severity for this compiler severity"),
    };
}
