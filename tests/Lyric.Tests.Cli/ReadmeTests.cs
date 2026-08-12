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

        // Die erwartete Ausgabe wird aus dem README GELESEN, nicht hier hineingeschrieben: der
        // Block direkt unter dem Programm ist die Behauptung, und genau die soll geprueft werden.
        // Stuende sie hier, pruefte der Test seine eigene Kopie und liesse den README driften.
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
        // Der konkrete Satz, der acht Meilensteine überlebt hat. Ein Test auf genau ihn ist
        // schmal, aber er kostet nichts und hätte die Peinlichkeit verhindert.
        var readme = File.ReadAllText(Path.Combine(Toolchain.RepositoryRoot, "README.md"));

        Assert.DoesNotContain("no working compiler", readme, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("milestone: M0", readme, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Was die README und der CI-Job als Kommando aufrufen, liegt auch im Repository.
    ///
    /// <para><b>Der Anlass ist ein Befund.</b> <c>build/publish.proj</c> war von
    /// <c>.gitignore</c> erfasst (<c>build/</c>, gemeint waren Ausgaben) und lag deshalb in
    /// keinem Clone. Der CI-Schritt „Publish toolchain" rief es trotzdem auf — er ist nie
    /// gelaufen, weil <c>needs:</c> ihn uebersprang, solange die Tests rot waren. Die README
    /// nennt dasselbe Kommando unter „Shipping": wer dem Text folgte, bekam einen Fehler.</para>
    ///
    /// <para>Die Datei existierte die ganze Zeit auf der Platte des Maintainers. <c>File.Exists</c>
    /// haette also nichts gefunden — die Frage ist nicht, ob sie da ist, sondern ob sie
    /// <b>ausgeliefert</b> wird. Nur git kann das beantworten.</para>
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

            // Dieselbe Datei steht unter „Shipping" in der README. Liefe der CI-Job kuenftig
            // etwas anderes, waere die Anleitung fuer den Menschen die veraltete Haelfte.
            Assert.Contains(path, readme, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Der Projektbaum in der README nennt jedes Projekt aus <c>src/</c>.
    ///
    /// <para><b>Der Anlass ist ein Befund.</b> <c>Lyrrepl/</c> fehlte im Baum, waehrend der
    /// Abschnitt zwei Bildschirme darunter „The four binaries" heisst — dieselbe Datei
    /// widersprach sich selbst. Ein Baum ist eine zweite Beschreibung des Verzeichnisses daneben,
    /// und zwei Beschreibungen driften; die TextMate-Grammatik haengt aus demselben Grund am
    /// Lexer.</para>
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
        // Eine Zahl in der Doku, die niemand nachzählt, ist irgendwann falsch.
        var readme = File.ReadAllText(Path.Combine(Toolchain.RepositoryRoot, "README.md"));
        var claimed = Regex.Match(readme, @"has (\d+) programs");

        Assert.True(claimed.Success, "README no longer states how many examples there are");

        var actual = Directory.GetFiles(Path.Combine(Toolchain.RepositoryRoot, "examples"), "*.lyr").Length;
        Assert.Equal(actual, int.Parse(claimed.Groups[1].Value));
    }
}
