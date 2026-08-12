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
}

/// <summary>
/// The script panicked: division by zero, a force-unwrap of an absent value, an explicit
/// <c>panic</c>.
///
/// <para>Separate from <see cref="ScriptException"/>, which covers a script the host is not
/// permitted or able to run.</para>
/// </summary>
public sealed class ScriptPanicException : ScriptException
{
    internal ScriptPanicException(string code, string message, Exception? inner)
        : base(code, message, inner) { }
}
