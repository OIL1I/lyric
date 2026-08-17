using Lyric.Core;

namespace Lyric.Tests.Cli;

/// <summary>
/// <c>lyric new</c>: a project that builds.
///
/// <para>THE LOAD-BEARING TEST IS THE FIRST ONE. A scaffold whose output does not compile is worse
/// than none, because the first thing it teaches is wrong — and it would be found by a user rather
/// than here.</para>
///
/// <para>The last one is why the templates are files in the repository instead of strings in C#:
/// they are compilable Lyric, and this compiles them.</para>
/// </summary>
public class NewProjectTests
{
    /// <summary>Runs the driver with the temporary directory as ITS working directory: <c>new</c>
    /// writes relative to where it was called, and every other call in this project runs in the
    /// repository root.</summary>
    private static ToolResult NewIn(TemporaryDirectory directory, params string[] args) =>
        Toolchain.RunIn(directory.Path, Toolchain.LyricPath, args);

    [Fact]
    public void A_new_app_builds_and_runs()
    {
        using var workspace = Toolchain.TempDirectory();

        Assert.Equal(ExitCodes.Success, NewIn(workspace, "new", "demo").ExitCode);

        var project = Path.Combine(workspace.Path, "demo");
        Assert.Equal(ExitCodes.Success, Toolchain.Lyric("build", project).ExitCode);

        var run = Toolchain.Lyric("run", Path.Combine(project, "out", "demo.lyrbc"));
        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Contains("Hello, Lyric!", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void The_output_is_named_after_the_project()
    {
        // The one place the name appears in the app template, and the reason there is a placeholder
        // at all.
        using var workspace = Toolchain.TempDirectory();
        NewIn(workspace, "new", "tetris");

        Assert.Equal(ExitCodes.Success,
            Toolchain.Lyric("build", Path.Combine(workspace.Path, "tetris")).ExitCode);
        Assert.True(File.Exists(Path.Combine(workspace.Path, "tetris", "out", "tetris.lyrbc")));
    }

    [Fact]
    public void A_library_names_its_module_file_after_the_project()
    {
        // The placeholder applies to path segments as well as contents: 'import mylib' has to find
        // src/mylib.lyr, and the header inside has to agree with the path it was loaded from.
        using var workspace = Toolchain.TempDirectory();

        Assert.Equal(ExitCodes.Success, NewIn(workspace, "new", "mylib", "--lib").ExitCode);

        var module = Path.Combine(workspace.Path, "mylib", "src", "mylib.lyr");
        Assert.True(File.Exists(module));
        Assert.Contains("module mylib;", File.ReadAllText(module), StringComparison.Ordinal);

        // And nothing to build: a library is source someone imports.
        Assert.False(File.Exists(Path.Combine(workspace.Path, "mylib", "build.lyr")));
    }

    [Fact]
    public void A_library_can_be_imported_by_a_program()
    {
        // Both templates together, which is the only way to see that the library one is usable.
        using var workspace = Toolchain.TempDirectory();
        NewIn(workspace, "new", "greeter", "--lib");

        var project = workspace.Write(Path.Combine("user", "lyric.json"),
            $$"""{ "sourceRoot": "{{Path.Combine(workspace.Path, "greeter", "src").Replace('\\', '/')}}" }""");
        var entry = workspace.Write(Path.Combine("user", "main.lyr"), """
            import greeter { greeting };
            import std.io.console { println };

            fn main(): int {
                println(greeting());
                return 0;
            }
            """);

        Assert.NotNull(project);
        var run = Toolchain.Lyric("run", entry);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Contains("Hello, Lyric!", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void The_ignore_file_arrives_with_its_dot()
    {
        // Stored as 'gitignore' so it does not take effect in the repository that ships it, and
        // written with the dot where it is meant to.
        using var workspace = Toolchain.TempDirectory();
        NewIn(workspace, "new", "demo");

        Assert.True(File.Exists(Path.Combine(workspace.Path, "demo", ".gitignore")));
        Assert.False(File.Exists(Path.Combine(workspace.Path, "demo", "gitignore")));
    }

    [Fact]
    public void A_name_that_is_not_an_identifier_is_refused()
    {
        // It becomes a module name. Refused here rather than as a parse error in a file the user
        // did not write.
        using var workspace = Toolchain.TempDirectory();

        var result = NewIn(workspace, "new", "3d-engine");

        Assert.NotEqual(ExitCodes.Success, result.ExitCode);
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "3d-engine")));
    }

    [Fact]
    public void A_directory_that_holds_something_is_refused()
    {
        // Merging would overwrite a build.lyr someone had already changed.
        using var workspace = Toolchain.TempDirectory();
        workspace.Write(Path.Combine("demo", "build.lyr"), "// mine");

        var result = NewIn(workspace, "new", "demo");

        Assert.NotEqual(ExitCodes.Success, result.ExitCode);
        Assert.Equal("// mine",
            File.ReadAllText(Path.Combine(workspace.Path, "demo", "build.lyr")));
    }

    [Fact]
    public void Missing_the_name_says_what_to_type()
    {
        using var workspace = Toolchain.TempDirectory();

        var result = NewIn(workspace, "new");

        Assert.NotEqual(ExitCodes.Success, result.ExitCode);
        Assert.Contains("lyric new myapp", result.Err, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("app", "build.lyr")]
    [InlineData("app", "src/main.lyr")]
    [InlineData("lib", "src/__name__.lyr")]
    public void Every_template_source_file_compiles_as_it_stands(string template, string file)
    {
        // Why the templates are files rather than strings in C#. '__name__' is a valid Lyric
        // identifier, so a template is compilable Lyric and not text with holes in it — and a
        // template that stopped compiling would be a red test rather than a first impression.
        var path = Path.Combine(Toolchain.RepositoryRoot, "templates", template,
            Path.Combine(file.Split('/')));

        Assert.True(File.Exists(path), $"{path} is missing");
        Assert.Equal(ExitCodes.Success, Toolchain.Lyrc("check", path).ExitCode);
    }
}
