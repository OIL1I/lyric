using Lyric.Resolver;

namespace Lyric.Ir.Lowering;

/// <summary>
/// Vergibt die Local- und Temp-Slots einer Funktion und hält die Zuordnung
/// Sema-Symbol → <see cref="LocalId"/>.
///
/// <para>Die Dichtheit der Tabellen (<c>Locals[i].Id.Value == i</c>) ist hier <b>strukturell</b>
/// garantiert, nicht geprüft: die Id wird aus <c>Count</c> gebildet und der Eintrag unmittelbar
/// danach angehängt. Die Id ist der Slot-Index im späteren Bytecode, eine Lücke wäre ein
/// Falsch-Slot-Read in der VM.</para>
///
/// <para>Temps werden <b>nie</b> wiederverwendet — jeder Ausdruck bekommt einen frischen. Das ist
/// die Single-Definition-Zusage der IR, auf der die Dominanz-Argumentation des Verifiers beruht.
/// Die Tabelle wächst dadurch linear mit der Ausdrucks-Anzahl; Slot-Recycling ist Sache eines
/// späteren Registerallokators, nicht des Lowerings.</para>
/// </summary>
internal sealed class SlotAllocator
{
    private readonly List<IrLocal> _locals = new();
    private readonly List<IrTemp> _temps = new();
    private readonly Dictionary<Symbol, LocalId> _bySymbol = new(ReferenceEqualityComparer.Instance);
    private int _syntheticCount;

    public List<IrLocal> Locals => _locals;
    public List<IrTemp> Temps => _temps;

    /// <summary>Legt einen Slot für ein Sema-Symbol an (Parameter oder lokale Bindung).
    /// Reihenfolge zählt: die ersten <c>ParamCount</c> Aufrufe müssen die Parameter sein.</summary>
    public LocalId DeclareFor(Symbol symbol, IrType type)
    {
        var id = new LocalId(_locals.Count);
        _locals.Add(new IrLocal(id, symbol.Name, type));
        _bySymbol[symbol] = id;
        return id;
    }

    /// <summary>Slot ohne Quell-Symbol — Träger für Werte, die über Blockgrenzen fließen
    /// (if-Ausdruck, <c>&amp;&amp;</c>, <c>||</c>). Der Zähler ist global über alle Arten, damit die
    /// Namen deterministisch sind: Golden-Snapshots vergleichen sie.</summary>
    public LocalId DeclareSynthetic(string hint, IrType type)
    {
        var id = new LocalId(_locals.Count);
        _locals.Add(new IrLocal(id, $"${hint}{_syntheticCount++}", type));
        return id;
    }

    public bool TryLookup(Symbol symbol, out LocalId id) => _bySymbol.TryGetValue(symbol, out id);

    public IrType TypeOfLocal(LocalId id) => _locals[id.Value].Type;

    public TempId NewTemp(IrType type)
    {
        var id = new TempId(_temps.Count);
        _temps.Add(new IrTemp(id, type));
        return id;
    }
}
