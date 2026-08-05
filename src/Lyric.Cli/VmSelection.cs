using System.Diagnostics;
using Lyric.Core;

namespace Lyric.Cli;

/// <summary>
/// Welche Runtime fuehrt aus — die mitgelieferte oder eine fremde?
///
/// <para>Gestaffelt: <c>--vm &lt;pfad&gt;</c> schlaegt <c>LYRIC_VM</c> schlaegt „mitgeliefert"
/// (ADR-017). Das ist kein neues Muster, sondern dasselbe wie beim bereits existierenden
/// <c>LYRIC_STDLIB</c> in <c>StdlibLoader</c>.</para>
///
/// <para>Bewusst <b>kein</b> Registry (<c>lyric vm add …</c>) und keine Konfigurationsdatei:
/// persistente Konfiguration zoege Projektdateien nach sich, und die ein Projektsystem. Ein Pfad
/// in einer Variablen erledigt denselben Job zustandsfrei.</para>
/// </summary>
public sealed record VmSelection(string? ExecutablePath)
{
    /// <summary>Die mitgelieferte Runtime laeuft in-process. Das spart auf dem haeufigsten
    /// Kommando einen zweiten .NET-Prozessstart (~50–70 ms) — bei einer Sprache fuer CLI-Tools
    /// und Game-Scripting ist das der falsche Preis fuer Symmetrie.</summary>
    public bool IsBundled => ExecutablePath is null;

    /// <summary>Der Name fuer Meldungen.</summary>
    public string Display => ExecutablePath ?? "bundled";

    /// <summary>
    /// Liest <c>--vm</c> aus den Argumenten, sonst <c>LYRIC_VM</c>, sonst mitgeliefert. Liefert
    /// die Argumente ohne das Flag zurueck, damit die Kommandos es nicht selbst herausfiltern
    /// muessen.
    /// </summary>
    public static (VmSelection Vm, string[] Remaining, string? Error) Parse(string[] args)
    {
        string? path = null;
        var rest = new List<string>(args.Length);

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--") { rest.AddRange(args[i..]); break; }

            if (args[i] is not "--vm") { rest.Add(args[i]); continue; }

            if (i + 1 >= args.Length) return (Bundled, [], "--vm: missing path argument");
            path = args[++i];
        }

        path ??= Environment.GetEnvironmentVariable(EnvironmentVariable) is { } configured
                 && !string.IsNullOrWhiteSpace(configured)
            ? configured
            : null;

        return (new VmSelection(path), rest.ToArray(), null);
    }

    /// <summary>Umgebungsvariable, die die Runtime waehlt. Analog zu <c>LYRIC_STDLIB</c>.</summary>
    public const string EnvironmentVariable = "LYRIC_VM";

    public static VmSelection Bundled => new((string?)null);

    /// <summary>
    /// Startet die Fremd-Runtime nach dem Runner-Vertrag: <c>&lt;vm&gt; run &lt;datei.lyrbc&gt;</c>,
    /// Stroeme geerbt, Exit-Code durchgereicht.
    ///
    /// <para>stdin/stdout/stderr werden <b>nicht</b> umgeleitet. Wuerde man puffern, um die
    /// Ausgabe nachzubearbeiten, verloere ein Lyric-Programm seine Interaktivitaet und seine
    /// Ausgabereihenfolge relativ zu stderr — beides Teil dessen, was der Vertrag zusichert.</para>
    /// </summary>
    public int RunForeign(string modulePath, IReadOnlyList<string> programArguments, TextWriter error)
    {
        Debug.Assert(!IsBundled);

        var info = new ProcessStartInfo(ExecutablePath!) { UseShellExecute = false };
        info.ArgumentList.Add("run");
        info.ArgumentList.Add(modulePath);
        if (programArguments.Count > 0)
        {
            info.ArgumentList.Add("--");
            foreach (var argument in programArguments) info.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(info);
            if (process is null)
                return CliDiagnostics.Fail(error, CliDiagnostics.VmLaunchFailed,
                    $"could not start runtime: {ExecutablePath}", ExitCodes.Failure);

            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            return CliDiagnostics.Fail(error, CliDiagnostics.VmLaunchFailed,
                $"could not start runtime '{ExecutablePath}': {ex.Message}", ExitCodes.Failure);
        }
    }

    /// <summary>Prueft vorab, dass eine benannte Fremd-Runtime ueberhaupt existiert — sonst
    /// scheitert der Lauf erst nach dem Compilieren, und die Meldung kaeme aus dem
    /// Prozess-Start statt aus der Konfiguration.</summary>
    public bool Exists() => IsBundled || File.Exists(ExecutablePath);
}
