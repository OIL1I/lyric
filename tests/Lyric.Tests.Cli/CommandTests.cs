using Lyric.Core;

namespace Lyric.Tests.Cli;

/// <summary>
/// Die Kommando-Matrix ueber <c>examples/</c>.
///
/// <para>Der Test, der seit M6 gefehlt hat. Dass <c>check</c> den <c>ModuleLoader</c> nicht
/// verdrahtete und deshalb jeden Stdlib-Aufruf <i>stumm gar nicht</i> prueste, fiel damals nur
/// beim Handprobieren auf — die Sema-Tests setzen den Loader selbst. Ein Test, der die Kommandos
/// gegen die Beispiele faehrt, haette es gefangen.</para>
/// </summary>
public sealed class CommandTests
{
    /// <summary>Die Programme, die mit dem heutigen Backend-Stand laufen, mit ihrem
    /// Exit-Code. Was hier fehlt, wartet auf einen M7-Slice (Structs, Closures, Coroutinen,
    /// Generics) oder auf M8 — bewusst nicht als „erwartet fehlerhaft" gelistet, weil dieser
    /// Test die Toolchain prueft und nicht den Sprachumfang.</summary>
    public static TheoryData<string, int> RunnableExamples => new()
    {
        { "hello.lyr", 0 },
        { "arith.lyr", 55 },
        { "objects.lyr", 21 },
        { "arrays.lyr", 144 },
        { "optionals.lyr", 200 },
        { "enums.lyr", 24 },
        { "interfaces.lyr", 140 },
        { "vectors.lyr", 115 },
        { "constants.lyr", 140 },
        { "closures.lyr", 83 },
    };

    [Theory]
    [MemberData(nameof(RunnableExamples))]
    public void Lyric_run_produces_the_documented_exit_code(string example, int expected)
    {
        var result = Toolchain.Lyric("run", Toolchain.Example(example));

        Assert.Equal(expected, result.ExitCode);
        Assert.Equal("", result.Err);
    }

    [Theory]
    [MemberData(nameof(RunnableExamples))]
    public void Lyric_check_accepts_every_runnable_example(string example, int _)
    {
        var result = Toolchain.Lyric("check", Toolchain.Example(example));

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("ok", result.Out);
    }

    /// <summary>
    /// Die Aequivalenz, die den Split zusammenhaelt: die bequeme Oberflaeche muss dasselbe tun
    /// wie die technische. Waeren es zwei Pipelines, drifteten sie — der Treiber ruft deshalb
    /// dieselben Bibliotheks-Einstiege (ADR-017).
    /// </summary>
    [Theory]
    [MemberData(nameof(RunnableExamples))]
    public void Lyric_run_equals_lyrc_build_plus_lyrvm_run(string example, int expected)
    {
        using var module = Toolchain.Temp(".lyrbc");

        var build = Toolchain.Lyrc("build", Toolchain.Example(example), "-o", module.Path);
        Assert.Equal(ExitCodes.Success, build.ExitCode);

        var split = Toolchain.Lyrvm("run", module.Path);
        var driver = Toolchain.Lyric("run", Toolchain.Example(example));

        Assert.Equal(expected, split.ExitCode);
        Assert.Equal(driver.ExitCode, split.ExitCode);
        Assert.Equal(driver.Out, split.Out);
    }

    /// <summary>
    /// Die zweite Aequivalenz, und der Grund, warum der Zwei-Pfade-Entwurf vertretbar ist: der
    /// In-Process-Pfad (mitgelieferte Runtime) und der Subprozess-Pfad (<c>--vm</c>) muessen
    /// dasselbe liefern. Das mitgelieferte <c>lyrvm</c> ist hier das Testdouble fuer eine
    /// Fremd-Runtime — es ist per Konstruktion vertragskonform und kostet nichts extra.
    /// </summary>
    [Theory]
    [MemberData(nameof(RunnableExamples))]
    public void In_process_and_foreign_vm_paths_agree(string example, int expected)
    {
        var inProcess = Toolchain.Lyric("run", Toolchain.Example(example));
        var foreign = Toolchain.Lyric("run", Toolchain.Example(example), "--vm", Toolchain.LyrvmPath);

        Assert.Equal(expected, inProcess.ExitCode);
        Assert.Equal(inProcess.ExitCode, foreign.ExitCode);
        Assert.Equal(inProcess.Out, foreign.Out);
    }

