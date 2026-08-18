namespace Lyric.Core;

/// <summary>
/// The <c>LYR-CLI####</c> catalogue.
///
/// <para>It lives in <c>Lyric.Core</c>, the only project every binary shares; <c>lyrvm</c>
/// references nothing compiler-side.</para>
///
/// <para>Codes are stable identifiers: a number is not reused when a case disappears.</para>
/// </summary>
public static class CliDiagnostics
{
    /// <summary>A given file could not be read.</summary>
    public const string FileUnreadable = "LYR-CLI0001";

    /// <summary>A command was invoked without its required argument.</summary>
    public const string MissingArgument = "LYR-CLI0002";

    /// <summary>Unknown command or option.</summary>
    public const string UnknownCommand = "LYR-CLI0003";

    /// <summary>The file has the wrong kind for this command, such as <c>lyrvm run</c> on a
    /// <c>.lyr</c> source. An error rather than a silent forward.</summary>
    public const string WrongFileKind = "LYR-CLI0004";

    /// <summary>The runtime named by <c>--vm</c> or <c>LYRIC_VM</c> does not exist.</summary>
    public const string VmNotFound = "LYR-CLI0005";

    /// <summary>The external runtime could not be started.</summary>
    public const string VmLaunchFailed = "LYR-CLI0006";

    // LYR-CLI0007 is retired. The number stays taken and is not reused: a reused diagnostic
    // number would make every older report of it wrong.

    /// <summary>The output file could not be written.</summary>
    public const string OutputUnwritable = "LYR-CLI0008";

    /// <summary>A function named by <c>--function</c> is not in the module. An error rather than
    /// empty output, which would read as an empty function.</summary>
    public const string UnknownFunction = "LYR-CLI0009";

    /// <summary>No <c>build.lyr</c> in the directory a build was pointed at.</summary>
    public const string NoBuildScript = "LYR-CLI0011";

    /// <summary>The build script ran and did not finish its job: it panicked, has no <c>build</c>
    /// function, or declared nothing to compile. Distinct from a script that does not compile,
    /// which reports its own diagnostics with real spans.</summary>
    public const string BuildScriptFailed = "LYR-CLI0012";

    /// <summary>A <c>lyric.json</c> was found and could not be understood. Not finding one is
    /// normal and silent; finding a broken one stops the build, because carrying on would compile
    /// against a module root the file was trying to change.</summary>
    public const string BadProjectFile = "LYR-CLI0010";

    /// <summary>The stub was started directly: the executable carries no program. Not an
    /// accident of a damaged pack, which is <see cref="PackDamaged"/>.</summary>
    public const string StubEmpty = "LYR-CLI0013";

    /// <summary>A pack footer is present and does not hold together: wrong footer version, or a
    /// payload length that reaches outside the file. A truncated download looks like this.</summary>
    public const string PackDamaged = "LYR-CLI0014";

    /// <summary>No stub to pack into: the resolution ladder (<c>--stub</c>, <c>LYRIC_STUB</c>,
    /// <c>stubs/&lt;rid&gt;/</c> beside the binary) ended empty-handed.</summary>
    public const string StubNotFound = "LYR-CLI0015";

    /// <summary>Reports a CLI diagnostic and renders it immediately. It has no source span, so
    /// there is nothing to collect or order.</summary>
    public static int Fail(TextWriter error, string code, string message, int exitCode)
    {
        var engine = new DiagnosticEngine(new SourceManager());
        engine.Report(code, Severity.Error, default, message);
        engine.RenderText(error);
        return exitCode;
    }
}
