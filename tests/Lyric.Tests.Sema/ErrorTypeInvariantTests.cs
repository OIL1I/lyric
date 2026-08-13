using System.Runtime.CompilerServices;
using System.Text;
using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Sema;

/// <summary>
/// The invariant behind <see cref="ErrorType"/>: it means "a diagnostic has already been reported" —
/// not "unknown", not "not computed yet".
///
/// <para>Whoever sees it stays silent, so one error does not trigger an avalanche of follow-ups.
/// Whoever PRODUCES one must therefore have reported first. Breaking that is especially unpleasant:
/// the sema stays silent and the crash comes later out of the lowering, far from the cause.</para>
///
/// <para>That happened three times — at the globals (leading to <c>LYR-SEM0057</c>), at the type
/// arguments, and at the yield type of an iterator. Three times the same cause means the convention
/// alone does not carry. These tests make it checkable.</para>
/// </summary>
public class ErrorTypeInvariantTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    /// <summary>
    /// Checks the invariant on a program: if any expression contains an <see cref="ErrorType"/>, at least
    /// one diagnostic has to be present.
    /// </summary>
    private static void Holds(string source, string label)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de)
        {
            ModuleLoader = StdlibLoader.ForRoot(Path.Combine(RepoRoot(), "stdlib"), sm, de),
        };
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        var types = Semantics.Analyze(comp, comp.Resolve(), de);

        var poisoned = types.AllTypes.Where(pair => pair.Value.IsError).ToArray();

        if (poisoned.Length > 0 && !de.HasErrors)
            Assert.Fail(
                $"{label}: {poisoned.Length} expression(s) carry ErrorType, but nothing was "
                + "reported. ErrorType means 'already diagnosed' — an unreported one makes the "
                + "sema silent and the lowering crash later, far from the cause. "
                + $"First at {poisoned[0].Key.Span}.");
    }

    // ------------------------------------------------------------------ valid programs

    [Theory]
    [InlineData("hello.lyr")]
    [InlineData("arith.lyr")]
    [InlineData("objects.lyr")]
    [InlineData("arrays.lyr")]
    [InlineData("optionals.lyr")]
    [InlineData("enums.lyr")]
    [InlineData("interfaces.lyr")]
    [InlineData("vectors.lyr")]
    [InlineData("constants.lyr")]
    [InlineData("closures.lyr")]
    [InlineData("generator.lyr")]
    [InlineData("fizzbuzz.lyr")]
    public void No_valid_example_produces_an_unreported_error_type(string example)
    {
        // An error-free program must carry no ErrorType at all; this is where a lookup that silently gives
        // up shows.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "examples", example), Encoding.UTF8);
        Holds(source, example);
    }

    // ------------------------------------------------------------------ fehlerhafte Programme

    [Theory]
    // Every case hits a different place where the sema gives up. They MAY produce an ErrorType, but only
    // together with a message.
    [InlineData("fn main(): int { return unbekannt; }", "unbekannter Bezeichner")]
    [InlineData("fn main(): int { let x: Fehlt = 1; return 0; }", "unbekannter Typ")]
    [InlineData("fn main(): int { return \"a\" - 1; }", "unpassender Operator")]
    [InlineData("fn main(): int { return nichts(); }", "unbekannte Funktion")]
    [InlineData("fn main(): int { let x = 1; return x.feld; }", "Member auf Skalar")]
    [InlineData("fn main(): int { let a: ?int = null; return a; }", "fehlendes Narrowing")]
    [InlineData("fn f(): int { }\nfn main(): int { return f(); }", "fehlendes return")]
    [InlineData("let a = b + 1;\nlet b = 2;\nfn main(): int { return a; }", "Global vor Init")]
    [InlineData("fn id<T>(x: T): T { return x; }\nfn main(): int { return id(); }", "fehlendes Argument")]
    [InlineData("fn main(): int { for (x in 42) { } return 0; }", "nicht iterierbar")]
    public void An_error_type_never_appears_without_a_diagnostic(string source, string label) =>
        Holds(source, label);

    [Fact]
    public void The_check_would_actually_catch_a_violation()
    {
        // The counter-check to the checker itself: without it every test above would only prove that the
        // programs are error-free, not that the check applies.
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", "fn main(): int { return unbekannt; }");
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        var types = Semantics.Analyze(comp, comp.Resolve(), de);

        // Here there is an ErrorType AND a diagnostic: exactly the allowed case.
        Assert.Contains(types.AllTypes, pair => pair.Value.IsError);
        Assert.True(de.HasErrors);
    }
}
