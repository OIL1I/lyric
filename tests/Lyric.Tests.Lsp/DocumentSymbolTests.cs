using System.Runtime.CompilerServices;
using System.Text.Json;
using Lyric.Compiler;
using Lyric.Core;
using Lyric.Lsp.Analysis;
using Lyric.Lsp.Documents;
using Lyric.Lsp.Protocol;
using Range = Lyric.Lsp.Protocol.Range;

namespace Lyric.Tests.Lsp;

/// <summary>
/// What a file declares, as an editor's outline.
///
/// <para>The tests assert names, kinds and nesting rather than ranges. Ranges are two spans the AST
/// already carries and whose containment is fixed where they are produced; what can go wrong here is
/// which declarations are listed and how they are grouped.</para>
/// </summary>
public sealed class DocumentSymbolTests
{
    private static IReadOnlyList<DocumentSymbol> SymbolsOf(string program)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "symbols.lyr");
        var result = SourceCompiler.Check(ScriptSource.FromBuffer(path, program));
        Assert.NotNull(result.Model);

        return DocumentSymbolProvider.Of(result.Sources, result.Model.Entry);
    }

    private static string[] Names(IEnumerable<DocumentSymbol> symbols) =>
        symbols.Select(s => s.Name).ToArray();

    // ------------------------------------------------------------------ per declaration

    [Fact]
    public void A_function_is_a_function_and_a_method_is_a_method()
    {
        // The distinction comes from the walk, not from the declaration: nothing on a FunctionDecl
        // tells the two apart.
        var symbols = SymbolsOf("fn free(): int { return 1; }\nclass C { fn member(): int { return 2; }, }\n");

        Assert.Equal(SymbolKind.Function, symbols[0].Kind);
        Assert.Equal(SymbolKind.Method, symbols[1].Children![0].Kind);
    }

    [Fact]
    public void A_bodiless_function_at_the_top_level_is_still_a_function()
    {
        // The case that broke the first attempt at telling a method from a function. A native
        // declaration has no body and stands at the top level; an interface member has no body
        // either and does not.
        var symbols = SymbolsOf("interface I { fn m(): int; }\n");

        Assert.Equal(SymbolKind.Interface, symbols[0].Kind);
        Assert.Equal(SymbolKind.Method, symbols[0].Children![0].Kind);
    }

    [Fact]
    public void A_struct_lists_its_fields_and_methods_as_children()
    {
        var symbols = SymbolsOf(
            "struct Point {\n    x: int,\n    y: int,\n    fn sum(): int { return this.x + this.y; }\n}\n");

        var point = Assert.Single(symbols);
        Assert.Equal("Point", point.Name);
        Assert.Equal(SymbolKind.Struct, point.Kind);
        Assert.Equal(["x", "y", "sum"], Names(point.Children!));
        Assert.Equal(SymbolKind.Field, point.Children![0].Kind);
    }

    [Fact]
    public void An_enum_lists_its_variants_and_its_methods()
    {
        var symbols = SymbolsOf(
            "enum Shape {\n    Circle(float),\n    Square(float);\n\n    fn area(): float { return 0.0; }\n}\n");

        var shape = Assert.Single(symbols);
        Assert.Equal(SymbolKind.Enum, shape.Kind);
        Assert.Equal(["Circle", "Square", "area"], Names(shape.Children!));
        Assert.Equal(SymbolKind.EnumMember, shape.Children![0].Kind);
    }

    [Fact]
    public void A_class_a_global_and_an_alias_each_get_their_kind()
    {
        var symbols = SymbolsOf("class C { v: int, }\nlet answer = 42;\ntype Count = int;\n");

        Assert.Equal(SymbolKind.Class, symbols[0].Kind);
        Assert.Equal(SymbolKind.Constant, symbols[1].Kind);
        Assert.Equal("answer", symbols[1].Name);
        Assert.Equal(SymbolKind.Class, symbols[2].Kind);
    }

    [Fact]
    public void A_static_constant_is_a_constant_inside_its_type()
    {
        var symbols = SymbolsOf("struct P {\n    static let origin = 0;\n}\n");

        var member = Assert.Single(symbols[0].Children!);
        Assert.Equal("origin", member.Name);
        Assert.Equal(SymbolKind.Constant, member.Kind);
    }

    [Fact]
    public void An_extend_block_is_named_after_what_it_extends()
    {
        // It has no name of its own, and it is the one container that is not an INamedDecl.
        var symbols = SymbolsOf(
            "struct P { v: int, }\nextend P {\n    fn twice(): int { return this.v * 2; }\n}\n");

        Assert.Equal("extend P", symbols[1].Name);
        Assert.Equal(SymbolKind.Namespace, symbols[1].Kind);
        Assert.Equal(["twice"], Names(symbols[1].Children!));
    }

    // ------------------------------------------------------------------ what is left out

    [Fact]
    public void An_import_is_not_listed()
    {
        // It says what the file NEEDS. An outline says what it offers.
        var symbols = SymbolsOf("import std.os { cpuCount };\nfn main(): int { return 0; }\n");

        Assert.Equal(["main"], Names(symbols));
    }

    [Fact]
    public void Parameters_and_locals_and_loop_variables_are_not_listed()
    {
        // All three are INamedDecl since slice 1, so leaving them out is a decision this provider
        // makes rather than a limit it inherits.
        var symbols = SymbolsOf(
            "fn run(input: int): int {\n    let total = 0;\n    for (n in 0..3) {\n"
            + "        let step = n;\n    }\n    return total;\n}\n");

        var run = Assert.Single(symbols);
        Assert.Equal("run", run.Name);
        Assert.Null(run.Children);
    }

    [Fact]
    public void A_type_with_no_members_has_no_children_rather_than_an_empty_list()
    {
        // An empty array makes a client draw an expander that opens onto nothing.
        var symbols = SymbolsOf("struct Empty { }\n");
        Assert.Null(Assert.Single(symbols).Children);
    }

    [Fact]
    public void There_is_no_detail_field_to_fill()
    {
        // The protocol's optional 'detail' takes a signature beside the name, and rendering one from
        // syntax needs a printer for TypeNode — a second one beside TypeFacts.Display would be a
        // second answer to what a type is called. Absent from the type rather than present and
        // always null, so adding it later is a decision someone makes on purpose.
        Assert.Null(typeof(DocumentSymbol).GetProperty("Detail"));
    }

    // ------------------------------------------------------------------ order and robustness

    [Fact]
    public void The_order_is_the_order_of_the_file()
    {
        // Not alphabetical. A client that wants it sorted can sort a list given in source order;
        // the other direction is not available.
        var symbols = SymbolsOf("fn zebra(): int { return 1; }\nfn alpha(): int { return 2; }\n");
        Assert.Equal(["zebra", "alpha"], Names(symbols));
    }

    [Fact]
    public void A_file_that_does_not_type_check_still_has_an_outline()
    {
        // The property the whole approach rests on. An outline is read WHILE the file is broken, and
        // this test is only green as long as nothing here consults the binding or type tables.
        var path = Path.Combine(AppContext.BaseDirectory, "broken.lyr");
        var result = SourceCompiler.Check(ScriptSource.FromBuffer(
            path, "struct P { v: int, }\nfn main(): int {\n    return \"not an int\" + nowhere;\n}\n"));

        Assert.True(result.Diagnostics.HasErrors);
        Assert.NotNull(result.Model);

        Assert.Equal(["P", "main"], Names(DocumentSymbolProvider.Of(result.Sources, result.Model.Entry)));
    }

    // ------------------------------------------------------------------ real input

    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    public static TheoryData<string> StandardLibrary()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(
            Path.Combine(RepoRoot(), "stdlib"), "*.lyr", SearchOption.AllDirectories))
            data.Add(file);
        return data;
    }

    /// <summary>
    /// Every standard library file produces an outline, and every entry in it satisfies the
    /// protocol's containment rule.
    ///
    /// <para>Two things at once, both of which only real files reach: the kind switch is total over
    /// the declarations that actually occur, and no combination of them produces a selection outside
    /// its range.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(StandardLibrary))]
    public void Every_standard_library_file_produces_a_well_formed_outline(string path)
    {
        var result = SourceCompiler.Check(ScriptSource.FromBuffer(path, File.ReadAllText(path)));
        Assert.NotNull(result.Model);

        var symbols = DocumentSymbolProvider.Of(result.Sources, result.Model.Entry);
        Assert.NotEmpty(symbols);

        Check(symbols);

        static void Check(IEnumerable<DocumentSymbol> symbols)
        {
            foreach (var symbol in symbols)
            {
                Assert.False(string.IsNullOrEmpty(symbol.Name));
                Assert.True(Contains(symbol.Range, symbol.SelectionRange),
                    $"'{symbol.Name}': the selection is not inside the range");

                if (symbol.Children is { } children) Check(children);
            }
        }

        static bool Contains(Range outer, Range inner) =>
            Before(outer.Start, inner.Start) && Before(inner.End, outer.End);

        static bool Before(Position first, Position second) =>
            first.Line < second.Line
            || (first.Line == second.Line && first.Character <= second.Character);
    }
}

