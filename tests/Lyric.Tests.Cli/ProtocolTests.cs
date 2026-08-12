using Lyric.Core;

namespace Lyric.Tests.Cli;

/// <summary>
/// Die Negativfaelle des Runner-Vertrags (<c>docs/Bytecode.md</c>) und der Rollentrennung.
///
/// <para>Jeder Fall prueft <b>Exit-Code und Diagnose-Code</b>, nicht den Meldungstext: Codes sind
/// stabile Bezeichner, Texte sind es nicht. Ein Test, der am Wortlaut haengt, wird beim naechsten
/// Formulierungs-Update rot, ohne dass etwas kaputt ist.</para>
/// </summary>
public sealed class ProtocolTests
{
    [Fact]
    public void Lyrvm_refuses_source_files()
    {
        // Kein stiller Durchgriff auf den Compiler: eine Runtime, die Quelltext frisst, ist keine
        // Runtime mehr, und die Trennung waere nach zwei Wochen wieder weich (ADR-017).
        var result = Toolchain.Lyrvm("run", Toolchain.Example("hello.lyr"));

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains(CliDiagnostics.WrongFileKind, result.Err);
        Assert.Equal("", result.Out);
    }

    [Fact]
    public void Lyrc_has_no_run_command()
    {
        var result = Toolchain.Lyrc("run", Toolchain.Example("hello.lyr"));

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains(CliDiagnostics.UnknownCommand, result.Err);
    }

    [Fact]
    public void Driver_has_no_compiler_internals()
    {
        // 'lower', 'parse' und 'tokenize' sind Compiler-Interna und wohnen in lyrc. Sie im
        // Treiber zu spiegeln waere der erste Schritt zurueck zum Monolithen.
        foreach (var command in new[] { "lower", "parse", "tokenize" })
        {
            var result = Toolchain.Lyric(command, Toolchain.Example("hello.lyr"));
            Assert.Equal(ExitCodes.Usage, result.ExitCode);
            Assert.Contains(CliDiagnostics.UnknownCommand, result.Err);
        }
    }

    [Fact]
    public void Missing_file_argument_is_a_usage_error_everywhere()
    {
        Assert.Equal(ExitCodes.Usage, Toolchain.Lyrc("build").ExitCode);
        Assert.Equal(ExitCodes.Usage, Toolchain.Lyrvm("run").ExitCode);
        Assert.Equal(ExitCodes.Usage, Toolchain.Lyric("run").ExitCode);

        Assert.Contains(CliDiagnostics.MissingArgument, Toolchain.Lyric("run").Err);
    }

    [Fact]
    public void Unreadable_module_fails_to_load_rather_than_to_parse()
    {
        var result = Toolchain.Lyrvm("run", Path.Combine(Toolchain.RepositoryRoot, "nope.lyrbc"));

        Assert.Equal(ExitCodes.Failure, result.ExitCode);
        Assert.Contains(CliDiagnostics.FileUnreadable, result.Err);
    }

    [Fact]
    public void Corrupt_module_is_rejected_at_load_time()
    {
        // ADR-013: geprueft wird beim Laden, nicht beim Aufruf. Also darf hier nichts von der
        // Programmausgabe erscheinen, bevor es scheitert.
        using var module = Toolchain.Temp(".lyrbc");
        File.WriteAllBytes(module.Path, "not a lyric module at all"u8.ToArray());

        var result = Toolchain.Lyrvm("run", module.Path);

        Assert.Equal(ExitCodes.Failure, result.ExitCode);
        Assert.NotEqual("", result.Err);
        Assert.Equal("", result.Out);
    }

    [Fact]
    public void Truncated_module_is_rejected_without_crashing()
    {
        using var full = Toolchain.Temp(".lyrbc");
        using var cut = Toolchain.Temp(".lyrbc");
        Toolchain.Lyrc("build", Toolchain.Example("arith.lyr"), "-o", full.Path);

        var bytes = File.ReadAllBytes(full.Path);
        File.WriteAllBytes(cut.Path, bytes[..(bytes.Length / 2)]);

        var result = Toolchain.Lyrvm("run", cut.Path);

        // 1, nicht 101 und nicht ein .NET-Stacktrace: ein kaputtes Modul ist ein Ladefehler,
        // kein panic und erst recht kein Absturz der Runtime.
        Assert.Equal(ExitCodes.Failure, result.ExitCode);
        Assert.DoesNotContain("Unhandled exception", result.Err);
    }

    [Fact]
    public void Missing_foreign_runtime_is_reported_before_compiling()
    {
        var result = Toolchain.Lyric(
            "run", Toolchain.Example("hello.lyr"), "--vm", "/nonexistent/runtime");

        Assert.Equal(ExitCodes.Failure, result.ExitCode);
        Assert.Contains(CliDiagnostics.VmNotFound, result.Err);
    }

    [Fact]
    public void Vm_flag_without_a_path_is_a_usage_error()
    {
        var result = Toolchain.Lyric("run", Toolchain.Example("hello.lyr"), "--vm");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
    }

