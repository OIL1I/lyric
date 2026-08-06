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

    /// <summary>
    /// Locals, die eine Closure sich mit ihrer umgebenden Funktion <b>teilt</b> — sie leben nicht
    /// in einem Frame-Slot, sondern in einer Zelle auf dem Heap (ADR-018).
    ///
    /// <para>Ein <c>var</c>, das gefangen wird, muss geteilt werden: schreibt die Closure, sieht
    /// die Funktion es, und umgekehrt. Ein Frame-Slot kann das nicht, sobald der Frame endet und
    /// die Closure weiterlebt.</para>
    ///
    /// <para><b>Nur <c>var</c>.</b> Ein <c>let</c> und ein Parameter aendern sich nie (Zuweisung
    /// an einen Parameter ist <c>LYR-SEM0019</c>) — fuer sie ist „Wert kopieren" von „Variable
    /// teilen" nicht unterscheidbar, und die Kopie ist billiger. Die Unterscheidung kostet hier
    /// ein <c>if</c> und spart im erzeugten Code jede Zelle, die niemand braucht.</para>
    /// </summary>
    private readonly HashSet<Symbol> _boxed = new(ReferenceEqualityComparer.Instance);

    public void MarkBoxed(Symbol symbol) => _boxed.Add(symbol);

    /// <summary>
    /// Die Typargumente einer Aufrufstelle — inferiert oder geschrieben (Sprache.md §12).
    ///
    /// <para>Die Sema leitet sie ohnehin ab, um den Aufruf zu pruefen; ohne sie hier abzulegen
    /// muesste das Lowering die Inferenz <b>ein zweites Mal</b> ausfuehren, um zu wissen, welche
    /// Instanz von <c>id&lt;T&gt;</c> es rufen soll — zwei Wahrheiten ueber dieselbe Frage, und
    /// die zweite haette keine Diagnosen, mit denen sie sich melden koennte.</para>
    ///
    /// <para>Die Reihenfolge ist die der Generics-Deklaration, nicht die der Argumente: sie ist
    /// das, was eine Instanz identifiziert.</para>
    /// </summary>
    private readonly Dictionary<Node, LyrType[]> _typeArguments =
        new(ReferenceEqualityComparer.Instance);

    public void SetTypeArguments(Node call, LyrType[] arguments) =>
        _typeArguments[call] = arguments;

    /// <summary>Die Typargumente eines Aufrufs; leer, wenn der Aufgerufene nicht generisch ist.</summary>
    public LyrType[] TypeArgumentsOf(Node call) =>
        _typeArguments.TryGetValue(call, out var args) ? args : [];

    /// <summary>Lebt dieses Symbol in einer Zelle statt in einem Frame-Slot? Das Lowering fragt
    /// das an <b>jeder</b> Zugriffsstelle — auch ausserhalb der Closure, denn beide Seiten muessen
    /// dieselbe Zelle sehen.</summary>
    public bool IsBoxed(Symbol symbol) => _boxed.Contains(symbol);
}
