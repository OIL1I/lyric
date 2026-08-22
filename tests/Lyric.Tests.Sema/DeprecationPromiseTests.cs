using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Xunit;

namespace Lyric.Tests.Sema;

/// <summary>
/// The <c>until</c> half of <c>@Deprecated</c> (LYR-SEM0081): a version this is kept until, and a
/// build that has reached it stops.
///
/// <para>What it exists for: a deprecation is two promises — "use something else" and "this goes
/// away" — and the second one lived in a release note, which is another way of saying it lived in
/// somebody's memory. A form kept past its date teaches everyone that the dates mean nothing.
/// </para>
///
/// <para>The comparison is against the version the TREE claims, so the failure arrives while the
/// release that has to do the removing is being prepared — which is why the cases below pass a
/// toolchain version rather than reading the real one: what they check is the rule, not what
/// today's version happens to be.</para>
/// </summary>
public class DeprecationPromiseTests
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

    /// <summary>One promise against one toolchain version, without a compilation around it.
    /// </summary>
    private static Diagnostic? Promise(string until, string toolchain)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", "x");
        var de = new DiagnosticEngine(sm);
        DeprecationPromise.Check(until, new Span(id, 0, 1), de, toolchain);

        // A Diagnostic is a struct, so FirstOrDefault would answer with a blank one rather than
        // with nothing -- and "no diagnostic" is exactly what half these cases assert.
        var hits = de.Diagnostics.Where(d => d.Code == "LYR-SEM0081").ToList();
        return hits.Count == 0 ? null : hits[0];
    }

    private const string Import = "import std.core { Deprecated };\n\n";

    // ------------------------------------------------------------------ the rule

    [Theory]
    [InlineData("3.5", "2.12.0")]
    [InlineData("3.5", "3.4.9")]
    [InlineData("3.5.1", "3.5.0")]
    [InlineData("10.0", "9.99.99")]
    public void A_promise_in_the_future_says_nothing(string until, string toolchain) =>
        Assert.Null(Promise(until, toolchain));

    [Theory]
    [InlineData("3.5", "3.5.0")]
    [InlineData("3.5", "3.5.1")]
    [InlineData("3.5", "4.0.0")]
    [InlineData("2.0", "2.12.0")]
    public void A_promise_that_has_come_due_is_an_error(string until, string toolchain)
    {
        var diagnostic = Promise(until, toolchain);
        Assert.NotNull(diagnostic);
        Assert.Equal(Severity.Error, diagnostic.Value.Severity);
        Assert.Contains(until, diagnostic.Value.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_named_version_is_the_one_that_removes_it()
    {
        // The edge the whole feature turns on, stated as a case rather than as prose: "kept until
        // 3.5" fails AT 3.5, not one release later. 3.4.x still builds.
        Assert.Null(Promise("3.5", "3.4.99"));
        Assert.NotNull(Promise("3.5", "3.5.0"));
    }

    [Theory]
    [InlineData("soon")]
    [InlineData("")]
    [InlineData("3")]
    [InlineData("3.5.1.2")]
    [InlineData("3.x")]
    [InlineData("v3.5")]
    [InlineData("-1.0")]
    public void A_version_that_cannot_be_read_is_refused(string until)
    {
        // Except the empty one, which is the ordinary case: no promise was made.
        if (until.Length == 0)
        {
            Assert.Null(Promise(until, "2.12.0"));
            return;
        }

        var diagnostic = Promise(until, "2.12.0");
        Assert.NotNull(diagnostic);
        Assert.Equal(Severity.Error, diagnostic.Value.Severity);
    }

    // ------------------------------------------------------------------ through the attribute

    [Fact]
    public void An_expired_promise_stops_the_build_at_the_declaration()
    {
        var de = Check(Import
            + "@Deprecated { message = \"gone\", until = \"1.0\" }\npub fn old(): int {\n    return 1;\n}\n\n"
            + "fn main(): int {\n    return 0;\n}\n");

        Assert.True(de.HasErrors);
        Assert.Single(de.Diagnostics, d => d.Code == "LYR-SEM0081");
    }

    [Fact]
    public void It_fires_even_though_nothing_uses_the_declaration()
    {
        // The point of checking the DECLARATION: a form kept past its date is wrong whether or
        // not anyone still calls it, and the use-site warning would never fire for dead code.
        var de = Check(Import
            + "@Deprecated { until = \"1.0\" }\npub fn nobodyCallsThis(): int {\n    return 1;\n}\n\n"
            + "fn main(): int {\n    return 0;\n}\n");

        Assert.Single(de.Diagnostics, d => d.Code == "LYR-SEM0081");
        Assert.DoesNotContain(de.Diagnostics, d => d.Code == "LYR-SEM0076");
    }

    [Fact]
    public void A_promise_still_in_the_future_leaves_the_ordinary_warning_alone()
    {
        var de = Check(Import
            + "@Deprecated { message = \"use renew\", until = \"99.0\" }\npub fn old(): int {\n    return 1;\n}\n\n"
            + "fn main(): int {\n    return old();\n}\n");

        Assert.False(de.HasErrors);
        var warning = Assert.Single(de.Diagnostics, d => d.Code == "LYR-SEM0076");
        Assert.Equal(Severity.Warning, warning.Severity);
        Assert.Contains("use renew", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_deprecation_without_a_promise_is_unchanged()
    {
        var de = Check(Import
            + "@Deprecated { message = \"use renew\" }\npub fn old(): int {\n    return 1;\n}\n\n"
            + "fn main(): int {\n    return old();\n}\n");

        Assert.False(de.HasErrors);
        Assert.DoesNotContain(de.Diagnostics, d => d.Code == "LYR-SEM0081");
    }

    [Fact]
    public void The_standard_library_keeps_no_promise_that_has_come_due()
    {
        // The ratchet aimed at this repository rather than at a user: every '@Deprecated' the
        // shipped stdlib carries is compiled here on every build, so a date that has passed stops
        // the toolchain's own build. That is the mechanism, and this is the test that says so.
        var de = Check("fn main(): int {\n    return 0;\n}\n");
        Assert.DoesNotContain(de.Diagnostics, d => d.Code == "LYR-SEM0081");
    }
}
