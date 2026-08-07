using Lyric.AST;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Ir.Lowering;

/// <summary>
/// Die <b>benutzten</b> Extension-Methoden eines Moduls (Sprache.md §3.6).
///
/// <para>Betonung auf benutzt. Bis M8/S1a registrierte Pass 1 <b>jede</b> Methode <b>jedes</b>
/// <c>extend</c>-Blocks, und das war solange harmlos, wie Extensions nur in Nutzer-Programmen
/// standen: wer einen Block schreibt, benutzt ihn meist auch. Mit den <c>Display</c>-Extensions
/// in <c>std.core</c> kippte das — <c>std.core</c> wird immer geladen, also trug plötzlich
/// <b>jedes</b> Programm fünf Extension-Funktionen und vier <c>std.string</c>-Importe mit sich,
/// auch ein <c>hello.lyr</c>, das keine davon anfasst.</para>
///
/// <para>Damit gilt hier dieselbe Regel wie schon für Typen und Importe: <b>im Bytecode steht
/// nur, was tatsächlich benutzt wurde.</b> Eine deklarierte, nie gerufene Extension gehört
/// genauso wenig hinein wie eine deklarierte, nie instanziierte Klasse.</para>
///
/// <para><b>Warum Worklist und nicht Rekursion</b> — dieselbe Begründung wie bei
/// <see cref="LambdaTable"/>: die Id wird bei der <b>Registrierung</b> vergeben, damit der
/// Aufrufer seinen <c>call</c> sofort schreiben kann; gelowert wird danach, und dabei darf der
/// Rumpf weitere Extensions anfordern. Eine Rekursion hätte die Reihenfolge in der
/// Funktionsliste von der Aufrufverschachtelung abhängig gemacht, und die Liste ist im Bytecode
/// indexbehaftet (ADR-013).</para>
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

    /// <summary>Wer schon eine Id hat. Ohne diese Map bekäme dieselbe Methode bei jedem Aufruf
    /// eine neue — und der Verifier lehnt doppelte Funktionsnamen ab.</summary>
    private readonly Dictionary<FunctionSymbol, FunctionId> _requested =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>Bis wohin schon gelowert wurde. Die Tabelle wird MEHRFACH geleert — eine
    /// Extension kann ein Lambda anfordern, ein Lambda eine Extension —, und ohne diese Marke
    /// entstünde bei jedem Durchgang alles noch einmal.</summary>
    private int _lowered;

    private readonly FunctionIds _ids;

    public ExtensionTable(FunctionIds ids) => _ids = ids;

    /// <summary>Ist diese Methode schon angefordert? Liefert die Id, unter der sie aufrufbar
    /// ist.</summary>
    public bool TryGet(FunctionSymbol symbol, out FunctionId id) =>
        _requested.TryGetValue(symbol, out id);

    /// <summary>
    /// Fordert eine Extension-Methode an und liefert die Id, unter der sie aufrufbar sein wird.
    /// Mehrfache Anfragen für dieselbe Methode liefern dieselbe Id.
    /// </summary>
    /// <param name="declaringModule">Das Modul, in dem der <c>extend</c>-Block steht — nicht das
    /// des Zieltyps. <c>extend string</c> darf in jedem Modul stehen.</param>
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
    /// Lowert alle angemeldeten Extensions — auch die, die dabei erst entstehen. Die Schleife
    /// läuft über einen Index statt über einen Enumerator, weil <see cref="_pending"/> während
    /// des Durchlaufs wachsen kann.
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
