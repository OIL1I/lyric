using Lyric.Core;
using Lyric.Lsp.Protocol;

namespace Lyric.Lsp.Analysis;

/// <summary>
/// A compiler diagnostic as the editor wants it.
///
/// <para>The span conversion lives in <see cref="SpanMapper"/>, which three features now share.
/// Diagnostics take the WIDENING form: a zero-width squiggle is invisible.</para>
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
            if (Documents.DocumentUri.PathComparer.Equals(sources.GetPath(id), path)) return id;
        }
        return FileId.None;
    }

    private static LspDiagnostic Map(SourceManager sources, Diagnostic diagnostic) =>
        new()
        {
            Range = SpanMapper.ToVisibleRange(sources, diagnostic.Span),
            Severity = ToSeverity(diagnostic.Severity),
            Code = diagnostic.Code,
            Message = diagnostic.Message,
        };

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
