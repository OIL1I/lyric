using Lyric.AST;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Ir.Lowering;

/// <summary>
/// Die angehobenen Lambdas eines Moduls (ADR-018).
///
/// <para>Ein Lambda wird zu einer <b>gewoehnlichen</b> <see cref="IrFunction"/>: Parameter 0 ist
/// sein Environment, danach kommen die geschriebenen Parameter. Damit ist ein Closure-Aufruf
/// derselbe Mechanismus wie ein Methodenaufruf mit Empfaenger (ADR-014) und nicht ein zweiter
/// daneben — die VM braucht fuer <c>callind</c> keinen eigenen Frame-Aufbau.</para>
///
/// <para><b>Warum eine Tabelle mit Worklist und keine Rekursion.</b> Der ModuleLowerer vergibt
/// alle FunctionIds in Pass 1, bevor irgendein Rumpf gelowert wird — ein Lambda taucht aber erst
/// in Pass 2 auf. Es bekommt seine Id deshalb bei der <b>Registrierung</b>, und gelowert wird es
/// erst danach; dabei darf es weitere Lambdas registrieren, die hinten anwachsen. Eine direkte
/// Rekursion haette dasselbe geleistet, aber die Reihenfolge in der Funktionsliste von der
/// Aufrufverschachtelung abhaengig gemacht — und die Liste ist im Bytecode indexbehaftet
/// (ADR-013).</para>
/// </summary>
internal sealed class LambdaTable
{
    /// <summary>Ein registriertes, noch nicht gelowertes Lambda samt allem, was sein Rumpf
    /// braucht.</summary>
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

    /// <summary>Die erste Id, die an ein Lambda gehen darf — hinter allen geschriebenen Funktionen
    /// und hinter dem Global-Initialisierer.</summary>
    /// <summary>Bis wohin schon gelowert wurde. Die Tabelle wird MEHRFACH geleert — eine
    /// Instanz kann ein Lambda anfordern, ein Lambda eine Instanz —, und ohne diese Marke
    /// entstuende bei jedem Durchgang alles noch einmal.</summary>
    private int _lowered;

    private readonly FunctionIds _ids;

    public LambdaTable(FunctionIds ids) => _ids = ids;

    public bool IsEmpty => _pending.Count == 0;

    /// <summary>
    /// Meldet ein Lambda an und liefert die Id, unter der es aufrufbar sein wird. Der Rumpf ist zu
    /// diesem Zeitpunkt noch nicht gelowert — deshalb kann der Aufrufer sein <c>mkclosure</c>
    /// sofort schreiben.
    /// </summary>
    /// <param name="substitution">Die Substitution der umgebenden Funktion. Ein Lambda IN einer
    /// monomorphisierten Instanz sieht deren Typ-Parameter — <c>(a: T, b: T) =&gt; …</c> in
    /// <c>sortList&lt;T&gt;</c> ist der Normalfall, nicht die Ausnahme. Ohne sie blieb das <c>T</c>
    /// im Rumpf unaufgeloest und das Lowering brach ab.</param>
    public FunctionId Register(LambdaExpr lambda, string enclosing, IReadOnlyList<Symbol> captures,
        bool capturesThis, IrType environmentType, TypeSymbol? receiver,
        IReadOnlyDictionary<GenericParamSymbol, LyrType> substitution)
    {
        var id = _ids.Next();

        // Der Name landet im Bytecode und in jeder Diagnose. '<' kann in keinem Lyric-Bezeichner
        // vorkommen (Sprache.md §1.3), also kollidiert er mit nichts; die laufende Nummer haelt
        // zwei Lambdas derselben Funktion auseinander.
        var name = $"{enclosing}.<lambda{_pending.Count}>";

        _pending.Add(new Pending(lambda, name, id, captures, capturesThis, environmentType,
            receiver, substitution));
        return id;
    }

    /// <summary>
    /// Lowert alle angemeldeten Lambdas — auch die, die dabei erst entstehen. Die Schleife laeuft
    /// ueber einen Index statt ueber einen Enumerator, weil <see cref="_pending"/> waehrend des
    /// Durchlaufs waechst: ein Lambda in einem Lambda ist der Normalfall, kein Sonderfall.
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
