using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Lyric.Embedding;

namespace Lyric.Tests.Embedding;

/// <summary>
/// <c>Doku.md</c> §21 beschreibt die Embedding-API — und der Abschnitt hat die gesamte
/// M10-Arbeit hindurch etwas beschrieben, das es nicht gab.
///
/// <para><b>Vier Zusagen, jede einzeln widerlegt</b>: <c>Compile</c> ohne Modulnamen (§2.1 leitet
/// ihn sonst aus dem Pfad ab, und den gibt es nicht), <c>playSound("hit")</c> ohne Import (§2.2
/// kennt keinen impliziten Namensraum), <c>builder.Field</c> (braucht einen Feldindex, den ein
/// Host-Typ nicht hat), <c>vm.Call</c> statt einer Instanz (eine VM kann zwei Skripte halten).
/// </para>
///
/// <para>Dieser Test hält sie fern. Er ist schmal und kostet nichts — und genau so einer hätte den
/// Satz „no working compiler exists yet" nicht acht Meilensteine im README überleben lassen.</para>
/// </summary>
public class DocumentationTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string EmbeddingSection()
    {
        var doku = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "Doku.md"));
        var start = doku.IndexOf("## 21. Embedding", StringComparison.Ordinal);
        var end = doku.IndexOf("## 22. Standardbibliothek", StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start, "Doku.md has no §21 — did the section move?");
        return doku[start..end];
    }

    [Theory]
    // 'Compile' ohne zweites Argument: der Modulname ist Pflicht.
    [InlineData("vm.Compile(File.ReadAllText")]
    // Ein Host-'Feld': braucht ein ldfld, das es fuer einen Host-Typ nicht gibt.
    [InlineData("builder.Field")]
    [InlineData(".Field(")]
    // 'Call' auf der VM statt auf einer Instanz.
    [InlineData("vm.Call<")]
    // Der Konstruktor nahm nie ein 'Capabilities'-Objekt.
    [InlineData("new Capabilities")]
    // 'Reload' mit Pfad auf der VM.
    [InlineData("vm.Reload(")]
    public void The_embedding_section_does_not_promise_what_does_not_exist(string withdrawn) =>
        Assert.DoesNotContain(withdrawn, EmbeddingSection(), StringComparison.Ordinal);

    /// <summary>
    /// Die Gegenprobe. Ohne sie bliebe der Test darüber auch dann grün, wenn §21 gelöscht würde —
    /// ein Abschnitt, der nichts behauptet, behauptet auch nichts Falsches.
    /// </summary>
    [Theory]
    [InlineData("HostOptions")]
    [InlineData("RegisterFunction")]
    [InlineData("RegisterType")]
    [InlineData("Instantiate")]
    [InlineData("Reload()")]
    [InlineData("import host")]
    public void The_embedding_section_names_the_api_that_exists(string expected) =>
        Assert.Contains(expected, EmbeddingSection(), StringComparison.Ordinal);

    /// <summary>
    /// Jeder Lyric-Schnipsel in §21 übersetzt — gegen einen Host, der genau das registriert, was
    /// der Abschnitt daneben zeigt.
    ///
    /// <para>Das ist die Prüfung, die zählt: Prosa bleibt Prosa, aber ein Beispiel, das nicht
    /// übersetzt, ist eine Lüge in der Doku. Dieselbe Regel wie beim README-Beispiel seit M9.</para>
    /// </summary>
    [Fact]
    public void Every_lyric_snippet_in_the_embedding_section_compiles()
    {
        var snippets = Regex.Matches(EmbeddingSection(), "```lyr\r?\n(.*?)```", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .ToArray();

        Assert.NotEmpty(snippets);

        foreach (var snippet in snippets)
        {
            var vm = new LangVm(new HostOptions
            {
                StdlibRoot = Path.Combine(RepoRoot(), "stdlib"),
            });

            // Genau das, was §21 daneben registriert.
            vm.RegisterType<Spieler>("Spieler", t => t
                .Getter("name", (Spieler s) => s.Name)
                .Getter("leben", (Spieler s) => s.Leben)
                .Method("schaden", (Spieler s, long wieviel) => s.Schaden(wieviel), mutates: true));

            vm.RegisterFunction("playSound", (string _) => { });
            vm.RegisterFunction("zufall", (long grenze) => grenze);
            vm.RegisterFunction("held", () => new Spieler("test"));

            vm.Compile(snippet, "doku");
        }
    }

    /// <summary>Der Spieler aus §21.5 — dieselbe Form, damit die Schnipsel dagegen übersetzen.
    /// </summary>
    private sealed class Spieler(string name)
    {
        public string Name { get; } = name;
        public long Leben { get; private set; } = 100;
        public void Schaden(long wieviel) => Leben -= wieviel;
    }
}
