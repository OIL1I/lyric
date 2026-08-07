using System.Text.Json;

namespace Lyric.Tests.Cli;

/// <summary>
/// Das VS-Code-Manifest gegen das, was daneben liegt.
///
/// <para>Ein Extension-Manifest ist eine Ansammlung von Pfaden und Bezeichnern, die auf andere
/// Dateien zeigen — und nichts davon prüft VS Code beim Laden: ein falscher Pfad heißt einfach,
/// dass die Färbung fehlt. Das ist dieselbe Sorte stiller Fehler wie eine Doku, die niemand
/// nachliest, und diese Tests sind dieselbe Antwort darauf.</para>
/// </summary>
public sealed class ExtensionTests
{
    private static string Dir => Path.Combine(Toolchain.RepositoryRoot, "tooling", "vscode-lyric");

    private static JsonDocument Manifest() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "package.json")));

    [Fact]
    public void Every_path_in_the_manifest_exists()
    {
        // Der haeufigste Fehler an einem Extension-Manifest, und der stillste: VS Code laedt die
        // Extension trotzdem, nur faerbt sie nichts.
        using var manifest = Manifest();
        var contributes = manifest.RootElement.GetProperty("contributes");

        var paths = new List<string> { manifest.RootElement.GetProperty("main").GetString()! };
        paths.AddRange(contributes.GetProperty("languages").EnumerateArray()
            .Select(l => l.GetProperty("configuration").GetString()!));
        paths.AddRange(contributes.GetProperty("grammars").EnumerateArray()
            .Select(g => g.GetProperty("path").GetString()!));

        foreach (var relative in paths)
            Assert.True(File.Exists(Path.Combine(Dir, relative.TrimStart('.', '/'))),
                $"the manifest points at '{relative}', which does not exist");
    }

    [Fact]
    public void The_grammar_and_the_language_agree_on_the_scope()
    {
        // Zwei Stellen nennen denselben Bezeichner: das Manifest verdrahtet Sprache und
        // Grammatik ueber 'scopeName'. Weichen sie ab, wird gar nichts gefaerbt — ohne Meldung.
        using var manifest = Manifest();
        using var grammar = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(Dir, "syntaxes", "lyric.tmLanguage.json")));

        var contributed = manifest.RootElement.GetProperty("contributes")
            .GetProperty("grammars").EnumerateArray().Single();

        Assert.Equal(grammar.RootElement.GetProperty("scopeName").GetString(),
            contributed.GetProperty("scopeName").GetString());

        Assert.Equal("lyric", contributed.GetProperty("language").GetString());
    }

    [Fact]
    public void The_extension_claims_the_file_extension_the_language_uses()
    {
        using var manifest = Manifest();
        var language = manifest.RootElement.GetProperty("contributes")
            .GetProperty("languages").EnumerateArray().Single();

        Assert.Contains(".lyr", language.GetProperty("extensions").EnumerateArray()
            .Select(e => e.GetString()));
    }

    [Fact]
    public void Every_command_and_keybinding_refers_to_a_declared_command()
    {
        // Ein Keybinding oder Menue-Eintrag auf ein Kommando, das es nicht gibt, ist ein
        // Menuepunkt, der nichts tut.
        using var manifest = Manifest();
        var contributes = manifest.RootElement.GetProperty("contributes");

        var declared = contributes.GetProperty("commands").EnumerateArray()
            .Select(c => c.GetProperty("command").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var binding in contributes.GetProperty("keybindings").EnumerateArray())
            Assert.Contains(binding.GetProperty("command").GetString(), declared);

        foreach (var menu in contributes.GetProperty("menus").EnumerateObject())
            foreach (var item in menu.Value.EnumerateArray())
                Assert.Contains(item.GetProperty("command").GetString(), declared);
    }

    [Fact]
    public void The_run_command_calls_the_driver_and_not_a_tool()
    {
        // ADR-019: der Treiber ist das eine Kommando, das uebersetzt UND ausfuehrt. Riefe die
        // Extension 'lyrc', bekaeme der Nutzer eine .lyrbc statt eines Laufs; riefe sie 'lyrvm',
        // scheiterte es an einer Quelldatei.
        // Geprueft wird die AUFRUFZEILE, nicht die Datei: der Kommentar daneben nennt 'lyrc' und
        // 'lyrvm' ausdruecklich, um zu begruenden, warum sie es nicht sind. Ein Test, der die
        // ganze Datei absucht, faellt genau ueber diese Begruendung — beim ersten Lauf passiert.
        var call = File.ReadAllLines(Path.Combine(Dir, "extension.js"))
            .Single(line => line.Contains("sendText", StringComparison.Ordinal));

        Assert.Contains(" run ", call);
        Assert.DoesNotContain("lyrc", call);
        Assert.DoesNotContain("lyrvm", call);
    }

    [Fact]
    public void An_unsaved_file_is_written_before_running()
    {
        // Der Compiler liest von der Platte, nicht aus dem Editor-Puffer. Ohne das Speichern
        // laeuft die vorige Fassung, und der Nutzer sucht den Fehler in seinem Programm statt
        // in seinem Editor.
        var code = File.ReadAllText(Path.Combine(Dir, "extension.js"));

        Assert.Contains("isDirty", code);
        Assert.Contains("save()", code);
    }
}
