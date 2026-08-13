using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Sema;

/// <summary>
/// A field default is an expression and is checked like any other.
///
/// <para>The sema never visited it. A wrongly typed default was therefore no error, and as soon as an
/// initializer omitted the field it became a compiler CRASH in the lowering: there the default is
/// evaluated at the construction site, the side table did not know its type, so <c>ErrorType</c>, so
/// "ir: type not lowerable: &lt;error&gt;". And because no diagnostic was reported, <c>lyric check</c>
/// said "ok" beforehand.</para>
///
/// <para>Noticed while building <c>console.lines()</c>, whose <c>LineIterator { }</c> does exactly that.
/// The runtime side lives in <c>Lyric.Tests.Vm.StructTests</c>.</para>
/// </summary>
public class DefaultFieldTests
{
    private static DiagnosticEngine Check(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        Semantics.Analyze(comp, comp.Resolve(), de);
        return de;
    }

    private static void Reports(string code, string source) =>
        Assert.Contains(Check(source).Diagnostics, d => d.Code == code);

    [Fact]
    public void A_default_of_the_wrong_type_is_a_diagnostic_not_a_crash() =>
        Reports("LYR-SEM0001", """
            class K { a: int = "nope", }
            fn main(): int { return 0; }
            """);

    [Fact]
    public void A_struct_default_is_checked_as_well() =>
        // Both kinds of declaration run through CheckMethods; without this test it would stay unchecked
        // whether the branch for 'struct' is reached at all.
        Reports("LYR-SEM0001", """
            struct V { n: bool = 1, }
            fn main(): int { return 0; }
            """);

    [Fact]
    public void An_unknown_name_in_a_default_is_reported() =>
        Reports("LYR-SEM0002", """
            class K { a: int = gibtsNicht, }
            fn main(): int { return 0; }
            """);

    [Fact]
    public void A_correct_default_passes()
    {
        var de = Check("""
            class K { a: int = 5, b: string = "x", }
            fn main(): int { return 0; }
            """);

        Assert.False(de.HasErrors,
            string.Join("\n", de.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }
}

/// <summary>
/// A type parameter the inference cannot bind is reported by the SEMA.
///
/// <para>It used to become an <c>ErrorType</c> silently, and only the lowering tripped over it.
/// <c>lyric check</c> said "ok", <c>lyric build</c> did not — the same rift between sema and backend
/// <c>AgreementTests</c> was built against, only with a diagnostic at the end rather than a crash.</para>
/// </summary>
public class InferenceDiagnosticTests
{
    private static string Diagnostics(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        Semantics.Analyze(comp, comp.Resolve(), de);

        var writer = new StringWriter();
        de.RenderText(writer);
        return writer.ToString();
    }

    [Fact]
    public void An_unbindable_type_parameter_is_reported_by_the_sema()
    {
        // T occurs in no parameter, and the conformance unification cannot save that either.
        var diagnostics = Diagnostics("""
            pub fn leer<T>(n: int): int { return n; }
            fn main(): int { return leer(3); }
            """);

        Assert.Contains("LYR-SEM0060", diagnostics);
        Assert.Contains("write it explicitly", diagnostics);
    }

    [Fact]
    public void An_explicit_type_argument_silences_it() =>
        Assert.Equal("", Diagnostics("""
            pub fn leer<T>(n: int): int { return n; }
            fn main(): int { return leer<int>(3); }
            """));

    [Fact]
    public void A_broken_argument_does_not_add_inference_noise()
    {
        // When an argument is faulty itself, the cause is reported. A second line about a type argument
        // would be follow-up noise — the most common way diagnostics become unreadable.
        var diagnostics = Diagnostics("""
            pub fn id<T>(v: T): T { return v; }
            fn main(): int { return id(gibtsNicht); }
            """);

        Assert.Contains("LYR-SEM0002", diagnostics);
        Assert.DoesNotContain("LYR-SEM0060", diagnostics);
    }
}

/// <summary>
/// An error INSIDE a type counts as reported — the poison rule, one level deeper.
///
/// <para>It used to apply only when the type itself was an <c>ErrorType</c>. A
/// <c>fn(int) -&gt; &lt;error&gt;</c> — intact outside, broken inside — passed and produced follow-up
/// messages that buried the actual cause.</para>
///
/// <para>Noticed on a block lambda without a return type annotation: THREE diagnostics for one error,
/// and the only one with a usable hint (<c>LYR-SEM0046</c>, "add a return type annotation") stood at
/// the bottom.</para>
/// </summary>
public class DiagnosticNoiseTests
{
    private static string[] Codes(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        Semantics.Analyze(comp, comp.Resolve(), de);
        return de.Diagnostics.Select(d => d.Code).ToArray();
    }

    private const string BlockLambda = """
        pub fn anwenden<T, U>(v: T, f: fn(T) -> U): U { let g = f; return g(v); }
        fn main(): int { let b = anwenden(3, (n: int) => { return n * 2; }); return 0; }
        """;

    [Fact]
    public void A_block_lambda_without_annotation_reports_the_helpful_code() =>
        Assert.Contains("LYR-SEM0046", Codes(BlockLambda));

    [Fact]
    public void It_does_not_also_complain_about_the_type_argument() =>
        // LYR-SEM0060 would be noise here: the cause stands in SEM0046, with instructions.
        Assert.DoesNotContain("LYR-SEM0060", Codes(BlockLambda));

    [Fact]
    public void It_does_not_also_complain_about_assignability() =>
        // "cannot assign 'fn(int) -> <error>' to 'fn(int) -> U'" says nothing to the reader
        Assert.DoesNotContain("LYR-SEM0001", Codes(BlockLambda));

    [Fact]
    public void The_annotated_form_compiles_cleanly() =>
        // The counter-check: what the message suggests has to work, or the hint is wrong, and that is
        // worse than no hint.
        Assert.Empty(Codes("""
            pub fn anwenden<T, U>(v: T, f: fn(T) -> U): U { let g = f; return g(v); }
            fn main(): int { return anwenden(3, (n: int): int => { return n * 2; }); }
            """));
}
