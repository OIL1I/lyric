using System.Text.Json;
using Lyric.Compiler;
using Lyric.Lsp.Analysis;
using Lyric.Lsp.Documents;
using Lyric.Lsp.Protocol;

namespace Lyric.Tests.Lsp;

/// <summary>
/// Every place a name occurs.
///
/// <para>The cursor is marked with <c>$</c>. The assertions are on the TEXT at each site rather than
/// on offsets: a list of offsets says nothing to a reader about whether the answer was right, and it
/// stops being about the question the moment the fixture gains a character.</para>
/// </summary>
public sealed class ReferenceTests
{
    private static (string[] Texts, CompileResult Result) SitesAt(
        string programWithMarker, bool includeDeclaration = false)
    {
        var offset = programWithMarker.IndexOf('$');
        Assert.True(offset >= 0, "the fixture has no '$' marking the cursor");

        var program = programWithMarker.Remove(offset, 1);
        var path = Path.Combine(AppContext.BaseDirectory, "references.lyr");

        var result = SourceCompiler.Check(ScriptSource.FromBuffer(path, program));
        Assert.NotNull(result.Model);

        var file = DiagnosticMapper.FindFile(result.Sources, path);
        var sites = ReferenceProvider.At(result.Model, file, offset, includeDeclaration);

        if (sites is null) return ([], result);

        var texts = sites
            .OrderBy(s => s.Span.Start)
            .Select(s => result.Sources.GetText(s.File).Substring(s.Span.Start, s.Span.Length))
            .ToArray();

        return (texts, result);
    }

    private static string[] Sites(string marked, bool includeDeclaration = false) =>
        SitesAt(marked, includeDeclaration).Texts;

    // ------------------------------------------------------------------ the basic cases

    [Fact]
    public void A_local_is_found_at_every_use()
    {
        var sites = Sites(
            "fn main(): int {\n    let cou$nt = 1;\n    let a = count + 1;\n    return count + a;\n}\n");

        Assert.Equal(["count", "count"], sites);
    }

    [Fact]
    public void A_parameter_is_found_at_every_use()
    {
        var sites = Sites("fn twice(n$: int): int { return n + n; }\n");
        Assert.Equal(["n", "n"], sites);
    }

    [Fact]
    public void A_function_is_found_from_its_own_name()
    {
        // Standing on the declaration is the ordinary gesture, and a FunctionDecl is bound to itself
        // in no table — the fallback through the module's symbol table is what answers here.
        var sites = Sites(
            "fn tw$ice(n: int): int { return n * 2; }\n"
            + "fn main(): int {\n    return twice(1) + twice(2);\n}\n");

        Assert.Equal(["twice", "twice"], sites);
    }

    [Fact]
    public void A_type_is_found_where_the_resolver_bound_it()
    {
        // The annotation, which the resolver binds. An initializer of the same type is NOT found —
        // see A_struct_initializer_is_not_found_and_why below.
        var sites = Sites(
            "struct Po$int { x: int, }\n"
            + "fn main(): int {\n    let p: Point = Point { x = 1 };\n    return p.x;\n}\n");

        Assert.Equal(["Point"], sites);
    }

    [Fact]
    public void A_type_argument_is_found_as_well_as_a_plain_annotation()
    {
        // Both come from the resolver's table, and this is what makes using it worthwhile beside the
        // sema's: neither annotation is an expression, so the sema binds neither.
        var sites = Sites(
            "struct It$em { v: int, }\n"
            + "fn take(xs: Item[]): int { return 0; }\n"
            + "fn main(): int {\n    let one: Item = Item { v = 1 };\n    return take([one]);\n}\n");

        Assert.Equal(["Item", "Item"], sites);
    }

    [Fact]
    public void An_enum_variant_is_found_at_its_uses()
    {
        var sites = Sites(
            "enum Shape {\n    Cir$cle(float),\n    Square(float),\n}\n"
            + "fn main(): int {\n    let a = Shape.Circle(1.0);\n    let b = Shape.Circle(2.0);\n"
            + "    return 0;\n}\n");

        Assert.Equal(2, sites.Length);
    }

    [Fact]
    public void A_global_is_found_at_its_uses()
    {
        var sites = Sites(
            "let ans$wer = 42;\nfn main(): int {\n    return answer + answer;\n}\n");

        Assert.Equal(["answer", "answer"], sites);
    }

    // ------------------------------------------------------------------ the declaration

    [Fact]
    public void The_declaration_is_included_only_when_it_is_asked_for()
    {
        const string program =
            "fn twice(n: int): int { return n * 2; }\n"
            + "fn main(): int {\n    return tw$ice(1);\n}\n";

        Assert.Single(Sites(program));
        Assert.Equal(2, Sites(program, includeDeclaration: true).Length);
    }

