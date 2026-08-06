using Lyric.Core;
using Lyric.Ir;

namespace Lyric.Bytecode;

/// <summary>Wo lebt der Wert eines Temps, wenn der Bytecode läuft?</summary>
internal enum Placement
{
    /// <summary>Er bleibt auf dem Operanden-Stack und wird direkt von der nächsten passenden
    /// Instruktion konsumiert. Kein <c>stloc</c>/<c>ldloc</c>.</summary>
    Stack,

    /// <summary>Er wandert in einen Local-Slot: <c>stloc</c> nach der Definition, <c>ldloc</c> an
    /// jeder Verwendung.</summary>
    Slot,

    /// <summary>Er wird nie gelesen — direkt nach der Definition <c>pop</c>. Praktisch nur bei
    /// einer verworfenen Call-Rückgabe (<c>foo();</c> bei <c>foo(): int</c>).</summary>
    Discard,
}

/// <summary>Ergebnis des Schedulings für eine Funktion.</summary>
internal sealed class FunctionLayout
{
    public required IReadOnlyDictionary<TempId, Placement> Placements { get; init; }
    /// <summary>Slot-Index je Temp mit <see cref="Placement.Slot"/>.</summary>
    public required IReadOnlyDictionary<TempId, int> TempSlots { get; init; }
    /// <summary>Typ jedes Slots: erst die IR-Locals (Parameter zuerst), dann die ausgelagerten
    /// Temps.</summary>
    public required IReadOnlyList<IrType> SlotTypes { get; init; }
    public required int MaxStack { get; init; }
}

/// <summary>
/// Übersetzt die temp-basierte IR in die Stack-Disziplin der VM: welche Werte bleiben auf dem
/// Operanden-Stack, welche brauchen einen Local-Slot?
///
/// <para><b>Die tragende Invariante: der Stack ist an jeder Blockgrenze leer.</b> Werte, die
/// Blöcke überqueren, laufen durch Locals — und genau das garantiert das Lowering aus P4 schon
/// strukturell (synthetische Locals für if-Ausdruck und <c>&amp;&amp;</c>/<c>||</c>, weil ein Temp nur
/// einmal definiert werden darf). Damit ist das Scheduling rein blocklokal, und die Stack-Tiefe
/// ist beim Laden statisch prüfbar — ADR-013s Validierung beim Load.</para>
///
/// <para><b>Warum überhaupt Scheduling?</b> Der naive Weg — jedes Temp bekommt einen Slot —
/// erzeugt für <c>t2 = add t0, t1</c> zehn statt vier Instruktionen. M5s Exit-Kriterium verlangt,
/// dass die Disassembly „sinnvolle Instruktionen" zeigt; eine Ausgabe voller redundanter
/// store/load-Paare erfüllt das nicht.</para>
///
/// <para><b>Korrektheit hängt nie an der Optimierung.</b> Der Slot-Weg ist immer verfügbar; das
/// Scheduling entscheidet nur, wo er entfallen darf. Es läuft optimistisch und stuft bei jeder
/// Kollision Temps auf Slots zurück — monoton, also terminierend.</para>
/// </summary>
internal static class StackScheduler
{
    public static FunctionLayout Schedule(IrFunction function)
    {
        var placements = InitialPlacements(function);

        // Optimistisch simulieren; jede Kollision stuft Temps auf Slot zurück. Da nur in eine
        // Richtung umgestuft wird (Stack -> Slot) und es endlich viele Temps gibt, terminiert das.
        int maxStack;
        while (!TrySimulate(function, placements, out maxStack)) { }

        var slotTypes = new List<IrType>(function.Locals.Select(l => l.Type));
        var tempSlots = new Dictionary<TempId, int>();
        foreach (var temp in function.Temps) // nach TempId, also deterministisch
        {
            if (placements[temp.Id] != Placement.Slot) continue;
            tempSlots[temp.Id] = slotTypes.Count;
            slotTypes.Add(temp.Type);
        }

        return new FunctionLayout
        {
            Placements = placements,
            TempSlots = tempSlots,
            SlotTypes = slotTypes,
            MaxStack = maxStack,
        };
    }

