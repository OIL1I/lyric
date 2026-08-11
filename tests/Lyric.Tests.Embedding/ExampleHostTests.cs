using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Lyric.Tests.Embedding;

/// <summary>
/// Das Gate von M10/E1: <c>examples/embedded-host/</c> laeuft.
///
/// <para><b>Als Prozess und nicht in-process</b>, wie bei <c>Lyric.Tests.Cli</c>. Die Frage ist,
/// was ein fremder Konsument sieht — jemand, der nur <c>lyrembed.dll</c> hat und die Stdlib
/// danebenlegt. In-process liefe der Test mit dem Testkontext im Ruecken und beantwortete sie
/// nicht.</para>
///
/// <para><b>Warum das Gate mehr ist als „laeuft ohne Absturz".</b> CONTRIBUTING Rule 3 verlangt,
/// dass jemand das Repo klonen und <i>etwas tun</i> kann. Fuer eine Embedding-API heisst das: ein
/// Host, den man lesen und abschreiben kann. Der Test prueft deshalb die vier Aussagen des
/// Beispiels einzeln — dass eine Sandbox laeuft, dass sie etwas verhindert, dass eine zweite VM
/// daneben mehr darf, und dass ein Fehler als Diagnose ankommt.</para>
/// </summary>
public class ExampleHostTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string Configuration =>
        AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";

    private static (int Exit, string Out) RunHost()
    {
        var name = OperatingSystem.IsWindows() ? "embedded-host.exe" : "embedded-host";
        var path = Path.Combine(RepoRoot(), "examples", "embedded-host", "bin",
            Configuration, "net10.0", name);

        Assert.True(File.Exists(path),
            $"the example host was not built at {path} — the test project references it, so this "
            + "means the build layout changed.");

        using var process = Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(path)!,
        })!;

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        Assert.Equal("", stderr.Result);
        return (process.ExitCode, stdout.Result.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void The_example_host_runs_a_sandboxed_script()
    {
        var (exit, output) = RunHost();

        Assert.Equal(0, exit);
        Assert.Contains("hallo vom Skript", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Die wichtigste Zeile des Beispiels: die Sandbox haelt. Ohne diese Zusage waere der Host ein
    /// Beispiel dafuer, wie man Lyric laufen laesst — und nicht dafuer, wofuer es eine VM gibt.
    /// </summary>
    [Fact]
    public void The_example_host_shows_a_capability_being_denied()
    {
        var (_, output) = RunHost();

        Assert.Contains("LYR-CAP0001", output, StringComparison.Ordinal);
        Assert.Contains("fileAccess", output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_example_host_shows_a_second_vm_with_more_rights()
    {
        var (_, output) = RunHost();
        Assert.Contains("gibt es geheim.txt? false", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Das E2-Gate: der Host <b>ruft</b> Funktionen, und der Zustand zwischen den Aufrufen bleibt.
    /// Drei Aufrufe mit wachsendem Ergebnis sagen beides in einer Zeile — ein einzelner Aufruf
    /// liesse offen, ob die Modul-Konstante jedes Mal neu berechnet wird.
    /// </summary>
    [Fact]
    public void The_example_host_calls_into_the_script_and_keeps_its_state()
    {
        var (_, output) = RunHost();

        Assert.Contains("treffer(10) = 10", output, StringComparison.Ordinal);
        Assert.Contains("treffer(5)  = 15", output, StringComparison.Ordinal);
        Assert.Contains("treffer(1)  = 16", output, StringComparison.Ordinal);
    }

    /// <summary>Ein Wert, der nicht verlustfrei ueber die Grenze passt, wird abgelehnt statt
    /// abgeschnitten — und das Beispiel zeigt es, statt es zu verschweigen.</summary>
    [Fact]
    public void The_example_host_shows_a_value_refused_at_the_boundary()
    {
        var (_, output) = RunHost();
        Assert.Contains("LYR-EMB0005", output, StringComparison.Ordinal);
    }

    /// <summary>Das E3-Gate: das Skript ruft zurueck in den Host, und der Host sieht den
    /// Seiteneffekt.</summary>
    [Fact]
    public void The_example_host_lets_the_script_call_back_into_it()
    {
        var (_, output) = RunHost();

        // Die erzeugte Deklaration steht im Beispiel — sie ist die Antwort auf "welche Signatur
        // hat meine Funktion in Lyric?".
        Assert.Contains("pub fn playSound(name: string): void;", output, StringComparison.Ordinal);
        Assert.Contains("explodieren(8) = 4", output, StringComparison.Ordinal);
        Assert.Contains("gespielt: boom, nachhall", output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_example_host_shows_a_compile_error_as_a_diagnostic()
    {
        var (_, output) = RunHost();
        Assert.Contains("LYR-SEM0002", output, StringComparison.Ordinal);
    }
}
