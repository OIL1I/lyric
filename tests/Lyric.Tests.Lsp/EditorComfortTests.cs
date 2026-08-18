using Lyric.Compiler;
using Lyric.Lsp.Analysis;
using Lyric.Lsp.Protocol;

namespace Lyric.Tests.Lsp;

/// <summary>
/// Signature help: which call, which parameter, whose declaration.
///
/// <para>The cursor is a <c>$</c> marker, as in the hover tests: a test that says "character 14"
/// stops being about the question the moment the fixture changes.</para>
/// </summary>
public sealed class SignatureHelpTests
{
    private static SignatureHelp? HelpAt(string programWithMarker)
    {
        var offset = programWithMarker.IndexOf('$');
        Assert.True(offset >= 0, "the fixture has no '$' marking the cursor");

        var program = programWithMarker.Remove(offset, 1);
        var path = Path.Combine(AppContext.BaseDirectory, "sighelp.lyr");

        return SignatureHelpProvider.At(path, program, offset, new CompilerOptions());
    }

    private const string Fixture =
        "fn blend(first: int, second: int, weight: float): int {\n"
        + "    return first;\n"
        + "}\n"
        + "\n"
        + "fn main(): int {\n"
        + "    return blend(MARKER);\n"
        + "}\n";

    private static SignatureHelp? AtCallWith(string arguments)
    {
        var help = HelpAt(Fixture.Replace("MARKER", arguments));
        return help;
    }

    [Fact]
    public void The_label_is_the_declaration_as_written()
    {
        var help = AtCallWith("$");

        Assert.NotNull(help);
        var signature = Assert.Single(help.Signatures);
        Assert.Equal("fn blend(first: int, second: int, weight: float): int", signature.Label);

        // Each parameter is a substring of the label — the property the highlight matches on.
        Assert.Equal(3, signature.Parameters.Count);
        Assert.All(signature.Parameters, p => Assert.Contains(p.Label, signature.Label));
        Assert.Equal(0, help.ActiveParameter);
    }

    [Fact]
    public void The_comma_moves_the_active_parameter()
    {
        Assert.Equal(1, AtCallWith("1, $")!.ActiveParameter);
        Assert.Equal(2, AtCallWith("1, 2, $")!.ActiveParameter);

        // Inside an argument, not after it: still that argument.
        Assert.Equal(0, AtCallWith("1$23")!.ActiveParameter);
    }

    [Fact]
    public void A_nested_call_answers_for_the_inner_one()
    {
        const string program = "fn inner(x: int): int { return x; }\n"
            + "fn outer(a: int, b: int): int { return a + b; }\n"
            + "fn main(): int {\n"
            + "    return outer(1, inner($));\n"
            + "}\n";

        var help = HelpAt(program);

        Assert.NotNull(help);
        Assert.StartsWith("fn inner", Assert.Single(help.Signatures).Label);
        Assert.Equal(0, help.ActiveParameter);
    }

    [Fact]
    public void On_the_callee_name_there_is_no_signature_popup()
    {
        // The cursor on 'blend' itself asks hover's question; the popup opens only inside the
        // parens.
        const string program = "fn blend(a: int): int { return a; }\n"
            + "fn main(): int { return ble$nd(1); }\n";

        Assert.Null(HelpAt(program));
    }

    [Fact]
    public void A_function_value_shows_types_without_invented_names()
    {
        const string program = "fn main(): int {\n"
            + "    let f = (n: int) => n * 2;\n"
            + "    return f($);\n"
            + "}\n";

        var help = HelpAt(program);

        Assert.NotNull(help);
        var signature = Assert.Single(help.Signatures);
        Assert.Contains("int", signature.Label);
        Assert.Empty(signature.Parameters);
    }

    [Fact]
    public void The_standard_library_answers_from_its_declaration()
    {
        const string program = "import std.io.console { println };\n"
            + "fn main(): int {\n"
            + "    println($);\n"
            + "    return 0;\n"
            + "}\n";

        var help = HelpAt(program);

        Assert.NotNull(help);
        Assert.StartsWith("fn println", Assert.Single(help.Signatures).Label);
    }
}

