using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Xunit;

namespace Lyric.Tests.Sema;

/// <summary>
/// What a member access dispatches on: whether the receiver NAMES a type or a module, or produces a
/// value.
///
/// <para>That is read off the receiver's TYPE — <c>CheckTarget</c> yields a <c>NonValueType</c> for a
/// name and an ordinary type for a value. It used to be read off the reference table instead, which
/// cannot tell <c>Point</c> from <c>Point { … }</c>: both mention the type, only one denotes it. The
/// two are one character apart in the source and opposite in meaning, so both stand here.</para>
/// </summary>
public class MemberReceiverTests
{
    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static DiagnosticEngine Check(string source, bool withStdlib = false)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);

        if (withStdlib)
            comp.ModuleLoader = StdlibLoader.ForRoot(Path.Combine(RepoRoot(), "stdlib"), sm, de);

        comp.AddModule(new Parser(sm, id, de).ParseModule());
        Semantics.Analyze(comp, comp.Resolve(), de);
        return de;
    }

    private static void AssertClean(string source)
    {
        var de = Check(source);
        Assert.False(de.HasErrors,
            "expected this to check clean, but got:\n"
            + string.Join("\n", de.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }

    private static void AssertReports(string source, string code)
    {
        var de = Check(source);
        Assert.Contains(de.Diagnostics, d => d.Code == code);
    }

    // ------------------------------------------------------------------ a value receiver

    [Fact]
    public void A_member_on_a_struct_initializer_is_an_instance_access()
    {
        // The regression that reverted the first attempt at recording the initializer's type. With
        // the dispatch on the table, 'Pair<int> { a = 6 }.a' read as a STATIC access and was
        // rejected as LYR-SEM0055.
        AssertClean(
            "struct Pair<T> { a: T, }\nfn main(): int { return Pair<int> { a = 6 }.a; }\n");
    }

    [Fact]
    public void A_member_on_a_non_generic_initializer_is_an_instance_access()
    {
        AssertClean("struct Point { x: int, }\nfn main(): int { return Point { x = 1 }.x; }\n");
    }

    [Fact]
    public void A_member_on_a_call_result_is_an_instance_access()
    {
        // The general form of the same thing: an expression that PRODUCES a value of a type is not
        // that type, however the value was made.
        AssertClean(
            "struct Point { x: int, }\nfn make(): Point { return Point { x = 1 }; }\n"
            + "fn main(): int { return make().x; }\n");
    }

    // ------------------------------------------------------------------ a name receiver

    [Fact]
    public void A_member_on_a_type_name_is_a_static_access()
    {
        AssertClean(
            "struct Point {\n    x: int,\n    static fn origin(): int { return 0; }\n}\n"
            + "fn main(): int { return Point.origin(); }\n");
    }

    [Fact]
    public void A_member_on_a_generic_type_path_is_a_static_access()
    {
        AssertClean(
            "struct Box<T> {\n    v: T,\n    static fn zero(): int { return 0; }\n}\n"
            + "fn main(): int { return Box<int>.zero(); }\n");
    }

    [Fact]
    public void A_member_on_a_module_name_is_a_module_access()
    {
        var de = Check(
            "import std.io.console;\nfn main(): int {\n    console.println(\"hi\");\n    return 0;\n}\n",
            withStdlib: true);

        Assert.False(de.HasErrors,
            "expected this to check clean, but got:\n"
            + string.Join("\n", de.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }

    // ------------------------------------------------------------------ the errors stay errors

    [Fact]
    public void A_static_member_reached_through_an_instance_is_still_rejected()
    {
        // The counter-check. Dispatching on the type rather than on the table must not make the
        // static-versus-instance rule more permissive — only correct about which side it is on.
        AssertReports(
            "struct Point {\n    x: int,\n    static fn origin(): int { return 0; }\n}\n"
            + "fn main(): int {\n    let p = Point { x = 1 };\n    return p.origin();\n}\n",
            "LYR-SEM0055");
    }

    [Fact]
    public void An_instance_member_reached_through_the_type_is_still_rejected()
    {
        AssertReports(
            "struct Point { x: int, }\nfn main(): int { return Point.x; }\n",
            "LYR-SEM0055");
    }

    [Fact]
    public void A_static_extension_reached_through_an_instance_is_accepted()
    {
        // Measured, not endorsed. The instance path falls through to the extension lookup without
        // checking 'static', while the type path rejects a non-static extension explicitly — so
        // 'p.make()' compiles and 'Point.instanceMethod()' does not. The asymmetry is recorded under
        // Still open; this test says which way round it currently is, so a decision to change it
        // arrives here first.
        AssertClean(
            "struct Point { x: int, }\n"
            + "extend Point {\n    static fn make(): int { return 7; }\n}\n"
            + "fn main(): int {\n    let p = Point { x = 1 };\n    return p.make();\n}\n");
    }

    [Fact]
    public void A_member_that_does_not_exist_is_still_reported()
    {
        AssertReports(
            "struct Point { x: int, }\nfn main(): int { return Point { x = 1 }.nowhere; }\n",
            "LYR-SEM0012");
    }

    [Fact]
    public void An_unresolvable_import_as_a_receiver_reports_once()
    {
        // The edge the rewrite had to preserve. The old code short-circuited on an ExternalSymbol
        // before looking for an instance member; now the receiver's type is simply an error type and
        // the fall-through returns without a second diagnostic. Two errors for one cause would send
        // the reader looking for a member problem that does not exist.
        var de = Check(
            "import nowhere.at.all { thing };\nfn main(): int {\n    return thing.member;\n}\n");

        Assert.DoesNotContain(de.Diagnostics, d => d.Code == "LYR-SEM0012");
    }
}
