using System.Runtime.CompilerServices;
using Lyric.Compiler;
using Lyric.Lsp.Analysis;
using Lyric.Lsp.Protocol;

namespace Lyric.Tests.Lsp;

/// <summary>
/// The names a position can see.
///
/// <para>The cursor is marked with <c>$</c>. The fixtures are programs mid-edit, which is the state
/// this feature is asked in — most of them do not compile, and that is the point.</para>
/// </summary>
public sealed class ScopeCompletionTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static IReadOnlyList<CompletionItem> Items(string programWithCursor)
    {
        var offset = programWithCursor.IndexOf('$');
        Assert.True(offset >= 0, "the fixture has no '$' marking the cursor");

        var program = programWithCursor.Remove(offset, 1);
        var path = Path.Combine(AppContext.BaseDirectory, "scope.lyr");

        var items = CompletionProvider.At(path, program, offset, new CompilerOptions
        {
            StdlibRoot = Path.Combine(RepoRoot(), "stdlib"),
        });

        Assert.NotNull(items);
        return items;
    }

    private static string[] Labels(string programWithCursor) =>
        Items(programWithCursor).Select(i => i.Label).ToArray();

    // ------------------------------------------------------------------ one per level

    [Fact]
    public void A_local_declared_before_the_cursor_is_offered()
    {
        var labels = Labels("fn main(): int {\n    let count = 1;\n    return $;\n}\n");
        Assert.Contains("count", labels);
    }

    [Fact]
    public void A_parameter_is_offered()
    {
        var labels = Labels("fn twice(value: int): int {\n    return $;\n}\n");
        Assert.Contains("value", labels);
    }

    [Fact]
    public void A_type_parameter_is_offered()
    {
        var labels = Labels("fn id<T>(v: T): int {\n    let x: $ = 0;\n    return 0;\n}\n");
        Assert.Contains("T", labels);
    }

    [Fact]
    public void A_loop_variable_is_offered_inside_the_body()
    {
        var labels = Labels(
            "fn main(): int {\n    var t = 0;\n    for (n in 0..3) {\n        t = $;\n    }\n"
            + "    return t;\n}\n");

        Assert.Contains("n", labels);
    }

    [Fact]
    public void A_catch_binding_is_offered_inside_the_body()
    {
        var labels = Labels(
            "fn boom(): int throws { throw Error { message = \"x\" }; }\n"
            + "fn main(): int {\n    try {\n        return boom();\n    } catch (e) {\n"
            + "        let m = $;\n        return 1;\n    }\n}\n");

        Assert.Contains("e", labels);
    }

    [Fact]
    public void A_destructured_name_is_offered()
    {
        var labels = Labels(
            "fn main(): int {\n    let pair = (1, 2);\n    let (a, b) = pair;\n    return $;\n}\n");

        Assert.Contains("a", labels);
        Assert.Contains("b", labels);
    }

    [Fact]
    public void A_lambda_parameter_is_offered_inside_the_lambda()
    {
        var labels = Labels(
            "fn main(): int {\n    let double = (n: int) => $;\n    return 0;\n}\n");

        Assert.Contains("n", labels);
    }

    [Fact]
    public void The_module_and_the_builtins_are_offered()
    {
        var labels = Labels(
            "struct Point { x: int, }\nfn helper(): int { return 1; }\n"
            + "fn main(): int {\n    return $;\n}\n");

        Assert.Contains("helper", labels);
        Assert.Contains("Point", labels);
        Assert.Contains("int", labels);
    }

    [Fact]
    public void An_imported_name_is_offered()
    {
        var labels = Labels(
            "import std.os { cpuCount };\nfn main(): int {\n    return $;\n}\n");

        Assert.Contains("cpuCount", labels);
    }

    // ------------------------------------------------------------------ the rules

    [Fact]
    public void An_inner_name_shadows_an_outer_one()
    {
        var items = Items(
            "fn main(value: int): int {\n    if (true) {\n        let value = 2;\n"
            + "        return $;\n    }\n    return value;\n}\n");

        // One entry, and it is the inner one — the local, not the parameter.
        var value = Assert.Single(items, i => i.Label == "value");
        Assert.Equal("local", value.Detail);
    }

    [Fact]
    public void A_binding_is_not_visible_in_its_own_initializer()
    {
        Assert.DoesNotContain("count", Labels("fn main(): int {\n    let count = $;\n    return 0;\n}\n"));
    }

    [Fact]
    public void A_local_declared_after_the_cursor_is_not_offered()
    {
        var labels = Labels(
            "fn main(): int {\n    let before = 1;\n    let x = $;\n    let after = 2;\n"
            + "    return before + after;\n}\n");

        Assert.Contains("before", labels);
        Assert.DoesNotContain("after", labels);
    }

    [Fact]
    public void A_loop_variable_is_not_offered_in_the_loop_head()
    {
        // The iterable is evaluated where the variable does not yet exist.
        Assert.DoesNotContain("n", Labels(
            "fn main(): int {\n    var t = 0;\n    for (n in $) {\n        t = 1;\n    }\n"
            + "    return t;\n}\n"));
    }

    // ------------------------------------------------------------------ the two contexts

    [Fact]
    public void After_a_dot_the_members_are_offered_and_not_the_scope()
    {
        var labels = Labels(
            "struct Point { x: int, }\nfn helper(): int { return 1; }\n"
            + "fn main(): int {\n    let p = Point { x = 1 };\n    return p.$;\n}\n");

        Assert.Contains("x", labels);
        Assert.DoesNotContain("helper", labels);
    }

    [Fact]
    public void In_the_receiver_the_scope_is_offered_and_not_the_members()
    {
        // The cursor is inside a member expression but BEFORE the dot: what is being typed is the
        // receiver, so what belongs here are the names in scope. Answering with the members of
        // whatever the receiver currently names would be a list about a different question.
        var labels = Labels(
            "struct Point { x: int, }\nfn helper(): int { return 1; }\n"
            + "fn main(): int {\n    let p = Point { x = 1 };\n    return p$.x;\n}\n");

        Assert.Contains("helper", labels);
        Assert.Contains("p", labels);
        Assert.DoesNotContain("x", labels);
    }

    // ------------------------------------------------------------------ robustness

    [Fact]
    public void A_file_with_type_errors_still_offers_the_scope()
    {
        var labels = Labels(
            "fn main(): int {\n    let count = 1;\n    let bad = \"s\" + nowhere;\n"
            + "    return $;\n}\n");

        Assert.Contains("count", labels);
    }

    [Fact]
    public void No_label_contains_the_marker()
    {
        Assert.All(Labels("fn main(): int {\n    let count = 1;\n    return $;\n}\n"),
            label => Assert.DoesNotContain("lyric_completion", label));
    }

    [Fact]
    public void A_local_and_a_function_get_different_kinds()
    {
        var items = Items(
            "fn helper(): int { return 1; }\n"
            + "fn main(): int {\n    let count = 1;\n    return $;\n}\n");

        Assert.Equal(CompletionItemKind.Variable, Assert.Single(items, i => i.Label == "count").Kind);
        Assert.Equal(CompletionItemKind.Function, Assert.Single(items, i => i.Label == "helper").Kind);
    }

    [Fact]
    public void An_item_carries_the_documentation_written_above_it()
    {
        var items = Items(
            "/// Does the thing.\nfn helper(): int { return 1; }\n"
            + "fn main(): int {\n    return $;\n}\n");

        Assert.Equal("Does the thing.", Assert.Single(items, i => i.Label == "helper").Documentation?.Value);
    }
}
