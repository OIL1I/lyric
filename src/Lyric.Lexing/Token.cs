using Lyric.Core;

namespace Lyric.Lexing;

public readonly record struct Token(
    TokenKind TokenKind,
    Span Span
);