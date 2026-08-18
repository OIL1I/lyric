using System.Runtime.CompilerServices;
using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Sema;

/// <summary>
/// What an attribute is allowed to name, and what its arguments are allowed to hold.
///
/// <para>The pinned decisions: an attribute is a STRUCT, and where it may sit is the marker
/// interface it declares — conformance, not the name, the same nominal rule the operators follow.
/// Arguments are literals, because they end up in a bytecode section, and the emitted row is
/// complete: a field the use does not write needs a literal default.</para>
/// </summary>
public class AttributeTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static DiagnosticEngine Check(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
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

    private static void AssertReports(string code, string messagePart, string source)
    {
        var found = Check(source).Diagnostics.FirstOrDefault(d => d.Code == code);
        Assert.NotNull(found);
        Assert.Contains(messagePart, found.Message);
    }

    private const string Markers = """
        import std.core { OnModule, OnType, OnFunction };

        struct Plugin :: [OnModule] { name: string, api: int }
        struct Component :: [OnType] { }
        struct System :: [OnFunction] { order: int = 0 }
        """;

    // ------------------------------------------------------------------ the positive cases

    [Fact]
    public void An_attribute_with_the_right_marker_checks_clean_on_each_target() =>
        AssertClean(Markers + """

            @Component
            struct Health { value: int, max: int }

            @System { order = 10 }
            fn tick(dt: float): void { }

            fn main(): int { return 0; }
            """);

    [Fact]
    public void A_module_attribute_checks_against_OnModule() =>
        AssertClean("@Plugin { name = \"m\", api = 2 }\nmodule m;\n" + Markers
            + "\nfn main(): int { return 0; }");

    [Fact]
    public void A_missing_field_with_a_literal_default_is_no_error() =>
        // 'order' has '= 0'; the emitted row fills it in, so '@System' alone is complete.
        AssertClean(Markers + """

            @System
            fn tick(): void { }

            fn main(): int { return 0; }
            """);

    [Fact]
    public void A_negative_literal_is_a_literal() =>
        AssertClean(Markers + """

            @System { order = -1 }
            fn tick(): void { }

            fn main(): int { return 0; }
            """);

    [Fact]
    public void A_class_and_an_enum_take_OnType() =>
        AssertClean(Markers + """

            @Component
            class World { }

            @Component
            enum Phase { Start, End }

            fn main(): int { return 0; }
            """);

    /// <summary>One struct may carry more than one marker; it then sits on both kinds of
    /// target. Listing conformances is this language's way of saying "both" — there is no
    /// interface inheritance to combine them.</summary>
    [Fact]
    public void Two_markers_admit_two_kinds_of_target() =>
        AssertClean("""
            import std.core { OnType, OnFunction };

            struct Tag :: [OnFunction, OnType] { }

            @Tag
            struct S { v: int }

            @Tag
            fn f(): void { }

            fn main(): int { return 0; }
            """);

    // ------------------------------------------------------------------ what stays rejected

    [Fact]
    public void An_unknown_name_is_an_unknown_type() =>
        AssertReports("LYR-SEM0011", "unknown type 'Nope'", Markers + """

            @Nope
            fn f(): void { }

            fn main(): int { return 0; }
            """);

    [Fact]
    public void A_struct_without_the_marker_is_rejected_with_the_declare_hint() =>
        AssertReports("LYR-SEM0065", ":: [OnFunction]", Markers + """

            struct Plain { v: int }

            @Plain
            fn f(): void { }

            fn main(): int { return 0; }
            """);

    [Fact]
    public void The_wrong_marker_for_the_target_is_rejected() =>
        // 'System' declares OnFunction; on a struct the message asks for OnType.
        AssertReports("LYR-SEM0065", ":: [OnType]", Markers + """

            @System
            struct S { v: int }

            fn main(): int { return 0; }
            """);

    [Fact]
    public void A_module_attribute_needs_OnModule() =>
        AssertReports("LYR-SEM0065", ":: [OnModule]",
            "@Component\nmodule m;\n" + Markers + "\nfn main(): int { return 0; }");

    [Fact]
    public void A_class_cannot_be_an_attribute() =>
        AssertReports("LYR-SEM0065", "an attribute is a struct", Markers + """

            class NotAnAttr { }

            @NotAnAttr
            fn f(): void { }

            fn main(): int { return 0; }
            """);

    [Fact]
    public void An_interface_cannot_be_an_attribute() =>
        AssertReports("LYR-SEM0065", "an attribute is a struct", Markers + """

            @OnFunction
            fn f(): void { }

            fn main(): int { return 0; }
            """);

    [Fact]
    public void A_generic_attribute_type_is_rejected() =>
        AssertReports("LYR-SEM0065", "generic", """
            import std.core { OnFunction };

            struct Tag<T> :: [OnFunction] { }

            @Tag
            fn f(): void { }

            fn main(): int { return 0; }
            """);

    [Fact]
    public void A_generic_target_is_rejected() =>
        AssertReports("LYR-SEM0067", "generic declaration", Markers + """

            @Component
            struct Pool<T> { v: T }

            fn main(): int { return 0; }
            """);

    [Fact]
    public void The_same_attribute_twice_is_rejected() =>
        AssertReports("LYR-SEM0068", "twice", Markers + """

            @Component
            @Component
            struct S { v: int }

            fn main(): int { return 0; }
            """);

    [Fact]
    public void The_same_field_twice_is_rejected() =>
        AssertReports("LYR-SEM0068", "sets 'order' twice", Markers + """

            @System { order = 1, order = 2 }
            fn f(): void { }

            fn main(): int { return 0; }
            """);

    [Fact]
    public void A_computed_argument_is_rejected() =>
        AssertReports("LYR-SEM0066", "must be a literal", Markers + """

            @System { order = 1 + 2 }
            fn f(): void { }

            fn main(): int { return 0; }
            """);

    [Fact]
    public void Null_is_not_an_attribute_argument() =>
        AssertReports("LYR-SEM0066", "must be a literal", """
            import std.core { OnFunction };

            struct Tag :: [OnFunction] { label: ?string }

            @Tag { label = null }
            fn f(): void { }

            fn main(): int { return 0; }
            """);

    [Fact]
    public void A_wrongly_typed_argument_is_an_ordinary_assignability_error() =>
        AssertReports("LYR-SEM0001", "", Markers + """

            @System { order = "nope" }
            fn f(): void { }

            fn main(): int { return 0; }
            """);

    [Fact]
    public void An_unknown_field_is_an_ordinary_no_such_field_error() =>
        AssertReports("LYR-SEM0015", "has no field 'nope'", Markers + """

            @System { nope = 1 }
            fn f(): void { }

            fn main(): int { return 0; }
            """);

    [Fact]
    public void A_field_without_value_and_without_default_is_rejected() =>
        // 'Plugin' has 'name' and 'api', neither with a default; '@Plugin' alone leaves both empty.
        AssertReports("LYR-SEM0069", "without a value",
            "@Plugin\nmodule m;\n" + Markers + "\nfn main(): int { return 0; }");

    [Fact]
    public void A_non_literal_default_cannot_fill_the_row() =>
        AssertReports("LYR-SEM0069", "not a literal", """
            import std.core { OnFunction };

            fn compute(): int { return 3; }

            struct Tag :: [OnFunction] { order: int = compute() }

            @Tag
            fn f(): void { }

            fn main(): int { return 0; }
            """);

    /// <summary>The attribute is a reference to its type: the side table records it, which is what
    /// go-to-definition and find-references read.</summary>
    [Fact]
    public void The_attribute_use_survives_alongside_ordinary_uses_of_the_struct() =>
        // 'System' as attribute AND as ordinary value in one program: the struct stays a struct.
        AssertClean(Markers + """

            @System { order = 1 }
            fn f(): void { }

            fn main(): int {
                let s = System { order = 2 };
                return s.order;
            }
            """);
}
