using Lyric.Core;
using Lyric.Parsing;

namespace Lyric.Formatting;

/// <summary>
/// The whole pipeline of formatting one file: parse, build the document, render.
///
/// <para>A file that does not parse is NOT formatted — the answer is <c>null</c> and the
/// diagnostics stand in the engine. A formatter that writes its guess over a file with a typo
/// in it destroys the one thing the user still had: their text.</para>
/// </summary>
public static class Formatter
{
    public static string? Format(SourceManager sources, FileId file, DiagnosticEngine diagnostics)
    {
        var module = new Parser(sources, file, diagnostics).ParseModule();
        if (diagnostics.HasErrors) return null;

        return DocRenderer.Render(AstFormatter.Build(module, sources.GetText(file)));
    }
}
