using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lyric.Tests.Cli;

/// <summary>
/// The TextMate grammar against the lexer.
///
/// <para>WHY THIS TEST EXISTS: an editor grammar is a SECOND DESCRIPTION OF THE SAME LANGUAGE, and two
/// descriptions drift. When Lyric gets a keyword, nobody notices it in the grammar — the editor simply
/// does not colour it, and that looks like an identifier.</para>
///
/// <para>The keyword list is checked, because it is the only set both sides know completely. Whether an
/// identifier is a type is known only to the sema; the grammar guesses it from the capitalisation, and
/// that is a convention that cannot be checked.</para>
/// </summary>
public sealed class GrammarTests
{
    private static string GrammarPath => Path.Combine(Toolchain.RepositoryRoot,
        "tooling", "textmate", "syntaxes", "lyric.tmLanguage.json");

    private static string LexerPath => Path.Combine(Toolchain.RepositoryRoot,
        "src", "Lyric.Frontend", "Lexing", "Lexer.cs");

    /// <summary>The keywords the grammar colours, from all the patterns under
    /// <c>repository.keywords</c>, because they are separated by category.</summary>
    private static HashSet<string> GrammarKeywords()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(GrammarPath));
        var patterns = document.RootElement
            .GetProperty("repository").GetProperty("keywords").GetProperty("patterns");

        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pattern in patterns.EnumerateArray())
        {
            // The '\b' anchors go first: otherwise the extractor reads 'bthis' rather than 'this', with
            // the 'b' from the escape stuck to the word.
            var match = pattern.GetProperty("match").GetString()!.Replace(@"\b", "");
            foreach (var word in Regex.Matches(match, @"[a-z][a-z0-9]*").Select(m => m.Value))
                found.Add(word);
        }
        return found;
    }

    /// <summary>The lexer's keyword table. It is the truth: what stands there is a keyword, and everything
    /// else is an identifier.</summary>
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
            + " — add them to tooling/textmate/syntaxes/lyric.tmLanguage.json");
    }

    [Fact]
    public void The_grammar_colours_nothing_that_is_not_a_keyword()
    {
        // The other direction. A word the grammar colours but the language does not know is a trap: the
        // editor claims a meaning that does not exist, and while typing it looks as if the identifier were
        // reserved.
        //
        // Exempt are the built-in TYPES: they are identifiers in the root scope rather than keywords, and
        // are coloured rightly.
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
        // The language allows nested block comments, a rarity most grammars get wrong. Without the
        // self-reference '/* /* */ */' ends one level too early and the rest of the file colours itself as
        // a comment.
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
