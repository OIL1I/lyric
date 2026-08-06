using System.Runtime.CompilerServices;
using System.Text;
using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Sema;

/// <summary>
/// Die Invariante hinter <see cref="ErrorType"/>: er bedeutet <b>„hier wurde bereits gemeldet"</b>
/// — nicht „unbekannt", nicht „noch nicht berechnet".
///
/// <para>Wer ihn sieht, schweigt, damit ein Fehler keine Lawine von Folgefehlern auslöst. Wer ihn
/// <b>erzeugt</b>, muss deshalb vorher gemeldet haben. Bricht jemand das, ist der Effekt
/// besonders unangenehm: die Sema schweigt, und der Absturz kommt später aus dem Lowering, weit
/// weg von der Ursache.</para>
///
/// <para><b>Genau das ist in M7 dreimal passiert</b> — bei den Globals (führte zu
/// <c>LYR-SEM0057</c>), bei den Typargumenten und beim Yield-Typ eines Iterators. Dreimal
/// dieselbe Ursache heißt: die Konvention allein trägt nicht. Diese Tests machen sie
/// überprüfbar.</para>
/// </summary>
public class ErrorTypeInvariantTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    /// <summary>
    /// Prüft die Invariante an einem Programm: enthält irgendein Ausdruck einen
    /// <see cref="ErrorType"/>, muss mindestens eine Diagnose vorliegen.
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

    // ------------------------------------------------------------------ gültige Programme

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
        // Ein fehlerfreies Programm darf ueberhaupt keinen ErrorType tragen — hier faellt auf,
        // wenn irgendein Lookup still aufgibt.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "examples", example), Encoding.UTF8);
        Holds(source, example);
    }

    // ------------------------------------------------------------------ fehlerhafte Programme

    [Theory]
    // Jeder Fall trifft eine andere Stelle, an der die Sema aufgibt. Sie DUERFEN ErrorType
    // erzeugen — aber nur zusammen mit einer Meldung.
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
        // Die Gegenprobe zum Prüfer selbst: ohne sie bewiesen alle Tests darüber nur, dass die
        // Programme fehlerfrei sind — nicht, dass die Prüfung greift.
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", "fn main(): int { return unbekannt; }");
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        var types = Semantics.Analyze(comp, comp.Resolve(), de);

        // Hier gibt es ErrorType UND eine Diagnose — genau der erlaubte Fall.
        Assert.Contains(types.AllTypes, pair => pair.Value.IsError);
        Assert.True(de.HasErrors);
    }
}
