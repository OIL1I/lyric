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
    /// <summary>Die Programme, die mit dem heutigen Backend-Stand laufen: Name, Exit-Code und ob
    /// das Programm <b>selbst</b> auf stderr schreibt.
    ///
    /// <para>Die dritte Spalte trennt zwei Dinge, die vorher eines waren: <c>bank.lyr</c> faengt
    /// einen Fehler und meldet ihn auf stderr — das ist die Pointe des Beispiels und nicht
    /// Rauschen der Toolchain. Ohne die Spalte muesste es aus der Matrix fallen, und ein Beispiel
    /// aus der Matrix zu nehmen, damit die Matrix gruen bleibt, ist die falsche Richtung.</para>
    /// </summary>
    public static TheoryData<string, int, bool> RunnableExamples => new()
    {
        { "hello.lyr", 0, false },
        { "arith.lyr", 55, false },
        { "objects.lyr", 21, false },
        { "arrays.lyr", 144, false },
        { "optionals.lyr", 200, false },
        { "enums.lyr", 24, false },
        { "interfaces.lyr", 140, false },
        { "vectors.lyr", 115, false },
        { "constants.lyr", 140, false },
        { "closures.lyr", 83, false },
        { "generator.lyr", 40, false },
        { "fizzbuzz.lyr", 0, false },
        { "greet.lyr", 0, false },
        { "tuples.lyr", 26, false },
        { "inventory.lyr", 0, false },
        { "stats.lyr", 0, false },
        { "shapes.lyr", 0, false },
        { "stack.lyr", 0, false },
        { "bank.lyr", 0, true },
        { "fibonacci.lyr", 0, false },
    };

    /// <summary>Beispiele, die absichtlich nicht in der Matrix stehen — mit Grund.</summary>
    private static readonly Dictionary<string, string> NotInTheMatrix = new()
    {
        ["wc.lyr"] = "hat eigene Tests in GateTests: es liest eine Datei und braucht Argumente",
        ["embedded.lyr"] = "laeuft nur im Host-Prozess (M10), nicht ueber 'lyric run'",
    };

    /// <summary>
    /// <b>Jedes</b> Beispiel steht in der Matrix oder auf der Ausnahmeliste.
    ///
    /// <para>Ohne diesen Test ist die Matrix eine handgepflegte Liste, und eine handgepflegte Liste
    /// vergisst. Genau das ist passiert: <c>stack.lyr</c> hat ADR-016 nicht überlebt — <c>T[]</c>
    /// wurde ein echtes Array ohne <c>push</c> — und lag danach <i>drei Meilensteine lang kaputt
    /// im Verzeichnis</i>, weil kein Test es je angefasst hat. <c>bank.lyr</c> und
    /// <c>fibonacci.lyr</c> liefen zwar, aber aus Glück und nicht aus Zusicherung.</para>
    /// </summary>
    [Fact]
    public void Every_example_is_covered_or_listed_as_an_exception()
    {
        var inMatrix = RunnableExamples.Select(row => (string)row[0]).ToHashSet();

        var uncovered = Directory.GetFiles(
                Path.Combine(Toolchain.RepositoryRoot, "examples"), "*.lyr")
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => !inMatrix.Contains(name) && !NotInTheMatrix.ContainsKey(name))
            .OrderBy(name => name)
            .ToArray();

        Assert.Empty(uncovered);
    }

    [Theory]
    [MemberData(nameof(RunnableExamples))]
    public void Lyric_run_produces_the_documented_exit_code(
        string example, int expected, bool writesToStderr)
    {
        var result = Toolchain.Lyric("run", Toolchain.Example(example));

        Assert.Equal(expected, result.ExitCode);
        if (!writesToStderr) Assert.Equal("", result.Err);
    }

    [Theory]
    [MemberData(nameof(RunnableExamples))]
    #pragma warning disable xUnit1026 // Die Matrix ist dreispaltig; check braucht nur den Namen.
    public void Lyric_check_accepts_every_runnable_example(string example, int _, bool __)
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
    public void Lyric_run_equals_lyrc_build_plus_lyrvm_run(string example, int expected, bool _)
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
    /// Frueher stand hier <c>In_process_and_foreign_vm_paths_agree</c> — der Beweis, dass zwei
    /// Ausfuehrungspfade dasselbe liefern. Seit ADR-019 gibt es nur einen: der Treiber startet
    /// immer ein Werkzeug. Der Test ist damit nicht gestrichen, sondern <b>gegenstandslos</b>;
    /// was er absicherte, kann nicht mehr auseinanderlaufen.
    /// </summary>
    [Fact]
    public void Program_arguments_reach_a_main_that_asks_for_them()
    {
        // Punkt 4 des Runner-Vertrags (Bytecode.md §9): alles nach dem ersten '--' gehoert dem
        // Programm. greet.lyr gibt ihre Zahl zurueck.
        var result = Toolchain.Lyric("run", Toolchain.Example("greet.lyr"), "--", "Welt", "Lyric");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("hallo, Welt", result.Out);
        Assert.Contains("hallo, Lyric", result.Out);
    }

    [Fact]
    public void A_parameterless_main_ignores_program_arguments()
    {
        // Kein Fehler — dieselbe Freiheit, die jede Shell hat. Vorher lehnte die Runtime hier ab,
        // weil sie Argumente ueberhaupt nicht zustellen konnte.
        var result = Toolchain.Lyric("run", Toolchain.Example("arith.lyr"), "--", "ignoriert");

        Assert.Equal(55, result.ExitCode);
    }

    [Fact]
    public void A_module_without_a_main_builds_but_does_not_run()
    {
        // Der Embedding-Fall: gueltiger Bytecode, aber kein Programm. Ein Host laedt so etwas und
        // ruft einzelne Funktionen daraus — 'run' ist dafuer die falsche Frage, und die Antwort
        // muss das sagen statt still nichts zu tun.
        using var module = Toolchain.Temp(".lyrbc");

        var build = Toolchain.Lyrc("build", Toolchain.Example("embedded.lyr"), "-o", module.Path);
        Assert.Equal(ExitCodes.Success, build.ExitCode);

        Assert.Equal(ExitCodes.Success, Toolchain.Lyrvm("verify", module.Path).ExitCode);

        var run = Toolchain.Lyrvm("run", module.Path);
        Assert.NotEqual(ExitCodes.Success, run.ExitCode);
        Assert.Contains("library", run.Err);
    }

    [Fact]
    public void Info_names_a_library_as_such() =>
        // Was ein Host zuerst wissen will: hat dieses Modul einen Einstieg?
        Assert.Contains("library", Toolchain.Lyrvm("info", BuildLibrary()).Out);

    private static string BuildLibrary()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lib-{Guid.NewGuid():N}.lyrbc");
        Toolchain.Lyrc("build", Toolchain.Example("embedded.lyr"), "-o", path);
        return path;
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
