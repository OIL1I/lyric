namespace Lyric.Tests.Cli;

/// <summary>
/// Die REPL — `lyrrepl`, gefahren als echter Prozess über eine Pipe.
///
/// <para><b>Die zentrale Zusicherung</b> ist die Trennung aus ADR-021: Deklarationen sammeln sich
/// an, Statements laufen einmal. Wer schlicht den Quelltext akkumuliert und alles neu übersetzt,
/// lässt jedes `println` bei jeder folgenden Eingabe erneut laufen — der Test
/// <c>An_earlier_print_does_not_repeat</c> misst genau das, und ohne ihn wäre der Fehler
/// unsichtbar, weil alles andere richtig aussieht.</para>
/// </summary>
public sealed class ReplTests
{
    /// <summary>Fährt eine Sitzung: jede Zeile eine Eingabe, am Ende <c>:quit</c>.</summary>
    private static ToolResult Session(params string[] lines) =>
        Toolchain.RunWithInput(Toolchain.LyrreplPath,
            ["--stdlib", Path.Combine(Toolchain.RepositoryRoot, "stdlib")],
            string.Join('\n', lines.Append(":quit")) + '\n');

    [Fact]
    public void An_expression_prints_its_value() =>
        Assert.Contains("5", Session("2 + 3").Out);

    [Fact]
    public void A_declaration_survives_to_the_next_entry() =>
        // Der Kern einer REPL. Ohne ihn waere sie ein Taschenrechner.
        Assert.Contains("10", Session("let x = 5", "x * 2").Out);

    [Fact]
    public void A_function_survives_too() =>
        Assert.Contains("42", Session(
            "fn double(n: int): int { return n * 2; }",
            "double(21)").Out);

    [Fact]
    public void An_earlier_print_does_not_repeat()
    {
        // DER Test des Slice. Eine REPL, die den Quelltext akkumuliert, druckt 'erste' bei jeder
        // folgenden Eingabe erneut — hier steht es genau einmal.
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
        // Wer sich vertippt, sitzt danach nicht auf einem Vorspann, der nicht mehr uebersetzt.
        // Ohne diese Eigenschaft ist eine Sitzung nach dem ersten Fehler unbrauchbar.
        var result = Session(
            "let good = 1",
            "fn broken(: int { }",
            "good + 1");

        Assert.Contains("2", result.Out);
    }

    [Fact]
    public void A_panic_ends_the_entry_but_not_the_session()
    {
        // In einem Programm beendet ein panic die VM (Sprache.md 9). Interaktiv beendet er die
        // EINGABE — sonst waere jeder Tippfehler das Ende der Sitzung.
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
