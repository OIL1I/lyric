using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Sema;

/// <summary>
/// Where <c>?.</c> with a call STOPS.
///
/// <para>A LINK CAN BE A METHOD OR HOLD A FUNCTION VALUE, and only in the first case is there one
/// question. For <c>f: ?fn() -&gt; int</c> there are two — whether the receiver is there and whether the
/// field is set — and a single <c>?</c>. Unwrapping here answers the second silently with yes, and that
/// is a call on null.</para>
///
/// <para>The language has no <c>?()</c>. The form therefore does not exist, and the message says so
/// together with the way out, rather than the old statement about an intermediate type nobody
/// hat.</para>
/// </summary>
public class OptionalCallLimitTests
{
    private static IReadOnlyList<Diagnostic> Check(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        Semantics.Analyze(comp, comp.Resolve(), de);
        return de.Diagnostics;
    }

    /// <summary>The dangerous case: without the message the call would run on a <c>null</c>
    /// stattfinden.</summary>
    [Fact]
    public void A_chain_onto_a_nullable_function_field_is_rejected()
    {
        var reported = Assert.Single(Check("""
            class Box { f: ?fn() -> int, }
            fn main(): int {
                let b: ?Box = Box { f = null };
                return b?.f() ?? -1;
            }
            """));

        Assert.Equal("LYR-SEM0062", reported.Code);
        Assert.Contains("holds a function value", reported.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the harmless one beside it. It is rejected AS WELL, because the distinction hangs on the
    /// binding rather than on nullability — an indirect call through a <c>?.</c> link is no form of the
    /// language. The message names the way out, and it is one line.
    /// </summary>
    [Fact]
    public void A_chain_onto_a_plain_function_field_is_rejected_too() =>
        Assert.Contains(Check("""
            fn eins(): int { return 1; }
            class Box { f: fn() -> int, }
            fn main(): int {
                let b: ?Box = Box { f = eins };
                return b?.f() ?? -1;
            }
            """), d => d.Code == "LYR-SEM0062");

    /// <summary>The way out named by the message has to work; otherwise it is a
    /// Hinweis ins Leere.</summary>
    [Fact]
    public void Reading_the_field_first_works()
    {
        var diagnostics = Check("""
            fn eins(): int { return 1; }
            class Box { f: fn() -> int, }
            fn main(): int {
                let b: ?Box = Box { f = eins };
                let g = b?.f;
                return if (g != null) g() else -1;
            }
            """);

        Assert.Empty(diagnostics);
    }

    /// <summary>The counter-check: a METHOD stays callable. Without it a condition that is too strict
    /// would be green.</summary>
    [Fact]
    public void A_method_is_still_callable_through_a_chain() =>
        Assert.Empty(Check("""
            class Box { fn get(): int { return 1; } }
            fn main(): int {
                let b: ?Box = Box { };
                return b?.get() ?? -1;
            }
            """));
}
