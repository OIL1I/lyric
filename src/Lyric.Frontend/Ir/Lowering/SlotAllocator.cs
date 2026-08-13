using Lyric.Resolver;

namespace Lyric.Ir.Lowering;

/// <summary>
/// Assigns the local and temp slots of a function and holds the mapping from sema symbol to
/// <see cref="LocalId"/>.
///
/// <para>The density of the tables (<c>Locals[i].Id.Value == i</c>) is STRUCTURALLY guaranteed here
/// rather than checked: the id is formed from <c>Count</c> and the entry appended immediately after.
/// The id is the slot index in the later bytecode, and a gap would be a wrong-slot read in the
/// VM.</para>
///
/// <para>Temps are NEVER reused; every expression gets a fresh one. That is the single-definition
/// promise of the IR, on which the verifier's dominance argument rests. The table therefore grows
/// linearly with the number of expressions; slot recycling is the business of a later register
/// allocator, not of the lowering.</para>
/// </summary>
internal sealed class SlotAllocator
{
    private readonly List<IrLocal> _locals = new();
    private readonly List<IrTemp> _temps = new();
    private readonly Dictionary<Symbol, LocalId> _bySymbol = new(ReferenceEqualityComparer.Instance);
    private int _syntheticCount;

    public List<IrLocal> Locals => _locals;
    public List<IrTemp> Temps => _temps;

    /// <summary>Creates a slot for a sema symbol, a parameter or a local binding. The order counts: the
    /// first <c>ParamCount</c> calls have to be the parameters.</summary>
    public LocalId DeclareFor(Symbol symbol, IrType type)
    {
        var id = new LocalId(_locals.Count);
        _locals.Add(new IrLocal(id, symbol.Name, type));
        _bySymbol[symbol] = id;
        return id;
    }

    /// <summary>A named slot without a sema symbol, for the receiver of a method: <c>this</c> is a
    /// keyword expression rather than a declared parameter and therefore has no symbol to look up, so
    /// the lowerer holds its slot directly.</summary>
    public LocalId Declare(string name, IrType type)
    {
        var id = new LocalId(_locals.Count);
        _locals.Add(new IrLocal(id, name, type));
        return id;
    }

    /// <summary>A slot without a source symbol, carrying values that flow across block boundaries: an
    /// if expression, <c>&amp;&amp;</c>, <c>||</c>. The counter is global across all kinds, so the names
    /// are deterministic: golden snapshots compare them.</summary>
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
