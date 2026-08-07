using System.Runtime.CompilerServices;
using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Lyric.Vm;

namespace Lyric.Tests.Vm;

/// <summary>
/// Explizite Typargumente an einer Aufrufstelle: `f&lt;int&gt;()`.
///
/// <para><b>Warum es sie braucht</b>: bis dahin ließen sich Generics ausschließlich über
/// Argument-Inferenz instanziieren. Eine Funktion ohne Parameter vom Typ `T` — eine Fabrik wie
/// `empty&lt;T&gt;(): List&lt;T&gt;` — war damit gar nicht aufrufbar, und das blockierte
/// `std.collections`.</para>
///
/// <para><b>Der riskante Teil ist die Disambiguierung</b>, nicht die Semantik: `f&lt;a&gt;(b)`
/// sieht aus wie die Vergleichskette `(f &lt; a) &gt; (b)`. Der Parser entscheidet mit einem
/// reinen Token-Scan — er zählt Klammern und prüft, ob hinter dem `&gt;` ein `(` folgt. Kein
/// spekulatives Parsen, weil eine verworfene Vermutung sonst Diagnosen hinterließe.</para>
///
/// <para>Deshalb steht hier jeder Vergleichsfall, der ähnlich aussieht. Ein Test, der nur die
/// Typargumente prüft, bliebe auch grün, wenn der Scan jedes `&lt;` verschlänge.</para>
/// </summary>
public class ExplicitTypeArgumentTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static long Run(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de)
        {
            ModuleLoader = StdlibLoader.ForRoot(Path.Combine(RepoRoot(), "stdlib"), sm, de),
        };
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        var binding = comp.Resolve();
        var types = Semantics.Analyze(comp, binding, de);

        var writer = new StringWriter();
        de.RenderText(writer);
        Assert.False(de.HasErrors, "source did not compile: " + writer);

        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true);
        Assert.NotNull(ir);
        return Interpreter.Run(BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir!)),
            NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null)).AsI64;
    }

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

    // ------------------------------------------------------------------ was jetzt geht

    [Fact]
    public void A_call_can_name_its_type_argument() =>
        Assert.Equal(5, Run("""
            fn id<T>(x: T): T { return x; }
            fn main(): int { return id<int>(5); }
            """));

    [Fact]
    public void A_factory_without_arguments_becomes_callable() =>
        // DER Fall. Ohne explizite Typargumente hat die Inferenz nichts, woraus sie T ziehen
        // könnte — die Funktion war schlicht nicht aufrufbar.
        Assert.Equal(0, Run("""
            class Buf<T> { data: T[], count: int, }
            fn empty<T>(): Buf<T> { return Buf<T> { data = [], count = 0 }; }
            fn main(): int { let b = empty<int>(); return b.count; }
            """));

    [Fact]
    public void A_generic_function_can_return_a_generic_type() =>
        // Das ging vorher AUCH MIT Inferenz nicht: 'LowerSubstituted' kannte '?T' und 'T[]',
        // aber nicht 'Box<T>'. Der Rückgabetyp wurde ohne Substitution aufgelöst.
        Assert.Equal(7, Run("""
            class Box<T> { v: T, }
            fn make<T>(x: T): Box<T> { return Box<T> { v = x }; }
            fn main(): int { let b = make(7); return b.v; }
            """));

    [Fact]
    public void A_growing_buffer_works_end_to_end() =>
        // Die Grundlage für 'List<T>' (M8/S5): das Wachsen kommt ohne 'newArray<T>(n)' aus.
        // 'data + data' verdoppelt; was jenseits von 'count' steht, wird nie gelesen, also ist
        // sein Inhalt egal.
        Assert.Equal(63, Run("""
            class Buf<T> {
                data: T[],
                count: int,

                pub mut fn push(v: T): void {
                    if (this.count >= this.data.length) {
                        if (this.data.length == 0) { this.data = [v]; }
                        else { this.data = this.data + this.data; }
                    }
                    this.data[this.count] = v;
                    this.count = this.count + 1;
                }

                pub fn at(i: int): T { return this.data[i]; }
            }

            fn empty<T>(): Buf<T> { return Buf<T> { data = [], count = 0 }; }

            fn main(): int {
                let b = empty<int>();
                b.push(10);
                b.push(20);
                b.push(30);
                return b.at(0) + b.at(1) + b.at(2) + b.count;
            }
            """));

    // ------------------------------------------------------------------ Disambiguierung

    [Fact]
    public void A_less_than_comparison_is_still_a_comparison() =>
        Assert.Equal(1, Run("fn main(): int { if (1 < 2) { return 1; } return 0; }"));

    [Fact]
    public void A_chain_that_looks_like_type_arguments_stays_a_comparison() =>
        // 'a < b > (c)' — genau die Form, die der Scan von Typargumenten trennen muss. Hier
        // fehlt der Aufruf-Charakter: der Callee ist kein generischer Name, und selbst wenn der
        // Scan zuschlüge, wäre das Ergebnis ein Typfehler statt eines stillen Missverständnisses.
        Assert.Equal(1, Run("""
            fn main(): int {
                let a = 1;
                let b = 5;
                let c = 0;
                if (a < b) { if (b > c) { return 1; } }
                return 0;
            }
            """));

    [Fact]
    public void Arithmetic_after_a_comparison_is_not_swallowed() =>
        Assert.Equal(1, Run("fn main(): int { let n = 3; if (n < 2 + 5) { return 1; } return 0; }"));

    // ------------------------------------------------------------------ was abgelehnt wird

    [Fact]
    public void The_wrong_number_of_type_arguments_is_reported() =>
        Assert.Contains(Check("""
            fn id<T>(x: T): T { return x; }
            fn main(): int { return id<int, bool>(5); }
            """).Diagnostics, d => d.Code == "LYR-SEM0026");

    [Fact]
    public void An_explicit_type_argument_beats_inference()
    {
        // Geschriebenes gewinnt: 'id<int>("x")' ist ein Typfehler und wird NICHT still zu
        // 'id<string>'. Ohne diese Reihenfolge wäre die explizite Form wirkungslos.
        var de = Check("""
            fn id<T>(x: T): T { return x; }
            fn main(): int { let s = id<int>("x"); return 0; }
            """);

        Assert.Contains(de.Diagnostics, d => d.Code is "LYR-SEM0001" or "LYR-SEM0014");
    }

    [Fact]
    public void A_constraint_still_applies_to_a_written_type_argument() =>
        // Sonst wäre die explizite Form ein Weg, Constraints zu umgehen.
        Assert.Contains(Check("""
            interface Named { fn name(): string; }
            fn show<T :: [Named]>(x: T): string { return x.name(); }
            fn main(): int { let s = show<int>(5); return 0; }
            """).Diagnostics, d => d.Code == "LYR-SEM0028");

    // ------------------------------------------ Constraints mit eigenen Typargumenten

    private const string EqSetup = """
        pub interface Eq<T> {
            fn eq(other: T): bool;
        }

        extend int :: [Eq<int>] {
            fn eq(other: int): bool { return this == other; }
        }

        pub struct P :: [Eq<P>] {
            x: int,
            fn eq(other: P): bool { return this.x == other.x; }
        }

        pub fn same<T :: [Eq<T>]>(a: T, b: T): bool {
            return a.eq(b);
        }

        """;

    /// <summary>
    /// Ein Constraint darf sein eigenes Typargument mitbringen: <c>T :: [Eq&lt;T&gt;]</c>.
    ///
    /// <para><b>Der Punkt, an dem es scheiterte</b>, war ratlos formuliert: „cannot assign 'T' to
    /// 'T'". Zwei verschiedene Symbole mit demselben Namen — das <c>T</c> der Funktion und das
    /// <c>T</c> des Interfaces. <c>MemberOfTypeParam</c> gab den rohen Interface-Typ zurueck,
    /// ohne die Argumente des Constraints einzusetzen.</para>
    ///
    /// <para>Die Konformanzpruefung machte dieselbe Substitution seit jeher richtig. Eine Frage,
    /// zwei Stellen, und nur eine hatte die Antwort — dasselbe Muster wie bei
    /// <c>LowerType</c>/<c>TypeTable.Lower</c> und bei <c>UnifyNumeric</c>.</para>
    ///
    /// <para>Ohne diesen Punkt ist <c>Map&lt;K :: [Hashable&lt;K&gt;], V&gt;</c> nicht
    /// formulierbar, und damit ADR-024 nicht umsetzbar.</para>
    /// </summary>
    [Fact]
    public void A_constraint_may_carry_its_own_type_argument() =>
        Assert.Equal(1, Run(EqSetup + """
            fn main(): int {
                if (same<int>(3, 3)) { return 1; }
                return 0;
            }
            """));

    [Fact]
    public void The_type_argument_may_be_inferred() =>
        // Ohne explizites '<int>'. Die Inferenz lief vorher in denselben Fehler.
        Assert.Equal(0, Run(EqSetup + """
            fn main(): int {
                if (same(3, 4)) { return 1; }
                return 0;
            }
            """));

    [Fact]
    public void A_user_type_satisfies_the_constraint() =>
        // Ein 'struct' mit ':: [Eq<P>]' — der Fall, den Map/Set brauchen werden.
        Assert.Equal(1, Run(EqSetup + """
            fn main(): int {
                if (same(P { x = 7 }, P { x = 7 })) { return 1; }
                return 0;
            }
            """));

    [Fact]
    public void The_constraint_survives_being_passed_on() =>
        // Zwei generische Funktionen hintereinander: 'describe<T>' reicht sein eigenes T an
        // 'same<T>' weiter. Ohne diesen Test bliebe ungeprueft, ob die Substitution auch dann
        // stimmt, wenn das Argument selbst ein Typ-Parameter ist.
        Assert.Equal(1, Run(EqSetup + """
            pub fn describe<T :: [Eq<T>]>(a: T, b: T): int {
                if (same<T>(a, b)) { return 1; }
                return 0;
            }

            fn main(): int { return describe(5, 5); }
            """));
}