    [Fact]
    public void The_included_declaration_is_the_name_and_not_the_whole_function()
    {
        var sites = Sites(
            "fn twice(n: int): int {\n    return n * 2;\n}\n"
            + "fn main(): int {\n    return tw$ice(1);\n}\n",
            includeDeclaration: true);

        Assert.Equal(["twice", "twice"], sites);
    }

    [Fact]
    public void A_declaration_nobody_uses_has_no_references()
    {
        // The counter-check that gives the others their meaning. Without it an implementation that
        // returns every entry of the table stays green on all of them.
        Assert.Empty(Sites("fn unu$sed(): int { return 1; }\nfn main(): int { return 0; }\n"));
    }

    [Fact]
    public void A_declaration_nobody_uses_still_answers_with_itself_when_asked()
    {
        Assert.Equal(["unused"],
            Sites("fn unu$sed(): int { return 1; }\nfn main(): int { return 0; }\n",
                includeDeclaration: true));
    }

    [Fact]
    public void A_local_binding_is_not_reported_as_a_use_of_itself()
    {
        // The sema binds a BindingStmt to its own symbol for the definite-assignment analysis. That
        // entry is a declaration wearing the shape of a use, and it is the reason the two are told
        // apart by the symbol rather than by the table they came from.
        var sites = Sites("fn main(): int {\n    let cou$nt = 1;\n    return count;\n}\n");
        Assert.Equal(["count"], sites);
    }

    [Fact]
    public void A_loop_variable_is_not_reported_as_a_use_of_itself()
    {
        var sites = Sites(
            "fn main(): int {\n    var t = 0;\n    for (n$ in 0..3) {\n        t = t + n;\n    }\n"
            + "    return t;\n}\n");

        Assert.Equal(["n"], sites);
    }

    // ------------------------------------------------------------------ scopes and modules

    [Fact]
    public void Two_locals_of_the_same_name_in_different_scopes_do_not_mix()
    {
        // Symbols are identity objects, and the comparison is by reference for exactly this case.
        var sites = Sites(
            "fn main(): int {\n"
            + "    if (true) {\n        let v$alue = 1;\n        let a = value;\n    }\n"
            + "    let value = 2;\n    let b = value + value;\n    return b;\n}\n");

        Assert.Single(sites);
    }

    [Fact]
    public void A_standard_library_function_is_found_across_the_module_boundary()
    {
        var (texts, result) = SitesAt(
            "import std.os { cpuCount };\nfn main(): int {\n    return cpuC$ount() + cpuCount();\n}\n",
            includeDeclaration: true);

        // Two uses in this file plus the declaration, which stands in another one.
        Assert.Equal(3, texts.Length);
        Assert.All(texts, text => Assert.Equal("cpuCount", text));
        Assert.NotNull(result.Model);
    }

    // ------------------------------------------------------------------ nothing to answer

    [Fact]
    public void A_position_on_no_symbol_answers_null_rather_than_an_empty_list()
    {
        // The two are different answers: null means "not a name", an empty list means "a name
        // nobody uses". An editor says so differently.
        var offset = "fn main(): int { return 1; }\n".IndexOf("return", StringComparison.Ordinal);
        var path = Path.Combine(AppContext.BaseDirectory, "nothing.lyr");

        var result = SourceCompiler.Check(
            ScriptSource.FromBuffer(path, "fn main(): int { return 1; }\n"));
        Assert.NotNull(result.Model);

        var file = DiagnosticMapper.FindFile(result.Sources, path);
        Assert.Null(ReferenceProvider.At(result.Model, file, offset, includeDeclaration: false));
    }

    [Fact]
    public void A_file_that_does_not_type_check_still_answers_for_what_resolved()
    {
        var sites = Sites(
            "fn main(): int {\n    let cou$nt = 1;\n    let bad = \"x\" + nowhere;\n"
            + "    return count + count;\n}\n");

        Assert.Equal(["count", "count"], sites);
    }

    // ------------------------------------------------------------------ the recorded limit

