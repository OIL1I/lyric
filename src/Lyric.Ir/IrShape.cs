using Lyric.Core;

namespace Lyric.Ir;

/// <summary>
/// Struktur-Zugriff auf Instruktionen: welche Temps liest sie, welches schreibt sie, wohin
/// verzweigt sie.
///
/// <para>Eigene Klasse, weil inzwischen mehrere Stufen dieselbe Frage stellen — der Verifier für
/// Def/Use und Reachability, der Bytecode-Emitter fürs Stack-Scheduling. Zwei Kopien dieser
/// <c>switch</c>-Blöcke wären ein Drift-Risiko der übelsten Sorte: eine neue Instruktion, die in
/// einer Kopie fehlt, führt zu still falschem Code statt zu einem Fehler.</para>
///
/// <para>Der <c>default</c>-Wurf ist auch hier die Vollständigkeits-Garantie: eine neue Instruktion
/// bricht sofort und sichtbar, statt stillschweigend als operandenlos durchzugehen.</para>
/// </summary>
public static class IrShape
{
    public static IReadOnlyList<TempId> OperandsOf(IrOp op) => op switch
    {
        Const => Array.Empty<TempId>(),
        BinOp b => new[] { b.Lhs, b.Rhs },
        UnOp u => new[] { u.Operand },
        Convert cv => new[] { cv.Operand },
        LoadLocal => Array.Empty<TempId>(),
        StoreLocal s => new[] { s.Value },
        Call k => k.Args,
        CallImport k => k.Args,
        _ => throw new InternalCompilationException($"ir: unhandled op {op.GetType().Name}")
    };

    public static IReadOnlyList<TempId> OperandsOf(IrTerminator terminator) => terminator switch
    {
        Return r => r.Value is { } value ? new[] { value } : Array.Empty<TempId>(),
        Branch => Array.Empty<TempId>(),
        CondBranch c => new[] { c.Cond },
        Unreachable => Array.Empty<TempId>(),
        _ => throw new InternalCompilationException(
            $"ir: unhandled terminator {terminator.GetType().Name}")
    };

    /// <summary>Das Temp, das die Instruktion definiert — <c>null</c>, wenn sie keins schreibt
    /// (<c>store</c>, void-<c>call</c>).</summary>
    public static TempId? DestOf(IrOp op) => op switch
    {
        Const c => c.Dest,
        BinOp b => b.Dest,
        UnOp u => u.Dest,
        Convert cv => cv.Dest,
        LoadLocal l => l.Dest,
        StoreLocal => null,
        Call k => k.Dest,
        CallImport k => k.Dest,
        _ => throw new InternalCompilationException($"ir: unhandled op {op.GetType().Name}")
    };

    public static IReadOnlyList<BlockId> SuccessorsOf(IrTerminator terminator) => terminator switch
    {
        Return => Array.Empty<BlockId>(),
        Branch b => new[] { b.Target },
        CondBranch c => new[] { c.IfTrue, c.IfFalse },
        Unreachable => Array.Empty<BlockId>(),
        _ => throw new InternalCompilationException(
            $"ir: unhandled terminator {terminator.GetType().Name}")
    };
}
