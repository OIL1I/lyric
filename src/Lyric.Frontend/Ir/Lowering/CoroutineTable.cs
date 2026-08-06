using Lyric.AST;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Ir.Lowering;

/// <summary>
/// Die Rumpf-Funktionen der Coroutinen eines Moduls (Sprache.md §8).
///
/// <para>Aus einer geschriebenen Coroutine werden <b>zwei</b> Funktionen: die <b>Fabrik</b> behaelt
/// die regulaere <see cref="FunctionId"/>, damit ein Aufrufer unveraendert <c>call</c> schreibt und
/// ein Zustandsobjekt zurueckbekommt; der <b>Rumpf</b> wird hier angemeldet und hinten angehaengt,
/// genau wie ein angehobenes Lambda (ADR-018) — und aus demselben Grund: er entsteht erst in
/// Pass 2, seine Id muss aber schon feststehen, waehrend die Fabrik gelowert wird.</para>
/// </summary>
internal sealed class CoroutineTable
{
    private readonly record struct Pending(
        FunctionDecl Decl, string Name, FunctionId Id, TypeId State, IrType Yield,
        TypeSymbol? Receiver);

    private readonly List<Pending> _pending = new();
    private readonly int _firstId;

    public CoroutineTable(int firstId) => _firstId = firstId;

    public bool IsEmpty => _pending.Count == 0;
    public int Count => _pending.Count;

    /// <summary>Meldet einen Rumpf an und liefert die Id, unter der die Fabrik ihn spaeter
    /// referenziert.</summary>
    public FunctionId Register(FunctionDecl decl, string name, TypeId state, IrType yield,
        TypeSymbol? receiver)
    {
        var id = new FunctionId(_firstId + _pending.Count);

        // '<' kann in keinem Lyric-Bezeichner vorkommen (§1.3), also kollidiert der Name mit
        // nichts — dieselbe Konvention wie bei '<globals>' und '<lambda0>'.
        _pending.Add(new Pending(decl, $"{name}.<body>", id, state, yield, receiver));
        return id;
    }

    public List<IrFunction> LowerAll(TypeResult types,
        IReadOnlyDictionary<FunctionSymbol, FunctionId> functions, ImportTable imports,
        TypeTable typeTable, GlobalTable globals, LambdaTable lambdas)
    {
        var lowered = new List<IrFunction>(_pending.Count);

        foreach (var p in _pending)
            lowered.Add(FunctionLowerer.ForCoroutineBody(p.Decl, p.Name, p.State, p.Yield,
                p.Receiver, types, functions, imports, typeTable, globals, lambdas).Run());

        return lowered;
    }
}
