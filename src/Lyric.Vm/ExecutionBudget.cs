using Lyric.Core;

namespace Lyric.Vm;

/// <summary>
/// How many instructions an execution may run before it is stopped.
///
/// <para>What a capability cannot express: a module that reaches nothing at all still owns the
/// thread it runs on, and <c>while (true) { }</c> is neither a capability nor a bug the loader can
/// see. A host that runs foreign code — a mod, a downloaded script — hands one of these in and gets
/// the thread back either way.</para>
///
/// <para>COUNTED, NOT TIMED, and that is the point rather than a limitation: the same program with
/// the same budget stops at the same instruction on every machine and in every run, which is what a
/// replay needs. A wall-clock limit would be the other design — it needs a second thread, and it
/// answers differently on a loaded machine than on a quiet one.</para>
///
/// <para>An object rather than a number, because a budget is asked three questions: how much is
/// left (a host calibrates its number from <see cref="Consumed"/>, and there is no other way to
/// arrive at one), may several calls share one kitty (a frame that drives four scripts), and does a
/// native that calls back INTO the script draw from the same one (it does, when the host passes the
/// same object).</para>
///
/// <para>It is not thread-safe, which is the runtime's standing contract: one thread.</para>
/// </summary>
public sealed class ExecutionBudget
{
    private long _remaining;

    /// <param name="instructions">How many instructions this budget covers. Must be positive: a
    /// budget of zero would stop before the first instruction, which no caller means.</param>
    public ExecutionBudget(long instructions)
    {
        if (instructions <= 0)
            throw new ArgumentOutOfRangeException(nameof(instructions), instructions,
                "an execution budget must be positive");
        Limit = instructions;
        _remaining = instructions;
    }

    /// <summary>The budget this was created with, and what <see cref="Reset"/> returns to.</summary>
    public long Limit { get; }

    /// <summary>Instructions still available. Zero means the next instruction stops the run.</summary>
    public long Remaining => _remaining < 0 ? 0 : _remaining;

    /// <summary>Instructions spent since the last <see cref="Reset"/>. The number a host reads to
    /// find out what its own workload actually costs.</summary>
    public long Consumed => Limit - Remaining;

    /// <summary>Refills to <see cref="Limit"/>. The per-frame call for a host that keeps one
    /// budget per script rather than allocating one each time.</summary>
    public void Reset() => _remaining = Limit;

    /// <summary>
    /// Charges one instruction, and stops the run when nothing is left.
    ///
    /// <para>A PANIC rather than an exception, and that is a security property, not a formality:
    /// a panic leaves the interpreter as a host-side throw, so no <c>catch</c> in the running
    /// program sees it and no <c>defer</c> gets to run afterwards. A catchable stop would be one
    /// a hostile script could sit out.</para>
    /// </summary>
    internal void Charge()
    {
        if (--_remaining >= 0) return;

        _remaining = 0;
        throw new LyricPanic(VmDiagnostics.BudgetExhausted,
            $"execution budget of {Limit} instruction(s) is exhausted");
    }
}
