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
        NewObject => Array.Empty<TempId>(),
        LoadField f => new[] { f.Object },
        // Reihenfolge ist Vertrag, nicht Geschmack: der Stack-Scheduler legt die Operanden in
        // genau dieser Folge ab, und Bytecode.md §5 schreibt fest, dass bei stfld die Referenz
        // unter dem Wert liegt. Vertauschen hier heißt vertauschte Argumente in der VM.
        StoreField f => new[] { f.Object, f.Value },

        NewArray a => a.Elements,
        LoadElem e => new[] { e.Array, e.Index },
        // Reihenfolge ist Vertrag (Bytecode.md §5): Array, Index, Wert — von unten nach oben.
        StoreElem e => new[] { e.Array, e.Index, e.Value },
        ArrayLen a => new[] { a.Array },
        ArrayConcat c => new[] { c.Left, c.Right },
        ArrayRepeat r => new[] { r.Array, r.Count },

        OptNone => Array.Empty<TempId>(),
        OptSome s => new[] { s.Value },
        OptIsSome i => new[] { i.Option },
        OptGet g => new[] { g.Option },

        NewVariant v => v.Fields,
        EnumTag t => new[] { t.Value },
        EnumAs a => new[] { a.Value },

        MakeInterface m => new[] { m.Value },
        StructCopy c => new[] { c.Value },
        // Der Empfaenger ist Arg 0 und liegt damit zuunterst — dieselbe Konvention wie bei Call
        // (ADR-014). CallVirt braucht keine Sonderbehandlung.
        CallVirt c => c.Args,

        _ => throw new InternalCompilationException($"ir: unhandled op {op.GetType().Name}")
    };

    public static IReadOnlyList<TempId> OperandsOf(IrTerminator terminator) => terminator switch
    {
        Return r => r.Value is { } value ? new[] { value } : Array.Empty<TempId>(),
        Branch => Array.Empty<TempId>(),
        CondBranch c => new[] { c.Cond },
        Unreachable => Array.Empty<TempId>(),
        Throw t => new[] { t.Value },
        EndFinally => Array.Empty<TempId>(),
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
        NewObject n => n.Dest,
        LoadField f => f.Dest,
        StoreField => null,

        NewArray a => a.Dest,
        LoadElem e => e.Dest,
        StoreElem => null,
        ArrayLen a => a.Dest,
        ArrayConcat c => c.Dest,
        ArrayRepeat r => r.Dest,

        OptNone n => n.Dest,
        OptSome s => s.Dest,
        OptIsSome i => i.Dest,
        OptGet g => g.Dest,

        NewVariant v => v.Dest,
        EnumTag t => t.Dest,
        EnumAs a => a.Dest,

        MakeInterface m => m.Dest,
        StructCopy c => c.Dest,
        CallVirt c => c.Dest,

        _ => throw new InternalCompilationException($"ir: unhandled op {op.GetType().Name}")
    };

    public static IReadOnlyList<BlockId> SuccessorsOf(IrTerminator terminator) => terminator switch
    {
        Return => Array.Empty<BlockId>(),
        Branch b => new[] { b.Target },
        CondBranch c => new[] { c.IfTrue, c.IfFalse },
        // Throw und EndFinally haben keine Nachfolger IM CFG — wohin es weitergeht, entscheidet
        // die Handler-Tabelle, nicht der Kontrollfluss des Blocks. Der Verifier behandelt
        // Handler-Bloecke deshalb gesondert als erreichbar.
        Unreachable or Throw or EndFinally => Array.Empty<BlockId>(),
        _ => throw new InternalCompilationException(
            $"ir: unhandled terminator {terminator.GetType().Name}")
    };
}
