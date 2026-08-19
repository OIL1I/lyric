using Lyric.Core;
using Lyric.Lexing;

namespace Lyric.Tests.Lexing;

/// <summary>
/// The opt-in trivia collection: comments the compile path throws away arrive in source order
/// with exact spans, and the compile path itself stays untouched.
/// </summary>
public class TriviaTests
{
    private static (IReadOnlyList<Trivia> Trivia, List<Token> Tokens, string Source) Lex(
        string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("<test>", source);
        var de = new DiagnosticEngine(sm);
        var lexer = new Lexer(sm, id, de, collectTrivia: true);

        var tokens = new List<Token>();
        Token token;
        do
        {
            token = lexer.Next();
            tokens.Add(token);
        } while (token.TokenKind != TokenKind.Eof);

        return (lexer.CollectedTrivia, tokens, source);
    }

    private static string TextOf(Trivia trivia, string source) =>
        source.Substring(trivia.Span.Start, trivia.Span.Length);

    [Fact]
    public void Comments_arrive_in_source_order_with_their_exact_text()
    {
        var (trivia, _, source) = Lex("""
            // first
            let x = 1; /* second */ let y = 2;
            // third
            """);

        Assert.Equal(
            ["// first", "/* second */", "// third"],
            trivia.Select(t => TextOf(t, source)));
        Assert.Equal(
            [TriviaKind.LineComment, TriviaKind.BlockComment, TriviaKind.LineComment],
            trivia.Select(t => t.Kind));
    }

    [Fact]
    public void A_line_comment_ends_before_its_newline()
    {
        var (trivia, _, source) = Lex("// note\nlet x = 1;\n");

        var text = TextOf(Assert.Single(trivia), source);
        Assert.Equal("// note", text);
        Assert.DoesNotContain('\n', text);
    }

    [Fact]
    public void A_nested_block_comment_is_one_trivia()
    {
        var (trivia, _, source) = Lex("/* outer /* inner */ still outer */ let x = 1;");

        Assert.Equal("/* outer /* inner */ still outer */",
            TextOf(Assert.Single(trivia), source));
    }

    [Fact]
    public void A_doc_comment_stays_a_token_and_is_not_trivia()
    {
        // The parser attaches '///' to the declaration it documents; recording it here too
        // would give a consumer two sources for one piece of text.
        var (trivia, tokens, _) = Lex("/// docs\nfn f(): void { }\n");

        Assert.Empty(trivia);
        Assert.Contains(tokens, t => t.TokenKind == TokenKind.DocComment);
    }

    [Fact]
    public void A_comment_inside_an_interpolation_is_collected_too()
    {
        var (trivia, _, source) = Lex("let s = f\"{ /* why */ 1 }\";");

        Assert.Equal("/* why */", TextOf(Assert.Single(trivia), source));
    }

    [Fact]
    public void Without_the_flag_nothing_is_collected()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("<test>", "// comment\nlet x = 1;");
        var lexer = new Lexer(sm, id, new DiagnosticEngine(sm));

        while (lexer.Next().TokenKind != TokenKind.Eof) { }

        Assert.Empty(lexer.CollectedTrivia);
    }

    [Fact]
    public void The_token_stream_is_identical_with_and_without_collection()
    {
        const string source = """
            // leading
            fn main(): int { /* inline */ return 40 + 2; } // trailing
            """;

        var (_, collected, _) = Lex(source);

        var sm = new SourceManager();
        var id = sm.AddVirtual("<test>", source);
        var lexer = new Lexer(sm, id, new DiagnosticEngine(sm));
        var plain = new List<Token>();
        Token token;
        do
        {
            token = lexer.Next();
            plain.Add(token);
        } while (token.TokenKind != TokenKind.Eof);

        Assert.Equal(plain, collected);
    }

    [Fact]
    public void An_unterminated_block_comment_is_still_recorded()
    {
        // The diagnostic is reported either way; the formatter never runs on an error file,
        // but the collection must not silently lose the text that IS there.
        var (trivia, _, source) = Lex("let x = 1; /* runs off");

        Assert.Equal("/* runs off", TextOf(Assert.Single(trivia), source));
    }
}
