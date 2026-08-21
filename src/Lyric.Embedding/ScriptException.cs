using Lyric.Vm;

namespace Lyric.Embedding;

/// <summary>
/// A script failed at runtime.
///
/// <para>Declared here rather than in the runtime assembly, which a host does not reference and
/// whose exception types it therefore could not name in a <c>catch</c>. The original exception
/// hangs off <see cref="Exception.InnerException"/>.</para>
/// </summary>
public class ScriptException : Exception
{
    internal ScriptException(string code, string message, Exception? inner)
        : base(message, inner) => Code = code;

    /// <summary>The diagnostic code (<c>LYR-VM####</c>), stable across versions.</summary>
    public string Code { get; }

    /// <summary>The embedding-side form of a panic that left the interpreter. One place, because
    /// a budget stop is a panic with its own type, and three call sites deciding that separately
    /// would eventually disagree.</summary>
    internal static ScriptException From(LyricPanic panic) =>
        panic.Code == VmDiagnostics.BudgetExhausted
            ? new ScriptBudgetException(panic.Code, panic.Message, panic)
            : new ScriptPanicException(panic.Code, panic.Message, panic);
}

/// <summary>
/// The script panicked: division by zero, a force-unwrap of an absent value, an explicit
/// <c>panic</c>.
///
/// <para>Separate from <see cref="ScriptException"/>, which covers a script the host is not
/// permitted or able to run.</para>
/// </summary>
public class ScriptPanicException : ScriptException
{
    internal ScriptPanicException(string code, string message, Exception? inner)
        : base(code, message, inner) { }
}

/// <summary>
/// The script spent the instruction budget the host granted it (<c>LYR-CAP0002</c>).
///
/// <para>A <see cref="ScriptPanicException"/> by inheritance, because that is what it is on the
/// runtime side and because a host that already catches panics keeps catching this. A type of its
/// own because the two mean different things to whoever handles them: a panic says the script is
/// broken, a spent budget says it was still working. A mod loader disables the first and may well
/// give the second a larger budget.</para>
/// </summary>
public sealed class ScriptBudgetException : ScriptPanicException
{
    internal ScriptBudgetException(string code, string message, Exception? inner)
        : base(code, message, inner) { }
}
