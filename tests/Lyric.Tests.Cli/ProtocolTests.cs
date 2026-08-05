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
    public void Program_arguments_are_rejected_rather_than_silently_dropped()
    {
        // Der Vertrag sieht '-- <args>' vor, die Sprache loest es noch nicht ein
        // (Sprache.md §11 kennt main(args: string[]), ModuleLowerer nimmt nur das parameterlose
        // main). Still verwerfen waere der Fehler aus der M7-Korrektur in der anderen Richtung:
        // die Toolchain taeuschte vor, Argumente zugestellt zu haben.
        var result = Toolchain.Lyric("run", Toolchain.Example("hello.lyr"), "--", "a", "b");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains(CliDiagnostics.ProgramArgumentsUnsupported, result.Err);
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
    public void All_three_binaries_report_the_same_toolchain_version()
    {
        var lyrc = Toolchain.Lyrc("--version").Out.Trim();
        var lyrvm = Toolchain.Lyrvm("--version").Out.Trim();
        var lyric = Toolchain.Lyric("--version").Out.Trim();

        Assert.StartsWith("lyrc " + ToolchainVersion.Value, lyrc);
        Assert.StartsWith("lyrvm " + ToolchainVersion.Value, lyrvm);
        Assert.StartsWith("lyric " + ToolchainVersion.Value, lyric);
    }
}
