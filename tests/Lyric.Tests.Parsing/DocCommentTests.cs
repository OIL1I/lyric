using System.Runtime.CompilerServices;
using Lyric.AST;
using Lyric.Core;
using Lyric.Parsing;
using Xunit;

namespace Lyric.Tests.Parsing;

/// <summary>
/// '///' blocks reach the side table and bind to the declaration below them.
///
/// <para>The block is bound by SOURCE OFFSET, not by walking the tree: a declaration span starts at
/// its first token, and the table is keyed by the offset of the token that follows the comment. Both
/// halves stand here — that the block binds where it should, and that it binds NOWHERE otherwise.
/// A table that attached everything to the next declaration would pass the first half alone.</para>
///
/// <para>The token stream itself stays free of doc comments, which is what lets every production
/// ignore them.</para>
/// </summary>
public class DocCommentTests
{
    private static (Module module, Parser parser, DiagnosticEngine diag) ParseModule(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var parser = new Parser(sm, id, de);
        return (parser.ParseModule(), parser, de);
    }

    // ------------------------------------------------------------------ binding

    [Fact]
    public void A_block_binds_to_the_declaration_below_it()
    {
        var (module, parser, de) = ParseModule("/// The answer.\nfn f(): int { return 42; }");
        Assert.False(de.HasErrors);
        Assert.Equal("The answer.", parser.DocOf(module.Declarations[0]));
    }

    [Fact]
    public void Several_lines_join_with_a_line_break()
    {
        var (module, parser, _) = ParseModule("/// First.\n/// Second.\nfn f(): void { }");
        Assert.Equal("First.\nSecond.", parser.DocOf(module.Declarations[0]));
    }

    [Fact]
    public void An_empty_doc_line_stays_an_empty_line()
    {
        var (module, parser, _) = ParseModule("/// First.\n///\n/// Third.\nfn f(): void { }");
        Assert.Equal("First.\n\nThird.", parser.DocOf(module.Declarations[0]));
    }

    [Fact]
    public void The_marker_and_one_space_are_dropped_but_further_indentation_is_kept()
    {
        var (module, parser, _) = ParseModule("///     indented\nfn f(): void { }");
        Assert.Equal("    indented", parser.DocOf(module.Declarations[0]));
    }

    [Fact]
    public void A_line_without_a_space_after_the_marker_keeps_its_first_character()
    {
        var (module, parser, _) = ParseModule("///no space\nfn f(): void { }");
        Assert.Equal("no space", parser.DocOf(module.Declarations[0]));
    }

    [Fact]
    public void Trailing_whitespace_is_dropped()
    {
        var (module, parser, _) = ParseModule("/// text   \nfn f(): void { }");
        Assert.Equal("text", parser.DocOf(module.Declarations[0]));
    }

    [Fact]
    public void A_carriage_return_does_not_survive_in_the_text()
    {
        // The working tree is LF, but a file may still arrive with CRLF; the '\r' sits inside the
        // comment span because the scan stops at the '\n'.
        var (module, parser, _) = ParseModule("/// text\r\nfn f(): void { }");
        Assert.Equal("text", parser.DocOf(module.Declarations[0]));
    }

    // ------------------------------------------------------------------ separation

    [Fact]
    public void A_blank_line_breaks_the_binding()
    {
        var (module, parser, _) = ParseModule("/// A file header.\n\nfn f(): void { }");
        Assert.Null(parser.DocOf(module.Declarations[0]));
    }

    [Fact]
    public void A_blank_line_between_two_blocks_drops_the_earlier_one()
    {
        var (module, parser, _) = ParseModule("/// Header.\n\n/// The real one.\nfn f(): void { }");
        Assert.Equal("The real one.", parser.DocOf(module.Declarations[0]));
    }

    [Fact]
    public void An_ordinary_comment_between_block_and_declaration_does_not_break_the_binding()
    {
        // '//' is not a token at all, so it cannot separate; only a blank line does.
        var (module, parser, _) = ParseModule("/// Doc.\n// note\nfn f(): void { }");
        Assert.Equal("Doc.", parser.DocOf(module.Declarations[0]));
    }

    [Fact]
    public void A_trailing_block_at_the_end_of_a_file_binds_to_nothing()
    {
        var (module, parser, _) = ParseModule("fn f(): void { }\n/// dangling\n");
        Assert.Single(module.Declarations);
        Assert.Null(parser.DocOf(module.Declarations[0]));
    }

    [Fact]
    public void A_block_binds_to_exactly_one_declaration()
    {
        var (module, parser, _) = ParseModule("/// Only the first.\nfn f(): void { }\nfn g(): void { }");
        Assert.Equal("Only the first.", parser.DocOf(module.Declarations[0]));
        Assert.Null(parser.DocOf(module.Declarations[1]));
    }

    // ------------------------------------------------------------------ what it binds to

