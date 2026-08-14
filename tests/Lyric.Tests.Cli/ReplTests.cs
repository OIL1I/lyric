namespace Lyric.Tests.Cli;

/// <summary>
/// The REPL — `lyrrepl`, run as a real process over a pipe.
///
/// <para>THE CENTRAL PROMISE is the separation: declarations accumulate, statements run once. Whoever
/// simply accumulates the source and recompiles everything lets every `println` run again on every
/// following input — the test <c>An_earlier_print_does_not_repeat</c> measures exactly that, and without
/// it the fault would be invisible, because everything else looks right.</para>
/// </summary>
public sealed class ReplTests
{
    /// <summary>Runs a session: one input per line, with <c>:quit</c> at the end.</summary>
    private static ToolResult Session(params string[] lines) =>
        Toolchain.RunWithInput(Toolchain.LyrreplPath,
            ["--stdlib", Path.Combine(Toolchain.RepositoryRoot, "stdlib")],
            string.Join('\n', lines.Append(":quit")) + '\n');

    [Fact]
    public void An_expression_prints_its_value() =>
        Assert.Contains("5", Session("2 + 3").Out);

    [Fact]
    public void A_declaration_survives_to_the_next_entry() =>
        // The core of a REPL. Without it, it would be a calculator.
        Assert.Contains("10", Session("let x = 5", "x * 2").Out);

    [Fact]
    public void A_function_survives_too() =>
        Assert.Contains("42", Session(
            "fn double(n: int): int { return n * 2; }",
            "double(21)").Out);

    [Fact]
    public void An_earlier_print_does_not_repeat()
    {
        // The core test. A REPL accumulating the source prints 'first' again on every following input;
        // here it stands exactly once.
        var result = Session(
            "console.println(\"einmal\")",
            "1 + 1",
            "2 + 2");

        var occurrences = result.Out.Split("einmal").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void A_failed_entry_changes_nothing()
    {
        // Whoever mistypes does not end up with a preamble that no longer compiles. Without this property
        // a session is unusable after the first error.
        var result = Session(
            "let good = 1",
            "fn broken(: int { }",
            "good + 1");

        Assert.Contains("2", result.Out);
    }

    [Fact]
    public void A_panic_ends_the_entry_but_not_the_session()
    {
        // In a program a panic ends the VM. Interactively it ends the INPUT; otherwise every typo would be
        // the end of the session.
        var result = Session(
            "let xs = [1, 2]",
            "xs[5]",
            "7 + 7");

        Assert.Contains("14", result.Out);
    }

    [Fact]
    public void List_shows_what_the_session_remembers()
    {
        var result = Session("let a = 1", "fn f(): int { return 2; }", ":list");

        Assert.Contains("let a = 1", result.Out);
        Assert.Contains("fn f()", result.Out);
    }

    [Fact]
    public void Reset_forgets_the_declarations()
    {
        var result = Session("let a = 1", ":reset", ":list");

        Assert.Contains("nothing declared", result.Out);
    }

    [Fact]
    public void An_unknown_colon_command_is_reported_without_leaving() =>
        Assert.Contains("unknown command", Session(":nope", "1 + 1").Err);

    [Fact]
    public void The_repl_reports_its_version() =>
        Assert.Contains("lyrrepl", Toolchain.Run(
            Toolchain.LyrreplPath, ["--version"]).Out);
}
