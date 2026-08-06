using System.Diagnostics;
using Lyric.Core;

namespace Lyric.Cli;

/// <summary>
/// Ein Werkzeug der Suite — <c>lyrc</c>, <c>lyrvm</c>, spaeter <c>lyrtest</c> (ADR-019).
///
/// <para><c>lyric</c> selbst uebersetzt und fuehrt nichts aus; es waehlt Werkzeuge, uebersetzt
/// bequeme Kommandos in technische und reicht durch, was zurueckkommt. Was die Werkzeuge koennen,
/// steht in ihnen und nicht hier ein zweites Mal.</para>
/// </summary>
public sealed record Tool(string Name, string Flag, string EnvironmentVariable)
{
    /// <summary>Der Compiler: Quelltext zu <c>.lyrbc</c>.</summary>
    public static readonly Tool Compiler = new("lyrc", "--compiler", "LYRIC_COMPILER");

    /// <summary>Die Runtime: fuehrt ein <c>.lyrbc</c> aus.</summary>
    public static readonly Tool Runtime = new("lyrvm", "--vm", "LYRIC_VM");

    public static readonly IReadOnlyList<Tool> All = [Compiler, Runtime];

    /// <summary>
    /// Wo das Werkzeug liegt: <c>--flag &lt;pfad&gt;</c> schlaegt Umgebungsvariable schlaegt „neben
    /// der eigenen exe".
    ///
    /// <para>Dieselbe Staffelung fuer jedes Werkzeug, und dieselbe wie bisher fuer die Runtime
    /// allein. <b>Warum ueberhaupt austauschbar</b>: ADR-013 sieht ausdruecklich vor, dass jemand
    /// eine zweite Runtime allein aus der Spec schreibt; fuer den Compiler gilt dasselbe, sobald
    /// die Sprache formell spezifiziert ist (post-v1). Ein Sonderweg nur fuer die Runtime waere
    /// heute bequemer und spaeter im Weg.</para>
    ///
    /// <para>Bewusst <b>kein</b> Registry und keine Konfigurationsdatei: persistente Konfiguration
    /// zoege Projektdateien nach sich, und die ein Projektsystem. Ein Pfad in einer Variablen
    /// erledigt denselben Job zustandsfrei.</para>
    /// </summary>
    public string Resolve(string? fromFlag)
    {
        if (!string.IsNullOrWhiteSpace(fromFlag)) return fromFlag;

        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        return Path.Combine(AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? $"{Name}.exe" : Name);
    }

    /// <summary>Der Name fuer Meldungen: „mitgeliefert" oder der Pfad, den jemand gewaehlt hat.</summary>
    public string Display(string? fromFlag) =>
        string.IsNullOrWhiteSpace(fromFlag)
        && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable))
            ? "bundled"
            : Resolve(fromFlag);

    /// <summary>
    /// Startet das Werkzeug und wartet.
    ///
    /// <para>stdin/stdout/stderr werden <b>nicht</b> umgeleitet, sondern geerbt. Das ist keine
    /// Bequemlichkeit: ein umgeleiteter Strom ist kein Terminal, und damit verlieren die Werkzeuge
    /// TTY-Erkennung, Farben und die Fortschrittszeile — alles, was in der Progress-Slice gebaut
    /// wurde. Ein Lyric-Programm verloere zusaetzlich seine Interaktivitaet und die
    /// Ausgabereihenfolge relativ zu stderr, und beides sichert der Runner-Vertrag zu
    /// (docs/Bytecode.md §9).</para>
    /// </summary>
    public static int Run(string executable, IEnumerable<string> arguments, TextWriter error)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(info);
            if (process is null)
                return CliDiagnostics.Fail(error, CliDiagnostics.VmLaunchFailed,
                    $"could not start '{executable}'", ExitCodes.Failure);

            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            return CliDiagnostics.Fail(error, CliDiagnostics.VmLaunchFailed,
                $"could not start '{executable}': {ex.Message}", ExitCodes.Failure);
        }
    }
}
