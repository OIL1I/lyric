using Lyric.AST;
using Lyric.Resolver;

namespace Lyric.Sema;

/// <summary>
/// The side table of type checking: the type of every expression plus the resolved symbols of
/// expression references (identifier to local, parameter, global, function, …). Like
/// <see cref="BindingResult"/> it leaves the AST immutable.
/// </summary>
public sealed class TypeResult
{
    private readonly Dictionary<Expr, LyrType> _types = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Node, Symbol> _refs = new(ReferenceEqualityComparer.Instance);

    /// <summary>The type of each module <c>let</c> and <c>static let</c>. The TypeChecker fills it,
    /// the lowering reads it: a global has no expression its type could hang on.</summary>
    private readonly Dictionary<GlobalSymbol, LyrType> _globals =
        new(ReferenceEqualityComparer.Instance);

    public void BindGlobal(GlobalSymbol symbol, LyrType type) => _globals[symbol] = type;
    private readonly HashSet<Node> _exhaustiveMatches = new(ReferenceEqualityComparer.Instance);

    public void SetType(Expr expr, LyrType type) => _types[expr] = type;

    /// <summary>Every typed expression with its type. The consumer is the test that checks the
    /// <see cref="ErrorType"/> invariant; without an enumeration it could only be checked where
    /// someone already looks.</summary>
    public IEnumerable<KeyValuePair<Expr, LyrType>> AllTypes => _types;
    public LyrType TypeOf(Expr expr) => _types.TryGetValue(expr, out var t) ? t : LyrType.Error;

    public void BindRef(Node node, Symbol symbol) => _refs[node] = symbol;
    public Symbol? RefOf(Node node) => _refs.TryGetValue(node, out var s) ? s : null;

    /// <summary>
    /// Every node bound to a symbol, uses and declarations alike.
    ///
    /// <para>Declarations are in here because the definite-assignment analysis needs them: a
    /// <c>BindingStmt</c>, a <c>Param</c>, a <c>ForInStmt</c> and the pattern bindings are each
    /// bound to the symbol they THEMSELVES declare. A consumer asking for uses separates the two by
    /// <c>ReferenceEquals(symbol.Declaration, node)</c> — no flag is needed, the symbol already
    /// knows where it was declared.</para>
    /// </summary>
    public IEnumerable<KeyValuePair<Node, Symbol>> AllReferences => _refs;

    /// <summary>The type of a module <c>let</c> or <c>static let</c>. Separate from
    /// <see cref="TypeOf"/>, because a global is not an expression: its type hangs on the symbol,
    /// not on a use site.</summary>
    public LyrType TypeOfGlobal(GlobalSymbol symbol) =>
        _globals.TryGetValue(symbol, out var t) ? t : LyrType.Error;

    // Exhaustiveness: matches proven by the TypeChecker. Flow and definite-assignment analysis read
    // this without needing type knowledge of their own.
    public void MarkMatchExhaustive(Node match) => _exhaustiveMatches.Add(match);
    public bool IsMatchExhaustive(Node match) => _exhaustiveMatches.Contains(match);

    // Captures: which outer locals, parameters and 'this' a lambda captures implicitly. The consumer
    // is closure lifting.
    private static readonly IReadOnlyList<Symbol> NoCaptures = [];
    private readonly Dictionary<Node, (IReadOnlyList<Symbol> Symbols, bool This)> _captures = new(ReferenceEqualityComparer.Instance);

    public void SetCaptures(Node lambda, IReadOnlyList<Symbol> symbols, bool capturesThis) =>
        _captures[lambda] = (symbols, capturesThis);
    public (IReadOnlyList<Symbol> Symbols, bool CapturesThis) CapturesOf(Node lambda) =>
        _captures.TryGetValue(lambda, out var c) ? c : (NoCaptures, false);

