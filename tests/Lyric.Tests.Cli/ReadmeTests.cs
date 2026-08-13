using System.Text.RegularExpressions;

namespace Lyric.Tests.Cli;

/// <summary>
/// The README claims things about the language, and this test checks them.
///
/// <para>It used to say "no working compiler exists yet. Current milestone: M0" after eight milestones
/// and 1700 tests. Documentation nobody checks drifts; the same experience as with the grammar (UTF-8
/// against UTF-16) and the format spec (<c>{value:0&gt;5}</c>, a notation that was never .NET).</para>
///
/// <para>What is checked is what is mechanically checkable: that the example in the README RUNS and
/// produces the output shown there. Prose stays prose.</para>
/// </summary>
public sealed class ReadmeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "lyric-readme-" + Guid.NewGuid().ToString("N")[..8]);

    public ReadmeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void The_readme_example_compiles_and_runs()
    {
        var readme = File.ReadAllText(Path.Combine(Toolchain.RepositoryRoot, "README.md"));
        var block = Regex.Match(readme, "```lyr\r?\n(.*?)```", RegexOptions.Singleline);

        Assert.True(block.Success, "README has no ```lyr block — did the quick taste move?");

        var path = Path.Combine(_dir, "taste.lyr");
        File.WriteAllText(path, block.Groups[1].Value);

        var result = Toolchain.Lyric("run", path);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Err);

        // The expected output is READ FROM the README rather than written here: the block directly below
        // the program is the claim, and that is what should be checked. Standing here, the test would
        // check its own copy and let the README drift.
        var claimed = Regex.Match(readme[(block.Index + block.Length)..],
            @"```\r?\n(.*?)```", RegexOptions.Singleline);
        Assert.True(claimed.Success, "README does not show the example's output");

        foreach (var line in claimed.Groups[1].Value
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Assert.Contains(line.Trim(), result.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void The_readme_does_not_claim_there_is_no_compiler()
    {
        // The concrete sentence that survived eight milestones. A test on exactly it is narrow, but it
        // costs nothing and would have prevented the embarrassment.
        var readme = File.ReadAllText(Path.Combine(Toolchain.RepositoryRoot, "README.md"));

        Assert.DoesNotContain("no working compiler", readme, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("milestone: M0", readme, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What the README and the CI job call as a command also lies in the repository.
    ///
    /// <para><c>build/publish.proj</c> was covered by <c>.gitignore</c> (<c>build/</c>, meaning outputs)
    /// and therefore lay in no clone. The CI step "Publish toolchain" called it all the same — it never
    /// ran, because <c>needs:</c> skipped it while the tests were red. The README names the same command
    /// under "Shipping": whoever followed the text got an error.</para>
    ///
    /// <para>The file existed the whole time on the maintainer's disk. <c>File.Exists</c> would have found
    /// nothing — the question is not whether it is there but whether it is SHIPPED. Only git can answer
    /// that.</para>
    /// </summary>
    [Fact]
    public void Every_file_the_ci_job_invokes_is_in_the_repository()
    {
        var workflow = File.ReadAllText(Path.Combine(
            Toolchain.RepositoryRoot, ".github", "workflows", "ci.yml"));

        var invoked = Regex.Matches(workflow, @"dotnet msbuild (\S+)")
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .ToArray();

        Assert.NotEmpty(invoked);

        var readme = File.ReadAllText(Path.Combine(Toolchain.RepositoryRoot, "README.md"));

        foreach (var path in invoked)
        {
            var tracked = Toolchain.Run("git", "ls-files", "--error-unmatch", path);
            Assert.True(tracked.ExitCode == 0,
                $"CI runs 'dotnet msbuild {path}', but that file is not tracked by git — "
                + "a fresh clone cannot run it. Check .gitignore.");

            // The same file stands under "Shipping" in the README. Were the CI job to run something else in
            // future, the instructions for the human would be the outdated half.
            Assert.Contains(path, readme, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The project tree in the README names every project from <c>src/</c>.
    ///
    /// <para><c>Lyrrepl/</c> was missing from the tree while the section two screens below is called "The
    /// four binaries" — the same file contradicting itself. A tree is a second description of the
    /// directory beside it, and two descriptions drift; the TextMate grammar hangs on the same lexer.
    /// </para>
    /// </summary>
    [Fact]
    public void The_project_tree_in_the_readme_names_every_source_project()
    {
        var readme = File.ReadAllText(Path.Combine(Toolchain.RepositoryRoot, "README.md"));

        var projects = Directory
            .GetDirectories(Path.Combine(Toolchain.RepositoryRoot, "src"))
            .Select(Path.GetFileName)
            .Where(name => File.Exists(Path.Combine(
                Toolchain.RepositoryRoot, "src", name!, name + ".csproj")))
            .ToArray();

        Assert.NotEmpty(projects);

        foreach (var project in projects)
            Assert.Contains($"{project}/", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void The_example_count_in_the_readme_is_right()
    {
        // A number in the documentation that nobody recounts is wrong sooner or later.
        var readme = File.ReadAllText(Path.Combine(Toolchain.RepositoryRoot, "README.md"));
        var claimed = Regex.Match(readme, @"has (\d+) programs");

        Assert.True(claimed.Success, "README no longer states how many examples there are");

        var actual = Directory.GetFiles(Path.Combine(Toolchain.RepositoryRoot, "examples"), "*.lyr").Length;
        Assert.Equal(actual, int.Parse(claimed.Groups[1].Value));
    }
}
