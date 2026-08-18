using System.Text.Json;
using Lyric.Compiler;
using Lyric.Lsp.Analysis;
using Lyric.Lsp.Documents;
using Lyric.Lsp.Protocol;

namespace Lyric.Tests.Lsp;

/// <summary>
/// The tokens are asserted DECODED — each quintuple turned back into the text it covers plus its
/// legend names. A test over raw deltas would pin arithmetic; this pins meaning: 'Point' is a
/// type wherever it stands, 'x' is a property in the access and in the initializer, and an
/// operator use colors nothing.
/// </summary>
public sealed class SemanticTokenTests
{
    private sealed record Token(string Text, string Type, string[] Modifiers, int Line);

    private static Token[] TokensOf(string program)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "semantic.lyr");
        var result = SourceCompiler.Check(ScriptSource.FromBuffer(path, program));
        Assert.NotNull(result.Model);

        var file = DiagnosticMapper.FindFile(result.Sources, path);
        Assert.True(file.IsValid);

        var data = SemanticTokensProvider.Of(
            result.Model, result.Model.Entry, file, result.Sources);

        return Decode(program, data);
    }

    /// <summary>The inverse of the wire encoding, against the very text the spans came from.</summary>
    private static Token[] Decode(string program, IReadOnlyList<int> data)
    {
        Assert.Equal(0, data.Count % 5);
        var lines = program.Split('\n');

        var tokens = new List<Token>();
        var (line, character) = (0, 0);

        for (var i = 0; i < data.Count; i += 5)
        {
            if (data[i] > 0) character = 0;
            line += data[i];
            character += data[i + 1];

            Assert.True(line < lines.Length, "token beyond the last line");
            Assert.True(character + data[i + 2] <= lines[line].Length,
                $"token beyond the end of line {line}");

            var modifiers = SemanticTokensProvider.TokenModifiers
                .Where((_, bit) => (data[i + 4] & (1 << bit)) != 0)
                .ToArray();

            tokens.Add(new Token(lines[line].Substring(character, data[i + 2]),
                SemanticTokensProvider.TokenTypes[data[i + 3]], modifiers, line));
        }

        return tokens.ToArray();
    }

    private const string Program =
        "import std.io.console { println };\n"
        + "\n"
        + "struct Point {\n"
        + "    x: int,\n"
        + "    fn scaled(factor: int): int {\n"
        + "        return this.x * factor;\n"
        + "    }\n"
        + "}\n"
        + "\n"
        + "fn area(p: Point): int {\n"
        + "    let total = p.x * p.scaled(2);\n"
        + "    return total;\n"
        + "}\n"
        + "\n"
        + "fn main(): int {\n"
        + "    let p = Point { x = 3 };\n"
        + "    println(\"{area(p)}\");\n"
        + "    return 0;\n"
        + "}\n";

    [Fact]
    public void Every_form_of_a_name_carries_its_meaning()
    {
        var tokens = TokensOf(Program);

        // The declarations say so.
        Assert.Contains(tokens, t => t is { Text: "Point", Type: "type", Modifiers: ["declaration"] });
        Assert.Contains(tokens, t => t is { Text: "x", Type: "property", Modifiers: ["declaration"] });
        Assert.Contains(tokens, t => t is { Text: "scaled", Type: "method", Modifiers: ["declaration"] });
        Assert.Contains(tokens, t => t is { Text: "area", Type: "function", Modifiers: ["declaration"] });
        Assert.Contains(tokens, t =>
            t is { Text: "total", Type: "variable", Modifiers: ["declaration", "readonly"] });
        Assert.Contains(tokens, t =>
            t is { Text: "factor", Type: "parameter", Modifiers: ["declaration"] });

        // The uses agree, without the declaration bit: the annotation and the initializer are the
        // TYPE; 'this.x', 'p.x' and the initializer field are all the FIELD.
        Assert.Equal(2, tokens.Count(t => t is { Text: "Point", Type: "type", Modifiers: [] }));
        Assert.Equal(3, tokens.Count(t => t is { Text: "x", Type: "property", Modifiers: [] }));
        Assert.Contains(tokens, t => t is { Text: "scaled", Type: "method", Modifiers: [] });
        Assert.Contains(tokens, t => t is { Text: "total", Type: "variable", Modifiers: ["readonly"] });

        // The import clause carries the target's meaning, and so does the call.
        Assert.Equal(2, tokens.Count(t => t is { Text: "println", Type: "function" }));
    }

    [Fact]
    public void Tokens_never_overlap_and_stay_ordered()
    {
        // The wire format breaks silently on both: a negative delta corrupts every later token.
        var path = Path.Combine(AppContext.BaseDirectory, "semantic.lyr");
        var result = SourceCompiler.Check(ScriptSource.FromBuffer(path, Program));
        Assert.NotNull(result.Model);
        var file = DiagnosticMapper.FindFile(result.Sources, path);

        var data = SemanticTokensProvider.Of(
            result.Model, result.Model.Entry, file, result.Sources);

        var (line, character) = (0, 0);
        var previousEnd = -1;
        for (var i = 0; i < data.Count; i += 5)
        {
            Assert.True(data[i] >= 0, "line delta must not be negative");
            if (data[i] > 0) { character = 0; previousEnd = -1; }
            line += data[i];
            character += data[i + 1];

            Assert.True(character >= previousEnd,
                $"token at {line}:{character} overlaps its predecessor");
            previousEnd = character + data[i + 2];
        }
    }

    [Fact]
    public void An_operator_use_colors_nothing()
    {
        // 'a + b' resolves to 'add' through the conformance on a node the sema synthesized. The
        // '+' must stay uncolored: there is no name in the text, and a token over the whole
        // expression would paint 'a + b' as a method.
        const string program = "import std.core { Add };\n"
            + "\n"
            + "struct Vec :: [Add<Vec>] {\n"
            + "    x: int,\n"
            + "    fn add(other: Vec): Vec {\n"
            + "        return Vec { x = this.x + other.x };\n"
            + "    }\n"
            + "}\n"
            + "\n"
            + "fn main(): int {\n"
            + "    let a = Vec { x = 1 };\n"
            + "    let b = Vec { x = 2 };\n"
            + "    let c = a + b;\n"
            + "    return c.x;\n"
            + "}\n";

        var tokens = TokensOf(program);

        // Every 'add' token is exactly the name — never an expression containing '+'.
        Assert.All(tokens.Where(t => t.Type == "method"), t => Assert.Equal("add", t.Text));
        Assert.DoesNotContain(tokens, t => t.Text.Contains('+'));

        // And the interface in the conformance list is one.
        Assert.Contains(tokens, t => t is { Text: "Add", Type: "interface" });
    }

    [Fact]
    public void An_enum_is_an_enum_and_its_variant_a_member()
    {
        const string program = "enum Shape {\n"
            + "    Circle(float),\n"
            + "    Square(float),\n"
            + "}\n"
            + "\n"
            + "fn main(): int {\n"
            + "    let s = Shape.Circle(1.0);\n"
            + "    return 0;\n"
            + "}\n";

        var tokens = TokensOf(program);

        Assert.Contains(tokens, t => t is { Text: "Shape", Type: "enum", Modifiers: ["declaration"] });
        Assert.Contains(tokens, t => t is { Text: "Shape", Type: "enum", Modifiers: [] });
        Assert.Contains(tokens, t => t is { Text: "Circle", Type: "enumMember" });
    }
}

