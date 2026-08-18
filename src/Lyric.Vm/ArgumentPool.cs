namespace Lyric.Vm;

/// <summary>
/// Reusable argument buffers for native calls, one free list per arity.
///
/// <para>Every native call used to build a fresh <c>LyrValue[arity]</c>. The allocation is
/// small; its SCATTER is not — a boundary-heavy game loop produced a hundred thousand of them
/// per second, and the measured cost was the GC tail, not the crossing. The pool follows the
/// frame pool's discipline: rent at the call, recycle after the implementation returns, clear
/// on recycle so a pooled buffer keeps nothing alive, abandon on an exception — losing a pool
/// entry costs the next allocation and never correctness.</para>
///
/// <para>REENTRANCY is what the stack shape is for: a native may call back into the VM, and the
/// inner call rents the NEXT buffer of its arity because the outer one is checked out. One pool
/// per <see cref="LoadedProgram"/>, which is also the VM's threading contract in one line: one
/// thread per program, as everywhere else.</para>
///
/// <para>The buffer is LOANED to the implementation for the duration of the call — the other
/// half of this contract is documented on <see cref="NativeRegistry"/>.</para>
/// </summary>
internal sealed class ArgumentPool
{
    /// <summary>Arities above this allocate plainly. Native signatures are hand-written and
    /// short; eight covers every one in the standard library with room to spare.</summary>
    private const int MaxPooledArity = 8;

    private readonly Stack<LyrValue[]>?[] _free = new Stack<LyrValue[]>?[MaxPooledArity + 1];

    public LyrValue[] Rent(int arity)
    {
        if (arity == 0) return [];
        if (arity > MaxPooledArity) return new LyrValue[arity];

        var free = _free[arity];
        return free is { Count: > 0 } ? free.Pop() : new LyrValue[arity];
    }

    public void Recycle(LyrValue[] buffer)
    {
        if (buffer.Length is 0 or > MaxPooledArity) return;

        System.Array.Clear(buffer);
        (_free[buffer.Length] ??= new Stack<LyrValue[]>()).Push(buffer);
    }
}
