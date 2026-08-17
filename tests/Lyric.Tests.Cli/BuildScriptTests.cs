using Lyric.Core;

namespace Lyric.Tests.Cli;

/// <summary>
/// <c>build.lyr</c>: a Lyric program that drives the Lyric compiler.
///
/// <para>What separates it from a manifest is that it may WORK — write a file, run a generator —
/// and one of the tests below does exactly that. Without it the script would be a list of values
/// with parentheses around them, and those belong in <c>lyric.json</c>.</para>
///
/// <para>Nothing is compiled while the script runs. It collects, and the compiles happen once
/// <c>build</c> has returned; the ordering test is what pins that.</para>
/// </summary>
public class BuildScriptTests
{
    private static TemporaryDirectory Project(string buildScript, string? projectFile = null)
    {
        var dir = Toolchain.TempDirectory();

        dir.Write("build.lyr", buildScript);
        if (projectFile is not null) dir.Write("lyric.json", projectFile);

        dir.Write(Path.Combine("src", "main.lyr"), """
            import std.io.console { println };

            fn main(): int {
                println("built");
                return 7;
            }
            """);

        return dir;
    }

    [Fact]
    public void A_script_compiles_what_it_declares_and_the_result_runs()
    {
        using var project = Project("""
            import std.build { addExecutable };

            pub fn build() {
                addExecutable("src/main.lyr", "out/app.lyrbc");
            }
            """);

        var build = Toolchain.Lyrbuild(project.Path);
        Assert.Equal(ExitCodes.Success, build.ExitCode);

        var output = Path.Combine(project.Path, "out", "app.lyrbc");
        Assert.True(File.Exists(output), $"{output} was not written");

        var run = Toolchain.Lyrvm("run", output);
        Assert.Equal(7, run.ExitCode);
        Assert.Contains("built", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void An_option_set_after_the_declaration_still_applies()
    {
        // The reason nothing is compiled while the script runs. 'sourceMap' stands on the line
        // AFTER the call that produced the artifact; compiling eagerly would have missed it.
        using var project = Project("""
            import std.build { addExecutable };

            pub fn build() {
                let app = addExecutable("src/main.lyr", "out/stripped.lyrbc");
                app.sourceMap(false);

                addExecutable("src/main.lyr", "out/mapped.lyrbc");
            }
            """);

        Assert.Equal(ExitCodes.Success, Toolchain.Lyrbuild(project.Path).ExitCode);

        var stripped = new FileInfo(Path.Combine(project.Path, "out", "stripped.lyrbc"));
        var mapped = new FileInfo(Path.Combine(project.Path, "out", "mapped.lyrbc"));

        Assert.True(stripped.Exists && mapped.Exists);
        Assert.True(stripped.Length < mapped.Length,
            $"stripped {stripped.Length} should be smaller than mapped {mapped.Length}");
    }

    [Fact]
    public void A_script_may_generate_a_source_file_and_compile_it()
    {
        // What a script buys over a manifest. It runs with every capability, so it writes the file
        // during 'build' — and the compile that reads it happens afterwards.
        using var project = Project("""
            import std.build { addExecutable };
            import std.io.file { writeText };

            pub fn build() {
                writeText("src/generated.lyr", "fn main(): int { return 42; }");
                addExecutable("src/generated.lyr", "out/generated.lyrbc");
            }
            """);

        var build = Toolchain.Lyrbuild(project.Path);
        Assert.Equal(ExitCodes.Success, build.ExitCode);

        Assert.Equal(42,
            Toolchain.Lyrvm("run", Path.Combine(project.Path, "out", "generated.lyrbc")).ExitCode);
    }

    [Fact]
    public void The_project_file_supplies_the_roots()
    {
        // The split: lyric.json says what the project IS, build.lyr says what to build. The script
        // names no root of its own.
        using var project = Project("""
            import std.build { addExecutable };

            pub fn build() {
                addExecutable("src/main.lyr", "out/app.lyrbc");
            }
            """, projectFile: """{ "sourceRoot": "src" }""");

        project.Write(Path.Combine("src", "helper.lyr"), """
            module helper;

            pub fn nine(): int { return 9; }
            """);
        project.Write(Path.Combine("src", "main.lyr"), """
            import helper { nine };

            fn main(): int { return nine(); }
            """);

        Assert.Equal(ExitCodes.Success, Toolchain.Lyrbuild(project.Path).ExitCode);
        Assert.Equal(9,
            Toolchain.Lyrvm("run", Path.Combine(project.Path, "out", "app.lyrbc")).ExitCode);
    }

    [Fact]
    public void A_directory_without_a_build_script_says_so()
    {
        using var empty = Toolchain.TempDirectory();

        var build = Toolchain.Lyrbuild(empty.Path);

        Assert.NotEqual(ExitCodes.Success, build.ExitCode);
        Assert.Contains(CliDiagnostics.NoBuildScript, build.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void A_script_that_does_not_compile_reports_its_own_diagnostics()
    {
        // With file, line and column, like every other Lyric error. The embedding exception carries
        // the diagnostics as data but not the spans' source, so this path compiles once more to
        // render them — and that it does is what this test pins.
        using var project = Project("""
            import std.build { addExecutable };

            pub fn build() {
                addExecutable("src/main.lyr");
            }
            """);

        var build = Toolchain.Lyrbuild(project.Path);

        Assert.NotEqual(ExitCodes.Success, build.ExitCode);
        Assert.Contains("build.lyr:", build.Err, StringComparison.Ordinal);
        Assert.DoesNotContain("Diagnostic {", build.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void A_script_without_a_build_function_says_so()
    {
        using var project = Project("""
            import std.build { addExecutable };

            pub fn assemble() {
                addExecutable("src/main.lyr", "out/app.lyrbc");
            }
            """);

        var build = Toolchain.Lyrbuild(project.Path);

        Assert.NotEqual(ExitCodes.Success, build.ExitCode);
        Assert.Contains(CliDiagnostics.BuildScriptFailed, build.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void A_script_that_declares_nothing_is_an_error()
    {
        // Silence would look like success and leave nothing behind, which is the one outcome a
        // build must never have.
        using var project = Project("""
            import std.io.console { println };

            pub fn build() { println("thinking about it"); }
            """);

        var build = Toolchain.Lyrbuild(project.Path);

        Assert.NotEqual(ExitCodes.Success, build.ExitCode);
        Assert.Contains(CliDiagnostics.BuildScriptFailed, build.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entry_file_that_is_not_there_is_named()
    {
        using var project = Project("""
            import std.build { addExecutable };

            pub fn build() {
                addExecutable("src/absent.lyr", "out/app.lyrbc");
            }
            """);

        var build = Toolchain.Lyrbuild(project.Path);

        Assert.NotEqual(ExitCodes.Success, build.ExitCode);
        Assert.Contains("absent.lyr", build.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void The_driver_sends_a_directory_here_and_a_file_to_the_compiler()
    {
        // Two different questions behind one verb: "build this project" and "compile this file".
        using var project = Project("""
            import std.build { addExecutable };

            pub fn build() {
                addExecutable("src/main.lyr", "out/app.lyrbc");
            }
            """);

        Assert.Equal(ExitCodes.Success, Toolchain.Lyric("build", project.Path).ExitCode);
        Assert.True(File.Exists(Path.Combine(project.Path, "out", "app.lyrbc")));

        using var single = Toolchain.Temp(".lyrbc");
        Assert.Equal(ExitCodes.Success,
            Toolchain.Lyric("build", Path.Combine(project.Path, "src", "main.lyr"),
                "-o", single.Path).ExitCode);
        Assert.True(File.Exists(single.Path));
    }
}
