using Lyric.AST;
using Lyric.Resolver;

namespace Lyric.Sema;

// Body-lokale Symbole. Leben in Lyric.Sema (nicht Lyric.Resolver), weil sie einen
// LyrType tragen — Resolver darf nicht auf Sema zeigen (Zyklus). Sie erben aber die
// Resolver-Symbol-Basis, passen also in eine SymbolTable.

public sealed class ParameterSymbol : Symbol
{
    public LyrType Type { get; }

    public ParameterSymbol(string name, LyrType type, Node? declaration) : base(name, declaration)
        => Type = type;
}

public sealed class LocalSymbol : Symbol
{
    public LyrType Type { get; }
    public bool IsMutable { get; } // var vs let (Mutabilitäts-Check ist Slice 3)

    public LocalSymbol(string name, LyrType type, bool isMutable, Node? declaration) : base(name, declaration)
    {
        Type = type;
        IsMutable = isMutable;
    }
}
