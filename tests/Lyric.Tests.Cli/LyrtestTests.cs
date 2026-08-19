namespace Lyric.Tests.Cli;

/// <summary>
/// <c>lyrtest</c>, end to end: a fixture project with a <c>tests/</c> directory, the runner
/// finding the <c>@Test</c> rows through the same attribute machinery a host uses, one fresh
/// instance per test, and the exit code carrying the verdict.
/// </summary>
public sealed class LyrtestTests
{
    private const string Library =
        "module mathx;\n\npub fn double(n: int): int {\n    return n * 2;\n}\n";

    private static TemporaryDirectory Project(string testSource)
    {
        var directory = Toolchain.TempDirectory();
        directory.Write("lyric.json", "{ \"sourceRoot\": \"src\" }\n");
        directory.Write(Path.Combine("src", "mathx.lyr"), Library);
        directory.Write(Path.Combine("tests", "math_tests.lyr"), testSource);
        return directory;
    }

    [Fact]
    public void A_passing_project_reports_and_exits_zero()
    {
        using var project = Project(
            "import std.test { Test, assertEq };\nimport mathx { double };\n\n"
            + "@Test\npub fn doubles(): void {\n    assertEq(double(2), 4);\n}\n");
        var result = Toolchain.Lyrtest(project.Path);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("PASS math_tests.doubles", result.Out);
        Assert.Contains("1 test(s), all passed", result.Out);
    }

    [Fact]
    public void A_failing_assertion_names_both_values_and_fails_the_run()
    {
        using var project = Project(
            "import std.test { Test, assertEq };\nimport mathx { double };\n\n"
            + "@Test\npub fn wrong(): void {\n    assertEq(double(2), 5);\n}\n");
        var result = Toolchain.Lyrtest(project.Path);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("FAIL math_tests.wrong: expected 5, got 4", result.Out);
        Assert.Contains("1 test(s), 1 FAILED", result.Out);
    }

    [Fact]
    public void Tests_run_in_fresh_instances()
    {
        // Two tests over one module-level counter object: were the instance shared, the second
        // would see the first one's increment and fail.
        using var project = Project(
            "import std.test { Test, assertEq };\n\n"
            + "class Counter {\n    n: int,\n}\n\n"
            + "let state = Counter { n = 0 };\n\n"
            + "fn bump(): int {\n    state.n = state.n + 1;\n    return state.n;\n}\n\n"
            + "@Test\npub fn first(): void {\n    assertEq(bump(), 1);\n}\n\n"
            + "@Test\npub fn second(): void {\n    assertEq(bump(), 1);\n}\n");
        var result = Toolchain.Lyrtest(project.Path);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("2 test(s), all passed", result.Out);
    }

    [Fact]
    public void A_project_without_a_tests_directory_has_no_tests()
    {
        using var project = Toolchain.TempDirectory();
        project.Write("lyric.json", "{ }\n");
        var result = Toolchain.Lyrtest(project.Path);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("no tests", result.Out);
    }

    [Fact]
    public void A_named_testRoot_that_does_not_exist_is_an_error()
    {
        // The named root is a promise; the DEFAULT root is a convention. Only the promise can be
        // broken — the project file validates its own paths, and the runner reports it the way
        // every tool reports a broken lyric.json.
        using var project = Toolchain.TempDirectory();
        project.Write("lyric.json", "{ \"testRoot\": \"checks\" }\n");
        var result = Toolchain.Lyrtest(project.Path);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("error[LYR-CLI0010]", result.Err);
        Assert.Contains("'testRoot' names 'checks'", result.Err);
    }

    [Fact]
    public void A_test_file_that_does_not_compile_fails_with_its_diagnostics()
    {
        using var project = Project(
            "import std.test { Test };\n\n@Test\npub fn broken(): void {\n    return nonsense;\n}\n");
        var result = Toolchain.Lyrtest(project.Path);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("error[LYR-SEM0002]", result.Err);
        Assert.Contains("nonsense", result.Err);
    }

    [Fact]
    public void A_panicking_test_fails_with_the_panic()
    {
        using var project = Project(
            "import std.test { Test };\n\n"
            + "@Test\npub fn divides(): void {\n    let zero = 0;\n    let _ = 1 / zero;\n}\n");
        var result = Toolchain.Lyrtest(project.Path);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("FAIL math_tests.divides", result.Out);
        Assert.Contains("division by zero", result.Out);
    }
}
