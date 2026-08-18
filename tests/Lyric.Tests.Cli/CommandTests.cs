using Lyric.Core;

namespace Lyric.Tests.Cli;

/// <summary>
/// The command matrix over <c>examples/</c>.
///
/// <para>That <c>check</c> did not wire up the <c>ModuleLoader</c> and therefore checked every stdlib
/// call SILENTLY NOT AT ALL showed only when trying it by hand — the sema tests set the loader
/// themselves. A test running the commands against the examples would have caught it.</para>
/// </summary>
public sealed class CommandTests
{
    /// <summary>The programs that run with today's backend: name, exit code, and whether the program
    /// ITSELF writes to stderr.
    ///
    /// <para>The third column separates two things that used to be one: <c>bank.lyr</c> catches an error
    /// and reports it on stderr — that is the point of the example rather than noise from the toolchain.
    /// Without the column it would have to fall out of the matrix, and taking an example out of the
    /// matrix so the matrix stays green is the wrong direction.</para>
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

    /// <summary>Examples deliberately not in the matrix, with a reason.</summary>
    private static readonly Dictionary<string, string> NotInTheMatrix = new()
    {
        ["wc.lyr"] = "hat eigene Tests in GateTests: es liest eine Datei und braucht Argumente",
        ["embedded.lyr"] = "laeuft nur im Host-Prozess (M10), nicht ueber 'lyric run'",
    };

    /// <summary>
    /// EVERY example stands in the matrix or on the exception list.
    ///
    /// <para>Without this test the matrix is a hand-maintained list, and a hand-maintained list forgets.
    /// That is what happened: <c>stack.lyr</c> did not survive the array change — <c>T[]</c> became a
    /// real array without <c>push</c> — and then lay broken in the directory for three milestones,
    /// because no test ever touched it. <c>bank.lyr</c> and <c>fibonacci.lyr</c> did run, but by luck
    /// rather than by promise.</para>
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
    #pragma warning disable xUnit1026 // the matrix has three columns; check needs only the name
    public void Lyric_check_accepts_every_runnable_example(string example, int _, bool __)
    {
        var result = Toolchain.Lyric("check", Toolchain.Example(example));

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("ok", result.Out);
    }

    /// <summary>
    /// The equivalence that holds the split together: the convenient surface has to do the same as the
    /// technical one. Were they two pipelines, they would drift, which is why the driver calls the same
    /// library entry points.
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
    /// <c>In_process_and_foreign_vm_paths_agree</c> used to stand here, the proof that two execution
    /// paths deliver the same. There is only one now: the driver always starts a tool. The test is
    /// therefore not deleted but MOOT; what it secured can no longer drift apart.
    /// </summary>
    [Fact]
    public void Program_arguments_reach_a_main_that_asks_for_them()
    {
        // Point 4 of the runner contract: everything after the first '--' belongs to the program.
        // greet.lyr returns their number.
        var result = Toolchain.Lyric("run", Toolchain.Example("greet.lyr"), "--", "Welt", "Lyric");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("hallo, Welt", result.Out);
        Assert.Contains("hallo, Lyric", result.Out);
    }

    [Fact]
    public void A_parameterless_main_ignores_program_arguments()
    {
        // No error: the same freedom every shell has. The runtime used to reject this, because it could
        // not deliver arguments at all.
        var result = Toolchain.Lyric("run", Toolchain.Example("arith.lyr"), "--", "ignoriert");

        Assert.Equal(55, result.ExitCode);
    }

    [Fact]
    public void A_module_without_a_main_builds_but_does_not_run()
    {
        // The embedding case: valid bytecode but no program. A host loads such a thing and calls
        // individual functions from it; 'run' is the wrong question for that, and the answer has to say
        // so rather than silently doing nothing.
        using var module = Toolchain.Temp(".lyrbc");

        var build = Toolchain.Lyrc("build", Toolchain.Example("embedded.lyr"), "-o", module.Path);
        Assert.Equal(ExitCodes.Success, build.ExitCode);

        Assert.Equal(ExitCodes.Success, Toolchain.Lyrvm("verify", module.Path).ExitCode);

        var run = Toolchain.Lyrvm("run", module.Path);
        Assert.NotEqual(ExitCodes.Success, run.ExitCode);
        Assert.Contains("library", run.Err);
    }

    [Fact]
    public void A_panic_names_its_line_unless_the_source_map_is_omitted()
    {
        // The flag has to be ACCEPTED by the binary a user actually calls, not merely defined on
        // CompilerOptions. A switch nothing passes is a switch nothing tests.
        using var source = Toolchain.Temp(".lyr");
        File.WriteAllText(source.Path, """
            fn divide(a: int, b: int): int {
                return
                    a / b;
            }

            fn main(): int {
                let n = 0;
                return divide(10, n);
            }
            """);

        using var mapped = Toolchain.Temp(".lyrbc");
        using var stripped = Toolchain.Temp(".lyrbc");

        Assert.Equal(ExitCodes.Success,
            Toolchain.Lyrc("build", source.Path, "-o", mapped.Path).ExitCode);
        Assert.Equal(ExitCodes.Success,
            Toolchain.Lyrc("build", source.Path, "-o", stripped.Path, "--no-source-map").ExitCode);

        var withMap = Toolchain.Lyrvm("run", mapped.Path);
        var without = Toolchain.Lyrvm("run", stripped.Path);

        // Same failure, same exit code: the map changes what is REPORTED, never what happens.
        Assert.Equal(withMap.ExitCode, without.ExitCode);

        // The expression stands on its own line, so the faulting division is line 3 and not the
        // line of the return it belongs to. 'divide' is small and inlined, so one frame remains:
        // the caller's name, the callee's line — spliced instructions keep their spans.
        var name = Path.GetFileName(source.Path);
        Assert.Contains($"in main.main ({name}:3)", withMap.Err, StringComparison.Ordinal);

        Assert.Contains("in main.main", without.Err, StringComparison.Ordinal);
        Assert.DoesNotContain(name, without.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Info_names_a_library_as_such() =>
        // What a host wants to know first: does this module have an entry point?
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
        // The variable points into nothing, the flag at the real runtime. If it runs all the same, the
        // flag won.
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
        // Had 'verify' executed, the program output would stand here.
        Assert.DoesNotContain("Hello, Lyric!", result.Out);
    }

    [Fact]
    public void Build_output_is_deterministic_across_binaries()
    {
        // The same input has to give byte-identical output. 'lyrc' and 'lyric' call the same writer; if
        // not, it shows here.
        using var fromLyrc = Toolchain.Temp(".lyrbc");
        using var fromDriver = Toolchain.Temp(".lyrbc");

        Toolchain.Lyrc("build", Toolchain.Example("enums.lyr"), "-o", fromLyrc.Path);
        Toolchain.Lyric("build", Toolchain.Example("enums.lyr"), "-o", fromDriver.Path);

        Assert.Equal(File.ReadAllBytes(fromLyrc.Path), File.ReadAllBytes(fromDriver.Path));
    }

    [Fact]
    public void Program_output_goes_to_stdout_and_stays_out_of_stderr()
    {
        // Point 3 of the runner contract. Without the separation a calling tool cannot distinguish the
        // output of a Lyric program from the runtime's complaint.
        var result = Toolchain.Lyrvm("--help");
        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal("", result.Err);
    }
}
