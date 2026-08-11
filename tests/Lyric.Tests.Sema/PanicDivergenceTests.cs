using System.Runtime.CompilerServices;
using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Sema;

/// <summary>
/// <c>panic</c> divergiert (§9, Rückgabetyp <c>never</c>) — und die Flussanalyse muss das sehen,
/// egal über welchen Namen es erreicht wurde.
///
/// <para><b>Der Anlass ist ein Befund aus M8b/S9</b>, und die gemeldete Ursache war die falsche.
/// Notiert war „<c>never</c> ist für die Flussanalyse unsichtbar"; gemessen war es das <b>nicht</b>
/// — <c>Flow.AlwaysReturns</c> behandelt einen divergierenden <c>ExprStmt</c> seit jeher. Die
/// eigentliche Ursache: <c>panic</c> gibt es <b>zweimal</b>. Einmal als Builtin im Wurzel-Scope,
/// damit es ohne Import aufrufbar ist, und einmal als native Deklaration in <c>std.core</c>, die
/// ihm Signatur und Native-Bindung gibt. Nur das Builtin trug <c>never</c>; wer
/// <c>import std.core { panic }</c> schrieb, bekam ein <c>void</c>.</para>
///
/// <para>Deshalb steht jeder Fall hier <b>doppelt</b> — einmal über den Builtin-Namen, einmal über
/// den importierten. Genau diese Verdopplung hat den Fehler sichtbar gemacht, und ohne sie wäre er
/// beim nächsten Mal wieder unsichtbar.</para>
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

    /// <summary>Ohne Import — das eingebaute <c>panic</c>. Mit Import — die Deklaration aus
    /// <c>std.core</c>. Dieselbe Funktion, und ab jetzt dieselbe Antwort.</summary>
    private const string Builtin = "";
    private const string Imported = "import std.core { panic };\n";

    // ------------------------------------------------------------------ Rückgabe-Abdeckung

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
    /// Ohne diesen Test bliebe alles oben auch dann grün, wenn die Rückgabe-Abdeckung ab jetzt
    /// jede Funktion durchwinkte. Eine gewöhnliche <c>void</c>-Funktion am Ende deckt kein
    /// <c>return</c> ab — sie kommt zurück.
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
