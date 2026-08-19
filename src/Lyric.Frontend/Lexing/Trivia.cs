using Lyric.Core;

namespace Lyric.Lexing;

public enum TriviaKind
{
    LineComment,
    BlockComment,

    /// <summary>Never produced by the lexer — a doc comment is a token. The formatter merges
    /// doc comments into its comment stream under this kind, so one positional mechanism
    /// carries all three comment forms.</summary>
    DocComment,
}

/// <summary>
/// A comment the lexer normally discards, kept with its exact span.
///
/// <para>Only the two plain comment forms appear here. A doc comment is a TOKEN
/// (<see cref="TokenKind.DocComment"/>) because the parser attaches it to the declaration it
/// documents; recording it twice would give a consumer two sources for one piece of text.
/// Whitespace is not recorded at all — a consumer that wants blank lines derives them from the
/// gap between neighbouring spans, which cannot disagree with the source.</para>
/// </summary>
public readonly record struct Trivia(TriviaKind Kind, Span Span);