    [Fact]
    public void A_struct_initializer_is_not_found_and_why()
    {
        // 'Point { … }' names its type and is bound to no symbol, so it is invisible here.
        //
        // Recording it looks like a one-line addition in CheckStructInit and is not: TypeChecker
        // reads the SAME table at the member-access path to decide whether a receiver is a type.
        // An entry there turns 'Pair<int> { a = 6 }.a' into a static member access and rejects it —
        // measured, by two failing tests and a guide chapter that stopped compiling. Separating
        // "what does this refer to" from "what kind of receiver is this" is its own change.
        // NULL rather than an empty list, and the difference is the point: the cursor is on nothing
        // the compiler bound. Walking outwards from here reaches the enclosing 'let p = …', which is
        // bound to 'p' for the definite-assignment analysis — answering with THAT was the behaviour
        // before this slice, and it is a wrong answer rather than a missing one.
        const string program = "struct Point { x: int, }\nfn main(): int {\n"
            + "    let p = Point { x = 1 };\n    return p.x;\n}\n";

        var path = Path.Combine(AppContext.BaseDirectory, "structinit.lyr");
        var result = SourceCompiler.Check(ScriptSource.FromBuffer(path, program));
        Assert.NotNull(result.Model);

        var file = DiagnosticMapper.FindFile(result.Sources, path);
        var offset = program.IndexOf("Point { x = 1 }", StringComparison.Ordinal) + 2;

        Assert.Null(ReferenceProvider.At(result.Model, file, offset, includeDeclaration: false));
    }

    [Fact]
    public void A_field_use_marks_the_whole_member_access()
    {
        // Measured, not claimed. A MemberExpr spans 'p.x' and carries no span for the member name
        // alone — the mirror of what slice 1 fixed on the declaration side. A use-site name span
        // would narrow this; it is not built.
        var sites = Sites(
            "struct Point { x$: int, }\n"
            + "fn main(): int {\n    let p = Point { x = 1 };\n    return p.x;\n}\n");

        Assert.Contains("p.x", sites);
    }
}

/// <summary>References over the wire.</summary>
public sealed class ReferenceProtocolTests
{
    private const string Program =
        "fn twice(n: int): int { return n * 2; }\nfn main(): int {\n    return twice(1) + twice(2);\n}\n";

    private static string BufferPath(
        [System.Runtime.CompilerServices.CallerMemberName] string name = "") =>
        Path.Combine(AppContext.BaseDirectory, $"{name}.lyr");

    private static string DidOpen(string uri, string text) =>
        JsonSerializer.Serialize(new
        {
            textDocument = new { uri, languageId = "lyric", version = 1, text },
        });

    private static string ReferencesAt(string uri, int line, int character, bool includeDeclaration) =>
        JsonSerializer.Serialize(new
        {
            textDocument = new { uri },
            position = new { line, character },
            context = new { includeDeclaration },
        });

    [Fact]
    public async Task The_capability_is_announced()
    {
        await using var harness = new ServerHarness();

        var id = await harness.RequestAsync(LspMethods.Initialize, "{}");
        var response = await harness.ReceiveResponseAsync(id);

        Assert.True(response.GetProperty("result").GetProperty("capabilities")
            .GetProperty("referencesProvider").GetBoolean());
    }

    [Fact]
    public async Task Both_uses_come_back_with_the_uri_the_client_sent()
    {
        const string asSent = "file:///c%3A/nowhere/refs.lyr";

        await using var harness = new ServerHarness();
        await harness.InitializeAsync();
        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(asSent, Program));
        await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);

        // Line 0, on the name of 'twice'.
        var id = await harness.RequestAsync(
            LspMethods.References, ReferencesAt(asSent, 0, 4, includeDeclaration: false));
        var result = (await harness.ReceiveResponseAsync(id)).GetProperty("result");

        Assert.Equal(JsonValueKind.Array, result.ValueKind);
        Assert.Equal(2, result.GetArrayLength());
        Assert.Equal(asSent, result[0].GetProperty("uri").GetString());
        Assert.Equal(2, result[0].GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
    }

    [Fact]
    public async Task The_declaration_comes_along_when_the_context_asks_for_it()
    {
        var uri = DocumentUri.FromFilePath(BufferPath());

        await using var harness = new ServerHarness();
        await harness.InitializeAsync();
        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(uri, Program));
        await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);

        var id = await harness.RequestAsync(
            LspMethods.References, ReferencesAt(uri, 0, 4, includeDeclaration: true));
        var result = (await harness.ReceiveResponseAsync(id)).GetProperty("result");

        Assert.Equal(3, result.GetArrayLength());
    }

    [Fact]
    public async Task A_position_with_no_symbol_answers_null_rather_than_an_error()
    {
        var uri = DocumentUri.FromFilePath(BufferPath());

        await using var harness = new ServerHarness();
        await harness.InitializeAsync();
        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(uri, Program));
        await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);

        var id = await harness.RequestAsync(
            LspMethods.References, ReferencesAt(uri, 0, 200, includeDeclaration: false));
        var response = await harness.ReceiveResponseAsync(id);

        Assert.Equal(JsonValueKind.Null, response.GetProperty("result").ValueKind);
        Assert.False(response.TryGetProperty("error", out _));
    }
}