    [Fact]
    public void Foreign_vm_can_be_selected_through_the_environment()
    {
        var result = Toolchain.RunWithEnvironment(Toolchain.LyricPath,
            new Dictionary<string, string?> { ["LYRIC_VM"] = Toolchain.LyrvmPath },
            "run", Toolchain.Example("arith.lyr"));

        Assert.Equal(55, result.ExitCode);
    }

    [Fact]
    public void Flag_beats_environment_variable()
    {
        // Die Variable zeigt ins Leere, das Flag auf die echte Runtime. Laeuft es trotzdem,
        // hat das Flag gewonnen — die Staffelung aus ADR-017.
        var result = Toolchain.RunWithEnvironment(Toolchain.LyricPath,
            new Dictionary<string, string?> { ["LYRIC_VM"] = "/nonexistent/runtime" },
            "run", Toolchain.Example("arith.lyr"), "--vm", Toolchain.LyrvmPath);

        Assert.Equal(55, result.ExitCode);
    }

    [Fact]
    public void Lyric_run_accepts_a_prebuilt_module()
    {
        using var module = Toolchain.Temp(".lyrbc");
        Toolchain.Lyrc("build", Toolchain.Example("hello.lyr"), "-o", module.Path);

        var result = Toolchain.Lyric("run", module.Path);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal("Hello, Lyric!\n", result.Out);
    }

    [Fact]
    public void Disassembly_is_identical_across_lyrvm_and_the_driver()
    {
        using var module = Toolchain.Temp(".lyrbc");
        Toolchain.Lyrc("build", Toolchain.Example("arith.lyr"), "-o", module.Path);

        Assert.Equal(Toolchain.Lyrvm("disasm", module.Path).Out,
            Toolchain.Lyric("disasm", module.Path).Out);
    }

    [Fact]
    public void Verify_validates_without_executing()
    {
        using var module = Toolchain.Temp(".lyrbc");
        Toolchain.Lyrc("build", Toolchain.Example("hello.lyr"), "-o", module.Path);

        var result = Toolchain.Lyrvm("verify", module.Path);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        // Haette 'verify' ausgefuehrt, staende hier die Programmausgabe.
        Assert.DoesNotContain("Hello, Lyric!", result.Out);
    }

    [Fact]
    public void Build_output_is_deterministic_across_binaries()
    {
        // ADR-013 verlangt byte-identischen Output bei gleichem Input. 'lyrc' und 'lyric' rufen
        // denselben Writer — wenn nicht, faellt es hier auf.
        using var fromLyrc = Toolchain.Temp(".lyrbc");
        using var fromDriver = Toolchain.Temp(".lyrbc");

        Toolchain.Lyrc("build", Toolchain.Example("enums.lyr"), "-o", fromLyrc.Path);
        Toolchain.Lyric("build", Toolchain.Example("enums.lyr"), "-o", fromDriver.Path);

        Assert.Equal(File.ReadAllBytes(fromLyrc.Path), File.ReadAllBytes(fromDriver.Path));
    }

    [Fact]
    public void Program_output_goes_to_stdout_and_stays_out_of_stderr()
    {
        // Punkt 3 des Runner-Vertrags. Ohne die Trennung kann ein aufrufendes Werkzeug die
        // Ausgabe eines Lyric-Programms nicht von der Klage der Runtime unterscheiden.
        var result = Toolchain.Lyrvm("--help");
        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal("", result.Err);
    }
}
