namespace Lyric.Ir.Lowering;

/// <summary>
/// Assigns the <see cref="FunctionId"/>s of the functions that arise DURING the lowering: lifted
/// lambdas, coroutine bodies and monomorphized instances.
///
/// <para>ONE COUNTER RATHER THAN A RANGE PER TABLE. All three grow simultaneously and without bound:
/// an instance can contain a lambda, a lambda can call a generic function. Separate ranges would have
/// to know their size in advance, and that is settled only once everything is lowered. A shared
/// counter solves it, because the id is then assigned in request order rather than in the order of a
/// category.</para>
///
/// <para>The position in the function list IS the id, so the list is sorted by it at the end. That
/// stays deterministic, because the request order is.</para>
/// </summary>
internal sealed class FunctionIds
{
    private int _next;

    public FunctionIds(int first) => _next = first;

    public FunctionId Next() => new(_next++);
}
