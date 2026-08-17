using Lyric.Core;

namespace Lyric.Tests.Cli;

/// <summary>
/// <c>lyric.json</c>: what a project says about itself.
///
/// <para>THE LOAD-BEARING TEST IS THE LAST ONE. Everything here is an addition, and an addition that
/// changes a project without the file is not an addition — it is a break wearing a new name.</para>
///
/// <para>Through the binaries, because the file is found by walking UP from the entry file and
/// "which directory did the tool start from" is only answerable from a real invocation.</para>
/// </summary>
public class ProjectFileTests
{
    /// <summary>A project with a nested module and a host SDK beside it.</summary>
    private static TemporaryDirectory Project(string projectFile)
    {
        var dir = Toolchain.TempDirectory();

        dir.Write("lyric.json", projectFile);
        dir.Write(Path.Combine("src", "shapes", "area.lyr"), """
            module shapes.area;

            pub fn square(n: int): int { return n * n; }
            """);
        dir.Write(Path.Combine("sdk", "engine", "input.lyr"), """
            module engine.input;

            pub fn keyDown(key: int): bool;
            """);
        dir.Write(Path.Combine("src", "main.lyr"), """
            import shapes.area { square };
            import engine.input { keyDown };

            fn main(): int { return if (keyDown(32)) square(7) else 0; }
            """);

        return dir;
    }

    private static string Entry(TemporaryDirectory project) =>
        Path.Combine(project.Path, "src", "main.lyr");

    [Fact]
    public void A_project_file_supplies_the_module_root_and_the_native_roots()
    {
        // Found by walking up from src/main.lyr, so 'shapes.area' resolves under 'src' and
        // 'engine.input' may declare a function without a body.
        using var project = Project("""
            { "sourceRoot": "src", "nativeRoots": { "engine": "sdk" } }
            """);

        var check = Toolchain.Lyrc("check", Entry(project));

        Assert.Equal(ExitCodes.Success, check.ExitCode);
        Assert.Equal("", check.Err);
    }

    [Fact]
    public void Without_the_file_nothing_changes()
    {
        // The additive guarantee, and the reason this is a minor and not a major. The same sources
        // without lyric.json behave exactly as they did before it existed: the entry file's
        // directory is the root, and nothing may declare a native.
        using var project = Toolchain.TempDirectory();
        project.Write(Path.Combine("src", "shapes", "area.lyr"), """
            module shapes.area;

            pub fn square(n: int): int { return n * n; }
            """);
        project.Write(Path.Combine("src", "main.lyr"), """
            import shapes.area { square };

            fn main(): int { return square(7); }
            """);

        // 'shapes.area' still resolves — relative to the ENTRY FILE, which is what v1.1.0 does.
        Assert.Equal(ExitCodes.Success,
            Toolchain.Lyrc("check", Path.Combine(project.Path, "src", "main.lyr")).ExitCode);
    }

    [Fact]
    public void Without_the_file_a_native_root_is_not_reachable()
    {
        // The other half of the same guarantee: the file is what GRANTS the native root, so taking
        // it away has to take the permission with it.
        using var project = Project("""
            { "sourceRoot": "src", "nativeRoots": { "engine": "sdk" } }
            """);
        File.Delete(Path.Combine(project.Path, "lyric.json"));

        var check = Toolchain.Lyrc("check", Entry(project));

        Assert.NotEqual(ExitCodes.Success, check.ExitCode);
        Assert.Contains("LYR-RES0003", check.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_allowed()
    {
        // The usual objection to JSON as a project format, and the reason it does not apply here.
        // Without them this would be the first external dependency in a shipped binary.
        using var project = Project("""
            {
              // where our own modules live
              "sourceRoot": "src",

              /* the engine ships its declarations as .lyr files */
              "nativeRoots": { "engine": "sdk" },
            }
            """);

        Assert.Equal(ExitCodes.Success, Toolchain.Lyrc("check", Entry(project)).ExitCode);
    }

    [Fact]
    public void A_file_that_cannot_be_parsed_is_a_diagnostic()
    {
        // Not a stack trace out of the compiler. The path is in the message because the file the
        // tool complains about is not the one it was pointed at.
        using var project = Project("""{ "sourceRoot": """);

        var check = Toolchain.Lyrc("check", Entry(project));

        Assert.NotEqual(ExitCodes.Success, check.ExitCode);
        Assert.Contains(CliDiagnostics.BadProjectFile, check.Err, StringComparison.Ordinal);
        Assert.Contains("lyric.json", check.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void A_root_that_is_not_a_directory_is_named()
    {
        // Named here rather than as "cannot find module" later, which would send the reader to the
        // import instead of to the line that is wrong.
        using var project = Project("""{ "sourceRoot": "nope" }""");

        var check = Toolchain.Lyrc("check", Entry(project));

        Assert.NotEqual(ExitCodes.Success, check.ExitCode);
        Assert.Contains(CliDiagnostics.BadProjectFile, check.Err, StringComparison.Ordinal);
        Assert.Contains("sourceRoot", check.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void A_native_root_key_has_to_be_one_segment()
    {
        // The compiler looks a native root up by the FIRST segment of an import, so a key with a
        // dot names something that can never be found.
        using var project = Project("""
            { "sourceRoot": "src", "nativeRoots": { "engine.input": "sdk" } }
            """);

        var check = Toolchain.Lyrc("check", Entry(project));

        Assert.NotEqual(ExitCodes.Success, check.ExitCode);
        Assert.Contains(CliDiagnostics.BadProjectFile, check.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_key_warns_and_the_build_carries_on()
    {
        // Tolerated for the same reason the bytecode reader skips a section it does not know: a
        // file written for a later version has to stay readable. The warning is what keeps a typo
        // from being silent.
        using var project = Project("""
            {
              "sourceRot": "src",
              "sourceRoot": "src",
              "nativeRoots": { "engine": "sdk" }
            }
            """);

        var check = Toolchain.Lyrc("check", Entry(project));

        Assert.Equal(ExitCodes.Success, check.ExitCode);
        Assert.Contains("sourceRot", check.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void The_file_is_found_above_the_entry_file()
    {
        // It sits at the project root and the entry file two directories below it. Searching only
        // beside the entry file would make the layout the file describes impossible.
        using var project = Project("""
            { "sourceRoot": "src", "nativeRoots": { "engine": "sdk" } }
            """);
        project.Write(Path.Combine("src", "deep", "nested", "start.lyr"), """
            import shapes.area { square };

            fn main(): int { return square(6); }
            """);

        var run = Toolchain.Lyric("run",
            Path.Combine(project.Path, "src", "deep", "nested", "start.lyr"));

        Assert.Equal(36, run.ExitCode);
    }
}
