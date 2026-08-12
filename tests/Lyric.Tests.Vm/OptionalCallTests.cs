using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Lyric.Vm;

namespace Lyric.Tests.Vm;

/// <summary>
/// Optional-Chaining mit <b>Aufruf</b>: <c>b?.get()</c> (Sprache.md §7).
///
/// <para><b>Bis 2026-08-11 war das <c>LYR-SEM0013: '?fn() -&gt; int' is not callable</c></b> — eine
/// Auskunft über einen Zwischentyp, den niemand hingeschrieben hat. Feldzugriff (<c>b?.v</c>) ging
/// seit P2b; der Aufruf nicht, und der Ausweg <c>if (b != null) { b.get() }</c> ist dreimal so
/// lang.</para>
///
/// <para><b>Der Aufruf läuft durch dieselbe Auflösung wie jeder andere</b>, nur mit einem bereits
/// ausgepackten Empfänger. Ein eigener Pfad hätte Virtual-Dispatch, Natives, Extensions und
/// Generics ein zweites Mal beantworten müssen — die Sorte Zweitkopie, die in diesem Projekt
/// neunmal auseinandergelaufen ist.</para>
/// </summary>
public class OptionalCallTests
{
    private static long Run(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        var binding = comp.Resolve();
        var types = Semantics.Analyze(comp, binding, de);

        var diagnostics = new StringWriter();
        de.RenderText(diagnostics);
        Assert.False(de.HasErrors, "source did not compile: " + diagnostics);

        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true);
        Assert.NotNull(ir);
        return Interpreter.Run(BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir!)),
            NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null)).AsI64;
    }

    private const string Box = """
        class Box {
            v: int = 7,
            fn get(): int { return this.v; }
            fn plus(n: int): int { return this.v + n; }
            fn leer(): ?int { return null; }
        }

        """;

    [Fact]
    public void A_call_on_a_present_receiver_returns_its_value() =>
        Assert.Equal(7, Run(Box + """
            fn main(): int {
                let b: ?Box = Box { };
                return b?.get() ?? -1;
            }
            """));

    [Fact]
    public void A_call_on_an_absent_receiver_yields_none() =>
        Assert.Equal(-1, Run(Box + """
            fn main(): int {
                let b: ?Box = null;
                return b?.get() ?? -1;
            }
            """));

    [Fact]
    public void Arguments_are_passed_through() =>
        Assert.Equal(10, Run(Box + """
            fn main(): int {
                let b: ?Box = Box { };
                return b?.plus(3) ?? -1;
            }
            """));

    /// <summary>
    /// Ein leerer Empfänger ruft nicht — und wertet deshalb auch die Argumente nicht aus. Der Test
    /// misst das mit einem Seiteneffekt; ohne ihn bliebe er grün, wenn die Argumente vor der
    /// Prüfung berechnet würden.
    /// </summary>
    [Fact]
    public void An_absent_receiver_does_not_evaluate_the_arguments() =>
        Assert.Equal(0, Run(Box + """
            class Zaehler { stand: int = 0 }
            let z = Zaehler { };

            fn mitzaehlen(): int { z.stand = z.stand + 1; return 1; }

            fn main(): int {
                let b: ?Box = null;
                let ignoriert = b?.plus(mitzaehlen()) ?? -1;
                return z.stand;
            }
            """));

    /// <summary>
    /// Liefert die Methode selbst ein Optional, bleibt es bei <b>einer</b> Ebene — Optionals
    /// verschachteln nicht (§4). Ohne die Kollabierung wäre das ein <c>??int</c>, und das Lowering
    /// lehnt es als <c>LYR-IR0001</c> ab.
    /// </summary>
    [Fact]
    public void A_method_returning_an_optional_does_not_nest() =>
        Assert.Equal(-1, Run(Box + """
            fn main(): int {
                let b: ?Box = Box { };
                return b?.leer() ?? -1;
            }
            """));

    /// <summary>Und dasselbe mit leerem Empfänger — beide Wege müssen bei derselben Ebene
    /// landen.</summary>
    [Fact]
    public void A_method_returning_an_optional_on_an_absent_receiver() =>
        Assert.Equal(-1, Run(Box + """
            fn main(): int {
                let b: ?Box = null;
                return b?.leer() ?? -1;
            }
            """));

    /// <summary>
    /// Die Gegenprobe: der gewöhnliche Aufruf ohne <c>?.</c> bleibt gewöhnlich. Das ist die Form,
    /// die in jedem Programm steht, und sie läuft seit dem Fix durch dieselbe Funktion.
    /// </summary>
    [Fact]
    public void An_ordinary_method_call_still_works() =>
        Assert.Equal(7, Run(Box + """
            fn main(): int {
                let b = Box { };
                return b.get();
            }
            """));

    /// <summary>Und der Feldzugriff über <c>?.</c>, den es seit P2b gibt.</summary>
    [Fact]
    public void Optional_field_access_still_works() =>
        Assert.Equal(7, Run(Box + """
            fn main(): int {
                let b: ?Box = Box { };
                return b?.v ?? -1;
            }
            """));

    /// <summary>
    /// Ein Feld, das selbst optional ist. Bis 2026-08-11 machte die Sema daraus ein
    /// <c>??int</c>, und der Fehler kam eine Ebene zu spät als „cannot assign '?int' to 'int'".
    /// </summary>
    [Fact]
    public void Optional_field_access_onto_an_optional_field_collapses() =>
        Assert.Equal(5, Run("""
            class B { w: ?int = 5, }
            fn main(): int {
                let b: ?B = B { };
                return b?.w ?? -1;
            }
            """));

    // ------------------------------------------------------- die anderen Empfaengerarten
    //
    // Sie stehen hier, weil ein erster Fix sie ALLE gebrochen haette: er hing den ausgepackten
    // Empfaenger an einen Sonderfall im Callee-'switch', und der stand vor der Generics- und der
    // Interface-Erkennung. 'Box<int>' wurde damit als „external or bodiless" gemeldet — eine
    // Diagnose auf die falsche Ursache, und zwar eine, die kein Test bemerkt haette.

    /// <summary>Dynamischer Dispatch über einen ausgepackten Empfänger.</summary>
    [Fact]
    public void A_call_on_an_interface_value_dispatches_virtually() =>
        Assert.Equal(3, Run("""
            interface Zeigbar { fn zeig(): int; }
            class A :: [Zeigbar] { fn zeig(): int { return 3; } }

            fn main(): int {
                let z: ?Zeigbar = A { };
                return z?.zeig() ?? -1;
            }
            """));

    /// <summary>Eine generische Instanz: die Methode gehört <c>Box&lt;int&gt;</c> und nicht
    /// <c>Box</c>, ihr Rückgabetyp ist erst dort ein <c>int</c>.</summary>
    [Fact]
    public void A_call_on_a_generic_instance_uses_the_instance_method() =>
        Assert.Equal(4, Run("""
            class Box<T> { v: T, fn get(): T { return this.v; } }

            fn main(): int {
                let b: ?Box<int> = Box<int> { v = 4 };
                return b?.get() ?? -1;
            }
            """));

    /// <summary>Ein Typ-Parameter mit Constraint — der dritte Dispatch-Weg (ADR-024).</summary>
    [Fact]
    public void A_call_on_a_constrained_type_parameter_works() =>
        Assert.Equal(3, Run("""
            interface Zeigbar { fn zeig(): int; }
            class A :: [Zeigbar] { fn zeig(): int { return 3; } }

            fn nimm<T :: [Zeigbar]>(x: ?T): int { return x?.zeig() ?? -1; }
            fn main(): int { let a: ?A = A { }; return nimm(a); }
            """));

    /// <summary>Eine Methode aus einem <c>extend</c>-Block (§3.6) — der vierte Weg.</summary>
    [Fact]
    public void A_call_on_an_extension_method_works() =>
        Assert.Equal(9, Run("""
            class Leer { }
            extend Leer { fn neun(): int { return 9; } }

            fn main(): int {
                let l: ?Leer = Leer { };
                return l?.neun() ?? -1;
            }
            """));

    /// <summary>
    /// Zwei Ketten ineinander. Sie tragen verschiedene AST-Knoten und dürfen sich deshalb nicht
    /// gegenseitig den ausgepackten Empfänger überschreiben — die Zusicherung, an der eine
    /// globale „aktuelle Kette" gescheitert wäre.
    /// </summary>
    [Fact]
    public void Nested_chains_do_not_interfere() =>
        Assert.Equal(14, Run(Box + """
            fn main(): int {
                let a: ?Box = Box { };
                let b: ?Box = Box { };
                return a?.plus(b?.get() ?? 0) ?? -1;
            }
            """));

    /// <summary>Und eine Kette an einer Kette: <c>a?.b?.c()</c>.</summary>
    [Fact]
    public void A_chain_on_a_chain_works() =>
        Assert.Equal(7, Run("""
            class Box { v: int = 7, fn get(): int { return this.v; } }
            class Aussen { inner: ?Box, }

            fn main(): int {
                let a: ?Aussen = Aussen { inner = Box { } };
                return a?.inner?.get() ?? -1;
            }
            """));

    /// <summary>
    /// Ein PRIMITIVER Empfänger mit inhärenter Extension (§3.6). Er ist der fünfte Dispatch-Weg
    /// und stand in derselben Fallunterscheidung — er wäre beim ersten Anlauf mit durchgefallen,
    /// und zwar nicht in eine Diagnose, sondern in einen Aufruf ohne Empfänger.
    /// </summary>
    [Fact]
    public void A_call_on_a_primitive_receiver_works() =>
        Assert.Equal(12, Run("""
            extend int { fn doppelt(): int { return this * 2; } }

            fn main(): int {
                let n: ?int = 6;
                return n?.doppelt() ?? -1;
            }
            """));
}
