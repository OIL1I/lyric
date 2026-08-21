using System.Text.Json;
using Lyric.Core;

namespace Lyric.Tests.Cli;

/// <summary>
/// The shared option and output layer: <c>--json</c>, <c>--quiet</c>, <c>--verbose</c> and the
/// progress display.
///
/// <para>The most important case is <see cref="Progress_never_touches_stdout"/>. Everything else here
/// is convenience; that one is the runner contract.</para>
/// </summary>
public sealed class OutputTests
{
    /// <summary>Forces the animated path a redirected stream would otherwise never take; without this
    /// switch exactly the promises that can break would stay untested.</summary>
    private const string ForceProgress = "--progress";

    // ---------------------------------------------------------------- progress and stdout

    [Theory]
    [MemberData(nameof(CommandTests.RunnableExamples), MemberType = typeof(CommandTests))]
    public void Progress_never_touches_stdout(string example, int expected, bool _)
    {
        // The promise everything hangs on: the display lives on stderr. Were it on stdout, no tool could
        // read the output of a Lyric program mechanically any more.
        var quiet = Toolchain.Lyric("run", Toolchain.Example(example), ForceProgress, "never");
        var loud = Toolchain.Lyric("run", Toolchain.Example(example), ForceProgress, "always");

        Assert.Equal(quiet.StdOut, loud.StdOut);
        Assert.Equal(quiet.ExitCode, loud.ExitCode);

        // And both deliver what is promised. Without this line two equally broken runs would be green:
        // the comparison alone says only that '--progress' changes nothing, not that the program runs.
        Assert.Equal(expected, quiet.ExitCode);
    }

    [Fact]
    public void Program_output_starts_clean_even_with_progress_on()
    {
        // The line has to be cleared BEFORE the first instruction runs, or it sticks in front of the
        // first println.
        var result = Toolchain.Lyric(
            "run", Toolchain.Example("hello.lyr"), ForceProgress, "always");

        Assert.Equal("Hello, Lyric!\n", result.Out);
    }

    [Fact]
    public void Progress_stays_silent_below_the_display_threshold()
    {
        // 'lyrvm info' is faster than the threshold: it reads a file and prints counters. Nothing may
        // appear despite --progress always.
        using var module = Toolchain.Temp(".lyrbc");
        Toolchain.Lyrc("build", Toolchain.Example("hello.lyr"), "-o", module.Path);

        var result = Toolchain.Lyrvm("info", module.Path, ForceProgress, "always");

        Assert.Equal("", result.Err);
    }

    [Fact]
    public void Progress_is_off_when_stderr_is_redirected()
    {
        // The default path in exactly the situation these tests run in: redirected, therefore silent.
        var result = Toolchain.Lyric("run", Toolchain.Example("enums.lyr"));

        Assert.Equal("", result.Err);
    }

    // ---------------------------------------------------------------- --verbose

    /// <summary>
    /// The table lists exactly the phases this build runs, in pipeline order, with a separator and a sum.
    ///
    /// <para>The expectation comes from <see cref="Pipeline.OfThisBuild"/> rather than as a literal:
    /// <c>verify</c> runs in debug builds only, and a written-out <c>"verify"</c> would make the test
    /// depend on the build configuration — green in debug, red in release, which is what CI and the
    /// shipped artifact use.</para>
    /// </summary>
    [Fact]
    public void Verbose_lists_every_phase_in_pipeline_order_with_a_total()
    {
        using var module = Toolchain.Temp(".lyrbc");
        var result = Toolchain.Lyrc("build", Toolchain.Example("hello.lyr"),
            "-o", module.Path, "--verbose");

        var phases = result.Err.Split('\n')
            .Select(line => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length > 0)
            .Select(parts => parts[0])
            .ToArray();

        string[] expected =
        [
            .. Pipeline.OfThisBuild.Select(PhaseNames.Short),
            "-----",
            "total",
        ];

        Assert.Equal(expected,
            phases.Select(p => p.StartsWith("---", StringComparison.Ordinal) ? "-----" : p).ToArray());
    }