    /// <summary>
    /// Erste Näherung aus reiner Zählung: ein Temp darf nur dann auf dem Stack leben, wenn es
    /// <b>genau einmal</b> und <b>im selben Block</b> gelesen wird. Mehrfach-Verwendung ginge
    /// nicht (der Stack konsumiert), block-übergreifend auch nicht (der Stack ist an der Grenze
    /// leer). Kein Leser heißt <see cref="Placement.Discard"/>.
    /// </summary>
    private static Dictionary<TempId, Placement> InitialPlacements(IrFunction function)
    {
        var useCount = new Dictionary<TempId, int>();
        var useBlock = new Dictionary<TempId, BlockId>();
        var defBlock = new Dictionary<TempId, BlockId>();

        foreach (var block in function.Blocks)
        {
            foreach (var op in block.Insts)
            {
                foreach (var operand in IrShape.OperandsOf(op)) Record(operand, block.Id);
                if (IrShape.DestOf(op) is { } dest) defBlock[dest] = block.Id;
            }
            foreach (var operand in IrShape.OperandsOf(block.Terminator!)) Record(operand, block.Id);
        }

        var placements = new Dictionary<TempId, Placement>();
        foreach (var temp in function.Temps)
        {
            var count = useCount.GetValueOrDefault(temp.Id);
            placements[temp.Id] =
                count == 0 ? Placement.Discard
                : count == 1 && defBlock.TryGetValue(temp.Id, out var def)
                             && useBlock[temp.Id] == def ? Placement.Stack
                : Placement.Slot;
        }
        return placements;

        void Record(TempId temp, BlockId block)
        {
            useCount[temp] = useCount.GetValueOrDefault(temp) + 1;
            useBlock[temp] = block;
        }
    }

    /// <summary>
    /// Simuliert den Operanden-Stack blockweise. Liefert false, sobald Temps zurückgestuft wurden —
    /// dann ist ein weiterer Durchlauf nötig.
    /// </summary>
    private static bool TrySimulate(IrFunction function,
        Dictionary<TempId, Placement> placements, out int maxStack)
    {
        maxStack = 0;

        foreach (var block in function.Blocks)
        {
            var stack = new List<TempId>();

            foreach (var op in block.Insts)
            {
                if (!Consume(IrShape.OperandsOf(op), stack, placements, ref maxStack)) return false;

                if (IrShape.DestOf(op) is not { } dest) continue;
                switch (placements[dest])
                {
                    case Placement.Stack:
                        stack.Add(dest);
                        maxStack = Math.Max(maxStack, stack.Count);
                        break;
                    case Placement.Discard:
                        // liegt kurz oben und wird sofort ge-pop-t
                        maxStack = Math.Max(maxStack, stack.Count + 1);
                        break;
                    case Placement.Slot:
                        maxStack = Math.Max(maxStack, stack.Count + 1); // bis zum stloc
                        break;
                }
            }

            if (!Consume(IrShape.OperandsOf(block.Terminator!), stack, placements, ref maxStack))
                return false;

            // Nach dem Terminator muss der Stack leer sein. Ein Rest hieße, ein Temp läge auf dem
            // Stack ohne Leser — das schließt Placement.Discard aus, also wäre es ein Bug hier.
            if (stack.Count != 0)
                throw new InternalCompilationException(
                    $"stack-scheduler: {function.Name}: {stack.Count} value(s) left on the stack " +
                    $"at the end of {block.Id}");
        }

        return true;
    }

    /// <summary>
    /// Konsumiert die Operanden einer Instruktion. Drei Fälle, und nur drei:
    /// <list type="bullet">
    /// <item>alle Operanden liegen als <b>Suffix</b> des Stacks in genau dieser Reihenfolge → sie
    /// werden gepoppt, es entstehen keine Loads;</item>
    /// <item>kein Operand liegt auf dem Stack → alle kommen per <c>ldloc</c> obendrauf und werden
    /// sofort konsumiert; ein Rest darunter bleibt unberührt;</item>
    /// <item>alles andere (gemischt, oder auf dem Stack aber in falscher Position) → nicht
    /// emittierbar, weil ein <c>ldloc</c> über einem bereits liegenden Operanden die Reihenfolge
    /// zerstören würde. Die betroffenen Temps werden auf Slot zurückgestuft.</item>
    /// </list>
    /// </summary>
    private static bool Consume(IReadOnlyList<TempId> operands, List<TempId> stack,
        Dictionary<TempId, Placement> placements, ref int maxStack)
    {
        if (operands.Count == 0) return true;

        var onStack = operands.Count(o => placements[o] == Placement.Stack);

        if (onStack == 0)
        {
            maxStack = Math.Max(maxStack, stack.Count + operands.Count);
            return true;
        }

        if (onStack == operands.Count && EndsWith(stack, operands))
        {
            maxStack = Math.Max(maxStack, stack.Count);
            stack.RemoveRange(stack.Count - operands.Count, operands.Count);
            return true;
        }

        // Kollision: zurückstufen und neu simulieren. Die Operanden reichen — alles, was sonst noch
        // auf dem Stack liegt, ist per Konstruktion tiefer und bleibt gültig.
        foreach (var operand in operands)
            if (placements[operand] == Placement.Stack)
                placements[operand] = Placement.Slot;

        return false;
    }

    private static bool EndsWith(List<TempId> stack, IReadOnlyList<TempId> operands)
    {
        if (stack.Count < operands.Count) return false;
        var offset = stack.Count - operands.Count;
        for (var i = 0; i < operands.Count; i++)
            if (stack[offset + i] != operands[i]) return false;
        return true;
    }
}
