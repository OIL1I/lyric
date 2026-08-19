namespace Lyric.Tests.Cli;

/// <summary>
/// The standard library's own tests, written in Lyric under <c>stdlib-tests/</c> and run by
/// <c>lyrtest</c> — the dogfood loop closed: the library is exercised by the language it is
/// written in, through the runner the toolchain ships. What they assert lives in the
/// <c>.lyr</c> files; this test holds the loop itself green.
/// </summary>
public sealed class StdlibSelfTests
{
    [Fact]
    public void The_standard_library_passes_its_own_tests()
    {
        var result = Toolchain.Lyrtest(Path.Combine(Toolchain.RepositoryRoot, "stdlib-tests"));

        Assert.True(result.ExitCode == 0,
            $"the stdlib's own tests failed:\n{result.Out}\n{result.Err}");
        Assert.Contains("all passed", result.Out);
        Assert.DoesNotContain("FAIL", result.Out);
    }
}
