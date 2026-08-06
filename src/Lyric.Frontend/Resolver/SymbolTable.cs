namespace Lyric.Resolver;

/// <summary>
/// Ein lexikalischer Scope: Name → Symbol, plus optionaler Parent für die
/// Lookup-Kette (Builtin → Modul → Typ-Member → …). Hält eine Insertion-geordnete
/// Liste für deterministische Dumps; das Dictionary ist der O(1)-Lookup.
/// </summary>
public sealed class SymbolTable
{
    private readonly Dictionary<string, Symbol> _byName = new();
    private readonly List<Symbol> _ordered = new();

    public SymbolTable? Parent { get; }

    public SymbolTable(SymbolTable? parent = null) => Parent = parent;

    public IReadOnlyList<Symbol> Symbols => _ordered;

    /// <summary>Deklariert ein Symbol. Liefert false, wenn der Name in DIESEM Scope
    /// schon vergeben ist (Duplikat) — dann bleibt das erste Symbol stehen.</summary>
    public bool TryDeclare(Symbol symbol)
    {
        if (_byName.ContainsKey(symbol.Name)) return false;
        _byName[symbol.Name] = symbol;
        _ordered.Add(symbol);
        return true;
    }

    /// <summary>Nur dieser Scope, ohne Parent-Kette.</summary>
    public Symbol? LookupLocal(string name) =>
        _byName.TryGetValue(name, out var s) ? s : null;

    /// <summary>Dieser Scope und dann die Parent-Kette hoch.</summary>
    public Symbol? Lookup(string name)
    {
        for (var scope = this; scope is not null; scope = scope.Parent)
            if (scope._byName.TryGetValue(name, out var s))
                return s;
        return null;
    }
}
