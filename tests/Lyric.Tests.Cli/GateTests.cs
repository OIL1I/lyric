namespace Lyric.Tests.Cli;

/// <summary>
/// The gate: `examples/wc.lyr` over real files.
///
/// <para>It does not stand in the example matrix, because it NEEDS ARGUMENTS: without them the right
/// answer is a usage message rather than a count. The matrix is for programs delivering a defined result
/// without help.</para>
///
/// <para>WHY A `wc` CLONE IS THE GATE: it loads the stdlib across rather than in depth — reading files,
/// splitting strings, collecting into a list, formatted output, an entry point with arguments. If one of
/// those fails, it does not run. It found two faults while being built that no slice test had noticed.
/// </para>
/// </summary>
public sealed class GateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "lyric-gate-" + Guid.NewGuid().ToString("N")[..8]);

    public GateTests()
    {
        Directory.CreateDirectory(_dir);

        // Four lines, six words, 33 characters: the same numbers POSIX wc yields.
        File.WriteAllText(Three, "eins zwei drei\nvier fuenf\n\nsechs\n");
        File.WriteAllText(NoNewline, "nur eine zeile ohne umbruch");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string Three => Path.Combine(_dir, "three.txt");
    private string NoNewline => Path.Combine(_dir, "flat.txt");

    private static string[] Columns(string line) =>
        line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void It_counts_like_wc()
    {
        var result = Toolchain.Lyric("run", Toolchain.Example("wc.lyr"), "--", Three);

        Assert.Equal(0, result.ExitCode);

        var columns = Columns(result.Out.Trim());
        Assert.Equal("4", columns[0]);   // lines
        Assert.Equal("6", columns[1]);   // words
        Assert.Equal("33", columns[2]);  // Zeichen
    }

    [Fact]
    public void Several_files_get_a_total()
    {
        var result = Toolchain.Lyric("run", Toolchain.Example("wc.lyr"), "--", Three, NoNewline);
        var lines = result.Out.Trim().ReplaceLineEndings("\n").Split('\n');

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(3, lines.Length);           // two files plus the total
        Assert.EndsWith("total", lines[^1]);

        // 33 plus 27 characters. The total is the interesting line: it checks that the results really land
        // in the list and come out again.
        Assert.Equal("60", Columns(lines[^1])[2]);
    }

    [Fact]
    public void A_single_file_gets_no_total() =>
        // A total line for one file would be a repetition.
        Assert.Single(Toolchain.Lyric("run", Toolchain.Example("wc.lyr"), "--", Three)
            .Out.Trim().ReplaceLineEndings("\n").Split('\n'));

    [Fact]
    public void A_file_without_a_trailing_newline_still_has_one_line()
    {
        // A deliberate deviation from POSIX: `wc -l` counts line breaks rather than lines, so for POSIX
        // this file has zero lines. For an example program the intuitive count is the better one, and the
        // test holds that it was a decision rather than an accident.
        var result = Toolchain.Lyric("run", Toolchain.Example("wc.lyr"), "--", NoNewline);

        Assert.Equal("1", Columns(result.Out.Trim())[0]);
        Assert.Equal("5", Columns(result.Out.Trim())[1]);
    }

    [Fact]
    public void A_missing_file_is_reported_but_not_fatal()
    {
        // As with the real `wc`: an unreadable file is an error but no reason not to count the others.
        var result = Toolchain.Lyric("run", Toolchain.Example("wc.lyr"), "--",
            Path.Combine(_dir, "gibtsnicht.txt"), Three);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("cannot read", result.Err);
        Assert.Contains("33", result.Out);   // the readable file was counted all the same
    }

    [Fact]
    public void Without_arguments_it_prints_usage()
    {
        var result = Toolchain.Lyric("run", Toolchain.Example("wc.lyr"));

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("usage", result.Err);
        Assert.Equal("", result.Out);
    }
}
