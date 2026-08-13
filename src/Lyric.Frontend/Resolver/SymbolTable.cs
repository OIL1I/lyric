namespace Lyric.Resolver;

/// <summary>
/// A lexical scope: name to symbol, plus an optional parent for the lookup chain (built-in,
/// module, type member, …). It keeps an insertion-ordered list for deterministic dumps; the
/// dictionary is the O(1) lookup.
/// </summary>
public sealed class SymbolTable
{
    private readonly Dictionary<string, Symbol> _byName = new();
    private readonly List<Symbol> _ordered = new();

    public SymbolTable? Parent { get; }

    public SymbolTable(SymbolTable? parent = null) => Parent = parent;

    public IReadOnlyList<Symbol> Symbols => _ordered;

    /// <summary>Declares a symbol. Returns false when the name is already taken in THIS scope;
    /// the first symbol then stays.</summary>
    public bool TryDeclare(Symbol symbol)
    {
        if (_byName.ContainsKey(symbol.Name)) return false;
        _byName[symbol.Name] = symbol;
        _ordered.Add(symbol);
        return true;
    }

    /// <summary>This scope only, without the parent chain.</summary>
    public Symbol? LookupLocal(string name) =>
        _byName.TryGetValue(name, out var s) ? s : null;

    /// <summary>This scope, then up the parent chain.</summary>
    public Symbol? Lookup(string name)
    {
        for (var scope = this; scope is not null; scope = scope.Parent)
            if (scope._byName.TryGetValue(name, out var s))
                return s;
        return null;
    }
}
