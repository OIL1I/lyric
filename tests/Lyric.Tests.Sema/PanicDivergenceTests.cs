using System.Runtime.CompilerServices;
using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Sema;

/// <summary>
/// <c>panic</c> diverges, having the return type <c>never</c>, and the flow analysis has to see that no
/// matter which name it was reached through.
///
/// <para>The reported cause was the wrong one. What was noted was "<c>never</c> is invisible to the flow
/// analysis"; measured, it was NOT — <c>Flow.AlwaysReturns</c> has always handled a diverging
/// <c>ExprStmt</c>. The actual cause: <c>panic</c> exists TWICE. Once as a builtin in the root scope, so
/// it is callable without an import, and once as a native declaration in <c>std.core</c>, which gives it
/// its signature and native binding. Only the builtin carried <c>never</c>; whoever wrote
/// <c>import std.core { panic }</c> got a <c>void</c>.</para>
///
/// <para>Every case therefore stands here TWICE — once through the builtin name, once through the
/// imported one. Exactly that duplication made the fault visible, and without it it would be invisible
/// next time.</para>
/// </summary>
public class PanicDivergenceTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
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

    private static void Compiles(string source)
    {
        var de = Check(source);
        Assert.False(de.HasErrors,
            string.Join("\n", de.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }

    /// <summary>Without an import: the built-in <c>panic</c>. With an import: the declaration from
    /// <c>std.core</c>. The same function, and from now on the same answer.</summary>
    private const string Builtin = "";
    private const string Imported = "import std.core { panic };\n";

    // ------------------------------------------------------------------ return coverage

    [Theory]
    [InlineData(Builtin)]
    [InlineData(Imported)]
    public void Panic_as_the_last_statement_covers_the_missing_return(string head) =>
        Compiles(head + """
            fn f(o: ?int, m: string): int {
                if (o != null) { return o; }
                panic(m);
            }
            """);

    // ------------------------------------------------------------------ Narrowing

    [Theory]
    [InlineData(Builtin)]
    [InlineData(Imported)]
    public void Panic_in_a_branch_narrows_what_follows(string head) =>
        Compiles(head + """
            fn f(o: ?int, m: string): int {
                if (o == null) { panic(m); }
                return o;
            }
            """);

    // ------------------------------------------------------------------ Gegenprobe

    /// <summary>
    /// Without this test everything above would stay green even if the return coverage waved every
    /// function through. An ordinary <c>void</c> function at the end covers no <c>return</c>: it comes
    /// back.
    /// </summary>
    [Fact]
    public void An_ordinary_void_call_does_not_cover_a_missing_return()
    {
        var de = Check("""
            import std.io.console { println };
            fn f(o: ?int): int {
                if (o != null) { return o; }
                println("nichts");
            }
            """);

        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0017");
    }
}