    /// <summary>
    /// Locals a closure SHARES with its enclosing function: they live in a heap cell rather than in a
    /// frame slot.
    ///
    /// <para>A captured <c>var</c> has to be shared: when the closure writes, the function sees it,
    /// and the other way round. A frame slot cannot do that once the frame ends and the closure
    /// lives on.</para>
    ///
    /// <para>Only <c>var</c>. A <c>let</c> and a parameter never change — assigning to a parameter is
    /// <c>LYR-SEM0019</c> — so for them "copy the value" and "share the variable" are
    /// indistinguishable, and the copy is cheaper.</para>
    /// </summary>
    private readonly HashSet<Symbol> _boxed = new(ReferenceEqualityComparer.Instance);

    public void MarkBoxed(Symbol symbol) => _boxed.Add(symbol);

    /// <summary>
    /// The method call an operator expression stands for.
    ///
    /// <para><c>==</c> on an <c>Equatable</c> type IS <c>a.equals(b)</c>: the checker builds that
    /// call from synthetic nodes, checks it through the ordinary member path, and stores it here.
    /// The lowering emits the stored call instead of a <c>BinOp</c> — deriving the method a second
    /// time there would be a second answer to which function an operator means.</para>
    ///
    /// <para><c>Negate</c> carries <c>!=</c>: there is no <c>notEquals</c> to call, the call's
    /// result is negated.</para>
    ///
    /// <para>The synthetic nodes hang in no tree, so syntax walks never meet them; they reuse the
    /// REAL operand nodes as receiver and argument, which is what makes the stored call lower the
    /// operands exactly once.</para>
    /// </summary>
    private readonly Dictionary<Node, (CallExpr Call, bool Negate)> _operatorCalls =
        new(ReferenceEqualityComparer.Instance);

    public void DesugarOperator(Node op, CallExpr call, bool negate) =>
        _operatorCalls[op] = (call, negate);

    public (CallExpr Call, bool Negate)? OperatorCallOf(Node op) =>
        _operatorCalls.TryGetValue(op, out var d) ? d : null;

    /// <summary>
    /// The type arguments of a call site, inferred or written.
    ///
    /// <para>The sema derives them anyway to check the call; without storing them here the lowering
    /// would have to run the inference A SECOND TIME to know which instance of <c>id&lt;T&gt;</c> to
    /// call — two truths about the same question, and the second one would have no diagnostics to
    /// speak up with.</para>
    ///
    /// <para>The order is that of the generics declaration, not that of the arguments: it is what
    /// identifies an instance.</para>
    /// </summary>
    private readonly Dictionary<Node, LyrType[]> _typeArguments =
        new(ReferenceEqualityComparer.Instance);

    public void SetTypeArguments(Node call, LyrType[] arguments) =>
        _typeArguments[call] = arguments;

    /// <summary>The type arguments of a call; empty when the callee is not generic.</summary>
    public LyrType[] TypeArgumentsOf(Node call) =>
        _typeArguments.TryGetValue(call, out var args) ? args : [];

    /// <summary>
    /// The types from <c>std.iter</c> that <c>for-in</c> needs.
    ///
    /// <para>The TypeChecker looks them up anyway to check the loop head; storing them here saves the
    /// lowering a second lookup, and two lookups would be two opportunities to find different
    /// symbols.</para>
    /// </summary>
    public TypeSymbol? IteratorInterface { get; set; }
    public TypeSymbol? ArrayIterator { get; set; }
    public TypeSymbol? RangeIterator { get; set; }
    public TypeSymbol? StringIterator { get; set; }

    /// <summary>'Indexable&lt;T&gt;' from std.collections, what '[i]' dispatches to.</summary>
    public TypeSymbol? Indexable { get; set; }

    /// <summary>'Iterable&lt;T&gt;' from std.iter, what 'for-in' asks first.</summary>
    public TypeSymbol? Iterable { get; set; }

    /// <summary>Does this symbol live in a cell rather than in a frame slot? The lowering asks at
    /// EVERY access site, outside the closure too, because both sides have to see the same
    /// cell.</summary>
    public bool IsBoxed(Symbol symbol) => _boxed.Contains(symbol);
}
