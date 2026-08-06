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

    /// <summary>Typ je Modul-<c>let</c> / <c>static let</c>. Der TypeChecker fuellt sie, das
    /// Lowering liest sie — ein Global hat keinen Ausdruck, an dem sein Typ haengen koennte.</summary>
    private readonly Dictionary<GlobalSymbol, LyrType> _globals =
        new(ReferenceEqualityComparer.Instance);

    public void BindGlobal(GlobalSymbol symbol, LyrType type) => _globals[symbol] = type;
    private readonly HashSet<Node> _exhaustiveMatches = new(ReferenceEqualityComparer.Instance);

    public void SetType(Expr expr, LyrType type) => _types[expr] = type;
    public LyrType TypeOf(Expr expr) => _types.TryGetValue(expr, out var t) ? t : LyrType.Error;

    public void BindRef(Node node, Symbol symbol) => _refs[node] = symbol;
    public Symbol? RefOf(Node node) => _refs.TryGetValue(node, out var s) ? s : null;

    /// <summary>Der Typ eines Modul-<c>let</c> oder <c>static let</c>. Getrennt von
    /// <see cref="TypeOf"/>, weil ein Global kein Ausdruck ist — sein Typ haengt am Symbol, nicht
    /// an einer Verwendungsstelle.</summary>
    public LyrType TypeOfGlobal(GlobalSymbol symbol) =>
        _globals.TryGetValue(symbol, out var t) ? t : LyrType.Error;

    // Exhaustivität (M4-2): vom TypeChecker bewiesene matches — Flow/DAA lesen das,
    // ohne selbst Typ-Wissen zu brauchen.
    public void MarkMatchExhaustive(Node match) => _exhaustiveMatches.Add(match);
    public bool IsMatchExhaustive(Node match) => _exhaustiveMatches.Contains(match);

    // Captures (M4-4, ADR-011): welche äußeren Locals/Params (und this) eine Lambda
    // implizit einfängt — Abnehmer ist das Closure-Lifting in M5.
    private static readonly IReadOnlyList<Symbol> NoCaptures = [];
    private readonly Dictionary<Node, (IReadOnlyList<Symbol> Symbols, bool This)> _captures = new(ReferenceEqualityComparer.Instance);

    public void SetCaptures(Node lambda, IReadOnlyList<Symbol> symbols, bool capturesThis) =>
        _captures[lambda] = (symbols, capturesThis);
    public (IReadOnlyList<Symbol> Symbols, bool CapturesThis) CapturesOf(Node lambda) =>
        _captures.TryGetValue(lambda, out var c) ? c : (NoCaptures, false);
}
