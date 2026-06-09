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
                TokenKind.Fn,   // fn
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
    
        // ─── Keywords (Slice 2) ────────────────────────────────────────────────

    [Theory]
    [InlineData("module",    TokenKind.Module)]
    [InlineData("import",    TokenKind.Import)]
    [InlineData("as",        TokenKind.As)]
    [InlineData("pub",       TokenKind.Pub)]
    [InlineData("struct",    TokenKind.Struct)]
    [InlineData("class",     TokenKind.Class)]
    [InlineData("enum",      TokenKind.Enum)]
    [InlineData("interface", TokenKind.Interface)]
    [InlineData("extend",    TokenKind.Extend)]
    [InlineData("fn",        TokenKind.Fn)]
    [InlineData("mut",       TokenKind.Mut)]
    [InlineData("let",       TokenKind.Let)]
    [InlineData("var",       TokenKind.Var)]
    [InlineData("params",    TokenKind.Params)]
    [InlineData("if",        TokenKind.If)]
    [InlineData("else",      TokenKind.Else)]
    [InlineData("while",     TokenKind.While)]
    [InlineData("do",        TokenKind.Do)]
    [InlineData("for",       TokenKind.For)]
    [InlineData("in",        TokenKind.In)]
    [InlineData("match",     TokenKind.Match)]
    [InlineData("break",     TokenKind.Break)]
    [InlineData("continue",  TokenKind.Continue)]
    [InlineData("return",    TokenKind.Return)]
    [InlineData("yield",     TokenKind.Yield)]
    [InlineData("resume",    TokenKind.Resume)]
    [InlineData("defer",     TokenKind.Defer)]
    [InlineData("try",       TokenKind.Try)]
    [InlineData("catch",     TokenKind.Catch)]
    [InlineData("throw",     TokenKind.Throw)]
    [InlineData("true",      TokenKind.True)]
    [InlineData("false",     TokenKind.False)]
    [InlineData("null",      TokenKind.Null)]
    [InlineData("this",      TokenKind.This)]
    public void Keyword_is_recognized_as_its_specific_kind(string input, TokenKind expectedKind)
    {
        var (tokens, diag) = Tokenize(input);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(expectedKind, tokens[0].TokenKind);
        Assert.Equal(0, tokens[0].Span.Start);
        Assert.Equal(input.Length, tokens[0].Span.End);
        Assert.False(diag.HasErrors);
    }

    [Theory]
    [InlineData("fnx")]      // Keyword als Präfix
    [InlineData("fn_")]      // Underscore-Suffix
    [InlineData("fn1")]      // Digit-Suffix
    [InlineData("_fn")]      // Underscore-Präfix
    [InlineData("FN")]       // Case-sensitive
    [InlineData("Fn")]
    [InlineData("LET")]
    public void Identifier_that_only_resembles_keyword_is_Identifier(string input)
    {
        var (tokens, _) = Tokenize(input);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.Identifier, tokens[0].TokenKind);
    }

    [Theory]
    [InlineData("async")]
    [InlineData("await")]
    [InlineData("const")]
    [InlineData("trait")]
    [InlineData("move")]
    [InlineData("own")]
    public void Reserved_post_v1_words_are_Identifier_in_v1(string input)
    {
        // Spec §1.4: async, await, const, trait, move, own sind in v1 keine
        // Keywords — sie bleiben normale Identifier.
        var (tokens, _) = Tokenize(input);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.Identifier, tokens[0].TokenKind);
    }

    [Fact]
    public void Keyword_followed_by_identifier_separated_by_whitespace()
    {
        var (tokens, _) = Tokenize("fn main");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenKind.Fn, tokens[0].TokenKind);
        Assert.Equal(TokenKind.Identifier, tokens[1].TokenKind);
        Assert.Equal(0, tokens[0].Span.Start);
        Assert.Equal(2, tokens[0].Span.End);
        Assert.Equal(3, tokens[1].Span.Start);
        Assert.Equal(7, tokens[1].Span.End);
    }

    [Fact]
    public void Hello_world_with_keyword_dispatch()
    {
        // Aus Slice 1 — jetzt sollte `fn` als Keyword erkannt werden statt Identifier.
        var (tokens, diag) = Tokenize("fn main() {}");
        var kinds = tokens.Select(t => t.TokenKind).ToArray();
        Assert.Equal(
            new[] {
                TokenKind.Fn,            // <-- jetzt Keyword
                TokenKind.Identifier,    // main
                TokenKind.LParen,
                TokenKind.RParen,
                TokenKind.LBrace,
                TokenKind.RBrace,
                TokenKind.Eof
            },
            kinds);
        Assert.False(diag.HasErrors);
    }

    // ─── Doc-Comments (Slice 2) ────────────────────────────────────────────

    [Fact]
    public void DocComment_simple_emits_token()
    {
        var (tokens, diag) = Tokenize("/// hello");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.DocComment, tokens[0].TokenKind);
        Assert.Equal(0, tokens[0].Span.Start);
        Assert.Equal(9, tokens[0].Span.End);   // "/// hello".Length
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void DocComment_empty_body()
    {
        var (tokens, _) = Tokenize("///");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.DocComment, tokens[0].TokenKind);
        Assert.Equal(0, tokens[0].Span.Start);
        Assert.Equal(3, tokens[0].Span.End);
    }

    [Fact]
    public void Four_slashes_are_DocComment_with_slash_body()
    {
        // "////" — ist DocComment mit Body "/".
        var (tokens, _) = Tokenize("////");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.DocComment, tokens[0].TokenKind);
        Assert.Equal(0, tokens[0].Span.Start);
        Assert.Equal(4, tokens[0].Span.End);
    }

    [Fact]
    public void DocComment_disambiguated_from_line_comment()
    {
        // Regression für die "PeekAt(2)"-Disambiguierung in SkipTrivia und Next.
        var (tokens, _) = Tokenize("// not a doc\n/// a doc");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.DocComment, tokens[0].TokenKind);
        Assert.Equal(13, tokens[0].Span.Start);   // nach "// not a doc\n"
        Assert.Equal(22, tokens[0].Span.End);     // bis Ende der Datei
    }

    [Fact]
    public void DocComment_followed_by_identifier_on_next_line()
    {
        var (tokens, _) = Tokenize("/// docs\nfoo");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenKind.DocComment, tokens[0].TokenKind);
        Assert.Equal(0, tokens[0].Span.Start);
        Assert.Equal(8, tokens[0].Span.End);      // bis vor \n
        Assert.Equal(TokenKind.Identifier, tokens[1].TokenKind);
        Assert.Equal(9, tokens[1].Span.Start);    // direkt nach \n
        Assert.Equal(12, tokens[1].Span.End);
    }

    [Fact]
    public void Multiple_DocComments_in_a_row()
    {
        var (tokens, _) = Tokenize("/// line1\n/// line2\nfoo");
        Assert.Equal(4, tokens.Count);
        Assert.Equal(TokenKind.DocComment, tokens[0].TokenKind);
        Assert.Equal(TokenKind.DocComment, tokens[1].TokenKind);
        Assert.Equal(TokenKind.Identifier, tokens[2].TokenKind);
        Assert.Equal(TokenKind.Eof, tokens[3].TokenKind);
    }

    [Fact]
    public void DocComment_at_EOF_without_newline()
    {
        var (tokens, _) = Tokenize("/// at the end");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.DocComment, tokens[0].TokenKind);
        Assert.Equal(14, tokens[0].Span.End);     // gesamte Länge
        Assert.Equal(14, tokens[1].Span.Start);   // EOF an Length
    }

    // ─── Block-Comments (Slice 2, als Trivia) ──────────────────────────────

    [Fact]
    public void Block_comment_simple_is_skipped()
    {
        var (tokens, diag) = Tokenize("/* hello */");
        Assert.Single(tokens);
        Assert.Equal(TokenKind.Eof, tokens[0].TokenKind);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Empty_block_comment_is_skipped()
    {
        var (tokens, _) = Tokenize("/**/");
        Assert.Single(tokens);
        Assert.Equal(TokenKind.Eof, tokens[0].TokenKind);
    }

    [Fact]
    public void Block_comment_between_identifiers()
    {
        var (tokens, _) = Tokenize("foo /* mid */ bar");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenKind.Identifier, tokens[0].TokenKind);
        Assert.Equal(TokenKind.Identifier, tokens[1].TokenKind);
        Assert.Equal(TokenKind.Eof, tokens[2].TokenKind);
        Assert.Equal(14, tokens[1].Span.Start);   // direkt nach "foo /* mid */ "
        Assert.Equal(17, tokens[1].Span.End);
    }

    [Fact]
    public void Multiline_block_comment_is_skipped()
    {
        var (tokens, diag) = Tokenize("/* line1\nline2\nline3 */");
        Assert.Single(tokens);
        Assert.Equal(TokenKind.Eof, tokens[0].TokenKind);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Nested_block_comment_one_level()
    {
        var (tokens, diag) = Tokenize("/* outer /* inner */ outer */");
        Assert.Single(tokens);
        Assert.Equal(TokenKind.Eof, tokens[0].TokenKind);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Nested_block_comment_deep()
    {
        var (tokens, diag) = Tokenize("/* a /* b /* c */ b */ a */");
        Assert.Single(tokens);
        Assert.Equal(TokenKind.Eof, tokens[0].TokenKind);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Block_comment_followed_by_identifier()
    {
        var (tokens, _) = Tokenize("/*foo*/bar");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.Identifier, tokens[0].TokenKind);
        Assert.Equal(7, tokens[0].Span.Start);   // direkt nach "/*foo*/"
        Assert.Equal(10, tokens[0].Span.End);
    }

    [Fact]
    public void Unterminated_block_comment_emits_LEX0002()
    {
        var (tokens, diag) = Tokenize("/* unterminated");
        Assert.Single(tokens);
        Assert.Equal(TokenKind.Eof, tokens[0].TokenKind);
        Assert.True(diag.HasErrors);
        Assert.Equal(1, diag.ErrorCount);
        Assert.Equal("LYR-LEX0002", diag.Diagnostics[0].Code);
        Assert.Equal(0, diag.Diagnostics[0].Span.Start);
        Assert.Equal(15, diag.Diagnostics[0].Span.End);
    }

    [Fact]
    public void Unterminated_nested_block_comment_emits_one_diagnostic()
    {
        // Eine offen gebliebene Verschachtelung — nur eine Diagnostic erwartet
        // (am Ende ist depth > 0, das löst genau einmal aus).
        var (_, diag) = Tokenize("/* outer /* inner ");
        Assert.Equal(1, diag.ErrorCount);
        Assert.Equal("LYR-LEX0002", diag.Diagnostics[0].Code);
    }

    [Fact]
    public void Block_comment_with_doc_comment_marker_inside_is_just_block()
    {
        // Die /// in einem Block-Comment ist Inhalt, kein DocComment-Token.
        var (tokens, _) = Tokenize("/* /// not a doc */");
        Assert.Single(tokens);
        Assert.Equal(TokenKind.Eof, tokens[0].TokenKind);
    }

    // ─── Trivia-Reihenfolge (Slice 2 Regressionen) ─────────────────────────

    [Fact]
    public void Line_then_block_then_doc()
    {
        var (tokens, _) = Tokenize("// line\n/* block */\n/// doc\nfoo");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenKind.DocComment, tokens[0].TokenKind);
        Assert.Equal(TokenKind.Identifier, tokens[1].TokenKind);
        Assert.Equal(TokenKind.Eof, tokens[2].TokenKind);
    }

    [Fact]
    public void Block_then_keyword()
    {
        var (tokens, _) = Tokenize("/* comment */ fn");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.Fn, tokens[0].TokenKind);
        Assert.Equal(14, tokens[0].Span.Start);   // direkt nach "/* comment */ "
        Assert.Equal(16, tokens[0].Span.End);
    }

    [Fact]
    public void DocComment_then_keyword_then_doc_again()
    {
        // Ein realistischeres Beispiel.
        var (tokens, diag) = Tokenize("/// docs\nfn foo() {}\n/// trailing");
        var kinds = tokens.Select(t => t.TokenKind).ToArray();
        Assert.Equal(
            new[] {
                TokenKind.DocComment,
                TokenKind.Fn,
                TokenKind.Identifier,    // foo
                TokenKind.LParen,
                TokenKind.RParen,
                TokenKind.LBrace,
                TokenKind.RBrace,
                TokenKind.DocComment,
                TokenKind.Eof
            },
            kinds);
        Assert.False(diag.HasErrors);
    }
    
        // ─── Dec Int Literals (Slice 3) ────────────────────────────────────────

    [Theory]
    [InlineData("0",            1)]
    [InlineData("1",            1)]
    [InlineData("42",           2)]
    [InlineData("1_000_000",    9)]
    [InlineData("1_",           2)]
    [InlineData("123456789",    9)]
    public void Decimal_int_literal(string input, int expectedEnd)
    {
        var (tokens, diag) = Tokenize(input);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.IntLiteral, tokens[0].TokenKind);
        Assert.Equal(0, tokens[0].Span.Start);
        Assert.Equal(expectedEnd, tokens[0].Span.End);
        Assert.False(diag.HasErrors);
    }

    // ─── Hex / Bin / Oct Int Literals ─────────────────────────────────────

    [Theory]
    [InlineData("0xFF",            4)]
    [InlineData("0xff",            4)]
    [InlineData("0xfF",            4)]
    [InlineData("0XfF",            4)]
    [InlineData("0xDEAD_BEEF",     11)]
    [InlineData("0x0",             3)]
    [InlineData("0x1234567890",    12)]
    public void Hex_int_literal(string input, int expectedEnd)
    {
        var (tokens, diag) = Tokenize(input);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.IntLiteral, tokens[0].TokenKind);
        Assert.Equal(expectedEnd, tokens[0].Span.End);
        Assert.False(diag.HasErrors);
    }

    [Theory]
    [InlineData("0b0",         3)]
    [InlineData("0b1",         3)]
    [InlineData("0b1010",      6)]
    [InlineData("0B1010_0101", 11)]
    public void Binary_int_literal(string input, int expectedEnd)
    {
        var (tokens, diag) = Tokenize(input);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.IntLiteral, tokens[0].TokenKind);
        Assert.Equal(expectedEnd, tokens[0].Span.End);
        Assert.False(diag.HasErrors);
    }

    [Theory]
    [InlineData("0o0",   3)]
    [InlineData("0o7",   3)]
    [InlineData("0o755", 5)]
    [InlineData("0O7_7", 5)]
    public void Octal_int_literal(string input, int expectedEnd)
    {
        var (tokens, diag) = Tokenize(input);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.IntLiteral, tokens[0].TokenKind);
        Assert.Equal(expectedEnd, tokens[0].Span.End);
        Assert.False(diag.HasErrors);
    }

    // ─── Int Literals with Valid Suffix ────────────────────────────────────

    [Theory]
    [InlineData("0i8",        3)]
    [InlineData("100i8",      5)]
    [InlineData("100i16",     6)]
    [InlineData("100i32",     6)]
    [InlineData("100i64",     6)]
    [InlineData("100u8",      5)]
    [InlineData("100u16",     6)]
    [InlineData("100u32",     6)]
    [InlineData("100u64",     6)]
    [InlineData("0xFFi8",     6)]
    [InlineData("0b1010u32",  9)]
    [InlineData("0o7u8",      5)]
    public void Int_literal_with_valid_suffix(string input, int expectedEnd)
    {
        var (tokens, diag) = Tokenize(input);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.IntLiteral, tokens[0].TokenKind);
        Assert.Equal(expectedEnd, tokens[0].Span.End);
        Assert.False(diag.HasErrors);
    }

    // ─── Float Literals: Dot Form ──────────────────────────────────────────

    [Theory]
    [InlineData("1.0",      3)]
    [InlineData("1.5",      3)]
    [InlineData("0.0",      3)]
    [InlineData("3.14159",  7)]
    [InlineData("1_0.5",    5)]
    [InlineData("1.5_5",    5)]
    public void Float_literal_with_dot(string input, int expectedEnd)
    {
        var (tokens, diag) = Tokenize(input);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.FloatLiteral, tokens[0].TokenKind);
        Assert.Equal(expectedEnd, tokens[0].Span.End);
        Assert.False(diag.HasErrors);
    }

    // ─── Float Literals: Exponent ──────────────────────────────────────────

    [Theory]
    [InlineData("1e5",    3)]
    [InlineData("1E5",    3)]
    [InlineData("1e0",    3)]
    [InlineData("1.5e3",  5)]
    [InlineData("1.5e+3", 6)]
    [InlineData("1.5e-3", 6)]
    [InlineData("1e+10",  5)]
    [InlineData("1e-10",  5)]
    [InlineData("1E-0",   4)]
    public void Float_literal_with_exponent(string input, int expectedEnd)
    {
        var (tokens, diag) = Tokenize(input);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.FloatLiteral, tokens[0].TokenKind);
        Assert.Equal(expectedEnd, tokens[0].Span.End);
        Assert.False(diag.HasErrors);
    }

    // ─── Float Literals: Valid Suffix ──────────────────────────────────────

    [Theory]
    [InlineData("1.0f32",  6)]
    [InlineData("1.5f64",  6)]
    [InlineData("1f32",    4)]   // DecLit FloatSuffix form
    [InlineData("1f64",    4)]
    [InlineData("100f32",  6)]
    [InlineData("1e5f64",  6)]
    [InlineData("1.5e3f32", 8)]
    public void Float_literal_with_valid_suffix(string input, int expectedEnd)
    {
        var (tokens, diag) = Tokenize(input);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.FloatLiteral, tokens[0].TokenKind);
        Assert.Equal(expectedEnd, tokens[0].Span.End);
        Assert.False(diag.HasErrors);
    }

    // ─── Float Disambiguation ──────────────────────────────────────────────

    [Fact]
    public void Float_disambiguation_dot_followed_by_identifier()
    {
        // 1.foo → IntLiteral(1), '.' as BadChar (until Slice 6 adds Dot punct),
        // Identifier(foo).
        var (tokens, diag) = Tokenize("1.foo");
        Assert.Equal(4, tokens.Count);
        Assert.Equal(TokenKind.IntLiteral, tokens[0].TokenKind);
        Assert.Equal((0, 1), (tokens[0].Span.Start, tokens[0].Span.End));
        Assert.Equal(TokenKind.BadChar, tokens[1].TokenKind);
        Assert.Equal((1, 2), (tokens[1].Span.Start, tokens[1].Span.End));
        Assert.Equal(TokenKind.Identifier, tokens[2].TokenKind);
        Assert.Equal((2, 5), (tokens[2].Span.Start, tokens[2].Span.End));
    }

    [Fact]
    public void Float_followed_by_dot_and_identifier_keeps_float_kind()
    {
        // Regression für Bug 2: 1.5.foo muss FloatLiteral sein, nicht IntLiteral.
        var (tokens, _) = Tokenize("1.5.foo");
        Assert.Equal(4, tokens.Count);
        Assert.Equal(TokenKind.FloatLiteral, tokens[0].TokenKind);
        Assert.Equal((0, 3), (tokens[0].Span.Start, tokens[0].Span.End));
        Assert.Equal(TokenKind.BadChar, tokens[1].TokenKind);
        Assert.Equal((3, 4), (tokens[1].Span.Start, tokens[1].Span.End));
        Assert.Equal(TokenKind.Identifier, tokens[2].TokenKind);
    }

    // ─── LYR-LEX0003: Invalid Suffix ───────────────────────────────────────

    [Fact]
    public void Invalid_int_suffix_emits_LEX0003()
    {
        var (tokens, diag) = Tokenize("100i7");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.IntLiteral, tokens[0].TokenKind);
        Assert.Equal((0, 5), (tokens[0].Span.Start, tokens[0].Span.End));
        Assert.Equal(1, diag.ErrorCount);
        Assert.Equal("LYR-LEX0003", diag.Diagnostics[0].Code);
        Assert.Contains("i7", diag.Diagnostics[0].Message);
    }

    [Theory]
    [InlineData("0xFFi7")]        // invalide Int-Größe
    [InlineData("0xFFu7")]
    [InlineData("0xFFi128")]      // gibt's nicht in v1
    public void Invalid_suffix_on_hex_emits_LEX0003(string input)
    {
        var (tokens, diag) = Tokenize(input);
        Assert.Equal(TokenKind.IntLiteral, tokens[0].TokenKind);
        Assert.Equal(1, diag.ErrorCount);
        Assert.Equal("LYR-LEX0003", diag.Diagnostics[0].Code);
    }

    [Fact]
    public void Float_suffix_on_binary_emits_LEX0003()
    {
        var (_, diag) = Tokenize("0b1010f32");
        Assert.Equal(1, diag.ErrorCount);
        Assert.Equal("LYR-LEX0003", diag.Diagnostics[0].Code);
    }

    [Fact]
    public void Float_suffix_on_octal_emits_LEX0003()
    {
        var (_, diag) = Tokenize("0o7f32");
        Assert.Equal(1, diag.ErrorCount);
        Assert.Equal("LYR-LEX0003", diag.Diagnostics[0].Code);
    }

    [Fact]
    public void Int_suffix_on_float_form_emits_LEX0003()
    {
        var (_, diag) = Tokenize("1.5i32");
        Assert.Equal(1, diag.ErrorCount);
        Assert.Equal("LYR-LEX0003", diag.Diagnostics[0].Code);
    }

    [Fact]
    public void Int_suffix_on_exponent_form_emits_LEX0003()
    {
        var (_, diag) = Tokenize("1e3i32");
        Assert.Equal(1, diag.ErrorCount);
        Assert.Equal("LYR-LEX0003", diag.Diagnostics[0].Code);
    }

    // ─── LYR-LEX0004: Empty Literal After Prefix ───────────────────────────

    [Theory]
    [InlineData("0x")]
    [InlineData("0X")]
    [InlineData("0b")]
    [InlineData("0B")]
    [InlineData("0o")]
    [InlineData("0O")]
    public void Empty_prefixed_literal_emits_LEX0004(string input)
    {
        var (tokens, diag) = Tokenize(input);
        Assert.Equal(TokenKind.IntLiteral, tokens[0].TokenKind);
        Assert.Equal(1, diag.ErrorCount);
        Assert.Equal("LYR-LEX0004", diag.Diagnostics[0].Code);
    }

    [Fact]
    public void Hex_literal_with_leading_underscore_emits_diagnostic()
    {
        // Aktuell LYR-LEX0004 — wenn du die Branches in ScanNonDecLiteral
        // vertauschst, wird's LYR-LEX0005. Beides ist OK; Test passt sich an.
        var (_, diag) = Tokenize("0x_FF");
        Assert.Equal(1, diag.ErrorCount);
        Assert.True(
            diag.Diagnostics[0].Code is "LYR-LEX0004" or "LYR-LEX0005",
            $"unexpected code {diag.Diagnostics[0].Code}");
    }

    // ─── LYR-LEX0006: Exponent Without Digits ──────────────────────────────

    [Theory]
    [InlineData("1e")]
    [InlineData("1e+")]
    [InlineData("1e-")]
    [InlineData("1E+")]
    [InlineData("1.5e+")]
    public void Exponent_without_digits_emits_LEX0006(string input)
    {
        var (_, diag) = Tokenize(input);
        Assert.Equal(1, diag.ErrorCount);
        Assert.Equal("LYR-LEX0006", diag.Diagnostics[0].Code);
    }

    // ─── Numbers Adjacent to Identifiers / Other Tokens ────────────────────

    [Fact]
    public void Number_followed_by_non_iuf_letter_starts_identifier()
    {
        // 100abc → IntLiteral(0..3), Identifier(3..6)
        var (tokens, diag) = Tokenize("100abc");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenKind.IntLiteral, tokens[0].TokenKind);
        Assert.Equal((0, 3), (tokens[0].Span.Start, tokens[0].Span.End));
        Assert.Equal(TokenKind.Identifier, tokens[1].TokenKind);
        Assert.Equal((3, 6), (tokens[1].Span.Start, tokens[1].Span.End));
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Number_with_valid_suffix_then_more_letters()
    {
        // 100i32x → IntLiteral(0..6), Identifier(6..7)
        var (tokens, _) = Tokenize("100i32x");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenKind.IntLiteral, tokens[0].TokenKind);
        Assert.Equal((0, 6), (tokens[0].Span.Start, tokens[0].Span.End));
        Assert.Equal(TokenKind.Identifier, tokens[1].TokenKind);
        Assert.Equal((6, 7), (tokens[1].Span.Start, tokens[1].Span.End));
    }

    [Fact]
    public void Multiple_numbers_separated_by_whitespace()
    {
        var (tokens, diag) = Tokenize("100 200 0xFF");
        Assert.Equal(4, tokens.Count);
        Assert.All(tokens.Take(3), t => Assert.Equal(TokenKind.IntLiteral, t.TokenKind));
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Number_inside_braces()
    {
        var (tokens, diag) = Tokenize("{ 42 }");
        var kinds = tokens.Select(t => t.TokenKind).ToArray();
        Assert.Equal(
            new[] { TokenKind.LBrace, TokenKind.IntLiteral, TokenKind.RBrace, TokenKind.Eof },
            kinds);
        Assert.False(diag.HasErrors);
    }
    
        // ─── String Literals: Plain ────────────────────────────────────────────

    [Theory]
    [InlineData("\"\"",             2)]
    [InlineData("\"a\"",            3)]
    [InlineData("\"hello\"",        7)]
    [InlineData("\"hello world\"", 13)]
    public void Plain_string_literal(string input, int expectedEnd)
    {
        var (tokens, diag) = Tokenize(input);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.StringLiteral, tokens[0].TokenKind);
        Assert.Equal(0, tokens[0].Span.Start);
        Assert.Equal(expectedEnd, tokens[0].Span.End);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void String_with_non_ascii_chars()
    {
        var (tokens, diag) = Tokenize("\"äöü日\"");
        Assert.Equal(TokenKind.StringLiteral, tokens[0].TokenKind);
        Assert.False(diag.HasErrors);
    }

    // ─── String Literals: Escapes ──────────────────────────────────────────

    [Theory]
    [InlineData("\"\\n\"",   4)]   // Lyric: "\n"
    [InlineData("\"\\r\"",   4)]
    [InlineData("\"\\t\"",   4)]
    [InlineData("\"\\\\\"",  4)]   // Lyric: "\\"
    [InlineData("\"\\\"\"",  4)]   // Lyric: "\""
    [InlineData("\"\\0\"",   4)]
    [InlineData("\"\\'\"",   4)]
    public void String_with_simple_escape(string input, int expectedEnd)
    {
        // Regression Bug 1: _pos++ nach ConsumeEscapeSequence.
        var (tokens, diag) = Tokenize(input);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.StringLiteral, tokens[0].TokenKind);
        Assert.Equal(expectedEnd, tokens[0].Span.End);
        Assert.False(diag.HasErrors);
    }

    [Theory]
    [InlineData("\"\\x1F\"",     6)]
    [InlineData("\"\\xFF\"",     6)]
    [InlineData("\"\\x00\"",     6)]
    [InlineData("\"\\xab\"",     6)]
    public void String_with_valid_hex_escape(string input, int expectedEnd)
    {
        // Regression Bug 3: Loop-Count-Bug in ConsumeHexEscape.
        var (tokens, diag) = Tokenize(input);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.StringLiteral, tokens[0].TokenKind);
        Assert.Equal(expectedEnd, tokens[0].Span.End);
        Assert.False(diag.HasErrors);
    }

    [Theory]
    [InlineData("\"\\u{41}\"",      8)]    // 'A'
    [InlineData("\"\\u{0}\"",       7)]
    [InlineData("\"\\u{1F30D}\"",  11)]    // 🌍
    [InlineData("\"\\u{10FFFF}\"", 12)]    // max valid
    public void String_with_valid_unicode_escape(string input, int expectedEnd)
    {
        // Regression Bug 3+4: Loop-Count + Int32.Parse ohne HexNumber.
        var (tokens, diag) = Tokenize(input);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.StringLiteral, tokens[0].TokenKind);
        Assert.Equal(expectedEnd, tokens[0].Span.End);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void String_with_multiple_escapes()
    {
        var (tokens, diag) = Tokenize("\"line1\\nline2\\t\\u{41}\"");
        Assert.Equal(TokenKind.StringLiteral, tokens[0].TokenKind);
        Assert.False(diag.HasErrors);
    }

    // ─── String Diagnostics ────────────────────────────────────────────────

    [Fact]
    public void Unknown_escape_emits_LEX0007()
    {
        var (tokens, diag) = Tokenize("\"\\q\"");
        Assert.Equal(TokenKind.StringLiteral, tokens[0].TokenKind);
        Assert.Equal(1, diag.ErrorCount);
        Assert.Equal("LYR-LEX0007", diag.Diagnostics[0].Code);
    }

    [Theory]
    [InlineData("\"\\x\"")]      // keine Hex-Digits
    [InlineData("\"\\x1\"")]     // nur 1 Hex-Digit
    [InlineData("\"\\xZZ\"")]    // non-hex
    public void Invalid_hex_escape_emits_LEX0007(string input)
    {
        var (_, diag) = Tokenize(input);
        Assert.True(diag.ErrorCount >= 1);
        Assert.Equal("LYR-LEX0007", diag.Diagnostics[0].Code);
    }

    [Theory]
    [InlineData("\"\\u\"")]        // kein {
    [InlineData("\"\\u{}\"")]      // leer — Regression Bug 5
    [InlineData("\"\\u{ZZ}\"")]    // non-hex
    [InlineData("\"\\u{41\"")]     // kein schließendes }
    public void Invalid_unicode_escape_emits_LEX0007(string input)
    {
        var (_, diag) = Tokenize(input);
        Assert.True(diag.ErrorCount >= 1);
        Assert.Equal("LYR-LEX0007", diag.Diagnostics[0].Code);
    }

    [Fact]
    public void Unicode_escape_out_of_range_emits_LEX0007()
    {
        var (_, diag) = Tokenize("\"\\u{110000}\"");
        Assert.True(diag.ErrorCount >= 1);
        Assert.Equal("LYR-LEX0007", diag.Diagnostics[0].Code);
    }

    [Theory]
    [InlineData("\"")]              // nur Open-Quote → EOF
    [InlineData("\"foo")]           // kein Close → EOF
    public void Unterminated_string_emits_LEX0009(string input)
    {
        var (tokens, diag) = Tokenize(input);
        Assert.Equal(TokenKind.StringLiteral, tokens[0].TokenKind);
        Assert.Equal(1, diag.ErrorCount);
        Assert.Equal("LYR-LEX0009", diag.Diagnostics[0].Code);
    }

    [Fact]
    public void String_with_unescaped_newline_yields_cascade()
    {
        // Recovery-Verhalten: jedes `"` startet einen neuen String.
        // Nach LYR-LEX0009 am `\n` lexed der Rest normal weiter, das
        // zweite `"` öffnet wieder einen unterminated String bis EOF.
        var (tokens, diag) = Tokenize("\"foo\nbar\"");
        var stringTokens = tokens.Where(t => t.TokenKind == TokenKind.StringLiteral).ToList();
        Assert.Equal(2, stringTokens.Count);
        Assert.Equal(2, diag.ErrorCount);
        Assert.All(diag.Diagnostics, d => Assert.Equal("LYR-LEX0009", d.Code));
    }

    // ─── Char Literals: Plain ──────────────────────────────────────────────

    [Theory]
    [InlineData("'a'",   3)]
    [InlineData("'Z'",   3)]
    [InlineData("' '",   3)]
    [InlineData("'5'",   3)]
    public void Plain_char_literal(string input, int expectedEnd)
    {
        // Regression Bug 2: schließendes ' returnt nicht.
        var (tokens, diag) = Tokenize(input);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.CharLiteral, tokens[0].TokenKind);
        Assert.Equal(0, tokens[0].Span.Start);
        Assert.Equal(expectedEnd, tokens[0].Span.End);
        Assert.False(diag.HasErrors);
    }

    [Theory]
    [InlineData("'\\n'",   4)]
    [InlineData("'\\t'",   4)]
    [InlineData("'\\\\'",  4)]
    [InlineData("'\\''",   4)]   // escaped '
    [InlineData("'\\0'",   4)]
    public void Char_with_simple_escape(string input, int expectedEnd)
    {
        var (tokens, diag) = Tokenize(input);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.CharLiteral, tokens[0].TokenKind);
        Assert.Equal(expectedEnd, tokens[0].Span.End);
        Assert.False(diag.HasErrors);
    }

    [Theory]
    [InlineData("'\\x1F'",     6)]
    [InlineData("'\\u{41}'",   8)]
    [InlineData("'\\u{1F30D}'", 11)]
    public void Char_with_full_escape(string input, int expectedEnd)
    {
        var (tokens, diag) = Tokenize(input);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.CharLiteral, tokens[0].TokenKind);
        Assert.Equal(expectedEnd, tokens[0].Span.End);
        Assert.False(diag.HasErrors);
    }

    // ─── Char Diagnostics ──────────────────────────────────────────────────

    [Fact]
    public void Empty_char_emits_LEX0008()
    {
        var (tokens, diag) = Tokenize("''");
        Assert.Equal(TokenKind.CharLiteral, tokens[0].TokenKind);
        Assert.Equal(1, diag.ErrorCount);
        Assert.Equal("LYR-LEX0008", diag.Diagnostics[0].Code);
    }

    [Theory]
    [InlineData("'ab'")]
    [InlineData("'abc'")]
    [InlineData("'xyz'")]
    public void Char_with_multiple_chars_emits_LEX0008(string input)
    {
        var (_, diag) = Tokenize(input);
        Assert.Equal(1, diag.ErrorCount);
        Assert.Equal("LYR-LEX0008", diag.Diagnostics[0].Code);
    }

    [Theory]
    [InlineData("'")]              // nur Open → EOF
    [InlineData("'a")]             // kein Close → EOF
    public void Unterminated_char_emits_LEX0010(string input)
    {
        var (tokens, diag) = Tokenize(input);
        Assert.Equal(TokenKind.CharLiteral, tokens[0].TokenKind);
        Assert.Equal(1, diag.ErrorCount);
        Assert.Equal("LYR-LEX0010", diag.Diagnostics[0].Code);
    }

    [Fact]
    public void Char_with_unescaped_newline_yields_cascade()
    {
        var (tokens, diag) = Tokenize("'a\n'");
        var charTokens = tokens.Where(t => t.TokenKind == TokenKind.CharLiteral).ToList();
        Assert.Equal(2, charTokens.Count);
        Assert.Equal(2, diag.ErrorCount);
        Assert.All(diag.Diagnostics, d => Assert.Equal("LYR-LEX0010", d.Code));
    }

    // ─── Adjazenz ──────────────────────────────────────────────────────────

    [Fact]
    public void String_followed_by_identifier()
    {
        var (tokens, diag) = Tokenize("\"hello\" world");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenKind.StringLiteral, tokens[0].TokenKind);
        Assert.Equal(TokenKind.Identifier, tokens[1].TokenKind);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Char_followed_by_int()
    {
        var (tokens, diag) = Tokenize("'a' 42");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenKind.CharLiteral, tokens[0].TokenKind);
        Assert.Equal(TokenKind.IntLiteral, tokens[1].TokenKind);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Multiple_strings_in_sequence()
    {
        var (tokens, diag) = Tokenize("\"foo\" \"bar\" \"baz\"");
        Assert.Equal(4, tokens.Count);
        Assert.All(tokens.Take(3), t => Assert.Equal(TokenKind.StringLiteral, t.TokenKind));
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Mixed_strings_and_chars()
    {
        var (tokens, diag) = Tokenize("\"foo\" 'x' \"bar\"");
        var kinds = tokens.Select(t => t.TokenKind).ToArray();
        Assert.Equal(
            new[] { TokenKind.StringLiteral, TokenKind.CharLiteral,
                    TokenKind.StringLiteral, TokenKind.Eof },
            kinds);
        Assert.False(diag.HasErrors);
    }
}