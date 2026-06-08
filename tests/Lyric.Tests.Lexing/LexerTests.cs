using Lyric.Core;
using Lyric.Lexing;
using Xunit;

namespace Lyric.Tests.Lexing;

public class LexerTests
{
    private static (List<Token> tokens, DiagnosticEngine diag) Tokenize(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("<test>", source);
        var de = new DiagnosticEngine(sm);
        var lexer = new Lexer(sm, id, de);
        var tokens = new List<Token>();
        Token t;
        do
        {
            t = lexer.Next();
            tokens.Add(t);
        } while (t.TokenKind != TokenKind.Eof);
        return (tokens, de);
    }

    // ─── Konstruktor ───────────────────────────────────────────────────────

    [Fact]
    public void Constructor_null_sources_throws()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("<test>", "");
        var de = new DiagnosticEngine(sm);
        Assert.Throws<ArgumentNullException>(() => new Lexer(null!, id, de));
    }

    [Fact]
    public void Constructor_null_diagnostics_throws()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("<test>", "");
        Assert.Throws<ArgumentNullException>(() => new Lexer(sm, id, null!));
    }

    [Fact]
    public void Constructor_with_unregistered_FileId_throws()
    {
        var sm = new SourceManager();
        var de = new DiagnosticEngine(sm);
        Assert.Throws<ArgumentException>(() => new Lexer(sm, new FileId(99), de));
    }

    // ─── EOF und Whitespace ────────────────────────────────────────────────

    [Fact]
    public void Empty_input_yields_only_EOF()
    {
        var (tokens, diag) = Tokenize("");
        Assert.Single(tokens);
        Assert.Equal(TokenKind.Eof, tokens[0].TokenKind);
        Assert.Equal(0, tokens[0].Span.Start);
        Assert.Equal(0, tokens[0].Span.End);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Only_whitespace_yields_only_EOF()
    {
        var (tokens, diag) = Tokenize("   \t\r\n  ");
        Assert.Single(tokens);
        Assert.Equal(TokenKind.Eof, tokens[0].TokenKind);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Trailing_whitespace_does_not_crash()
    {
        // Regression: SkipTrivia muss vor EOF-Check laufen, sonst OOB beim Sentinel.
        var (tokens, _) = Tokenize("foo  ");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.Identifier, tokens[0].TokenKind);
        Assert.Equal(TokenKind.Eof, tokens[1].TokenKind);
    }

    [Fact]
    public void Next_after_EOF_returns_EOF_again()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("<test>", "");
        var de = new DiagnosticEngine(sm);
        var lexer = new Lexer(sm, id, de);
        Assert.Equal(TokenKind.Eof, lexer.Next().TokenKind);
        Assert.Equal(TokenKind.Eof, lexer.Next().TokenKind);
        Assert.Equal(TokenKind.Eof, lexer.Next().TokenKind);
    }

    [Fact]
    public void EOF_span_is_empty_at_source_length()
    {
        var (tokens, _) = Tokenize("abc");
        var eof = tokens[^1];
        Assert.Equal(TokenKind.Eof, eof.TokenKind);
        Assert.Equal(3, eof.Span.Start);
        Assert.Equal(3, eof.Span.End);
    }

    // ─── Line-Comments ─────────────────────────────────────────────────────

    [Fact]
    public void Line_comment_only_yields_EOF()
    {
        var (tokens, diag) = Tokenize("// just a comment\n");
        Assert.Single(tokens);
        Assert.Equal(TokenKind.Eof, tokens[0].TokenKind);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Line_comment_at_EOF_without_newline_does_not_overshoot()
    {
        // Regression: nach Line-Comment der mit EOF endet (kein \n), darf _pos
        // nicht über die Source-Länge hinaus laufen.
        var (tokens, diag) = Tokenize("foo // tail");   // Länge 11
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.Identifier, tokens[0].TokenKind);
        Assert.Equal(TokenKind.Eof, tokens[1].TokenKind);
        Assert.Equal(11, tokens[1].Span.Start);
        Assert.Equal(11, tokens[1].Span.End);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Line_comment_followed_by_identifier_on_next_line()
    {
        var (tokens, _) = Tokenize("// comment\nfoo");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.Identifier, tokens[0].TokenKind);
        Assert.Equal(11, tokens[0].Span.Start);   // direkt nach "// comment\n"
        Assert.Equal(14, tokens[0].Span.End);
    }

    [Fact]
    public void Whitespace_after_line_comment_is_consumed()
    {
        // Regression für Outer-Loop-Struktur in SkipTrivia: nach Comment muss
        // weiterer Whitespace auch geskippt werden.
        var (tokens, diag) = Tokenize("// foo\n  bar");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.Identifier, tokens[0].TokenKind);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Multiple_line_comments_in_a_row()
    {
        var (tokens, diag) = Tokenize("// a\n// b\n// c\nfoo");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.Identifier, tokens[0].TokenKind);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Trailing_line_comment_after_identifier()
    {
        var (tokens, _) = Tokenize("foo // tail\n");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.Identifier, tokens[0].TokenKind);
        Assert.Equal(0, tokens[0].Span.Start);
        Assert.Equal(3, tokens[0].Span.End);
    }

    // ─── Identifier ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("a",      1)]
    [InlineData("foo",    3)]
    [InlineData("_under", 6)]
    [InlineData("a1b2",   4)]
    [InlineData("A_B",    3)]
    [InlineData("_",      1)]
    [InlineData("__init", 6)]
    public void Identifier_recognizes_valid_forms(string input, int expectedEnd)
    {
        var (tokens, _) = Tokenize(input);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.Identifier, tokens[0].TokenKind);
        Assert.Equal(0, tokens[0].Span.Start);
        Assert.Equal(expectedEnd, tokens[0].Span.End);
    }

    [Fact]
    public void Two_identifiers_separated_by_whitespace()
    {
        var (tokens, _) = Tokenize("foo bar");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenKind.Identifier, tokens[0].TokenKind);
        Assert.Equal(0, tokens[0].Span.Start);
        Assert.Equal(3, tokens[0].Span.End);
        Assert.Equal(TokenKind.Identifier, tokens[1].TokenKind);
        Assert.Equal(4, tokens[1].Span.Start);
        Assert.Equal(7, tokens[1].Span.End);
    }

    [Fact]
    public void Identifier_at_EOF_does_not_overshoot()
    {
        var (tokens, _) = Tokenize("xyz");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(0, tokens[0].Span.Start);
        Assert.Equal(3, tokens[0].Span.End);
        Assert.Equal(3, tokens[1].Span.Start);   // EOF
    }

    // ─── Punctuation ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("(", TokenKind.LParen)]
    [InlineData(")", TokenKind.RParen)]
    [InlineData("{", TokenKind.LBrace)]
    [InlineData("}", TokenKind.RBrace)]
    public void Single_punctuation_token(string input, TokenKind expectedKind)
    {
        var (tokens, _) = Tokenize(input);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(expectedKind, tokens[0].TokenKind);
        Assert.Equal(0, tokens[0].Span.Start);
        Assert.Equal(1, tokens[0].Span.End);
    }

    [Fact]
    public void Punctuation_combinations()
    {
        var (tokens, _) = Tokenize("({})");
        var kinds = tokens.Select(t => t.TokenKind).ToArray();
        Assert.Equal(
            new[] { TokenKind.LParen, TokenKind.LBrace, TokenKind.RBrace,
                    TokenKind.RParen, TokenKind.Eof },
            kinds);
    }

    // ─── Mixed: Hello World ────────────────────────────────────────────────

    [Fact]
    public void Hello_world_tokenizes()
    {
        // In Slice 1 ist `fn` noch ein normaler Identifier — Keywords kommen in Slice 2.
        var (tokens, diag) = Tokenize("fn main() {}");
        var kinds = tokens.Select(t => t.TokenKind).ToArray();
        Assert.Equal(
            new[] {
                TokenKind.Identifier,   // fn
                TokenKind.Identifier,   // main
                TokenKind.LParen,
                TokenKind.RParen,
                TokenKind.LBrace,
                TokenKind.RBrace,
                TokenKind.Eof
            },
            kinds);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Hello_world_token_spans_are_correct()
    {
        var (tokens, _) = Tokenize("fn main() {}");
        // Indizes: f(0),n(1), (2),m(3),a(4),i(5),n(6),((7),)(8), (9),{(10),}(11)
        Assert.Equal((0, 2),   (tokens[0].Span.Start, tokens[0].Span.End));   // fn
        Assert.Equal((3, 7),   (tokens[1].Span.Start, tokens[1].Span.End));   // main
        Assert.Equal((7, 8),   (tokens[2].Span.Start, tokens[2].Span.End));   // (
        Assert.Equal((8, 9),   (tokens[3].Span.Start, tokens[3].Span.End));   // )
        Assert.Equal((10, 11), (tokens[4].Span.Start, tokens[4].Span.End));   // {
        Assert.Equal((11, 12), (tokens[5].Span.Start, tokens[5].Span.End));   // }
        Assert.Equal((12, 12), (tokens[6].Span.Start, tokens[6].Span.End));   // EOF
    }

    // ─── Bad Characters ────────────────────────────────────────────────────

    [Fact]
    public void Bad_character_emits_token_and_diagnostic()
    {
        var (tokens, diag) = Tokenize("#");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.BadChar, tokens[0].TokenKind);
        Assert.Equal(0, tokens[0].Span.Start);
        Assert.Equal(1, tokens[0].Span.End);
        Assert.True(diag.HasErrors);
        Assert.Equal(1, diag.ErrorCount);
        Assert.Equal("LYR-LEX0001", diag.Diagnostics[0].Code);
    }

    [Fact]
    public void Multiple_bad_characters_each_get_token_and_diagnostic()
    {
        var (tokens, diag) = Tokenize("##");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenKind.BadChar, tokens[0].TokenKind);
        Assert.Equal(TokenKind.BadChar, tokens[1].TokenKind);
        Assert.Equal(TokenKind.Eof, tokens[2].TokenKind);
        Assert.Equal(2, diag.ErrorCount);
    }

    [Fact]
    public void Bad_character_between_identifiers()
    {
        var (tokens, diag) = Tokenize("foo#bar");
        var kinds = tokens.Select(t => t.TokenKind).ToArray();
        Assert.Equal(
            new[] {
                TokenKind.Identifier,
                TokenKind.BadChar,
                TokenKind.Identifier,
                TokenKind.Eof
            },
            kinds);
        Assert.Equal(1, diag.ErrorCount);
    }

    [Fact]
    public void Bad_character_message_quotes_the_character()
    {
        // Regression: Apostrophe um den Char herum, nicht nur einer.
        var (_, diag) = Tokenize("#");
        Assert.Contains("'#'", diag.Diagnostics[0].Message);
    }

    [Fact]
    public void Bad_character_message_names_the_actual_bad_char()
    {
        // Regression: vor dem Fix wurde Current NACH _pos++ gelesen,
        // also das falsche Zeichen gemeldet.
        var (_, diag) = Tokenize("#x");
        Assert.Contains("'#'", diag.Diagnostics[0].Message);
        Assert.DoesNotContain("'x'", diag.Diagnostics[0].Message);
    }

    [Fact]
    public void Bad_character_control_char_uses_unicode_format()
    {
        var (_, diag) = Tokenize("\u0001");
        Assert.Contains("U+0001", diag.Diagnostics[0].Message);
    }

    [Fact]
    public void Bad_character_diagnostic_span_covers_only_that_char()
    {
        var (_, diag) = Tokenize("foo#bar");
        var d = diag.Diagnostics[0];
        Assert.Equal(3, d.Span.Start);   // Position des '#'
        Assert.Equal(4, d.Span.End);
    }

    // ─── Span-Genauigkeit ──────────────────────────────────────────────────

    [Fact]
    public void Full_span_accuracy_mixed_input()
    {
        var (tokens, _) = Tokenize("foo (bar)");
        // Indizes: f(0)o(1)o(2) (3)((4)b(5)a(6)r(7))(8), Length 9
        Assert.Equal((0, 3), (tokens[0].Span.Start, tokens[0].Span.End));  // foo
        Assert.Equal((4, 5), (tokens[1].Span.Start, tokens[1].Span.End));  // (
        Assert.Equal((5, 8), (tokens[2].Span.Start, tokens[2].Span.End));  // bar
        Assert.Equal((8, 9), (tokens[3].Span.Start, tokens[3].Span.End));  // )
        Assert.Equal((9, 9), (tokens[4].Span.Start, tokens[4].Span.End));  // EOF
    }
}