namespace Lyric.Tests.Cli;

/// <summary>
/// The <c>--deny-warnings</c> gate: warnings never fail a run by themselves, the flag makes them
/// fail it, and the diagnostics keep their severity either way — the policy lives in the exit
/// code and one closing error, not in a relabeling.
///
/// <para>The warning source here is the one the toolchain has today: a <c>lyric.json</c> key
/// nobody knows. When the compiler learns to warn about programs, these tests stay right —
/// they test the gate, not the source.</para>
/// </summary>
public sealed class DenyWarningsTests
{
    private const string Program = "fn main(): int { return 0; }\n";

    private static TemporaryDirectory ProjectWithSuspectKey()
    {
        var directory = Toolchain.TempDirectory();
        directory.Write("lyric.json", "{ \"definitelyNotAKey\": true }\n");
        directory.Write("main.lyr", Program);
        return directory;
    }

    [Fact]
    public void A_warning_alone_does_not_fail_the_check()
    {
        using var project = ProjectWithSuspectKey();
        var result = Toolchain.Lyrc("check", Path.Combine(project.Path, "main.lyr"));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("warning[LYR-CLI0017]", result.Err);
        Assert.Contains("unknown key 'definitelyNotAKey'", result.Err);
    }

    [Fact]
    public void Deny_warnings_turns_the_same_run_into_a_failure()
    {
        using var project = ProjectWithSuspectKey();
        var result = Toolchain.Lyrc("check", Path.Combine(project.Path, "main.lyr"),
            "--deny-warnings");

        Assert.Equal(1, result.ExitCode);

        // The warning keeps its severity; the policy arrives as one error at the end.
        Assert.Contains("warning[LYR-CLI0017]", result.Err);
        Assert.Contains("error[LYR-CLI0016]", result.Err);
        Assert.Contains("1 warning denied by --deny-warnings", result.Err);
    }

    [Fact]
    public void A_clean_run_passes_the_gate()
    {
        using var project = Toolchain.TempDirectory();
        var file = project.Write("main.lyr", Program);
        var result = Toolchain.Lyrc("check", file, "--deny-warnings");

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("LYR-CLI0016", result.Err);
    }

    [Fact]
    public void A_denied_build_writes_no_artifact()
    {
        using var project = ProjectWithSuspectKey();
        var output = Path.Combine(project.Path, "main.lyrbc");
        var result = Toolchain.Lyrc("build", Path.Combine(project.Path, "main.lyr"),
            "-o", output, "--deny-warnings");

        Assert.Equal(1, result.ExitCode);
        Assert.False(File.Exists(output),
            "a build that failed the warning gate must not leave an artifact behind");
    }

    [Fact]
    public void Errors_still_win_over_the_gate()
    {
        // A run with errors fails as a compile failure; the gate never speaks.
        using var project = ProjectWithSuspectKey();
        project.Write("broken.lyr", "fn main(): int { return }\n");
        var result = Toolchain.Lyrc("check", Path.Combine(project.Path, "broken.lyr"),
            "--deny-warnings");

        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain("LYR-CLI0016", result.Err);
    }
}
