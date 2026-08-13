using Lyric.AST;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Ir.Lowering;

/// <summary>
/// The lifted lambdas of a module.
///
/// <para>A lambda becomes an ORDINARY <see cref="IrFunction"/>: parameter 0 is its environment,
/// followed by the written parameters. A closure call is therefore the same mechanism as a method call
/// with a receiver rather than a second one beside it, and the VM needs no separate frame setup for
/// <c>callind</c>.</para>
///
/// <para>A TABLE WITH A WORKLIST RATHER THAN RECURSION. The ModuleLowerer assigns all FunctionIds in
/// pass 1, before any body is lowered, but a lambda only appears in pass 2. It therefore gets its id
/// at REGISTRATION and is lowered afterwards, and while being lowered it may register further lambdas
/// that grow at the end. Direct recursion would have achieved the same but would have made the order
/// in the function list depend on the call nesting, and that list is index-bearing in the
/// bytecode.</para>
/// </summary>
internal sealed class LambdaTable
{
    /// <summary>A registered lambda that has not been lowered yet, with everything its body
    /// needs.</summary>
    private readonly record struct Pending(
        LambdaExpr Lambda,
        string Name,
        FunctionId Id,
        IReadOnlyList<Symbol> Captures,
        bool CapturesThis,
        IrType EnvironmentType,
        TypeSymbol? Receiver,
        IReadOnlyDictionary<GenericParamSymbol, LyrType> Substitution);

    private readonly List<Pending> _pending = new();

    /// <summary>The first id a lambda may take: behind all written functions and behind the global
    /// initializer.</summary>
    /// <summary>How far the lowering has come. The table is drained SEVERAL times — an instance can
    /// request a lambda, a lambda an instance — and without this mark everything would arise anew on
    /// every pass.</summary>
    private int _lowered;

    private readonly FunctionIds _ids;

    public LambdaTable(FunctionIds ids) => _ids = ids;

    public bool IsEmpty => _pending.Count == 0;

    /// <summary>
    /// Registers a lambda and returns the id under which it will be callable. The body is not lowered
    /// at that point, which is why the caller can write its <c>mkclosure</c> immediately.
    /// </summary>
    /// <param name="substitution">The substitution of the enclosing function. A lambda IN a monomorphized
    /// instance sees its type parameters — <c>(a: T, b: T) =&gt; …</c> inside <c>sortList&lt;T&gt;</c> is
    /// the normal case, not the exception. Without it the <c>T</c> in the body stays unresolved and the
    /// lowering aborts.</param>
    public FunctionId Register(LambdaExpr lambda, string enclosing, IReadOnlyList<Symbol> captures,
        bool capturesThis, IrType environmentType, TypeSymbol? receiver,
        IReadOnlyDictionary<GenericParamSymbol, LyrType> substitution)
    {
        var id = _ids.Next();

        // The name lands in the bytecode and in every diagnostic. '<' cannot occur in any Lyric
        // identifier, so it collides with nothing; the running number keeps two lambdas of the same
        // function apart.
        var name = $"{enclosing}.<lambda{_pending.Count}>";

        _pending.Add(new Pending(lambda, name, id, captures, capturesThis, environmentType,
            receiver, substitution));
        return id;
    }

    /// <summary>
    /// Lowers all registered lambdas, including the ones that arise while doing so. The loop runs over
    /// an index rather than an enumerator, because <see cref="_pending"/> grows during the pass: a
    /// lambda inside a lambda is the normal case, not a special one.
    /// </summary>
    public List<(FunctionId Id, IrFunction Function)> LowerAll(TypeResult types,
        IReadOnlyDictionary<FunctionSymbol, FunctionId> functions, ImportTable imports,
        TypeTable typeTable, GlobalTable globals, InstanceTable instances)
    {
        var lowered = new List<(FunctionId, IrFunction)>();

        for (; _lowered < _pending.Count; _lowered++)
        {
            var p = _pending[_lowered];
            lowered.Add((p.Id, FunctionLowerer.ForLambda(
                p.Lambda, p.Name, p.Captures, p.CapturesThis, p.EnvironmentType, p.Receiver,
                types, functions, imports, typeTable, globals, this, instances,
                p.Substitution).Run()));
        }

        return lowered;
    }
}
