namespace Lyric.Vm;

/// <summary>
/// Runtime diagnostics (<c>LYR-VM####</c>).
///
/// <para>Two classes. A panic is a programming error in the running program: not catchable, with
/// a backtrace (<see cref="LyricPanic"/>). A load error occurs before the first instruction and
/// carries no backtrace, because there is no call stack yet
/// (<see cref="LyricRuntimeException"/>).</para>
/// </summary>
public static class VmDiagnostics
{
    /// <summary>The module has no Start section: it is a library, not a program.</summary>
    public const string NoEntryPoint = "LYR-VM0001";

    /// <summary>Integer division or remainder by zero. Floating point follows IEEE (Inf/NaN) and
    /// is not an error.</summary>
    public const string DivisionByZero = "LYR-VM0002";

    /// <summary>An <c>unreachable</c> instruction executed. The compiler claimed this point could
    /// not be reached.</summary>
    public const string UnreachableExecuted = "LYR-VM0003";

    /// <summary>Call depth exceeded; how unbounded recursion surfaces.</summary>
    public const string CallDepthExceeded = "LYR-VM0004";

    /// <summary>The module requires imports the runtime does not bind.</summary>
    public const string ImportsNotBound = "LYR-VM0005";

    /// <summary>The module requires a capability this VM does not grant.
    ///
    /// <para>The code sits in the CAP range rather than with the VM errors: it describes a host
    /// policy, not a broken file. The same module runs elsewhere.</para></summary>
    public const string CapabilityDenied = "LYR-CAP0001";

    /// <summary>Element index outside the array bounds. Unlike type and field indices it is a
    /// runtime value and cannot be checked at load time.</summary>
    public const string IndexOutOfRange = "LYR-VM0006";

    /// <summary>Force-unwrap (<c>expr!</c>) of a <c>?T</c> that holds no value.</summary>
    public const string NullDereference = "LYR-VM0007";

    /// <summary><c>enumas</c> to a variant the value is not. The compiler proves this through
    /// <c>match</c>; the check remains because a <c>.lyrbc</c> may come from elsewhere.</summary>
    public const string WrongVariant = "LYR-VM0008";

    /// <summary>No vtable entry for (concrete type, interface, slot). Reachable only for a module
    /// assembled without the reader; the loader checks every <c>mkiface</c> against the Impls
    /// section.</summary>
    public const string NoImplementation = "LYR-VM0009";

    /// <summary>An exception left the entry point uncaught.
    ///
    /// <para>The sema requires a call to a <c>throws</c> function to be declared or wrapped, and
    /// <c>main</c> may declare nothing, so this is reachable only for a hand-built module. It
    /// aborts like a panic.</para>
    /// </summary>
    public const string UncaughtException = "LYR-VM0010";

    /// <summary>A <c>panic(msg)</c> from the program. Not catchable; the message is the
    /// caller's.</summary>
    public const string Panicked = "LYR-VM0011";

    /// <summary>A <c>char</c> result outside the Unicode range: beyond <c>0x10FFFF</c> or in the
    /// surrogate range <c>D800..DFFF</c>.
    ///
    /// <para>Checked where the value is produced rather than where it is used, so the error
    /// surfaces at the arithmetic that caused it.</para></summary>
    public const string InvalidCodepoint = "LYR-VM0012";
}

/// <summary>
/// A panic: the program broke a contract and does not continue. Not catchable with
/// <c>try</c>/<c>catch</c>.
///
/// <para>Carries the Lyric call stack, attached while leaving the interpreter loop, which is
/// where the frames are known.</para>
/// </summary>
public sealed class LyricPanic : Exception
{
    public string Code { get; }

    /// <summary>Function names from the panic site upwards. Empty until the panic leaves the
    /// interpreter loop.</summary>
    public IReadOnlyList<string> CallStack { get; init; } = Array.Empty<string>();

    public LyricPanic(string code, string message) : base(message) => Code = code;

    public LyricPanic WithCallStack(IReadOnlyList<string> callStack) =>
        new(Code, Message) { CallStack = callStack };
}

/// <summary>The module cannot be started at all: no entry point, unbound imports. Not a panic,
/// because nothing is running yet.</summary>
public sealed class LyricRuntimeException : Exception
{
    public string Code { get; }

    public LyricRuntimeException(string code, string message) : base(message) => Code = code;
}
