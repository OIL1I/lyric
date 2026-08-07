using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lyric.Tests.Cli;

/// <summary>
/// Die TextMate-Grammatik gegen den Lexer.
///
/// <para><b>Warum es diesen Test gibt</b>: eine Editor-Grammatik ist eine <b>zweite Beschreibung
/// derselben Sprache</b>, und zwei Beschreibungen driften. Wenn Lyric ein Keyword bekommt, fällt
/// es in der Grammatik niemandem auf — der Editor färbt es einfach nicht, und das sieht aus wie
/// ein Bezeichner. Dieselbe Erfahrung wie bei <c>Sprache.md</c> §4 und §2.2, nur diesmal
/// vorbeugend.</para>
///
/// <para>Geprüft wird die Keyword-Liste, weil sie die einzige Menge ist, die beide Seiten
/// vollständig kennen. Ob ein Bezeichner ein Typ ist, weiß nur die Sema; die Grammatik rät es an
/// der Großschreibung, und das ist eine Konvention, die sich nicht prüfen lässt.</para>
/// </summary>
public sealed class GrammarTests
{
    private static string GrammarPath => Path.Combine(Toolchain.RepositoryRoot,
        "tooling", "vscode-lyric", "syntaxes", "lyric.tmLanguage.json");

    private static string LexerPath => Path.Combine(Toolchain.RepositoryRoot,
        "src", "Lyric.Frontend", "Lexing", "Lexer.cs");

    /// <summary>Die Keywords, die die Grammatik färbt — aus allen Mustern unter
    /// <c>repository.keywords</c>, weil sie nach Kategorie getrennt sind.</summary>
    private static HashSet<string> GrammarKeywords()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(GrammarPath));
        var patterns = document.RootElement
            .GetProperty("repository").GetProperty("keywords").GetProperty("patterns");

        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pattern in patterns.EnumerateArray())
        {
            // Die '\b'-Anker zuerst weg: sonst liest der Extraktor 'bthis' statt 'this' — das
            // 'b' aus dem Escape klebt am Wort. Genau das ist beim ersten Lauf passiert.
            var match = pattern.GetProperty("match").GetString()!.Replace(@"\b", "");
            foreach (var word in Regex.Matches(match, @"[a-z][a-z0-9]*").Select(m => m.Value))
                found.Add(word);
        }
        return found;
    }

    /// <summary>Die Keyword-Tabelle des Lexers. Sie ist die Wahrheit — was dort steht, ist ein
    /// Keyword, und alles andere ist ein Bezeichner.</summary>
    private static HashSet<string> LexerKeywords() =>
        Regex.Matches(File.ReadAllText(LexerPath), @"\{ ""([a-z0-9]+)"", TokenKind\.")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Every_keyword_of_the_language_is_coloured()
    {
        var missing = LexerKeywords().Except(GrammarKeywords()).OrderBy(k => k).ToArray();

        Assert.True(missing.Length == 0,
            "the TextMate grammar does not colour: " + string.Join(", ", missing)
            + " — add them to tooling/vscode-lyric/syntaxes/lyric.tmLanguage.json");
    }

    [Fact]
    public void The_grammar_colours_nothing_that_is_not_a_keyword()
    {
        // Die andere Richtung. Ein Wort, das die Grammatik faerbt, das die Sprache aber nicht
        // kennt, ist eine Falle: der Editor behauptet eine Bedeutung, die es nicht gibt — und
        // beim Tippen sieht es aus, als sei der Bezeichner reserviert.
        //
        // Ausgenommen sind die eingebauten TYPEN: sie sind Bezeichner im Wurzel-Scope und keine
        // Keywords, werden aber zu Recht gefaerbt.
        var builtinTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "int", "int8", "int16", "int32", "int64",
            "uint", "uint8", "uint16", "uint32", "uint64",
            "float", "float32", "float64",
            "bool", "char", "string", "void", "never",
        };

        var extra = GrammarKeywords()
            .Except(LexerKeywords())
            .Except(builtinTypes)
            .OrderBy(k => k)
            .ToArray();

        Assert.True(extra.Length == 0,
            "the grammar colours words the language does not know: " + string.Join(", ", extra));
    }

    [Fact]
    public void The_grammar_is_valid_json_and_claims_the_right_extension()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(GrammarPath));
        var root = document.RootElement;

        Assert.Equal("source.lyric", root.GetProperty("scopeName").GetString());
        Assert.Contains("lyr", root.GetProperty("fileTypes").EnumerateArray()
            .Select(e => e.GetString()));
    }

    [Fact]
    public void Block_comments_nest()
    {
        // Sprache.md §1.1 erlaubt verschachtelte Block-Kommentare — eine Seltenheit, die die
        // meisten Grammatiken falsch machen. Ohne den Selbstbezug endet '/* /* */ */' eine Ebene
        // zu frueh, und der Rest der Datei faerbt sich als Kommentar.
        using var document = JsonDocument.Parse(File.ReadAllText(GrammarPath));
        var comments = document.RootElement
            .GetProperty("repository").GetProperty("comments").GetProperty("patterns");

        var block = comments.EnumerateArray()
            .First(p => p.TryGetProperty("name", out var n)
                        && n.GetString() == "comment.block.lyric");

        Assert.True(block.TryGetProperty("patterns", out var inner),
            "the block comment rule has no nested patterns — '/* /* */ */' would end too early");
        Assert.Contains("#comments", inner.EnumerateArray()
            .Select(p => p.GetProperty("include").GetString()));
    }
}