    [Fact]
    public void Program_arguments_reach_the_program()
    {
        // Punkt 4 des Vertrags ist eingeloest: alles nach '--' gehoert dem Programm. Ein
        // parameterloses 'main' ignoriert es — kein Fehler, dieselbe Freiheit hat jede Shell.
        // (Bis 2026-08-06 lehnte die Runtime hier ab, weil sie Argumente nicht zustellen konnte.)
        var ignored = Toolchain.Lyric("run", Toolchain.Example("hello.lyr"), "--", "a", "b");
        Assert.Equal(ExitCodes.Success, ignored.ExitCode);

        // Und ein 'main(args: string[])' bekommt sie wirklich.
        var received = Toolchain.Lyric("run", Toolchain.Example("greet.lyr"), "--", "a", "b");
        Assert.Equal(2, received.ExitCode);
    }

    [Fact]
    public void A_panic_exits_with_101_and_prints_a_backtrace()
    {
        // Der Fall, an dem sich Punkt 2 des Vertrags entscheidet: 101 ist von 'return 1;'
        // unterscheidbar, und der Backtrace geht nach stderr, nicht nach stdout.
        using var source = Toolchain.Temp(".lyr");
        File.WriteAllText(source.Path, """
            fn main(): int {
                var zero = 0;
                return 1 / zero;
            }
            """);

        var result = Toolchain.Lyric("run", source.Path);

        Assert.Equal(ExitCodes.Panic, result.ExitCode);
        Assert.Contains("panic", result.Err);
        Assert.Equal("", result.Out);
    }

    [Fact]
    public void A_panic_looks_the_same_through_a_foreign_runtime()
    {
        using var source = Toolchain.Temp(".lyr");
        File.WriteAllText(source.Path, """
            fn main(): int {
                var zero = 0;
                return 1 / zero;
            }
            """);

        var inProcess = Toolchain.Lyric("run", source.Path);
        var foreign = Toolchain.Lyric("run", source.Path, "--vm", Toolchain.LyrvmPath);

        Assert.Equal(ExitCodes.Panic, foreign.ExitCode);
        Assert.Equal(inProcess.ExitCode, foreign.ExitCode);
        Assert.Equal(inProcess.Err, foreign.Err);
    }

    [Fact]
    public void A_compile_error_never_reaches_the_runtime()
    {
        using var source = Toolchain.Temp(".lyr");
        File.WriteAllText(source.Path, "fn main(): int { return \"not an int\"; }");

        var result = Toolchain.Lyric("run", source.Path, "--vm", Toolchain.LyrvmPath);

        // 1 und nicht 101: es gab nichts auszufuehren.
        Assert.Equal(ExitCodes.Failure, result.ExitCode);
        Assert.Contains("LYR-SEM", result.Err);
    }

    [Fact]
    public void All_four_binaries_report_the_same_toolchain_version()
    {
        // lyrrepl fehlte hier, seit es sie gibt (ADR-021) — eine Liste, die „alle" heisst und
        // drei zaehlt, waechst nicht mit.
        Assert.StartsWith("lyrc " + ToolchainVersion.Value, Toolchain.Lyrc("--version").Out.Trim());
        Assert.StartsWith("lyrvm " + ToolchainVersion.Value, Toolchain.Lyrvm("--version").Out.Trim());
        Assert.StartsWith("lyric " + ToolchainVersion.Value, Toolchain.Lyric("--version").Out.Trim());
        Assert.StartsWith("lyrrepl " + ToolchainVersion.Value,
            Toolchain.Run(Toolchain.LyrreplPath, ["--version"]).Out.Trim());
    }

    /// <summary>
    /// Die Version im <c>Version</c>-Property von MSBuild ist dieselbe wie im Quelltext.
    ///
    /// <para>Sie steht notgedrungen zweimal — MSBuild kann keine C#-Konstante lesen. Zwei Stellen
    /// mit derselben Antwort driften; hier faellt das auf, statt dass ein Paket mit einer anderen
    /// Nummer erscheint, als das Werkzeug darin druckt.</para>
    /// </summary>
    [Fact]
    public void The_build_property_and_the_source_constant_agree()
    {
        var props = File.ReadAllText(
            Path.Combine(Toolchain.RepositoryRoot, "Directory.Build.props"));

        Assert.Contains($"<Version>{ToolchainVersion.Value}</Version>", props);
    }

    /// <summary>
    /// Jede Version, die in README oder Doku als <b>Ausgabe eines Werkzeugs</b> abgedruckt ist,
    /// stimmt mit dem, was das Werkzeug wirklich druckt.
    ///
    /// <para>Ohne diesen Test entsteht der Fehler, den er verhindert, beim Schreiben der Doku:
    /// eine plausible Ausgabe wird hingeschrieben, statt sie zu erzeugen. Genau so kam
    /// <c>Lyric 0.9.0</c> in beide Dateien, waehrend die REPL <c>0.0.1-dev</c> druckte — ein
    /// Beispiel, das nie gelaufen ist, gibt es hier nicht mehr.</para>
    /// </summary>
    [Theory]
    [InlineData("README.md")]
    public void Printed_versions_in_the_docs_are_the_real_one(string document)
    {
        var text = File.ReadAllText(Path.Combine(Toolchain.RepositoryRoot, document));

        var printed = System.Text.RegularExpressions.Regex
            .Matches(text, @"^Lyric (\S+) — :help",
                System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .ToArray();

        // Ohne diese Zeile ist der Test gruen, wenn die Regex nichts trifft — und faende damit
        // auch nichts, wenn jemand das Beispiel umformuliert. Ein Test, der still nichts prueft,
        // ist schlimmer als keiner: er sagt „geprueft".
        Assert.NotEmpty(printed);
        Assert.All(printed, version => Assert.Equal(ToolchainVersion.Value, version));
    }
}
