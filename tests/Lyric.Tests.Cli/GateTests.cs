namespace Lyric.Tests.Cli;

/// <summary>
/// Das M8-Gate: `examples/wc.lyr` über echte Dateien.
///
/// <para>Es steht nicht in der Beispiel-Matrix, weil es <b>Argumente braucht</b> — ohne sie ist
/// die richtige Antwort eine Usage-Meldung und kein Zählergebnis. Die Matrix ist für Programme,
/// die ohne Zutun ein definiertes Ergebnis liefern.</para>
///
/// <para><b>Warum ein `wc`-Klon das Gate ist</b>: er belastet die Stdlib quer statt in die Tiefe —
/// Dateien lesen (S7, `fileAccess`), Strings zerlegen (S2), in einer Liste sammeln (S5),
/// formatiert ausgeben (S3), Einstieg mit Argumenten (§11). Fällt eines davon aus, läuft er
/// nicht. Zwei Fehler hat er beim Bauen gefunden, die kein Slice-Test bemerkt hatte.</para>
/// </summary>
public sealed class GateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "lyric-gate-" + Guid.NewGuid().ToString("N")[..8]);

    public GateTests()
    {
        Directory.CreateDirectory(_dir);

        // Vier Zeilen, sechs Wörter, 33 Zeichen — dieselben Zahlen, die POSIX-wc liefert.
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
        Assert.Equal("4", columns[0]);   // Zeilen
        Assert.Equal("6", columns[1]);   // Wörter
        Assert.Equal("33", columns[2]);  // Zeichen
    }

    [Fact]
    public void Several_files_get_a_total()
    {
        var result = Toolchain.Lyric("run", Toolchain.Example("wc.lyr"), "--", Three, NoNewline);
        var lines = result.Out.Trim().ReplaceLineEndings("\n").Split('\n');

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(3, lines.Length);           // zwei Dateien plus Summe
        Assert.EndsWith("total", lines[^1]);

        // 33 + 27 Zeichen. Die Summe ist die interessante Zeile: sie prüft, dass die Ergebnisse
        // wirklich in der Liste landen und wieder herauskommen.
        Assert.Equal("60", Columns(lines[^1])[2]);
    }

    [Fact]
    public void A_single_file_gets_no_total() =>
        // Eine Summenzeile bei einer Datei wäre eine Wiederholung.
        Assert.Single(Toolchain.Lyric("run", Toolchain.Example("wc.lyr"), "--", Three)
            .Out.Trim().ReplaceLineEndings("\n").Split('\n'));

    [Fact]
    public void A_file_without_a_trailing_newline_still_has_one_line()
    {
        // Bewusste Abweichung von POSIX: `wc -l` zählt Zeilenumbrüche, nicht Zeilen — für POSIX
        // hat diese Datei null Zeilen. Für ein Beispielprogramm ist die intuitive Zählung die
        // bessere, und der Test hält fest, dass es eine Entscheidung war und kein Zufall.
        var result = Toolchain.Lyric("run", Toolchain.Example("wc.lyr"), "--", NoNewline);

        Assert.Equal("1", Columns(result.Out.Trim())[0]);
        Assert.Equal("5", Columns(result.Out.Trim())[1]);
    }

    [Fact]
    public void A_missing_file_is_reported_but_not_fatal()
    {
        // Wie das echte `wc`: eine nicht lesbare Datei ist ein Fehler, aber kein Grund, die
        // anderen nicht zu zählen.
        var result = Toolchain.Lyric("run", Toolchain.Example("wc.lyr"), "--",
            Path.Combine(_dir, "gibtsnicht.txt"), Three);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("cannot read", result.Err);
        Assert.Contains("33", result.Out);   // die lesbare Datei wurde trotzdem gezählt
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
