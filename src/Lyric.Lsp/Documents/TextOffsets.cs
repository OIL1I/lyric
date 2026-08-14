using Lyric.Lsp.Protocol;

namespace Lyric.Lsp.Documents;

/// <summary>
/// Between a protocol position and an offset into the text.
///
/// <para>The protocol speaks in 0-based lines and UTF-16 characters, the compiler in offsets;
/// <see cref="Core.SourceManager"/> converts one way only, because rendering a diagnostic never
/// needs the other. A request carries a position, so the server needs the inverse.</para>
///
/// <para>Computed against the text the ANSWER was built from rather than the current buffer. The
/// two differ while the user types, and taking the offset from the newer text would address the
/// older tree at a place that has moved.</para>
/// </summary>
public static class TextOffsets
{
    /// <summary>
    /// The offset a position names, clamped into the text.
    ///
    /// <para>Clamped rather than rejected: a position past the end is what arrives when the answer
    /// is one edit behind, and the end of the text is the closest true statement about it.</para>
    /// </summary>
    public static int ToOffset(string text, Position position)
    {
        if (position.Line < 0) return 0;

        var line = 0;
        var index = 0;

        while (line < position.Line && index < text.Length)
        {
            var newline = text.IndexOf('\n', index);
            if (newline < 0) return text.Length;
            index = newline + 1;
            line++;
        }

        if (line < position.Line) return text.Length;

        // The character offset counts UTF-16 code units, which is what a .NET string is indexed in,
        // so there is nothing to convert — only a bound to respect. A character count past the end
        // of the line must not spill into the next one.
        var end = text.IndexOf('\n', index);
        if (end < 0) end = text.Length;
        else if (end > index && text[end - 1] == '\r') end--;

        return Math.Min(index + Math.Max(position.Character, 0), end);
    }
}