    /// <summary>
    /// The verifier is the only phase a build may skip, and a release build does.
    ///
    /// <para>Without this test the one above would stay green even if <see cref="Pipeline.OfThisBuild"/>
    /// returned the same in BOTH configurations: it compares the output against the same list it arises
    /// from. Here the statement itself stands, against the <c>#if</c> the compiler really saw.</para>
    /// </summary>
    [Fact]
    public void Only_a_debug_build_runs_the_verifier()
    {
        var verifies = Pipeline.OfThisBuild.Contains(Phase.Verify);

#if DEBUG
        Assert.True(verifies);
#else
        Assert.False(verifies);
#endif
    }

    [Fact]
    public void Verbose_uses_invariant_number_formatting()
    {
        // A German locale would write "9,2 ms". The number before "ms" is checked deliberately: a comma
        // elsewhere is legitimate, since the module list is comma-separated.
        var result = Toolchain.Lyrc("check", Toolchain.Example("hello.lyr"), "--verbose");

        var durations = System.Text.RegularExpressions.Regex
            .Matches(result.Err, @"(\S+) ms")
            .Select(m => m.Groups[1].Value)
            .ToArray();

        Assert.NotEmpty(durations);
        Assert.All(durations, d => Assert.Matches(@"^\d+\.\d$", d));
    }

    [Fact]
    public void Verbose_goes_to_stderr_not_stdout()
    {
        var result = Toolchain.Lyrc("check", Toolchain.Example("hello.lyr"), "--verbose");

        Assert.DoesNotContain("ms", result.Out);
        Assert.Contains("ms", result.Err);
    }

    // ---------------------------------------------------------------- --json

    [Fact]
    public void Json_diagnostics_parse_as_json_and_carry_the_code()
    {
        using var source = Toolchain.Temp(".lyr");
        File.WriteAllText(source.Path, "fn main(): int { return \"not an int\"; }");

        var result = Toolchain.Lyrc("check", source.Path, "--json");

        Assert.Equal(ExitCodes.Failure, result.ExitCode);

        // Parsed rather than checked by substring, or the test would check the formatting.
        using var document = JsonDocument.Parse(result.Err);
        var diagnostics = document.RootElement.GetProperty("diagnostics");
        Assert.True(diagnostics.GetArrayLength() > 0);
        Assert.StartsWith("LYR-SEM", diagnostics[0].GetProperty("code").GetString());
    }

    [Fact]
    public void Json_is_honoured_by_every_binary_alike()
    {
        // The reason the option layer lives in Lyric.Core: three copies would guarantee a path on which
        // --json silently yields plain text.
        using var source = Toolchain.Temp(".lyr");
        File.WriteAllText(source.Path, "fn main(): int { return \"not an int\"; }");

        foreach (var run in new[]
                 {
                     Toolchain.Lyrc("check", source.Path, "--json"),
                     Toolchain.Lyric("check", source.Path, "--json"),
                 })
        {
            Assert.Equal(ExitCodes.Failure, run.ExitCode);
            using var document = JsonDocument.Parse(run.Err);
            Assert.True(document.RootElement.GetProperty("diagnostics").GetArrayLength() > 0);
        }
    }

    // ---------------------------------------------------------------- --quiet

    [Fact]
    public void Quiet_suppresses_success_chatter()
    {
        using var module = Toolchain.Temp(".lyrbc");

        var loud = Toolchain.Lyrc("build", Toolchain.Example("hello.lyr"), "-o", module.Path);
        var quiet = Toolchain.Lyrc("build", Toolchain.Example("hello.lyr"), "-o", module.Path, "-q");

        Assert.Contains("bytes", loud.Out);
        Assert.Equal("", quiet.Out);
        Assert.Equal(ExitCodes.Success, quiet.ExitCode);
    }

