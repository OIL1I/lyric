using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Xunit;

namespace Lyric.Tests.Sema;

/// <summary>
/// Die Regeln aus <b>ADR-014</b>: ein Member ohne Marker ist Instanz-Member mit gebundenem
/// <c>this</c>, <c>static</c> heißt kein Empfänger.
///
/// <para>Vor dieser Entscheidung war <b>jede Methode zugleich statisch und instanzgebunden</b> —
/// gemessen gingen <c>P.getHp()</c> ohne Empfänger, <c>p.new()</c> auf einer Instanz und sogar
/// <c>this.hp</c> in einer als <c>P.new()</c> gerufenen Methode durch die Typprüfung. Der letzte
/// Fall hätte beim Lowering einen Feldzugriff ohne Objekt erzeugt.</para>
/// </summary>
public class StaticMemberTests
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

    private static void AssertReports(string source, string code)
    {
        var de = Check(source);
        Assert.Contains(de.Diagnostics, d => d.Code == code);
    }

    private static void AssertClean(string source)
    {
        var de = Check(source);
        Assert.False(de.HasErrors,
            "expected this to check clean, but got:\n" +
            string.Join("\n", de.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }

    // --- Die Kreuzformen ---

    [Fact]
    public void An_instance_method_called_on_the_type_is_reported() =>
        AssertReports(
            """
            class P {
                hp: int,
                fn getHp(): int { return this.hp; }
            }
            fn main(): int { return P.getHp(); }
            """, "LYR-SEM0055");

    [Fact]
    public void A_static_method_called_on_an_instance_is_reported() =>
        AssertReports(
            """
            class P {
                hp: int,
                static fn make(): P { return P { hp = 1 }; }
            }
            fn main(): int { let p = P { hp = 2 }; return p.make().hp; }
            """, "LYR-SEM0055");

    /// <summary>Der gefährlichste Fall: <c>this</c> ohne jedes Objekt. Er lief durch, weil eine
    /// Fabrik dieselbe Methode war wie eine Instanzmethode.</summary>
    [Fact]
    public void This_inside_a_static_member_is_reported() =>
        AssertReports(
            """
            class P {
                hp: int,
                static fn make(): P { return P { hp = this.hp }; }
            }
            fn main(): int { return 0; }
            """, "LYR-SEM0008");

    [Fact]
    public void A_field_read_on_the_type_is_reported() =>
        AssertReports(
            """
            class P { hp: int }
            fn main(): int { return P.hp; }
            """, "LYR-SEM0055");

    // --- Marker-Kombinationen ---

    [Fact]
    public void Static_and_mut_together_are_reported() =>
        AssertReports(
            """
            class P {
                hp: int,
                static mut fn f() { }
            }
            fn main(): int { return 0; }
            """, "LYR-SEM0054");

    /// <summary>
    /// <c>mut</c> an einer Klassen-Methode bleibt <b>erlaubt</b>. Es setzt dort nichts durch, aber
    /// <c>Doku.md</c> §10.2 führt es ausdrücklich als Lesbarkeits-Konvention, und Interfaces
    /// deklarieren <c>mut fn</c>, das implementierende Klassen erfüllen müssen.
    ///
    /// <para>Die erste Fassung von ADR-014 wollte das verbieten; der Test hält fest, warum nicht.</para>
    /// </summary>
    [Fact]
    public void Mut_on_a_class_method_stays_legal() =>
        AssertClean(
            """
            interface Damageable { mut fn hurt(n: int); }
            class P :: [Damageable] {
                hp: int,
                mut fn hurt(n: int) { this.hp -= n; }
            }
            fn main(): int { return 0; }
            """);

    // --- Positiv ---

    [Fact]
    public void Static_members_and_instance_members_coexist() =>
        AssertClean(
            """
            class P {
                hp: int,

                static let ZERO: int = 0;

                static fn new(v: int): P { return P { hp = v }; }
                fn get(): int { return this.hp; }
            }
            fn main(): int { let p = P.new(7); return p.get() + P.ZERO; }
            """);

    /// <summary>Eine <c>static let</c>-Konstante hat kein <c>this</c> — ihr Initialisierer wird
    /// ohne Empfänger geprüft.</summary>
    [Fact]
    public void This_inside_a_static_binding_is_reported() =>
        AssertReports(
            """
            class P {
                hp: int,
                static let COPY: int = this.hp;
            }
            fn main(): int { return 0; }
            """, "LYR-SEM0008");
}
