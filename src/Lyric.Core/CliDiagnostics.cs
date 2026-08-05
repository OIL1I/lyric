namespace Lyric.Core;

/// <summary>
/// Der <c>LYR-CLI####</c>-Katalog (ROADMAP §Diagnostik-Code-Bereiche, eingefuehrt in M0).
///
/// <para>Liegt in <c>Lyric.Core</c>, weil ihn seit ADR-017 <b>alle drei</b> Binaries brauchen —
/// <c>lyrc</c>, <c>lyrvm</c> und <c>lyric</c> — und Core der einzige gemeinsame Vorfahr ist.
/// <c>lyrvm</c> darf nichts Compiler-seitiges referenzieren, der Katalog kann also nicht in
/// <c>Lyric.Compiler</c> wohnen; drei Kopien waeren drei Wahrheiten darueber, was
/// <c>LYR-CLI0002</c> bedeutet.</para>
///
/// <para>Codes sind stabile Bezeichner: eine Nummer wird nicht neu vergeben, wenn ein Fall
/// wegfaellt.</para>
/// </summary>
public static class CliDiagnostics
{
    /// <summary>Eine angegebene Datei liess sich nicht lesen.</summary>
    public const string FileUnreadable = "LYR-CLI0001";

    /// <summary>Ein Kommando wurde ohne sein Pflicht-Argument gerufen.</summary>
    public const string MissingArgument = "LYR-CLI0002";

    /// <summary>Unbekanntes Kommando oder unbekannte Option.</summary>
    public const string UnknownCommand = "LYR-CLI0003";

    /// <summary>Die Datei hat die falsche Art fuer dieses Kommando — etwa <c>lyrvm run</c> auf
    /// einer <c>.lyr</c>-Quelle. Bewusst ein Fehler und keine stille Weiterleitung: eine Runtime,
    /// die Quelltext frisst, ist keine Runtime mehr (ADR-017).</summary>
    public const string WrongFileKind = "LYR-CLI0004";

    /// <summary>Die ueber <c>--vm</c> oder <c>LYRIC_VM</c> benannte Runtime existiert nicht.</summary>
    public const string VmNotFound = "LYR-CLI0005";

    /// <summary>Die Fremd-Runtime liess sich nicht starten.</summary>
    public const string VmLaunchFailed = "LYR-CLI0006";

    /// <summary>Programm-Argumente (<c>-- …</c>) wurden uebergeben, sind aber noch nicht
    /// einloesbar: <c>fn main(args: string[])</c> aus <c>Sprache.md</c> §11 ist nicht verdrahtet,
    /// <c>ModuleLowerer</c> nimmt nur ein parameterloses <c>main</c> als Einstieg. Sie werden
    /// abgelehnt statt still verworfen.</summary>
    public const string ProgramArgumentsUnsupported = "LYR-CLI0007";

    /// <summary>Die Ausgabedatei liess sich nicht schreiben.</summary>
    public const string OutputUnwritable = "LYR-CLI0008";

    /// <summary>Meldet eine positionslose CLI-Diagnose und rendert sie sofort — CLI-Fehler haengen
    /// an keinem Quelltext-Span, es gibt also nichts zu sammeln oder zu sortieren.</summary>
    public static int Fail(TextWriter error, string code, string message, int exitCode)
    {
        var engine = new DiagnosticEngine(new SourceManager());
        engine.Report(code, Severity.Error, default, message);
        engine.RenderText(error);
        return exitCode;
    }
}
