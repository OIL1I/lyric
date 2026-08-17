using Lyric.Core;
using Lyric.Lsp.Protocol;
using Range = Lyric.Lsp.Protocol.Range;

namespace Lyric.Lsp.Analysis;

/// <summary>
/// A compiler span as a protocol range.
///
/// <para>The one conversion that matters is the ORIGIN. <see cref="SourceManager.Locate"/> counts
/// lines and columns from one, because that is what a message on a terminal says; the protocol
/// counts both from zero. The subtraction happens here and nowhere else, so there is one place to
/// be wrong in and one place to test.</para>
///
/// <para>No conversion of the character offset itself is needed: <see cref="Span"/> counts UTF-16
/// code units and the server announces <c>utf-16</c> as its position encoding, so the two agree by
/// construction rather than by luck.</para>
///
/// <para>Two forms, because the consumers want different things from the same span — see each
/// method. A single one with a flag would put the choice at the call site as a boolean, where the
/// reason for it is invisible.</para>
/// </summary>
public static class SpanMapper
{
    /// <summary>The span as it stands.</summary>
    public static Range ToRange(SourceManager sources, Span span)
    {
        var (start, end) = Clamp(sources, span);
        return Between(sources, span.File, start, end);
    }

    /// <summary>
    /// The span, widened to one character when it is empty.
    ///
    /// <para>For diagnostics. The compiler reports at a point where a token is MISSING rather than
    /// wrong, and an editor draws a zero-width range as nothing at all — the diagnostic would be in
    /// the list and invisible in the text.</para>
    /// </summary>
    public static Range ToVisibleRange(SourceManager sources, Span span)
    {
        var (start, end) = Clamp(sources, span);
        if (start == end && end < sources.GetText(span.File).Length) end++;
        return Between(sources, span.File, start, end);
    }

    /// <summary>
    /// Clamped rather than trusted. <see cref="SourceManager.Locate"/> throws past the end of the
    /// file, and a span one past the last character is an ordinary way to say 'unexpected end of
    /// input'.
    /// </summary>
    private static (int Start, int End) Clamp(SourceManager sources, Span span)
    {
        var length = sources.GetText(span.File).Length;
        var start = Math.Clamp(span.Start, 0, length);
        var end = Math.Clamp(Math.Max(span.End, span.Start), 0, length);
        return (start, end);
    }

    private static Range Between(SourceManager sources, FileId file, int start, int end) =>
        new()
        {
            Start = ToPosition(sources, file, start),
            End = ToPosition(sources, file, end),
        };

    private static Position ToPosition(SourceManager sources, FileId file, int offset)
    {
        var located = sources.Locate(file, offset);
        return new Position { Line = located.Line - 1, Character = located.Column - 1 };
    }
}
