using Lyric.Core;

namespace Lyric.Ir.Lowering;

/// <summary>
/// Emit-Cursor über die Basic-Blocks einer Funktion: legt Blöcke an, schreibt Instruktionen in den
/// aktuellen Block und versiegelt Blöcke mit ihrem Terminator.
///
/// <para><b>Dichte Block-Ids sind strukturell garantiert</b>: <see cref="Append"/> bildet die Id aus
/// <c>Count</c> und hängt den Block unmittelbar danach an, also gilt immer
/// <c>Blocks[i].Id.Value == i</c>. Der erste angelegte Block ist <c>bb0</c> und damit der Entry —
/// die zweite Verifier-Invariante fällt genauso von selbst ab.</para>
///
/// <para><b>Warum <see cref="SealBlock"/> einen fremden Block versiegeln kann</b>: ein
/// <c>if</c>-Zweig darf erst dann nach vorn springen, wenn feststeht, dass es überhaupt einen
/// Merge-Block gibt — und das weiß man erst, nachdem beide Zweige gelowert sind. Der Cursor ist
/// dann längst weitergezogen. Die Alternative wäre, den Merge-Block vorsorglich anzulegen und bei
/// Nichtgebrauch wieder zu entfernen; das bricht die Dichtheit, sobald der Zweig selbst Blöcke
/// angelegt hat. Nachträgliches Versiegeln ist der billigere Weg.</para>
///
/// <para>Die Reihenfolge der Blöcke in der Liste ist Anlage-Reihenfolge, nicht Kontrollfluss-
/// Reihenfolge: bei Schleifen muss der Exit-Block existieren, bevor der Body gelowert wird (ein
/// <c>break</c> im Body braucht sein Sprungziel), also steht er vor den Body-Blöcken. Das ist
/// korrekt und dicht, liest sich im Dump aber nicht linear.</para>
/// </summary>
internal sealed class BlockBuilder
{
    private readonly List<IrBlock> _blocks;
    private IrBlock _current;

    public BlockBuilder(List<IrBlock> blocks)
    {
        _blocks = blocks;
        _current = Append(); // bb0 = Entry
    }

    public BlockId CurrentId => _current.Id;

    /// <summary>Ist der aktuelle Block abgeschlossen? Wenn ja, ist der Kontrollfluss an dieser
    /// Stelle beendet und alles Folgende unerreichbar.</summary>
    public bool IsSealed => _current.Terminator is not null;

    /// <summary>Legt einen Block an, <b>ohne</b> den Cursor umzusetzen.</summary>
    public BlockId NewBlock() => Append().Id;

    public void SwitchTo(BlockId id) => _current = _blocks[id.Value];

    public void Emit(IrOp op)
    {
        if (IsSealed)
            throw new InternalCompilationException(
                $"lowering: cannot emit into sealed block {_current.Id}");
        _current.Insts.Add(op);
    }

    public void Seal(IrTerminator terminator) => SealBlock(_current.Id, terminator);

    public void SealBlock(BlockId id, IrTerminator terminator)
    {
        var block = _blocks[id.Value];
        if (block.Terminator is not null)
            throw new InternalCompilationException($"lowering: block {id} is already sealed");
        block.Terminator = terminator;
    }

    private IrBlock Append()
    {
        var block = new IrBlock(new BlockId(_blocks.Count), new List<IrOp>());
        _blocks.Add(block);
        return block;
    }
}
