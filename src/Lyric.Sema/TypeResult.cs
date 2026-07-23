using Lyric.AST;
using Lyric.Resolver;

namespace Lyric.Sema;

/// <summary>
/// Seiten-Tabelle der Typprüfung: der Typ jedes Ausdrucks plus die aufgelösten
/// Symbole von Ausdrucks-Referenzen (Identifier → Local/Param/Global/Function/…).
/// Wie <see cref="BindingResult"/> lässt sie den AST immutable.
/// </summary>
public sealed class TypeResult
{
    private readonly Dictionary<Expr, LyrType> _types = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Node, Symbol> _refs = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Node> _exhaustiveMatches = new(ReferenceEqualityComparer.Instance);

    public void SetType(Expr expr, LyrType type) => _types[expr] = type;
    public LyrType TypeOf(Expr expr) => _types.TryGetValue(expr, out var t) ? t : LyrType.Error;

    public void BindRef(Node node, Symbol symbol) => _refs[node] = symbol;
    public Symbol? RefOf(Node node) => _refs.TryGetValue(node, out var s) ? s : null;

    // Exhaustivität (M4-2): vom TypeChecker bewiesene matches — Flow/DAA lesen das,
    // ohne selbst Typ-Wissen zu brauchen.
    public void MarkMatchExhaustive(Node match) => _exhaustiveMatches.Add(match);
    public bool IsMatchExhaustive(Node match) => _exhaustiveMatches.Contains(match);
}
