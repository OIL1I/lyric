using Lyric.AST;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Ir.Lowering;

/// <summary>
/// The USED extension methods of a module.
///
/// <para>Emphasis on used. Registering every method of every <c>extend</c> block is harmless only as
/// long as extensions live in user programs: whoever writes a block usually uses it. With the
/// <c>Display</c> extensions in <c>std.core</c> that tips over — <c>std.core</c> is always loaded, so
/// every program would carry five extension functions and four <c>std.string</c> imports, even a
/// <c>hello.lyr</c> that touches none of them.</para>
///
/// <para>The same rule applies here as for types and imports: ONLY WHAT IS ACTUALLY USED GOES INTO
/// THE BYTECODE. A declared but never called extension belongs in it as little as a declared but never
/// instantiated class.</para>
///
/// <para>WORKLIST RATHER THAN RECURSION, the same reasoning as in <see cref="LambdaTable"/>: the id is
/// assigned at REGISTRATION, so the caller can write its <c>call</c> immediately; lowering happens
/// afterwards, and the body may request further extensions. Recursion would have made the order in the
/// function list depend on the call nesting, and that list is index-bearing in the bytecode.</para>
/// </summary>
internal sealed class ExtensionTable
{
    private readonly record struct Pending(
        FunctionDecl Decl,
        string Name,
        FunctionId Id,
        TypeSymbol? Receiver,
        TypeNode? ReceiverTypeNode);

    private readonly List<Pending> _pending = new();

    /// <summary>Who already has an id. Without this map the same method would get a new one on every
    /// call, and the verifier rejects duplicate function names.</summary>
    private readonly Dictionary<FunctionSymbol, FunctionId> _requested =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>How far the lowering has come. The table is drained SEVERAL times — an extension can
    /// request a lambda, a lambda an extension — and without this mark everything would arise anew on
    /// every pass.</summary>
    private int _lowered;

    private readonly FunctionIds _ids;

    public ExtensionTable(FunctionIds ids) => _ids = ids;

    /// <summary>Has this method already been requested? Returns the id under which it is
    /// callable.</summary>
    public bool TryGet(FunctionSymbol symbol, out FunctionId id) =>
        _requested.TryGetValue(symbol, out id);

    /// <summary>
    /// Requests an extension method and returns the id under which it will be callable. Repeated
    /// requests for the same method return the same id.
    /// </summary>
    /// <param name="declaringModule">The module the <c>extend</c> block stands in, not the one of the
    /// target type. <c>extend string</c> may stand in any module.</param>
    public FunctionId Request(FunctionSymbol symbol, FunctionDecl decl, ModuleSymbol declaringModule,
        string targetName, TypeSymbol? receiver, TypeNode? receiverTypeNode)
    {
        if (_requested.TryGetValue(symbol, out var existing)) return existing;

        var id = _ids.Next();
        _requested[symbol] = id;
        _pending.Add(new Pending(decl,
            NameMangling.ForExtension(declaringModule, targetName, decl.Name),
            id, receiver, receiverTypeNode));
        return id;
    }

    /// <summary>
    /// Lowers all registered extensions, including the ones that arise while doing so. The loop runs
    /// over an index rather than an enumerator, because <see cref="_pending"/> can grow during the pass.
    /// </summary>
    public List<(FunctionId Id, IrFunction Function)> LowerAll(TypeResult types,
        IReadOnlyDictionary<FunctionSymbol, FunctionId> functions, ImportTable imports,
        TypeTable typeTable, GlobalTable globals, LambdaTable lambdas, InstanceTable instances)
    {
        var lowered = new List<(FunctionId, IrFunction)>();

        for (; _lowered < _pending.Count; _lowered++)
        {
            var p = _pending[_lowered];
            lowered.Add((p.Id, new FunctionLowerer(p.Decl, p.Name, types, functions, imports,
                typeTable, ModuleLowerer.NoSubstitution, globals, lambdas, instances, p.Receiver,
                receiverTypeNode: p.ReceiverTypeNode).Run()));
        }

        return lowered;
    }
}
