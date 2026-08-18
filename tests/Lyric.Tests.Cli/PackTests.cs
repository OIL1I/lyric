using Lyric.Core;

namespace Lyric.Tests.Cli;

/// <summary>
/// <c>lyrpack</c> and the stub, across the process boundary: a module goes in, an executable
/// comes out, and the executable IS the program — output, arguments and exit code included.
/// </summary>
public class PackTests
{
    /// <summary>Compiles a source into <paramref name="directory"/> and returns the module path.
    /// </summary>
    private static string Module(TemporaryDirectory directory, string source)
    {
        var lyr = directory.Write("program.lyr", source);
        var lyrbc = Path.Combine(directory.Path, "program.lyrbc");
        var built = Toolchain.Lyrc("build", lyr, "-o", lyrbc, "--quiet");
        Assert.True(built.ExitCode == 0, "the test program did not compile:\n" + built.Err);
        return lyrbc;
    }

    private static string Exe(TemporaryDirectory directory, string name = "program") =>
        Path.Combine(directory.Path, OperatingSystem.IsWindows() ? $"{name}.exe" : name);

    [Fact]
    public void A_packed_program_runs_and_returns_its_exit_code()
    {
        using var directory = Toolchain.TempDirectory();
        var module = Module(directory, """
            import std.io.console;

            fn main(): int {
                console.println("packed and running");
                return 7;
            }
            """);

        var packed = Toolchain.Lyrpack(module, "-o", Exe(directory));
        Assert.Equal(0, packed.ExitCode);

        var run = Toolchain.Run(Exe(directory));
        Assert.Equal("packed and running\n", run.Out);
        Assert.Equal(7, run.ExitCode);
    }

    [Fact]
    public void The_whole_command_line_belongs_to_the_program()
    {
        // No '--' protocol: a packed executable is not a runner that also takes options, every
        // argument is the program's. '--help' therefore reaches main like any other word.
        using var directory = Toolchain.TempDirectory();
        var module = Module(directory, """
            import std.io.console;

            fn main(args: string[]): int {
                for (a in args) { console.println(a); }
                return args.length;
            }
            """);

        Assert.Equal(0, Toolchain.Lyrpack(module, "-o", Exe(directory)).ExitCode);

        var run = Toolchain.Run(Exe(directory), "alpha", "--help", "--");
        Assert.Equal("alpha\n--help\n--\n", run.Out);
        Assert.Equal(3, run.ExitCode);
    }

    [Fact]
    public void Without_an_output_the_executable_lands_beside_the_module()
    {
        using var directory = Toolchain.TempDirectory();
        var module = Module(directory, "fn main(): int { return 0; }");

        var packed = Toolchain.Lyrpack(module);
        Assert.Equal(0, packed.ExitCode);
        Assert.True(File.Exists(Exe(directory)), "expected the default output name");
        Assert.Equal(0, Toolchain.Run(Exe(directory)).ExitCode);
    }

    [Fact]
    public void A_panic_in_a_packed_program_reports_like_the_runtime()
    {
        using var directory = Toolchain.TempDirectory();
        var module = Module(directory, """
            fn divide(a: int, b: int): int { return a / b; }
            fn main(): int { return divide(1, 0); }
            """);

        Assert.Equal(0, Toolchain.Lyrpack(module, "-o", Exe(directory)).ExitCode);

        var run = Toolchain.Run(Exe(directory));
        Assert.Equal(101, run.ExitCode);
        Assert.Contains("division by zero", run.Err);
        Assert.Contains("program.lyr", run.Err); // the source map traveled with the module
    }

    [Fact]
    public void The_bare_stub_explains_itself()
    {
        var run = Toolchain.Run(Toolchain.StubPath);
        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Contains(CliDiagnostics.StubEmpty, run.Err);
        Assert.Contains("lyric pack", run.Err);
        Assert.Equal("", run.Out); // diagnostics belong on stderr, stdout stays the program's
    }

    [Fact]
    public void A_damaged_pack_is_reported_not_executed()
    {
        using var directory = Toolchain.TempDirectory();
        var module = Module(directory, "fn main(): int { return 0; }");
        Assert.Equal(0, Toolchain.Lyrpack(module, "-o", Exe(directory)).ExitCode);

        // Corrupt the recorded length; the magic stays intact. This is the shape of a file that
        // lost bytes in the middle — the footer says "program", the bounds say "gone".
        var bytes = File.ReadAllBytes(Exe(directory));
        BitConverter.GetBytes(ulong.MaxValue).CopyTo(bytes, bytes.Length - 16);
        File.WriteAllBytes(Exe(directory), bytes);

        var run = Toolchain.Run(Exe(directory));
        Assert.Equal(ExitCodes.Failure, run.ExitCode);
        Assert.Contains(CliDiagnostics.PackDamaged, run.Err);
    }

