using Lyric.Core;

namespace Lyric.Tests.Cli;

/// <summary>
/// A program made of more than one file.
///
/// <para>Through the binaries rather than through <c>SourceCompiler</c>: the module root is the
/// directory of the entry file, and "which directory did the compiler think it was in" is a question
/// only a real invocation answers.</para>
/// </summary>
public class MultiFileTests
{
    [Fact]
    public void A_module_beside_the_entry_file_is_importable()
    {
        using var project = Toolchain.TempDirectory();
        project.Write("util.lyr", """
            module util;

            pub fn double(n: int): int { return n * 2; }
            """);
        var app = project.Write("app.lyr", """
            import util { double };

            fn main(): int { return double(21); }
            """);

        var run = Toolchain.Lyric("run", app);

        Assert.Equal(42, run.ExitCode);
        Assert.Equal("", run.Err);
    }

    [Fact]
    public void A_dotted_import_becomes_a_sub_directory()
    {
        // The same derivation the standard library uses, in the other direction: 'deep.nested' is
        // '<root>/deep/nested.lyr'.
        using var project = Toolchain.TempDirectory();
        project.Write(Path.Combine("deep", "nested.lyr"), """
            module deep.nested;

            pub fn triple(n: int): int { return n * 3; }
            """);
        var app = project.Write("app.lyr", """
            import deep.nested { triple };

            fn main(): int { return triple(14); }
            """);

        Assert.Equal(42, Toolchain.Lyric("run", app).ExitCode);
    }

    [Fact]
    public void A_user_module_may_not_declare_a_native()
    {
        // Whether a module may bind host code follows its ORIGIN, not its content — otherwise
        // naming a file well enough would be a way to reach into the host. It is a COMPILER error,
        // not a load error: a build that reports success and then does not run is the shape this
        // project has paid for before.
        using var project = Toolchain.TempDirectory();
        project.Write("sneaky.lyr", """
            module sneaky;

            pub fn readSecret(path: string): string;
            """);
        var app = project.Write("app.lyr", """
            import sneaky { readSecret };

            fn main(): int { return 0; }
            """);

        var check = Toolchain.Lyrc("check", app);

        Assert.NotEqual(0, check.ExitCode);
        Assert.Contains("LYR-SEM0051", check.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_must_agree_with_the_path_it_was_loaded_from()
    {
        // Without the check the module registers under the name its header claims, the import that
        // pulled it in still finds nothing, and the message says "cannot find" about a file that
        // was just read.
        using var project = Toolchain.TempDirectory();
        project.Write("mismatch.lyr", """
            module util;

            pub fn wrong(): int { return 0; }
            """);
        var app = project.Write("app.lyr", """
            import mismatch { wrong };

            fn main(): int { return wrong(); }
            """);

        var check = Toolchain.Lyrc("check", app);

        Assert.NotEqual(0, check.ExitCode);
        Assert.Contains("LYR-RES0006", check.Err, StringComparison.Ordinal);

        // One diagnostic per cause. The "cannot find module" that used to be the only message is
        // now a consequence, and reporting it too would send the reader to the import instead of
        // to the header that is wrong.
        Assert.DoesNotContain("LYR-RES0003", check.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void The_program_directory_cannot_shadow_the_standard_library()
    {
        // 'std' resolves against the standard library and nothing else. A precedence rule would let
        // this file win SILENTLY, and silently is what makes it a trap rather than a mistake.
        using var project = Toolchain.TempDirectory();
        project.Write(Path.Combine("std", "io", "console.lyr"), """
            module std.io.console;

            pub fn println(text: string) { }
            """);
        var app = project.Write("app.lyr", """
            import std.io.console { println };

            fn main(): int {
                println("the real one");
                return 0;
            }
            """);

        var run = Toolchain.Lyric("run", app);

        Assert.Equal(0, run.ExitCode);

        // The impostor has an empty body, so its output would be nothing at all.
        Assert.Contains("the real one", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cycle_between_user_modules_is_reported()
    {
        // Already true before user modules existed, and only now reachable by anyone. It terminates
        // because registration happens before a module's own imports are examined.
        using var project = Toolchain.TempDirectory();
        project.Write("a.lyr", """
            module a;
            import b { fromB };

            pub fn fromA(): int { return fromB() + 1; }
            """);
        project.Write("b.lyr", """
            module b;
            import a { fromA };

            pub fn fromB(): int { return 1; }
            """);
        var app = project.Write("app.lyr", """
            import a { fromA };

            fn main(): int { return fromA(); }
            """);

        var check = Toolchain.Lyrc("check", app);

        Assert.NotEqual(0, check.ExitCode);
        Assert.Contains("LYR-RES0005", check.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void A_module_header_may_be_left_out_entirely()
    {
        // The grammar makes the header optional. For a loaded module the path is the authority
        // anyway, so a file without one is not a special case — it simply has nothing to disagree
        // with.
        using var project = Toolchain.TempDirectory();
        project.Write("util.lyr", "pub fn double(n: int): int { return n * 2; }");
        var app = project.Write("app.lyr", """
            import util { double };

            fn main(): int { return double(21); }
            """);

        Assert.Equal(42, Toolchain.Lyric("run", app).ExitCode);
    }
}
