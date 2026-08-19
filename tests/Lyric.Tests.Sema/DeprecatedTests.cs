using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Xunit;

namespace Lyric.Tests.Sema;

/// <summary>
/// <c>@Deprecated</c>, the first attribute the compiler reads: every use of a marked
/// declaration warns at the use site (LYR-SEM0076), the note points at the attribute, and the
/// message says what to use instead. It changes diagnostics and NOTHING else — a program that
/// ignores the warning compiles to the same module.
/// </summary>
public class DeprecatedTests
{
    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static DiagnosticEngine Check(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de)
        {
            ModuleLoader = StdlibLoader.ForRoot(Path.Combine(RepoRoot(), "stdlib"), sm, de),
        };

        comp.AddModule(new Parser(sm, id, de).ParseModule());
        Semantics.Analyze(comp, comp.Resolve(), de);
        return de;
    }

    private const string Import = "import std.core { Deprecated };\n\n";

    [Fact]
    public void A_use_of_a_deprecated_function_warns_with_the_message()
    {
        var de = Check(Import
            + "@Deprecated { message = \"use renew\" }\npub fn old(): int {\n    return 1;\n}\n\n"
            + "fn main(): int {\n    return old();\n}\n");
        Assert.False(de.HasErrors);
        var warning = Assert.Single(de.Diagnostics, d => d.Code == "LYR-SEM0076");
        Assert.Equal(Severity.Warning, warning.Severity);
        Assert.Equal("'old' is deprecated: use renew", warning.Message);

        var note = Assert.Single(warning.Notes!);
        Assert.Equal("declared deprecated here", note.Message);
        Assert.True(note.Location.File.IsValid);
    }

    [Fact]
    public void Without_a_message_the_warning_stands_alone()
    {
        var de = Check(Import
            + "@Deprecated\npub fn old(): int {\n    return 1;\n}\n\n"
            + "fn main(): int {\n    return old();\n}\n");
        var warning = Assert.Single(de.Diagnostics, d => d.Code == "LYR-SEM0076");
        Assert.Equal("'old' is deprecated", warning.Message);
    }

    [Fact]
    public void A_type_used_only_in_an_annotation_warns_too()
    {
        // The annotation use lives in the resolver's table; the pass reads both.
        var de = Check(Import
            + "@Deprecated { message = \"use Point\" }\npub struct OldPoint {\n    x: int,\n}\n\n"
            + "fn shift(p: OldPoint): int {\n    return p.x;\n}\n\n"
            + "fn main(): int {\n    return 0;\n}\n");
        Assert.False(de.HasErrors);
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0076"
            && d.Message.Contains("'OldPoint' is deprecated"));
    }

    [Fact]
    public void The_declaration_alone_warns_nobody()
    {
        var de = Check(Import
            + "@Deprecated\npub fn old(): int {\n    return 1;\n}\n\n"
            + "fn main(): int {\n    return 0;\n}\n");
        Assert.DoesNotContain(de.Diagnostics, d => d.Code == "LYR-SEM0076");
    }

    [Fact]
    public void A_deprecated_function_may_call_itself()
    {
        var de = Check(Import
            + "@Deprecated\npub fn old(n: int): int {\n"
            + "    return if (n <= 0) 0 else old(n - 1);\n}\n\n"
            + "fn main(): int {\n    return 0;\n}\n");
        Assert.DoesNotContain(de.Diagnostics, d => d.Code == "LYR-SEM0076");
    }

    [Fact]
    public void Deprecated_may_use_deprecated()
    {
        // The one place allowed not to care: a deprecated implementation delegating to its
        // deprecated sibling adds no new debt.
        var de = Check(Import
            + "@Deprecated\npub fn older(): int {\n    return 1;\n}\n\n"
            + "@Deprecated\npub fn old(): int {\n    return older();\n}\n\n"
            + "fn main(): int {\n    return 0;\n}\n");
        Assert.DoesNotContain(de.Diagnostics, d => d.Code == "LYR-SEM0076");
    }

    [Fact]
    public void A_struct_named_Deprecated_by_someone_else_deprecates_nothing()
    {
        // Identity, not name: the canonical struct is the one std.core declares.
        var de = Check(
            "import std.core { OnFunction };\n\n"
            + "pub struct Deprecated :: [OnFunction] { }\n\n"
            + "@Deprecated\npub fn old(): int {\n    return 1;\n}\n\n"
            + "fn main(): int {\n    return old();\n}\n");
        Assert.False(de.HasErrors);
        Assert.DoesNotContain(de.Diagnostics, d => d.Code == "LYR-SEM0076");
    }

    [Fact]
    public void A_generic_declaration_may_carry_Deprecated()
    {
        // The one exception to LYR-SEM0067: the compiler-read attribute needs no metadata row,
        // so one-row-many-instances never arises. The lowering emits no row for it.
        var de = Check(Import
            + "@Deprecated { message = \"use the static\" }\npub fn oldMake<T>(v: T): T {\n    return v;\n}\n\n"
            + "fn main(): int {\n    return oldMake<int>(1);\n}\n");
        Assert.False(de.HasErrors);
        Assert.DoesNotContain(de.Diagnostics, d => d.Code == "LYR-SEM0067");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0076"
            && d.Message.Contains("'oldMake' is deprecated"));
    }

    [Fact]
    public void Any_other_attribute_on_a_generic_declaration_stays_refused()
    {
        var de = Check(
            "import std.core { OnFunction };\n\n"
            + "pub struct Marked :: [OnFunction] { }\n\n"
            + "@Marked\npub fn generic<T>(v: T): T {\n    return v;\n}\n\n"
            + "fn main(): int {\n    return generic<int>(1);\n}\n");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0067");
    }

    [Fact]
    public void Every_use_site_warns_not_just_the_first()
    {
        var de = Check(Import
            + "@Deprecated\npub fn old(): int {\n    return 1;\n}\n\n"
            + "fn main(): int {\n    return old() + old();\n}\n");
        Assert.Equal(2, de.Diagnostics.Count(d => d.Code == "LYR-SEM0076"));
    }
}