/// <summary>The outline over the wire.</summary>
public sealed class DocumentSymbolProtocolTests
{
    private const string Program = "struct P {\n    x: int,\n}\nfn main(): int { return 0; }\n";

    private static string BufferPath([CallerMemberName] string name = "") =>
        Path.Combine(AppContext.BaseDirectory, $"{name}.lyr");

    private static string DidOpen(string uri, string text) =>
        JsonSerializer.Serialize(new
        {
            textDocument = new { uri, languageId = "lyric", version = 1, text },
        });

    private static string Document(string uri) =>
        JsonSerializer.Serialize(new { textDocument = new { uri } });

    private const string Hierarchical =
        """{"textDocument":{"documentSymbol":{"hierarchicalDocumentSymbolSupport":true}}}""";

    [Fact]
    public async Task The_capability_is_announced()
    {
        await using var harness = new ServerHarness();

        var id = await harness.RequestAsync(LspMethods.Initialize, "{}");
        var response = await harness.ReceiveResponseAsync(id);

        Assert.True(response.GetProperty("result").GetProperty("capabilities")
            .GetProperty("documentSymbolProvider").GetBoolean());
    }

    [Fact]
    public async Task A_hierarchical_client_gets_the_nested_form()
    {
        var uri = DocumentUri.FromFilePath(BufferPath());

        await using var harness = new ServerHarness();
        await harness.InitializeAsync(Hierarchical);
        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(uri, Program));
        await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);

        var id = await harness.RequestAsync(LspMethods.DocumentSymbol, Document(uri));
        var result = (await harness.ReceiveResponseAsync(id)).GetProperty("result");

        Assert.Equal(JsonValueKind.Array, result.ValueKind);
        Assert.Equal(2, result.GetArrayLength());

        var point = result[0];
        Assert.Equal("P", point.GetProperty("name").GetString());
        Assert.Equal((int)SymbolKind.Struct, point.GetProperty("kind").GetInt32());
        Assert.Equal("x", point.GetProperty("children")[0].GetProperty("name").GetString());

        // A function without members carries no 'children' member at all.
        Assert.False(result[1].TryGetProperty("children", out _));
    }

    [Fact]
    public async Task A_client_without_hierarchical_support_gets_nothing()
    {
        // The flat form is deprecated and has no children. Answering with nothing is the honest
        // reply; answering with a shape the client did not ask for is not.
        var uri = DocumentUri.FromFilePath(BufferPath());

        await using var harness = new ServerHarness();
        await harness.InitializeAsync();
        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(uri, Program));
        await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);

        var id = await harness.RequestAsync(LspMethods.DocumentSymbol, Document(uri));
        var response = await harness.ReceiveResponseAsync(id);

        Assert.Equal(JsonValueKind.Null, response.GetProperty("result").ValueKind);
        Assert.False(response.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task A_document_that_was_never_opened_answers_null()
    {
        await using var harness = new ServerHarness();
        await harness.InitializeAsync(Hierarchical);

        var id = await harness.RequestAsync(
            LspMethods.DocumentSymbol, Document(DocumentUri.FromFilePath(BufferPath())));

        Assert.Equal(JsonValueKind.Null,
            (await harness.ReceiveResponseAsync(id)).GetProperty("result").ValueKind);
    }
}
