using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Xunit;

namespace Lyric.Tests.Sema;

/// <summary>
/// Konstanten werden in <b>Deklarationsreihenfolge</b> initialisiert; ein Initialisierer darf nur
/// lesen, was vor ihm steht (<c>LYR-SEM0057</c>).
///
/// <para>Diese Prüfung existiert wegen der überladenen <c>ErrorType</c>-Invariante: ohne sie lieferte
/// der Lookup eines noch nicht berechneten Globals still <see cref="LyrType.Error"/> — „schon
/// gemeldet" —, die Sema schwieg, und das Lowering stürzte später über einen <c>&lt;error&gt;</c>-Typ
/// ab. Der Fehler muss dort entstehen, wo er sichtbar ist.</para>
/// </summary>
public class GlobalOrderTests
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

    private static void AssertReports(string source, string code) =>
        Assert.Contains(Check(source).Diagnostics, d => d.Code == code);

    private static void AssertClean(string source)
    {
        var de = Check(source);
        Assert.False(de.HasErrors, string.Join("\n", de.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }

    [Fact]
    public void An_initializer_may_not_read_a_later_constant() =>
        AssertReports("""
            let a = b + 1;
            let b = 2;
            fn main(): int { return a; }
            """, "LYR-SEM0057");

    [Fact]
    public void An_initializer_may_read_an_earlier_constant() =>
        AssertClean("""
            let a = 2;
            let b = a + 1;
            fn main(): int { return b; }
            """);

    [Fact]
    public void A_static_let_may_not_read_a_later_one() =>
        AssertReports("""
            class C {
                static let A: int = C.B + 1;
                static let B: int = 2;
            }
            fn main(): int { return C.A; }
            """, "LYR-SEM0057");

    [Fact]
    public void A_static_let_may_read_an_earlier_one() =>
        AssertClean("""
            class C {
                static let A: int = 2;
                static let B: int = C.A + 1;
            }
            fn main(): int { return C.B; }
            """);

    [Fact]
    public void A_constant_may_not_read_itself() =>
        AssertReports("""
            let a = a + 1;
            fn main(): int { return a; }
            """, "LYR-SEM0057");

    [Fact]
    public void A_function_body_may_read_any_constant() =>
        // Nur INNERHALB eines Initialisierers gilt die Reihenfolge. Wenn ein Rumpf läuft, ist die
        // Init-Phase längst vorbei — dort wäre die Einschränkung reine Schikane.
        AssertClean("""
            fn f(): int { return k; }
            let k = 4;
            fn main(): int { return f(); }
            """);
}
