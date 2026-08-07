using System.Text.RegularExpressions;

namespace Lyric.Tests.Cli;

/// <summary>
/// Die README behauptet Dinge über die Sprache — dieser Test prüft sie nach.
///
/// <para><b>Der Anlass ist ein Befund.</b> Bis M9 stand dort „no working compiler exists yet.
/// Current milestone: M0" — nach acht Meilensteinen und 1700 Tests. Eine Doku, die niemand
/// prüft, driftet; dieselbe Erfahrung wie bei `Sprache.md` §4 (UTF-8 vs. UTF-16) und §2.2
/// (`{value:0&gt;5}`, eine Notation, die nie .NET war).</para>
///
/// <para>Geprüft wird das, was maschinell prüfbar ist: dass das Beispiel im README <b>läuft</b>
/// und die dort gezeigte Ausgabe erzeugt. Prosa bleibt Prosa.</para>
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

        // Die Ausgabe steht im README direkt unter dem Programm. Stimmt sie nicht mehr, ist
        // entweder das Beispiel oder die Behauptung falsch — beides soll auffallen.
        Assert.Contains("|v| = 3.00", result.Out);
        Assert.Contains("area = 19.63", result.Out);
        Assert.Contains("area = 12.00", result.Out);
    }

    [Fact]
    public void The_readme_does_not_claim_there_is_no_compiler()
    {
        // Der konkrete Satz, der acht Meilensteine überlebt hat. Ein Test auf genau ihn ist
        // schmal, aber er kostet nichts und hätte die Peinlichkeit verhindert.
        var readme = File.ReadAllText(Path.Combine(Toolchain.RepositoryRoot, "README.md"));

        Assert.DoesNotContain("no working compiler", readme, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("milestone: M0", readme, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_example_count_in_the_readme_is_right()
    {
        // Eine Zahl in der Doku, die niemand nachzählt, ist irgendwann falsch.
        var readme = File.ReadAllText(Path.Combine(Toolchain.RepositoryRoot, "README.md"));
        var claimed = Regex.Match(readme, @"has (\d+) programs");

        Assert.True(claimed.Success, "README no longer states how many examples there are");

        var actual = Directory.GetFiles(Path.Combine(Toolchain.RepositoryRoot, "examples"), "*.lyr").Length;
        Assert.Equal(actual, int.Parse(claimed.Groups[1].Value));
    }
}