/// <summary>Folding: multi-line forms collapse, the closing line stays visible.</summary>
public sealed class FoldingTests
{
    private static IReadOnlyList<FoldingRange> Fold(string program)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "folding.lyr");
        var result = SourceCompiler.Check(ScriptSource.FromBuffer(path, program));
        Assert.NotNull(result.Model);

        var file = DiagnosticMapper.FindFile(result.Sources, path);
        return FoldingProvider.Of(result.Model.Entry, file, result.Sources);
    }

    [Fact]
    public void Declarations_and_blocks_fold_and_keep_their_last_line()
    {
        var ranges = Fold(
            "struct Point {\n"       // 0
            + "    x: int,\n"        // 1
            + "}\n"                  // 2
            + "\n"                   // 3
            + "fn main(): int {\n"   // 4
            + "    while (true) {\n" // 5
            + "        break;\n"     // 6
            + "    }\n"              // 7
            + "    return 0;\n"      // 8
            + "}\n");                // 9

        // The struct folds 0..1, the function 4..8, the loop body 5..6 — each keeps its closing
        // brace visible.
        Assert.Contains(ranges, r => r is { StartLine: 0, EndLine: 1 });
        Assert.Contains(ranges, r => r is { StartLine: 4, EndLine: 8 });
        Assert.Contains(ranges, r => r is { StartLine: 5, EndLine: 6 });
    }

    [Fact]
    public void A_one_line_form_has_nothing_to_hide()
    {
        var ranges = Fold("fn main(): int { return 0; }\n");
        Assert.Empty(ranges);
    }

    [Fact]
    public void One_start_line_carries_one_range()
    {
        // 'fn main(): int {' is the declaration AND its body block; a doubled fold control on one
        // line is the bug this pins against.
        var ranges = Fold("fn main(): int {\n    return 0;\n}\n");

        Assert.Single(ranges);
    }
}

/// <summary>Inlay hints: the inference made visible, and only where nothing was written.</summary>
public sealed class InlayHintTests
{
    private static IReadOnlyList<InlayHint> Hints(string program)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "inlay.lyr");
        var result = SourceCompiler.Check(ScriptSource.FromBuffer(path, program));
        Assert.NotNull(result.Model);

        var file = DiagnosticMapper.FindFile(result.Sources, path);
        var whole = new Lyric.Lsp.Protocol.Range
        {
            Start = new Position { Line = 0, Character = 0 },
            End = new Position { Line = int.MaxValue, Character = 0 },
        };

        return InlayHintProvider.Of(result.Model, result.Model.Entry, file, result.Sources, whole);
    }

    [Fact]
    public void An_unannotated_binding_shows_its_inferred_type()
    {
        var hints = Hints("fn main(): int {\n    let total = 1 + 2;\n    return total;\n}\n");

        var hint = Assert.Single(hints);
        Assert.Equal(": int", hint.Label);
        Assert.Equal(InlayHintKind.Type, hint.Kind);

        // Directly after the name: 'total' ends at character 13 of line 1.
        Assert.Equal(1, hint.Position.Line);
        Assert.Equal(13, hint.Position.Character);
    }

    [Fact]
    public void A_written_annotation_silences_the_hint()
    {
        Assert.Empty(Hints("fn main(): int {\n    let total: int = 3;\n    return total;\n}\n"));
    }

    [Fact]
    public void The_loop_variable_is_hinted()
    {
        var hints = Hints(
            "fn main(): int {\n"
            + "    var sum = 0;\n"
            + "    for n in [1, 2, 3] {\n"
            + "        sum = sum + n;\n"
            + "    }\n"
            + "    return sum;\n"
            + "}\n");

        Assert.Contains(hints, h => h.Label == ": int" && h.Position.Line == 2);
    }

    [Fact]
    public void A_binding_whose_initializer_failed_shows_nothing()
    {
        // ErrorType means "already reported here"; the squiggle owns that spot.
        var hints = Hints("fn main(): int {\n    let broken = nowhere;\n    return 0;\n}\n");

        Assert.Empty(hints);
    }
}