    [Fact]
    public void Quiet_does_not_suppress_diagnostics()
    {
        // The counter-check, and the actual test: a --quiet that swallows errors would be dangerous rather
        // than quiet.
        using var source = Toolchain.Temp(".lyr");
        File.WriteAllText(source.Path, "fn main(): int { return \"not an int\"; }");

        var result = Toolchain.Lyrc("check", source.Path, "--quiet");

        Assert.Equal(ExitCodes.Failure, result.ExitCode);
        Assert.Contains("LYR-SEM", result.Err);
    }

    [Fact]
    public void Quiet_does_not_suppress_requested_payload()
    {
        // A dump is payload rather than chatter.
        using var module = Toolchain.Temp(".lyrbc");
        Toolchain.Lyrc("build", Toolchain.Example("arith.lyr"), "-o", module.Path);

        var result = Toolchain.Lyrvm("disasm", module.Path, "--quiet");

        Assert.Contains("module (format", result.Out);
    }

    [Fact]
    public void Unknown_progress_mode_is_a_usage_error()
    {
        var result = Toolchain.Lyric("run", Toolchain.Example("hello.lyr"), "--progress", "sometimes");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
    }

    [Fact]
    public void Options_after_the_separator_belong_to_the_program()
    {
        // A '--quiet' behind '--' is an argument of the Lyric program rather than an option of the
        // toolchain. Recognisable by the output of hello.lyr NOT being suppressed: read as an option, it
        // would stay away.
        var result = Toolchain.Lyric("run", Toolchain.Example("hello.lyr"), "--", "--quiet");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Hello, Lyric!", result.Out);
    }

    // ---------------------------------------------------------------- lyrc --stdlib

    [Fact]
    public void Stdlib_flag_beats_the_environment_variable()
    {
        // The variable points into nothing, the flag at the real stdlib. If it compiles all the same, the
        // flag won — the same precedence as for --vm and LYRIC_VM.
        var result = Toolchain.RunWithEnvironment(Toolchain.LyrcPath,
            new Dictionary<string, string?> { ["LYRIC_STDLIB"] = "/nonexistent/stdlib" },
            "check", Toolchain.Example("hello.lyr"),
            "--stdlib", Path.Combine(Toolchain.RepositoryRoot, "stdlib"));

        Assert.Equal(ExitCodes.Success, result.ExitCode);
    }

    [Fact]
    public void A_missing_stdlib_is_reported_rather_than_ignored()
    {
        // The counter-check to the test above: an unfindable module used to pass silently.
        var result = Toolchain.Lyrc("check", Toolchain.Example("hello.lyr"),
            "--stdlib", "/nonexistent/stdlib");

        Assert.Equal(ExitCodes.Failure, result.ExitCode);
        Assert.Contains("LYR-RES", result.Err);
    }

    // ---------------------------------------------------------------- the encoding contract

    [Fact]
    public void Non_ascii_output_survives_a_redirected_stream()
    {
        // A redirected stream is UTF-8, whatever code page the console this process happens to be
        // attached to carries. Without that, Windows best-fit-maps on the way out: the em dash of
        // this very hint left one tool as a hyphen and another intact, which is how a comparison
        // between two tool runs could fail on a busy machine and nowhere else.
        using var file = Toolchain.Temp(".lyr");
        File.WriteAllText(file.Path, "fn main(): int {\n    var n = 1;\n    return n;\n}\n");

        var result = Toolchain.Lyrc("check", file.Path);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("LYR-SEM0075", result.Err);
        Assert.Contains("—", result.Err);
    }

    [Fact]
    public void The_two_runtimes_render_a_diagnostic_identically()
    {
        // The shape of the sporadic failure this pins: one path runs the program in the driver,
        // the other spawns lyrvm. Same text, two writers — and for months, occasionally, two
        // encodings.
        var inProcess = Toolchain.Lyric("run", Toolchain.Example("hello.lyr"), "--verbose");
        var spawned = Toolchain.Lyric("run", Toolchain.Example("hello.lyr"), "--verbose",
            "--vm", Toolchain.LyrvmPath);

        Assert.Equal(inProcess.Out, spawned.Out);
    }
}
