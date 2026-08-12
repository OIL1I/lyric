namespace Lyric.Bytecode;

/// <summary>
/// Diagnostic codes of the bytecode reader (<c>LYR-BC####</c>).
///
/// <para>They are permanent error classes of a file rather than temporary gaps in the backend, so
/// there are several rather than one.</para>
///
/// <para>All describe load time: a module is validated completely before it runs, and each of
/// these is a reason not to accept it at all.</para>
/// </summary>
public static class BytecodeDiagnostics
{
    /// <summary>The file does not begin with <c>LYRB</c>.</summary>
    public const string BadMagic = "LYR-BC0001";

    /// <summary>Unknown major version. Before v1.0 there is no migration path.</summary>
    public const string UnsupportedVersion = "LYR-BC0002";

    /// <summary>The file ends inside a structure, or a section length does not match its
    /// contents.</summary>
    public const string Truncated = "LYR-BC0003";

    /// <summary>An index is out of range: string pool, function, block or local slot.</summary>
    public const string IndexOutOfRange = "LYR-BC0004";

    /// <summary>Unknown opcode, type tag or section layout.</summary>
    public const string UnknownEncoding = "LYR-BC0005";

    /// <summary>Stack discipline violated: underflow, depth ≠ 0 at a block boundary, or more than
    /// the maximum announced in the function header.</summary>
    public const string StackDiscipline = "LYR-BC0006";
}

/// <summary>
/// A file that is not a valid module. It carries the diagnostic code, so the public entry point
/// can build a diagnostic without parsing the text.
///
/// <para>Not an <c>InternalCompilationException</c>: a broken file is not a compiler bug, and the
/// reader has to be robust on arbitrary bytes.</para>
/// </summary>
public sealed class MalformedBytecodeException : Exception
{
    public string Code { get; }

    public MalformedBytecodeException(string code, string message) : base(message) => Code = code;
}
