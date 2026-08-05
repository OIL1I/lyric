using System.Text.Json;
using Lyric.Core;

namespace Lyric.Tests.Cli;

/// <summary>
/// Die gemeinsame Options- und Ausgabe-Schicht: <c>--json</c>, <c>--quiet</c>, <c>--verbose</c>
/// und der Fortschritt.
///
/// <para>Der wichtigste Fall ist <see cref="Progress_never_touches_stdout"/>. Alles andere hier
/// ist Bequemlichkeit; das ist der Runner-Vertrag.</para>
/// </summary>
public sealed class OutputTests
{
    /// <summary>Erzwingt den animierten Pfad, den ein umgeleiteter Strom sonst nie nimmt — ohne
    /// diesen Schalter blieben genau die Zusagen ungetestet, die kaputtgehen koennen.</summary>
    private const string ForceProgress = "--progress";

    // ---------------------------------------------------------------- Fortschritt und stdout

    [Theory]
    [MemberData(nameof(CommandTests.RunnableExamples), MemberType = typeof(CommandTests))]
    public void Progress_never_touches_stdout(string example, int _)
    {
        // Die Zusage, an der alles haengt: die Anzeige lebt auf stderr. Waere sie auf stdout,
        // koennte kein Werkzeug die Ausgabe eines Lyric-Programms mehr maschinell lesen.
        var quiet = Toolchain.Lyric("run", Toolchain.Example(example), ForceProgress, "never");
        var loud = Toolchain.Lyric("run", Toolchain.Example(example), ForceProgress, "always");

        Assert.Equal(quiet.StdOut, loud.StdOut);
        Assert.Equal(quiet.ExitCode, loud.ExitCode);
    }

    [Fact]
    public void Program_output_starts_clean_even_with_progress_on()
    {
        // Die Zeile muss geloescht sein, BEVOR die erste Instruktion laeuft. Sonst klebt sie vor
        // dem ersten println.
        var result = Toolchain.Lyric(
            "run", Toolchain.Example("hello.lyr"), ForceProgress, "always");

        Assert.Equal("Hello, Lyric!\n", result.Out);
    }

    [Fact]
    public void Progress_stays_silent_below_the_display_threshold()
    {
        // 'lyrvm info' ist schneller als die Schwelle: es liest eine Datei und druckt Zaehler.
        // Also darf trotz --progress always nichts erscheinen.
        using var module = Toolchain.Temp(".lyrbc");
        Toolchain.Lyrc("build", Toolchain.Example("hello.lyr"), "-o", module.Path);

        var result = Toolchain.Lyrvm("info", module.Path, ForceProgress, "always");

        Assert.Equal("", result.Err);
    }

    [Fact]
    public void Progress_is_off_when_stderr_is_redirected()
    {
        // Der Default-Pfad in genau der Lage, in der diese Tests laufen: umgeleitet, also still.
        var result = Toolchain.Lyric("run", Toolchain.Example("enums.lyr"));

        Assert.Equal("", result.Err);
    }

    // ---------------------------------------------------------------- --verbose

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

        Assert.Equal(
            ["read", "parse", "load", "resolve", "check", "lower", "verify", "emit", "-----", "total"],
            phases.Select(p => p.StartsWith("---", StringComparison.Ordinal) ? "-----" : p).ToArray());
    }

    [Fact]
    public void Verbose_uses_invariant_number_formatting()
    {
        // Ein deutsches Gebietsschema schriebe "9,2 ms". Geprueft wird gezielt die Zahl vor
        // "ms" — ein Komma anderswo ist legitim, die Modulliste ist kommagetrennt.
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

        // Geparst, nicht per Substring geprueft: sonst testet der Test die Formatierung.
        using var document = JsonDocument.Parse(result.Err);
        var diagnostics = document.RootElement.GetProperty("diagnostics");
        Assert.True(diagnostics.GetArrayLength() > 0);
        Assert.StartsWith("LYR-SEM", diagnostics[0].GetProperty("code").GetString());
    }

    [Fact]
    public void Json_is_honoured_by_every_binary_alike()
    {
        // Der Grund, warum die Options-Schicht in Lyric.Core wohnt: drei Kopien haetten
        // garantiert einen Pfad, auf dem --json still Klartext liefert.
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
        // Die Gegenprobe, und der eigentliche Test: ein --quiet, das Fehler schluckt, waere
        // gefaehrlich statt leise.
        using var source = Toolchain.Temp(".lyr");
        File.WriteAllText(source.Path, "fn main(): int { return \"not an int\"; }");

        var result = Toolchain.Lyrc("check", source.Path, "--quiet");

        Assert.Equal(ExitCodes.Failure, result.ExitCode);
        Assert.Contains("LYR-SEM", result.Err);
    }

    [Fact]
    public void Quiet_does_not_suppress_requested_payload()
    {
        // Ein Dump ist Nutzlast, keine Plauderei.
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
        // '--quiet' hinter '--' ist ein Argument des Lyric-Programms, keine Option der Toolchain.
        // Erkennbar daran, dass es die Programm-Argument-Ablehnung ausloest statt still zu wirken.
        var result = Toolchain.Lyric("run", Toolchain.Example("hello.lyr"), "--", "--quiet");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains(CliDiagnostics.ProgramArgumentsUnsupported, result.Err);
    }

    // ---------------------------------------------------------------- lyrc --stdlib

    [Fact]
    public void Stdlib_flag_beats_the_environment_variable()
    {
        // Die Variable zeigt ins Leere, das Flag auf die echte Stdlib. Compiliert es trotzdem,
        // hat das Flag gewonnen - dieselbe Staffelung wie --vm/LYRIC_VM.
        var result = Toolchain.RunWithEnvironment(Toolchain.LyrcPath,
            new Dictionary<string, string?> { ["LYRIC_STDLIB"] = "/nonexistent/stdlib" },
            "check", Toolchain.Example("hello.lyr"),
            "--stdlib", Path.Combine(Toolchain.RepositoryRoot, "stdlib"));

        Assert.Equal(ExitCodes.Success, result.ExitCode);
    }

    [Fact]
    public void A_missing_stdlib_is_reported_rather_than_ignored()
    {
        // Die Gegenprobe zum Test darueber: ohne die Regression aus der M7-Reparatur waere ein
        // unauffindbares Modul still durchgegangen.
        var result = Toolchain.Lyrc("check", Toolchain.Example("hello.lyr"),
            "--stdlib", "/nonexistent/stdlib");

        Assert.Equal(ExitCodes.Failure, result.ExitCode);
        Assert.Contains("LYR-RES", result.Err);
    }
}