    [Fact]
    public void A_source_file_is_refused_with_the_way_out()
    {
        using var directory = Toolchain.TempDirectory();
        var lyr = directory.Write("program.lyr", "fn main(): int { return 0; }");

        var packed = Toolchain.Lyrpack(lyr);
        Assert.Equal(ExitCodes.Usage, packed.ExitCode);
        Assert.Contains(CliDiagnostics.WrongFileKind, packed.Err);
        Assert.Contains("lyric pack", packed.Err);
    }

    [Fact]
    public void A_missing_stub_names_the_ladder()
    {
        using var directory = Toolchain.TempDirectory();
        var module = Module(directory, "fn main(): int { return 0; }");

        var packed = Toolchain.Lyrpack(module, "--stub",
            Path.Combine(directory.Path, "no-such-stub"));
        Assert.Equal(ExitCodes.Failure, packed.ExitCode);
        Assert.Contains(CliDiagnostics.StubNotFound, packed.Err);
    }

    [Fact]
    public void Packing_over_an_input_is_refused()
    {
        using var directory = Toolchain.TempDirectory();
        var module = Module(directory, "fn main(): int { return 0; }");

        var ontoModule = Toolchain.Lyrpack(module, "-o", module);
        Assert.Equal(ExitCodes.Usage, ontoModule.ExitCode);
        Assert.Contains(CliDiagnostics.OutputUnwritable, ontoModule.Err);

        var ontoStub = Toolchain.Lyrpack(module, "-o", Toolchain.StubPath);
        Assert.Equal(ExitCodes.Usage, ontoStub.ExitCode);
        Assert.Contains(CliDiagnostics.OutputUnwritable, ontoStub.Err);

        // Both inputs untouched: the stub still packs, the module still runs.
        Assert.Equal(0, Toolchain.Lyrpack(module, "-o", Exe(directory)).ExitCode);
        Assert.Equal(0, Toolchain.Run(Exe(directory)).ExitCode);
    }

    [Fact]
    public void Lyric_pack_compiles_and_packs_in_one_step()
    {
        using var directory = Toolchain.TempDirectory();
        var lyr = directory.Write("greet.lyr", """
            import std.io.console;

            fn main(): int {
                console.println("one step");
                return 0;
            }
            """);

        var packed = Toolchain.Lyric("pack", lyr, "-o", Exe(directory, "greet"));
        Assert.True(packed.ExitCode == 0, packed.Err);

        var run = Toolchain.Run(Exe(directory, "greet"));
        Assert.Equal("one step\n", run.Out);
        Assert.Equal(0, run.ExitCode);
    }

    [Fact]
    public void Lyric_pack_names_the_executable_after_the_source()
    {
        // The default must come from the SOURCE: the module the driver hands the packer is a
        // temporary file, and an executable named after it would be a Guid nobody asked for.
        using var directory = Toolchain.TempDirectory();
        var lyr = directory.Write("greet.lyr", "fn main(): int { return 0; }");

        var packed = Toolchain.Lyric("pack", lyr);
        Assert.True(packed.ExitCode == 0, packed.Err);
        Assert.True(File.Exists(Exe(directory, "greet")),
            "expected the executable beside the source, named after it");
    }

    [Fact]
    public void Lyric_pack_refuses_program_arguments()
    {
        // 'lyric run app.lyr -- a b' hands a and b to the program it runs. pack runs nothing,
        // so the same tail would silently vanish — refused instead, with the reason.
        using var directory = Toolchain.TempDirectory();
        var lyr = directory.Write("greet.lyr", "fn main(): int { return 0; }");

        var packed = Toolchain.Lyric("pack", lyr, "--", "alpha");
        Assert.Equal(ExitCodes.Usage, packed.ExitCode);
        Assert.Contains("when it runs", packed.Err);
    }

    [Fact]
    public void A_compile_error_packs_nothing()
    {
        using var directory = Toolchain.TempDirectory();
        var lyr = directory.Write("broken.lyr", "fn main(): int { return; }");

        var packed = Toolchain.Lyric("pack", lyr);
        Assert.NotEqual(0, packed.ExitCode);
        Assert.False(File.Exists(Exe(directory, "broken")),
            "a program that does not compile must not leave an executable");
    }

    [Fact]
    public void A_failed_pack_leaves_no_output_behind()
    {
        using var directory = Toolchain.TempDirectory();
        var missing = Path.Combine(directory.Path, "never-built.lyrbc");

        var packed = Toolchain.Lyrpack(missing, "-o", Exe(directory));
        Assert.Equal(ExitCodes.Failure, packed.ExitCode);
        Assert.False(File.Exists(Exe(directory)),
            "a pack that failed must not leave a half-written executable");
    }
}
