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

    // ─── EOF and whitespace ────────────────────────────────────────────────

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
        // Regression: SkipTrivia has to run before the EOF check, or the sentinel reads out of bounds.
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

    // ─── line comments ─────────────────────────────────────────────────────

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
        // Regression: after a line comment ending at EOF, without a '\n', _pos must not run past the
        // length of the source.
        var (tokens, diag) = Tokenize("foo // tail");   // length 11
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
        Assert.Equal(11, tokens[0].Span.Start);   // directly after "// comment\n"
        Assert.Equal(14, tokens[0].Span.End);
    }

    [Fact]
    public void Whitespace_after_line_comment_is_consumed()
    {
        // Regression for the outer loop structure in SkipTrivia: whitespace after a comment has to be
        // skipped too.
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
        // Here `fn` is still an ordinary identifier; keywords come later.
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
        // Regression: apostrophes around the char, not only one.
        var (_, diag) = Tokenize("#");
        Assert.Contains("'#'", diag.Diagnostics[0].Message);
    }

    [Fact]
    public void Bad_character_message_names_the_actual_bad_char()
    {
        // Regression: reading Current AFTER _pos++ reports the wrong character.
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

    // ─── span accuracy ─────────────────────────────────────────────────────

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
    [InlineData("fnx")]      // a keyword as a prefix
    [InlineData("fn_")]      // an underscore suffix
    [InlineData("fn1")]      // a digit suffix
    [InlineData("_fn")]      // an underscore prefix
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
        // async, await, const, trait, move and own are not
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
        // `fn` should now be recognised as a keyword rather than an identifier.
        var (tokens, diag) = Tokenize("fn main() {}");
        var kinds = tokens.Select(t => t.TokenKind).ToArray();
        Assert.Equal(
            new[] {
                TokenKind.Fn,            // now a keyword
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

    // ─── doc comments ──────────────────────────────────────────────────────

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
        // "////" is a doc comment with the body "/".
        var (tokens, _) = Tokenize("////");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.DocComment, tokens[0].TokenKind);
        Assert.Equal(0, tokens[0].Span.Start);
        Assert.Equal(4, tokens[0].Span.End);
    }

    [Fact]
    public void DocComment_disambiguated_from_line_comment()
    {
        // Regression for the "PeekAt(2)" disambiguation in SkipTrivia and Next.
        var (tokens, _) = Tokenize("// not a doc\n/// a doc");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.DocComment, tokens[0].TokenKind);
        Assert.Equal(13, tokens[0].Span.Start);   // after "// not a doc\n"
        Assert.Equal(22, tokens[0].Span.End);     // to the end of the file
    }

    [Fact]
    public void DocComment_followed_by_identifier_on_next_line()
    {
        var (tokens, _) = Tokenize("/// docs\nfoo");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenKind.DocComment, tokens[0].TokenKind);
        Assert.Equal(0, tokens[0].Span.Start);
        Assert.Equal(8, tokens[0].Span.End);      // up to the newline
        Assert.Equal(TokenKind.Identifier, tokens[1].TokenKind);
        Assert.Equal(9, tokens[1].Span.Start);    // directly after the newline
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
        Assert.Equal(14, tokens[0].Span.End);     // the whole length
        Assert.Equal(14, tokens[1].Span.Start);   // EOF an Length
    }

    // ─── block comments, as trivia ─────────────────────────────────────────

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
        Assert.Equal(14, tokens[1].Span.Start);   // directly after "foo /* mid */ "
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
        Assert.Equal(7, tokens[0].Span.Start);   // directly after "/*foo*/"
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
        // A nesting left open: only one diagnostic is expected, because at the end depth > 0 fires
        // exactly once.
        var (_, diag) = Tokenize("/* outer /* inner ");
        Assert.Equal(1, diag.ErrorCount);
        Assert.Equal("LYR-LEX0002", diag.Diagnostics[0].Code);
    }

    [Fact]
    public void Block_comment_with_doc_comment_marker_inside_is_just_block()
    {
        // The /// inside a block comment is content rather than a doc comment token.
        var (tokens, _) = Tokenize("/* /// not a doc */");
        Assert.Single(tokens);
        Assert.Equal(TokenKind.Eof, tokens[0].TokenKind);
    }

    // ─── trivia order ──────────────────────────────────────────────────────

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
        Assert.Equal(14, tokens[0].Span.Start);   // directly after "/* comment */ "
        Assert.Equal(16, tokens[0].Span.End);
    }

    [Fact]
    public void DocComment_then_keyword_then_doc_again()
    {
        // A more realistic example.
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
        // 1.foo becomes IntLiteral(1), Dot, Identifier(foo).
        var (tokens, diag) = Tokenize("1.foo");
        Assert.Equal(4, tokens.Count);
        Assert.Equal(TokenKind.IntLiteral, tokens[0].TokenKind);
        Assert.Equal((0, 1), (tokens[0].Span.Start, tokens[0].Span.End));
        Assert.Equal(TokenKind.Dot, tokens[1].TokenKind);
        Assert.Equal((1, 2), (tokens[1].Span.Start, tokens[1].Span.End));
        Assert.Equal(TokenKind.Identifier, tokens[2].TokenKind);
        Assert.Equal((2, 5), (tokens[2].Span.Start, tokens[2].Span.End));
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Float_followed_by_dot_and_identifier_keeps_float_kind()
    {
        // Regression: 1.5.foo has to be a FloatLiteral rather than an IntLiteral, and the separating '.'
        // is a Dot token.
        var (tokens, diag) = Tokenize("1.5.foo");
        Assert.Equal(4, tokens.Count);
        Assert.Equal(TokenKind.FloatLiteral, tokens[0].TokenKind);
        Assert.Equal((0, 3), (tokens[0].Span.Start, tokens[0].Span.End));
        Assert.Equal(TokenKind.Dot, tokens[1].TokenKind);
        Assert.Equal((3, 4), (tokens[1].Span.Start, tokens[1].Span.End));
        Assert.Equal(TokenKind.Identifier, tokens[2].TokenKind);
        Assert.False(diag.HasErrors);
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
    [InlineData("0xFFi7")]        // an invalid integer size
    [InlineData("0xFFu7")]
    [InlineData("0xFFi128")]      // does not exist
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
        // Currently LYR-LEX0004; swapping the branches in ScanNonDecLiteral makes it LYR-LEX0005. Either
        // is fine, and the test accepts both.
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
        // Regression: _pos++ after ConsumeEscapeSequence.
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
        // Regression: the loop count in ConsumeHexEscape.
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
        // Regression: the loop count and Int32.Parse without HexNumber.
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
    [InlineData("\"\\x\"")]      // no hex digits
    [InlineData("\"\\x1\"")]     // only one hex digit
    [InlineData("\"\\xZZ\"")]    // non-hex
    public void Invalid_hex_escape_emits_LEX0007(string input)
    {
        var (_, diag) = Tokenize(input);
        Assert.True(diag.ErrorCount >= 1);
        Assert.Equal("LYR-LEX0007", diag.Diagnostics[0].Code);
    }

    [Theory]
    [InlineData("\"\\u\"")]        // no {
    [InlineData("\"\\u{}\"")]      // empty
    [InlineData("\"\\u{ZZ}\"")]    // non-hex
    [InlineData("\"\\u{41\"")]     // no closing }
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
    [InlineData("\"")]              // only an opening quote, then EOF
    [InlineData("\"foo")]           // no closing quote, then EOF
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
        // Recovery behaviour: every `"` starts a new string. After LYR-LEX0009 at the `\n` the rest lexes
        // normally, and the second `"` opens another unterminated string up to EOF.
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
        // Regression: the closing ' does not return.
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
    [InlineData("'")]              // only an opening quote, then EOF
    [InlineData("'a")]             // no closing quote, then EOF
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
    
        // ─── F-Strings: Basics ─────────────────────────────────────────────────

    [Fact]
    public void Empty_fstring_yields_start_and_end()
    {
        var (tokens, diag) = Tokenize("f\"\"");
        var kinds = tokens.Select(t => t.TokenKind).ToArray();
        Assert.Equal(
            new[] { TokenKind.FStringStart, TokenKind.FStringEnd, TokenKind.Eof },
            kinds);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Plain_fstring_text_only()
    {
        var (tokens, diag) = Tokenize("f\"hello\"");
        var kinds = tokens.Select(t => t.TokenKind).ToArray();
        Assert.Equal(
            new[] {
                TokenKind.FStringStart,
                TokenKind.FStringChunk,
                TokenKind.FStringEnd,
                TokenKind.Eof
            },
            kinds);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Plain_fstring_spans_are_correct()
    {
        var (tokens, _) = Tokenize("f\"hello\"");
        // Chars: f " h e l l o "  — length 8
        Assert.Equal((0, 2), (tokens[0].Span.Start, tokens[0].Span.End));   // FStringStart "f\""
        Assert.Equal((2, 7), (tokens[1].Span.Start, tokens[1].Span.End));   // Chunk "hello"
        Assert.Equal((7, 8), (tokens[2].Span.Start, tokens[2].Span.End));   // FStringEnd "\""
    }

    [Fact]
    public void Fstring_with_single_interpolation()
    {
        var (tokens, diag) = Tokenize("f\"{x}\"");
        var kinds = tokens.Select(t => t.TokenKind).ToArray();
        Assert.Equal(
            new[] {
                TokenKind.FStringStart,
                TokenKind.FStringInterpStart,
                TokenKind.Identifier,
                TokenKind.FStringInterpEnd,
                TokenKind.FStringEnd,
                TokenKind.Eof
            },
            kinds);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Fstring_chunk_then_interp_then_chunk()
    {
        var (tokens, diag) = Tokenize("f\"hi {x}!\"");
        var kinds = tokens.Select(t => t.TokenKind).ToArray();
        Assert.Equal(
            new[] {
                TokenKind.FStringStart,
                TokenKind.FStringChunk,       // "hi "
                TokenKind.FStringInterpStart,
                TokenKind.Identifier,
                TokenKind.FStringInterpEnd,
                TokenKind.FStringChunk,       // "!"
                TokenKind.FStringEnd,
                TokenKind.Eof
            },
            kinds);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Fstring_with_multiple_back_to_back_interpolations()
    {
        var (tokens, diag) = Tokenize("f\"{a}{b}\"");
        var kinds = tokens.Select(t => t.TokenKind).ToArray();
        Assert.Equal(
            new[] {
                TokenKind.FStringStart,
                TokenKind.FStringInterpStart,
                TokenKind.Identifier,
                TokenKind.FStringInterpEnd,
                TokenKind.FStringInterpStart,
                TokenKind.Identifier,
                TokenKind.FStringInterpEnd,
                TokenKind.FStringEnd,
                TokenKind.Eof
            },
            kinds);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Empty_interpolation_yields_consecutive_interp_markers()
    {
        // f"{}" — empty interp. Parser will reject, lexer just emits.
        var (tokens, diag) = Tokenize("f\"{}\"");
        var kinds = tokens.Select(t => t.TokenKind).ToArray();
        Assert.Equal(
            new[] {
                TokenKind.FStringStart,
                TokenKind.FStringInterpStart,
                TokenKind.FStringInterpEnd,
                TokenKind.FStringEnd,
                TokenKind.Eof
            },
            kinds);
        Assert.False(diag.HasErrors);
    }

    // ─── Escapes in F-String Chunks ────────────────────────────────────────

    [Theory]
    [InlineData("f\"\\n\"")]    // Lyric: f"\n"
    [InlineData("f\"\\t\"")]
    [InlineData("f\"\\r\"")]
    [InlineData("f\"\\\\\"")]   // Lyric: f"\\"
    [InlineData("f\"\\\"\"")]   // Lyric: f"\""
    [InlineData("f\"\\0\"")]
    public void Fstring_chunk_with_simple_escape_no_error(string input)
    {
        var (tokens, diag) = Tokenize(input);
        Assert.Equal(4, tokens.Count);  // FStringStart, Chunk, FStringEnd, Eof
        Assert.Equal(TokenKind.FStringChunk, tokens[1].TokenKind);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Fstring_chunk_with_hex_escape()
    {
        var (tokens, diag) = Tokenize("f\"\\x41\"");
        Assert.Equal(4, tokens.Count);
        Assert.Equal(TokenKind.FStringChunk, tokens[1].TokenKind);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Fstring_chunk_with_unicode_escape()
    {
        var (tokens, diag) = Tokenize("f\"hello \\u{1F30D}\"");
        Assert.Equal(4, tokens.Count);
        Assert.Equal(TokenKind.FStringChunk, tokens[1].TokenKind);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Fstring_chunk_with_invalid_escape_reports_LEX0007()
    {
        var (tokens, diag) = Tokenize("f\"\\q\"");
        Assert.Equal(TokenKind.FStringChunk, tokens[1].TokenKind);
        Assert.Equal(1, diag.ErrorCount);
        Assert.Equal("LYR-LEX0007", diag.Diagnostics[0].Code);
    }

    // ─── Format Spec ───────────────────────────────────────────────────────

    [Fact]
    public void Fstring_with_format_spec_basic()
    {
        var (tokens, diag) = Tokenize("f\"{x:N2}\"");
        var kinds = tokens.Select(t => t.TokenKind).ToArray();
        Assert.Equal(
            new[] {
                TokenKind.FStringStart,
                TokenKind.FStringInterpStart,
                TokenKind.Identifier,
                TokenKind.FStringFormatSpec,
                TokenKind.FStringInterpEnd,
                TokenKind.FStringEnd,
                TokenKind.Eof
            },
            kinds);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Format_spec_span_excludes_colon_and_brace()
    {
        // Chars: f " { x : N 2 } "  — length 9, indices 0..8
        var (tokens, _) = Tokenize("f\"{x:N2}\"");
        var spec = tokens.First(t => t.TokenKind == TokenKind.FStringFormatSpec);
        Assert.Equal(5, spec.Span.Start);   // directly after the ':'
        Assert.Equal(7, spec.Span.End);     // directly before the '}'
    }

    [Fact]
    public void Empty_format_spec_emits_empty_token()
    {
        // f"{x:}": the format spec is empty, and the token span is (5,5).
        var (tokens, diag) = Tokenize("f\"{x:}\"");
        var spec = tokens.FirstOrDefault(t => t.TokenKind == TokenKind.FStringFormatSpec);
        Assert.NotEqual(default, spec);
        Assert.Equal(0, spec.Span.Length);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Format_spec_with_special_chars_is_opaque()
    {
        // "0>5" would normally be IntLit Gt IntLit; as a format spec it is one token.
        var (tokens, diag) = Tokenize("f\"{x:0>5}\"");
        var spec = tokens.First(t => t.TokenKind == TokenKind.FStringFormatSpec);
        Assert.Equal(3, spec.Span.Length);   // "0>5"
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Format_spec_with_letters_and_digits()
    {
        var (tokens, diag) = Tokenize("f\"{x:foobar123}\"");
        var spec = tokens.First(t => t.TokenKind == TokenKind.FStringFormatSpec);
        Assert.Equal(9, spec.Span.Length);
        Assert.False(diag.HasErrors);
    }

    // ─── Brace Depth in Interp ─────────────────────────────────────────────

    [Fact]
    public void Inner_braces_in_interp_tokenize_as_LBrace_RBrace()
    {
        // f"{{x}}": the outer {} are interpolation markers, the inner {} are LBrace and RBrace.
        var (tokens, diag) = Tokenize("f\"{{x}}\"");
        var kinds = tokens.Select(t => t.TokenKind).ToArray();
        Assert.Equal(
            new[] {
                TokenKind.FStringStart,
                TokenKind.FStringInterpStart,    // outer {
                TokenKind.LBrace,                 // inner { (depth 0→1)
                TokenKind.Identifier,
                TokenKind.RBrace,                 // inner } (depth 1→0)
                TokenKind.FStringInterpEnd,      // outer }
                TokenKind.FStringEnd,
                TokenKind.Eof
            },
            kinds);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Empty_inner_brace_pair_in_interp()
    {
        var (tokens, diag) = Tokenize("f\"{{}}\"");
        var kinds = tokens.Select(t => t.TokenKind).ToArray();
        Assert.Equal(
            new[] {
                TokenKind.FStringStart,
                TokenKind.FStringInterpStart,
                TokenKind.LBrace,
                TokenKind.RBrace,
                TokenKind.FStringInterpEnd,
                TokenKind.FStringEnd,
                TokenKind.Eof
            },
            kinds);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Deeply_nested_braces_in_interp()
    {
        // f"{{ {x} }}": three brace depths in a row.
        var (tokens, diag) = Tokenize("f\"{{ {x} }}\"");
        var kinds = tokens.Select(t => t.TokenKind).ToArray();
        Assert.Equal(
            new[] {
                TokenKind.FStringStart,
                TokenKind.FStringInterpStart,
                TokenKind.LBrace,
                TokenKind.LBrace,
                TokenKind.Identifier,
                TokenKind.RBrace,
                TokenKind.RBrace,
                TokenKind.FStringInterpEnd,
                TokenKind.FStringEnd,
                TokenKind.Eof
            },
            kinds);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Colon_at_depth_greater_zero_does_not_trigger_format_spec()
    {
        // f"{{x:y}}": the inner `:` is at brace depth 1 and therefore triggers no format spec. It is a
        // Colon token.
        var (tokens, diag) = Tokenize("f\"{{x:y}}\"");
        var kinds = tokens.Select(t => t.TokenKind).ToArray();
        Assert.Equal(
            new[] {
                TokenKind.FStringStart,
                TokenKind.FStringInterpStart,
                TokenKind.LBrace,
                TokenKind.Identifier,             // x
                TokenKind.Colon,                  // : is not a format spec, because depth = 1
                TokenKind.Identifier,             // y
                TokenKind.RBrace,
                TokenKind.FStringInterpEnd,
                TokenKind.FStringEnd,
                TokenKind.Eof
            },
            kinds);
        Assert.False(diag.HasErrors);
    }

    // ─── Nested F-Strings ──────────────────────────────────────────────────

    [Fact]
    public void Nested_fstring_one_level()
    {
        // f"a={f"b={x}"}"
        var (tokens, diag) = Tokenize("f\"a={f\"b={x}\"}\"");
        var kinds = tokens.Select(t => t.TokenKind).ToArray();
        Assert.Equal(
            new[] {
                TokenKind.FStringStart,           // outer f"
                TokenKind.FStringChunk,           // "a="
                TokenKind.FStringInterpStart,     // outer {
                TokenKind.FStringStart,           // inner f"
                TokenKind.FStringChunk,           // "b="
                TokenKind.FStringInterpStart,     // inner {
                TokenKind.Identifier,             // x
                TokenKind.FStringInterpEnd,       // inner }
                TokenKind.FStringEnd,             // inner "
                TokenKind.FStringInterpEnd,       // outer }
                TokenKind.FStringEnd,             // outer "
                TokenKind.Eof
            },
            kinds);
        Assert.False(diag.HasErrors);
    }

    // ─── Disambiguation: f vs f-string ─────────────────────────────────────

    [Fact]
    public void Identifier_f_followed_by_other_token_is_identifier()
    {
        var (tokens, diag) = Tokenize("f x");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenKind.Identifier, tokens[0].TokenKind);
        Assert.Equal(TokenKind.Identifier, tokens[1].TokenKind);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Identifier_f_with_space_then_string_is_two_separate_tokens()
    {
        // "f " plus '"hello"': the space separates, so this is no f-string.
        var (tokens, diag) = Tokenize("f \"hello\"");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenKind.Identifier, tokens[0].TokenKind);
        Assert.Equal(TokenKind.StringLiteral, tokens[1].TokenKind);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Identifier_starting_with_f_is_not_fstring()
    {
        var (tokens, diag) = Tokenize("foo");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.Identifier, tokens[0].TokenKind);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Keyword_fn_is_not_treated_as_fstring()
    {
        // "fn" starts with 'f', but the second character is 'n' rather than '"'.
        var (tokens, diag) = Tokenize("fn");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.Fn, tokens[0].TokenKind);
        Assert.False(diag.HasErrors);
    }

    // ─── Interp Content Variety ────────────────────────────────────────────

    [Fact]
    public void Interp_with_int_literal()
    {
        var (tokens, diag) = Tokenize("f\"{42}\"");
        var kinds = tokens.Select(t => t.TokenKind).ToArray();
        Assert.Equal(
            new[] {
                TokenKind.FStringStart,
                TokenKind.FStringInterpStart,
                TokenKind.IntLiteral,
                TokenKind.FStringInterpEnd,
                TokenKind.FStringEnd,
                TokenKind.Eof
            },
            kinds);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Interp_with_keyword_true()
    {
        var (tokens, diag) = Tokenize("f\"{true}\"");
        var kinds = tokens.Select(t => t.TokenKind).ToArray();
        Assert.Equal(
            new[] {
                TokenKind.FStringStart,
                TokenKind.FStringInterpStart,
                TokenKind.True,
                TokenKind.FStringInterpEnd,
                TokenKind.FStringEnd,
                TokenKind.Eof
            },
            kinds);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Interp_with_whitespace_around_expression()
    {
        var (tokens, diag) = Tokenize("f\"{  x  }\"");
        // SkipTrivia frisst Whitespace in InterpMode.
        var kinds = tokens.Select(t => t.TokenKind).ToArray();
        Assert.Equal(
            new[] {
                TokenKind.FStringStart,
                TokenKind.FStringInterpStart,
                TokenKind.Identifier,
                TokenKind.FStringInterpEnd,
                TokenKind.FStringEnd,
                TokenKind.Eof
            },
            kinds);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Interp_with_block_comment_is_skipped()
    {
        // SkipTrivia in interpolation mode handles comments as everywhere else.
        var (tokens, diag) = Tokenize("f\"{ /* note */ x }\"");
        var kinds = tokens.Select(t => t.TokenKind).ToArray();
        Assert.Equal(
            new[] {
                TokenKind.FStringStart,
                TokenKind.FStringInterpStart,
                TokenKind.Identifier,
                TokenKind.FStringInterpEnd,
                TokenKind.FStringEnd,
                TokenKind.Eof
            },
            kinds);
        Assert.False(diag.HasErrors);
    }

    // ─── Unterminated F-Strings ────────────────────────────────────────────

    [Fact]
    public void Unterminated_fstring_text_emits_LEX0011()
    {
        var (_, diag) = Tokenize("f\"hello");
        Assert.Equal(1, diag.ErrorCount);
        Assert.Equal("LYR-LEX0011", diag.Diagnostics[0].Code);
    }

    [Fact]
    public void Unterminated_fstring_in_interp_emits_LEX0011()
    {
        var (_, diag) = Tokenize("f\"{x");
        Assert.Equal(1, diag.ErrorCount);
        Assert.Equal("LYR-LEX0011", diag.Diagnostics[0].Code);
    }

    [Fact]
    public void Unterminated_fstring_in_format_spec_emits_LEX0011()
    {
        var (_, diag) = Tokenize("f\"{x:N2");
        Assert.Equal(1, diag.ErrorCount);
        Assert.Equal("LYR-LEX0011", diag.Diagnostics[0].Code);
    }

    [Fact]
    public void Newline_in_fstring_text_emits_LEX0011()
    {
        var (_, diag) = Tokenize("f\"hello\nworld\"");
        Assert.True(diag.ErrorCount >= 1);
        Assert.Equal("LYR-LEX0011", diag.Diagnostics[0].Code);
    }

    [Fact]
    public void Recovery_after_unterminated_continues_in_normal_mode()
    {
        // After an unterminated string at EOF: nothing more, since EOF follows. After one at a '\n':
        // further tokens lex normally, and "bar" has to appear as an identifier after the recovery.
        var (tokens, diag) = Tokenize("f\"hi\nbar");
        Assert.True(diag.ErrorCount >= 1);
        Assert.Contains(tokens, t => t.TokenKind == TokenKind.Identifier);
    }

    // ─── Adjacency ─────────────────────────────────────────────────────────

    [Fact]
    public void Fstring_followed_by_identifier()
    {
        var (tokens, diag) = Tokenize("f\"a\" foo");
        Assert.Equal(5, tokens.Count);   // FStringStart, Chunk, FStringEnd, Identifier, Eof
        Assert.Equal(TokenKind.FStringStart, tokens[0].TokenKind);
        Assert.Equal(TokenKind.Identifier, tokens[3].TokenKind);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Multiple_fstrings_in_sequence()
    {
        var (tokens, diag) = Tokenize("f\"a\" f\"b\"");
        var fStarts = tokens.Count(t => t.TokenKind == TokenKind.FStringStart);
        var fEnds = tokens.Count(t => t.TokenKind == TokenKind.FStringEnd);
        Assert.Equal(2, fStarts);
        Assert.Equal(2, fEnds);
        Assert.False(diag.HasErrors);
    }

    // ─── operators: single character ───────────────────────────────────────

    [Theory]
    [InlineData("(", TokenKind.LParen)]
    [InlineData(")", TokenKind.RParen)]
    [InlineData("[", TokenKind.LBracket)]
    [InlineData("]", TokenKind.RBracket)]
    [InlineData("{", TokenKind.LBrace)]
    [InlineData("}", TokenKind.RBrace)]
    [InlineData(",", TokenKind.Comma)]
    [InlineData(".", TokenKind.Dot)]
    [InlineData(";", TokenKind.Semicolon)]
    [InlineData(":", TokenKind.Colon)]
    [InlineData("?", TokenKind.Question)]
    [InlineData("!", TokenKind.Exclamation)]
    [InlineData("+", TokenKind.Plus)]
    [InlineData("-", TokenKind.Minus)]
    [InlineData("*", TokenKind.Star)]
    [InlineData("/", TokenKind.Slash)]
    [InlineData("%", TokenKind.Percent)]
    [InlineData("&", TokenKind.Amp)]
    [InlineData("|", TokenKind.Pipe)]
    [InlineData("^", TokenKind.Caret)]
    [InlineData("~", TokenKind.Tilde)]
    [InlineData("<", TokenKind.Less)]
    [InlineData(">", TokenKind.Greater)]
    [InlineData("=", TokenKind.Equal)]
    public void Single_char_operator(string input, TokenKind expectedKind)
    {
        var (tokens, diag) = Tokenize(input);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(expectedKind, tokens[0].TokenKind);
        Assert.Equal(0, tokens[0].Span.Start);
        Assert.Equal(1, tokens[0].Span.End);
        Assert.False(diag.HasErrors);
    }

    // ─── operators: two characters ──────────────────────────────────────────

    [Theory]
    [InlineData("::", TokenKind.ColonColon)]
    [InlineData("->", TokenKind.Arrow)]
    [InlineData("=>", TokenKind.FatArrow)]
    [InlineData("?.", TokenKind.QuestionDot)]
    [InlineData("??", TokenKind.QuestionQuestion)]
    [InlineData("++", TokenKind.Inc)]
    [InlineData("--", TokenKind.Dec)]
    [InlineData("<<", TokenKind.Shl)]
    [InlineData(">>", TokenKind.Shr)]
    [InlineData("==", TokenKind.EqualEqual)]
    [InlineData("!=", TokenKind.ExclamationEqual)]
    [InlineData("<=", TokenKind.LessEqual)]
    [InlineData(">=", TokenKind.GreaterEqual)]
    [InlineData("&&", TokenKind.AmpAmp)]
    [InlineData("||", TokenKind.PipePipe)]
    [InlineData("..", TokenKind.DotDot)]
    [InlineData("+=", TokenKind.PlusEqual)]
    [InlineData("-=", TokenKind.MinusEqual)]
    [InlineData("*=", TokenKind.StarEqual)]
    [InlineData("/=", TokenKind.SlashEqual)]
    [InlineData("%=", TokenKind.PercentEqual)]
    [InlineData("&=", TokenKind.AmpEqual)]
    [InlineData("|=", TokenKind.PipeEqual)]
    [InlineData("^=", TokenKind.CaretEqual)]
    public void Two_char_operator(string input, TokenKind expectedKind)
    {
        var (tokens, diag) = Tokenize(input);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(expectedKind, tokens[0].TokenKind);
        Assert.Equal(0, tokens[0].Span.Start);
        Assert.Equal(2, tokens[0].Span.End);
        Assert.False(diag.HasErrors);
    }

    // ─── operators: three characters ────────────────────────────────────────

    [Theory]
    [InlineData("..=", TokenKind.DotDotEqual)]
    [InlineData("<<=", TokenKind.ShlEqual)]
    [InlineData(">>=", TokenKind.ShrEqual)]
    [InlineData("&&=", TokenKind.AmpAmpEqual)]
    [InlineData("||=", TokenKind.PipePipeEqual)]
    [InlineData("??=", TokenKind.QuestionQuestionEqual)]
    public void Three_char_operator(string input, TokenKind expectedKind)
    {
        var (tokens, diag) = Tokenize(input);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(expectedKind, tokens[0].TokenKind);
        Assert.Equal(0, tokens[0].Span.Start);
        Assert.Equal(3, tokens[0].Span.End);
        Assert.False(diag.HasErrors);
    }

    // ─── longest match: greedy beats short, but not too greedy ──────────────

    [Fact]
    public void Triple_equals_is_EqualEqual_then_Equal()
    {
        // === must not become == plus =, and must not become a phantom token either.
        var (tokens, diag) = Tokenize("===");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenKind.EqualEqual, tokens[0].TokenKind);
        Assert.Equal((0, 2), (tokens[0].Span.Start, tokens[0].Span.End));
        Assert.Equal(TokenKind.Equal, tokens[1].TokenKind);
        Assert.Equal((2, 3), (tokens[1].Span.Start, tokens[1].Span.End));
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Triple_lt_is_Shl_then_Less()
    {
        var (tokens, _) = Tokenize("<<<");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenKind.Shl, tokens[0].TokenKind);
        Assert.Equal(TokenKind.Less, tokens[1].TokenKind);
    }

    [Fact]
    public void Triple_gt_is_Shr_then_Greater()
    {
        var (tokens, _) = Tokenize(">>>");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenKind.Shr, tokens[0].TokenKind);
        Assert.Equal(TokenKind.Greater, tokens[1].TokenKind);
    }

    [Fact]
    public void Triple_amp_is_AmpAmp_then_Amp()
    {
        var (tokens, _) = Tokenize("&&&");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenKind.AmpAmp, tokens[0].TokenKind);
        Assert.Equal(TokenKind.Amp, tokens[1].TokenKind);
    }

    [Fact]
    public void Triple_pipe_is_PipePipe_then_Pipe()
    {
        var (tokens, _) = Tokenize("|||");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenKind.PipePipe, tokens[0].TokenKind);
        Assert.Equal(TokenKind.Pipe, tokens[1].TokenKind);
    }

    [Fact]
    public void Triple_plus_is_Inc_then_Plus()
    {
        var (tokens, _) = Tokenize("+++");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenKind.Inc, tokens[0].TokenKind);
        Assert.Equal(TokenKind.Plus, tokens[1].TokenKind);
    }

    [Fact]
    public void Four_dots_is_DotDot_then_DotDot()
    {
        var (tokens, _) = Tokenize("....");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenKind.DotDot, tokens[0].TokenKind);
        Assert.Equal(TokenKind.DotDot, tokens[1].TokenKind);
    }

    [Fact]
    public void Three_dots_is_DotDot_then_Dot()
    {
        // '...' does not exist, so it is DotDot plus Dot and the parser rejects it.
        var (tokens, _) = Tokenize("...");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenKind.DotDot, tokens[0].TokenKind);
        Assert.Equal(TokenKind.Dot, tokens[1].TokenKind);
    }

    [Fact]
    public void Triple_colon_is_ColonColon_then_Colon()
    {
        var (tokens, _) = Tokenize(":::");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenKind.ColonColon, tokens[0].TokenKind);
        Assert.Equal(TokenKind.Colon, tokens[1].TokenKind);
    }

    [Fact]
    public void Double_bang_is_two_Exclamations()
    {
        // '!=' is two characters, but '!!' has to be two Exclamation tokens.
        var (tokens, _) = Tokenize("!!");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenKind.Exclamation, tokens[0].TokenKind);
        Assert.Equal(TokenKind.Exclamation, tokens[1].TokenKind);
    }

    [Fact]
    public void QuestionQuestion_not_swallowed_by_QuestionDot()
    {
        // '??.' is '??' then '.', not '?' plus '?.'.
        var (tokens, _) = Tokenize("??.");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenKind.QuestionQuestion, tokens[0].TokenKind);
        Assert.Equal(TokenKind.Dot, tokens[1].TokenKind);
    }

    // ─── operators: adjacency without whitespace ────────────────────────────

    [Fact]
    public void Postfix_increment_after_identifier()
    {
        var (tokens, diag) = Tokenize("x++");
        var kinds = tokens.Select(t => t.TokenKind).ToArray();
        Assert.Equal(
            new[] { TokenKind.Identifier, TokenKind.Inc, TokenKind.Eof },
            kinds);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Compound_assign_between_identifier_and_number()
    {
        var (tokens, diag) = Tokenize("a+=1");
        var kinds = tokens.Select(t => t.TokenKind).ToArray();
        Assert.Equal(
            new[] { TokenKind.Identifier, TokenKind.PlusEqual, TokenKind.IntLiteral, TokenKind.Eof },
            kinds);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Shift_assign_glued_between_identifiers()
    {
        var (tokens, diag) = Tokenize("a<<=b");
        var kinds = tokens.Select(t => t.TokenKind).ToArray();
        Assert.Equal(
            new[] { TokenKind.Identifier, TokenKind.ShlEqual, TokenKind.Identifier, TokenKind.Eof },
            kinds);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Comparison_and_logical_chain()
    {
        var (tokens, diag) = Tokenize("a >= b && c != d");
        var kinds = tokens.Select(t => t.TokenKind).ToArray();
        Assert.Equal(
            new[] {
                TokenKind.Identifier, TokenKind.GreaterEqual, TokenKind.Identifier,
                TokenKind.AmpAmp,
                TokenKind.Identifier, TokenKind.ExclamationEqual, TokenKind.Identifier,
                TokenKind.Eof
            },
            kinds);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Arrow_in_function_type_context()
    {
        var (tokens, _) = Tokenize("fn()->int");
        var kinds = tokens.Select(t => t.TokenKind).ToArray();
        Assert.Equal(
            new[] {
                TokenKind.Fn, TokenKind.LParen, TokenKind.RParen,
                TokenKind.Arrow, TokenKind.Identifier, TokenKind.Eof
            },
            kinds);
    }

    [Fact]
    public void Implements_operator_with_bracket_list()
    {
        // 'struct X :: [I]': :: is the implements operator.
        var (tokens, diag) = Tokenize("X :: [I]");
        var kinds = tokens.Select(t => t.TokenKind).ToArray();
        Assert.Equal(
            new[] {
                TokenKind.Identifier, TokenKind.ColonColon,
                TokenKind.LBracket, TokenKind.Identifier, TokenKind.RBracket,
                TokenKind.Eof
            },
            kinds);
        Assert.False(diag.HasErrors);
    }

    // ─── operators: interaction with numbers and ranges ─────────────────────

    [Fact]
    public void Range_between_int_literals()
    {
        // Critical: '0..5' must not pull the '.' into a float.
        var (tokens, diag) = Tokenize("0..5");
        Assert.Equal(4, tokens.Count);
        Assert.Equal(TokenKind.IntLiteral, tokens[0].TokenKind);
        Assert.Equal((0, 1), (tokens[0].Span.Start, tokens[0].Span.End));
        Assert.Equal(TokenKind.DotDot, tokens[1].TokenKind);
        Assert.Equal((1, 3), (tokens[1].Span.Start, tokens[1].Span.End));
        Assert.Equal(TokenKind.IntLiteral, tokens[2].TokenKind);
        Assert.Equal((3, 4), (tokens[2].Span.Start, tokens[2].Span.End));
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Inclusive_range_between_int_literals()
    {
        var (tokens, diag) = Tokenize("0..=5");
        Assert.Equal(4, tokens.Count);
        Assert.Equal(TokenKind.IntLiteral, tokens[0].TokenKind);
        Assert.Equal((0, 1), (tokens[0].Span.Start, tokens[0].Span.End));
        Assert.Equal(TokenKind.DotDotEqual, tokens[1].TokenKind);
        Assert.Equal((1, 4), (tokens[1].Span.Start, tokens[1].Span.End));
        Assert.Equal(TokenKind.IntLiteral, tokens[2].TokenKind);
        Assert.Equal((4, 5), (tokens[2].Span.Start, tokens[2].Span.End));
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Dot_before_digit_is_Dot_not_float()
    {
        // '.5' is no float: a float literal needs a leading decimal literal.
        var (tokens, diag) = Tokenize(".5");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenKind.Dot, tokens[0].TokenKind);
        Assert.Equal(TokenKind.IntLiteral, tokens[1].TokenKind);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Member_access_on_float_keeps_float()
    {
        // '1.5.foo': a float, then Dot, then an identifier.
        var (tokens, diag) = Tokenize("1.5.foo");
        var kinds = tokens.Select(t => t.TokenKind).ToArray();
        Assert.Equal(
            new[] {
                TokenKind.FloatLiteral, TokenKind.Dot, TokenKind.Identifier, TokenKind.Eof
            },
            kinds);
        Assert.Equal((0, 3), (tokens[0].Span.Start, tokens[0].Span.End));
        Assert.False(diag.HasErrors);
    }

    // ─── operators: the complete table in one sequence ──────────────────────

    [Fact]
    public void All_operators_in_sequence_map_one_to_one()
    {
        // Every operator isolated by whitespace, or '//' would become a comment.
        var src =
            "( ) { } [ ] " +
            ", . ; : :: -> => " +
            "? ?. ?? ! " +
            "+ - * / % " +
            "& | ^ ~ " +
            "<< >> " +
            "== != < <= > >= " +
            "&& || " +
            "++ -- " +
            ".. ..= " +
            "= += -= *= /= %= " +
            "&= |= ^= <<= >>= " +
            "&&= ||= ??=";
        var (tokens, diag) = Tokenize(src);
        var kinds = tokens.Select(t => t.TokenKind).ToArray();
        Assert.Equal(
            new[] {
                TokenKind.LParen, TokenKind.RParen, TokenKind.LBrace, TokenKind.RBrace,
                TokenKind.LBracket, TokenKind.RBracket,
                TokenKind.Comma, TokenKind.Dot, TokenKind.Semicolon, TokenKind.Colon,
                TokenKind.ColonColon, TokenKind.Arrow, TokenKind.FatArrow,
                TokenKind.Question, TokenKind.QuestionDot, TokenKind.QuestionQuestion, TokenKind.Exclamation,
                TokenKind.Plus, TokenKind.Minus, TokenKind.Star, TokenKind.Slash, TokenKind.Percent,
                TokenKind.Amp, TokenKind.Pipe, TokenKind.Caret, TokenKind.Tilde,
                TokenKind.Shl, TokenKind.Shr,
                TokenKind.EqualEqual, TokenKind.ExclamationEqual,
                TokenKind.Less, TokenKind.LessEqual, TokenKind.Greater, TokenKind.GreaterEqual,
                TokenKind.AmpAmp, TokenKind.PipePipe,
                TokenKind.Inc, TokenKind.Dec,
                TokenKind.DotDot, TokenKind.DotDotEqual,
                TokenKind.Equal, TokenKind.PlusEqual, TokenKind.MinusEqual,
                TokenKind.StarEqual, TokenKind.SlashEqual, TokenKind.PercentEqual,
                TokenKind.AmpEqual, TokenKind.PipeEqual, TokenKind.CaretEqual,
                TokenKind.ShlEqual, TokenKind.ShrEqual,
                TokenKind.AmpAmpEqual, TokenKind.PipePipeEqual, TokenKind.QuestionQuestionEqual,
                TokenKind.Eof
            },
            kinds);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void Operators_emit_no_diagnostics()
    {
        var (_, diag) = Tokenize("+= -= *= /= %= &&= ||= ??= <<= >>= ..= ?. -> =>");
        Assert.False(diag.HasErrors);
    }
}