    [Fact]
    public void Pub_does_not_shift_the_anchor()
    {
        // The declaration span starts at 'pub', not at 'fn'.
        var (module, parser, _) = ParseModule("/// Public.\npub fn f(): void { }");
        Assert.Equal("Public.", parser.DocOf(module.Declarations[0]));
    }

    [Fact]
    public void The_module_header_carries_its_own_block()
    {
        var (module, parser, _) = ParseModule("/// The module.\nmodule std.math;\nfn f(): void { }");
        Assert.NotNull(module.Header);
        Assert.Equal("The module.", parser.DocOf(module.Header!));
    }

    [Fact]
    public void An_import_carries_a_block()
    {
        var (module, parser, _) = ParseModule("/// Why this import.\nimport std.io.console { println };");
        Assert.Equal("Why this import.", parser.DocOf(module.Declarations[0]));
    }

    [Theory]
    [InlineData("struct S { a: int, }")]
    [InlineData("class C { a: int, }")]
    [InlineData("enum E { A, B; }")]
    [InlineData("interface I { fn f(): void; }")]
    [InlineData("let x: int = 1;")]
    public void Every_declaration_form_takes_a_block(string declaration)
    {
        var (module, parser, de) = ParseModule("/// Documented.\n" + declaration);
        Assert.False(de.HasErrors);
        Assert.Equal("Documented.", parser.DocOf(module.Declarations[0]));
    }

    [Fact]
    public void A_member_inside_a_type_takes_a_block()
    {
        // The same table, no extra plumbing: a member span also starts at its first token.
        var (module, parser, de) = ParseModule(
            "class C {\n    /// The field.\n    a: int,\n    /// The method.\n    fn m(): void { }\n}");
        Assert.False(de.HasErrors);
        var c = Assert.IsType<ClassDecl>(module.Declarations[0]);
        Assert.Equal("The field.", parser.DocOf(c.Members[0]));
        Assert.Equal("The method.", parser.DocOf(c.Members[1]));
    }

    [Fact]
    public void An_enum_variant_takes_a_block()
    {
        var (module, parser, de) = ParseModule(
            "enum E {\n    /// Nothing there.\n    None,\n    /// Something there.\n    Some(int),\n}");
        Assert.False(de.HasErrors);
        var e = Assert.IsType<EnumDecl>(module.Declarations[0]);
        Assert.Equal("Nothing there.", parser.DocOf(e.Variants[0]));
        Assert.Equal("Something there.", parser.DocOf(e.Variants[1]));
    }

    // ------------------------------------------------------------------ the token stream

    [Fact]
    public void Doc_comments_stay_out_of_the_parse()
    {
        // The point of the side table: no production has to skip them. If they were tokens, this
        // would be a syntax error rather than a clean function.
        var (module, _, de) = ParseModule(
            "/// One.\n/// Two.\npub fn f(a: int): int {\n    /// inside\n    return a;\n}");
        Assert.False(de.HasErrors);
        var f = Assert.IsType<FunctionDecl>(module.Declarations[0]);
        Assert.Equal("f", f.Name);
    }

    [Fact]
    public void A_file_without_doc_comments_has_an_empty_table()
    {
        var (_, parser, _) = ParseModule("fn f(): void { }\n// an ordinary comment\n");
        Assert.Empty(parser.DocComments);
    }

    [Fact]
    public void A_marker_inside_a_string_is_not_a_doc_comment()
    {
        var (module, parser, de) = ParseModule("fn f(): string { return \"/// not a doc\"; }");
        Assert.False(de.HasErrors);
        Assert.Empty(parser.DocComments);
        Assert.Single(module.Declarations);
    }

    // ------------------------------------------------------------------ real input

    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    /// <summary>
    /// The blocks the standard library already carries arrive. Written against a real file rather
    /// than a synthetic string: the synthetic cases above fix the RULE, this one fixes that the rule
    /// meets the source as it is actually written.
    /// </summary>
    [Fact]
    public void The_blocks_in_the_standard_library_arrive()
    {
        var path = Path.Combine(RepoRoot(), "stdlib", "std", "os.lyr");
        var sm = new SourceManager();
        var id = sm.AddVirtual("os.lyr", File.ReadAllText(path));
        var de = new DiagnosticEngine(sm);
        var parser = new Parser(sm, id, de);
        var module = parser.ParseModule();

        Assert.False(de.HasErrors);

        var documented = module.Declarations
            .OfType<FunctionDecl>()
            .Where(f => parser.DocOf(f) is not null)
            .Select(f => f.Name)
            .ToArray();

        // The exact set is stdlib content and moves; that several arrive is the contract here.
        Assert.Contains("args", documented);
        Assert.Contains("nowMillis", documented);

        // A '//' file header above 'module' is separated by a blank line and binds to nothing.
        Assert.Null(parser.DocOf(module.Header!));
    }
}
