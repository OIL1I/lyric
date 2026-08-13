using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Lyric.Tests.Embedding;

/// <summary>
/// The gate: <c>examples/embedded-host/</c> runs.
///
/// <para>AS A PROCESS rather than in process, as in <c>Lyric.Tests.Cli</c>. The question is what a
/// foreign consumer sees — someone who has only <c>lyrembed.dll</c> and puts the stdlib beside it. In
/// process the test would run with the test context behind it and would not answer that.</para>
///
/// <para>WHY THE GATE IS MORE THAN "runs without crashing". The project rules require that someone can
/// clone the repository and DO SOMETHING. For an embedding API that means a host one can read and copy.
/// The test therefore checks the four statements of the example individually — that a sandbox runs, that
/// it prevents something, that a second VM beside it may do more, and that an error arrives as a
/// diagnostic.</para>
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
    /// The most important line of the example: the sandbox holds. Without that promise the host would be
    /// an example of how to run Lyric rather than of what a VM is for.
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
    /// The host CALLS functions and the state between the calls persists. Three calls with a growing
    /// result say both in one line; a single call would leave open whether the module constant is
    /// recomputed every time.
    /// </summary>
    [Fact]
    public void The_example_host_calls_into_the_script_and_keeps_its_state()
    {
        var (_, output) = RunHost();

        Assert.Contains("treffer(10) = 10", output, StringComparison.Ordinal);
        Assert.Contains("treffer(5)  = 15", output, StringComparison.Ordinal);
        Assert.Contains("treffer(1)  = 16", output, StringComparison.Ordinal);
    }

    /// <summary>A value that does not fit losslessly across the boundary is rejected rather than
    /// truncated, and the example shows it rather than concealing it.</summary>
    [Fact]
    public void The_example_host_shows_a_value_refused_at_the_boundary()
    {
        var (_, output) = RunHost();
        Assert.Contains("LYR-EMB0005", output, StringComparison.Ordinal);
    }

    /// <summary>The script calls back into the host, and the host sees the side effect.</summary>
    [Fact]
    public void The_example_host_lets_the_script_call_back_into_it()
    {
        var (_, output) = RunHost();

        // The generated declaration stands in the example: it is the answer to "what signature does my
        // function have in Lyric?".
        Assert.Contains("pub fn playSound(name: string): void;", output, StringComparison.Ordinal);
        Assert.Contains("explodieren(8) = 4", output, StringComparison.Ordinal);
        Assert.Contains("gespielt: boom, nachhall", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A host object with methods in the example, and the host sees the effect on its own object.
    /// </summary>
    [Fact]
    public void The_example_host_shows_a_host_object_with_methods()
    {
        var (_, output) = RunHost();

        // The generated class stands in the example, including the ';,' spelling the grammar requires for
        // a bodyless method.
        Assert.Contains("pub mut fn schaden(wieviel: int): void;,", output,
            StringComparison.Ordinal);

        Assert.Contains("runde(30) -> 70", output, StringComparison.Ordinal);
        Assert.Contains("runde(12) -> 58", output, StringComparison.Ordinal);

        // The host sees the same number on its own object: the identity promise, made visible in the
        // example.
        Assert.Contains("der Host sieht: 58", output, StringComparison.Ordinal);
    }

    /// <summary>And the example also shows what the script may NOT do.</summary>
    [Fact]
    public void The_example_host_shows_a_host_type_cannot_be_constructed()
    {
        var (_, output) = RunHost();
        Assert.Contains("LYR-SEM0061", output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_example_host_shows_a_compile_error_as_a_diagnostic()
    {
        var (_, output) = RunHost();
        Assert.Contains("LYR-SEM0002", output, StringComparison.Ordinal);
    }
}
