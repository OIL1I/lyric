using Lyric.Core;

namespace Lyric.Tests.Cli;

/// <summary>
/// <c>lyrfmt</c> across the process boundary: in place, <c>--check</c>, <c>--stdin</c>, the
/// driver's <c>lyric fmt</c>, and the duty on a broken file — reported, untouched, and the run
/// still fails at the end.
/// </summary>
public class FmtTests
{
    private const string Messy = "fn   main( ):int{return   0;}";
    private const string Clean = "fn main(): int {\n    return 0;\n}\n";

    [Fact]
    public void A_file_is_formatted_in_place()
    {
        using var directory = Toolchain.TempDirectory();
        var file = directory.Write("app.lyr", Messy);

        var result = Toolchain.Lyrfmt(file);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Clean, File.ReadAllText(file));
    }

    [Fact]
    public void An_already_formatted_file_is_left_alone()
    {
        using var directory = Toolchain.TempDirectory();
        var file = directory.Write("app.lyr", Clean);
        var before = File.GetLastWriteTimeUtc(file);

        var result = Toolchain.Lyrfmt(file);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(before, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public void A_directory_means_every_lyr_under_it()
    {
        using var directory = Toolchain.TempDirectory();
        var top = directory.Write("a.lyr", Messy);
        var nested = directory.Write(Path.Combine("sub", "b.lyr"), Messy);

        var result = Toolchain.Lyrfmt(directory.Path);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Clean, File.ReadAllText(top));
        Assert.Equal(Clean, File.ReadAllText(nested));
    }

    [Fact]
    public void Check_lists_and_fails_but_writes_nothing()
    {
        using var directory = Toolchain.TempDirectory();
        var file = directory.Write("app.lyr", Messy);

        var result = Toolchain.Lyrfmt(file, "--check");
        Assert.Equal(ExitCodes.Failure, result.ExitCode);
        Assert.Contains("app.lyr", result.Out);
        Assert.Equal(Messy, File.ReadAllText(file)); // nothing written

        directory.Write("app.lyr", Clean);
        var clean = Toolchain.Lyrfmt(file, "--check");
        Assert.Equal(0, clean.ExitCode);
        Assert.Equal("", clean.Out);
    }

    [Fact]
    public void Stdin_formats_to_stdout()
    {
        var result = Toolchain.RunWithInput(Toolchain.LyrfmtPath, ["--stdin"], Messy);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Clean, result.Out);
    }

    [Fact]
    public void A_broken_file_is_reported_untouched_and_fails_the_run()
    {
        using var directory = Toolchain.TempDirectory();
        var broken = directory.Write("broken.lyr", "fn f( {");
        var fine = directory.Write("fine.lyr", Messy);

        var result = Toolchain.Lyrfmt(directory.Path);
        Assert.Equal(ExitCodes.Failure, result.ExitCode);
        Assert.Contains("broken.lyr", result.Err);
        Assert.Equal("fn f( {", File.ReadAllText(broken)); // exactly as it was

        // The broken neighbour did not stop the clean one from being formatted.
        Assert.Equal(Clean, File.ReadAllText(fine));
    }

    [Fact]
    public void The_driver_forwards_fmt()
    {
        using var directory = Toolchain.TempDirectory();
        var file = directory.Write("app.lyr", Messy);

        var result = Toolchain.Lyric("fmt", file);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Clean, File.ReadAllText(file));
    }

    [Fact]
    public void Stdin_does_not_combine_with_paths_or_check()
    {
        var result = Toolchain.RunWithInput(Toolchain.LyrfmtPath, ["--stdin", "--check"], Messy);
        Assert.Equal(ExitCodes.Usage, result.ExitCode);
    }
}