/// <summary>The tokens over the wire: announced, asked for, delivered.</summary>
public sealed class SemanticTokenProtocolTests
{
    [Fact]
    public async Task The_capability_names_the_legend_and_the_request_delivers()
    {
        await using var harness = new ServerHarness();

        var id = await harness.RequestAsync(LspMethods.Initialize, "{}");
        var initialize = await harness.ReceiveResponseAsync(id);
        var provider = initialize.GetProperty("result").GetProperty("capabilities")
            .GetProperty("semanticTokensProvider");

        Assert.True(provider.GetProperty("full").GetBoolean());
        Assert.Equal("type",
            provider.GetProperty("legend").GetProperty("tokenTypes")[1].GetString());

        await harness.NotifyAsync(LspMethods.Initialized, "{}");

        var path = Path.Combine(AppContext.BaseDirectory, "tokens-wire.lyr");
        var uri = DocumentUri.FromFilePath(path);
        await harness.NotifyAsync(LspMethods.DidOpen, JsonSerializer.Serialize(new
        {
            textDocument = new
            {
                uri,
                languageId = "lyric",
                version = 1,
                text = "fn main(): int { let x = 1; return x; }\n",
            },
        }));
        await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);

        var request = await harness.RequestAsync(LspMethods.SemanticTokensFull,
            JsonSerializer.Serialize(new { textDocument = new { uri } }));
        var response = await harness.ReceiveResponseAsync(request);

        var data = response.GetProperty("result").GetProperty("data").EnumerateArray().ToArray();
        Assert.True(data.Length > 0 && data.Length % 5 == 0,
            $"expected non-empty quintuples, got {data.Length} values");
    }
}
