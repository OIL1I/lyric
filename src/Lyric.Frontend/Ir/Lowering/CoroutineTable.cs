using Lyric.AST;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Ir.Lowering;

/// <summary>
/// The body functions of a module's coroutines.
///
/// <para>A written coroutine becomes TWO functions: the FACTORY keeps the regular
/// <see cref="FunctionId"/>, so a caller writes an unchanged <c>call</c> and gets a state object back;
/// the BODY is registered here and appended at the end, exactly like a lifted lambda and for the same
/// reason — it arises only in pass 2, but its id has to be settled while the factory is
/// lowered.</para>
/// </summary>
internal sealed class CoroutineTable
{
    private readonly record struct Pending(
        FunctionDecl Decl, string Name, FunctionId Id, TypeId State, IrType Yield,
        TypeSymbol? Receiver);

    private readonly List<Pending> _pending = new();
    /// <summary>How far the lowering has come. The table is drained SEVERAL times — an instance can
    /// request a lambda, a lambda an instance — and without this mark everything would arise anew on
    /// every pass.</summary>
    private int _lowered;

    private readonly FunctionIds _ids;

    public CoroutineTable(FunctionIds ids) => _ids = ids;

    public bool IsEmpty => _pending.Count == 0;
    public int Count => _pending.Count;

    /// <summary>Registers a body and returns the id under which the factory later references it.
    /// </summary>
    public FunctionId Register(FunctionDecl decl, string name, TypeId state, IrType yield,
        TypeSymbol? receiver)
    {
        var id = _ids.Next();

        // '<' cannot occur in any Lyric identifier, so the name collides with nothing — the same
        // convention as for '<globals>' and '<lambda0>'.
        _pending.Add(new Pending(decl, $"{name}.<body>", id, state, yield, receiver));
        return id;
    }

    public List<(FunctionId Id, IrFunction Function)> LowerAll(TypeResult types,
        IReadOnlyDictionary<FunctionSymbol, FunctionId> functions, ImportTable imports,
        TypeTable typeTable, GlobalTable globals, LambdaTable lambdas, InstanceTable instances)
    {
        var lowered = new List<(FunctionId, IrFunction)>(_pending.Count);

        for (; _lowered < _pending.Count; _lowered++)
        {
            var p = _pending[_lowered];
            lowered.Add((p.Id, FunctionLowerer.ForCoroutineBody(p.Decl, p.Name, p.State, p.Yield,
                p.Receiver, types, functions, imports, typeTable, globals, lambdas, instances).Run()));
        }

        return lowered;
    }
}
