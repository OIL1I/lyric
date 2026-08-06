using System.Runtime.CompilerServices;
using System.Text;
using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Lyric.Vm;

namespace Lyric.Tests.Vm;

/// <summary>
/// Tests für den Interpreter (M6, Slice 1).
///
/// <para><b>Das sind die ersten Tests im Projekt, die prüfen, ob ein Programm das Richtige
/// <i>tut</i></b> — bis hierher konnte nur geprüft werden, ob es korrekt übersetzt wird. Deshalb
/// laufen sie über die gesamte Pipeline: Quelltext → Sema → IR → Bytecode → Ausführung. Ein Fehler
/// in irgendeiner Stufe fällt hier auf.</para>
///
/// <para>Geprüft wird der <b>Rückgabewert</b>, nicht der Prozess-Exit-Code: der ist auf ein Byte
/// maskiert (Sprache.md §11) und würde negative Werte und Überläufe unkenntlich machen — also
/// genau die Fälle, die interessant sind.</para>
/// </summary>
public class VmTests
{
    // ------------------------------------------------------------------ Helfer

    private static LyrValue Run(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        var binding = comp.Resolve();
        var types = Semantics.Analyze(comp, binding, de);

        var writer = new StringWriter();
        de.RenderText(writer);
        Assert.False(de.HasErrors, "source did not compile:\n" + writer.ToString());

        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true);
        Assert.NotNull(ir);

        var module = BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir!));
        return Interpreter.Run(module);
    }

    /// <summary>Kürzel: der Rumpf wird in ein `main` gepackt.</summary>
    private static long Eval(string body) => Run($"fn main(): int {{ {body} }}").AsI64;

    /// <summary>Ein Programmierfehler zur Laufzeit ist ein <c>panic</c> (Sprache.md §9) — nicht
    /// catchbar, mit Backtrace. Kein eigener VM-Fehlerweg daneben.</summary>
    private static LyricPanic RunExpectingPanic(string source) =>
        Assert.Throws<LyricPanic>(() => Run(source));

    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    /// <summary>Wie <see cref="Run"/>, aber mit Stdlib auf dem Modulpfad und den eingebauten
    /// Natives — für Beispiele, die <c>println</c> benutzen. Die Ausgabe wird eingesammelt statt
    /// nach <c>Console</c> geschrieben.</summary>
    private static (LyrValue Result, string Output) RunWithStdlib(string source)
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
        Assert.False(de.HasErrors, "source did not compile:\n" + writer.ToString());

        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true);
        Assert.NotNull(ir);

        var output = new StringWriter();
        var module = BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir!));
        var result = Interpreter.Run(module, NativeRegistry.CreateDefault(output, TextWriter.Null));
        return (result, output.ToString());
    }

    // ------------------------------------------------------------------ 1) Gate-Programm

    /// <summary>Das Gate-Artefakt von M7/P1: Objekte über die gesamte Pipeline, inklusive
    /// Referenz-Semantik über eine Funktionsgrenze und einer Klasse als Feldtyp.</summary>
    [Fact]
    public void Object_gate_program_computes_the_right_answer()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "examples", "objects.lyr"), Encoding.UTF8);
        var (result, output) = RunWithStdlib(source);

        // 10, zweimal +5 durch bump (Mutation wirkt beim Aufrufer), dann +1 über den Alias.
        Assert.Equal(21, result.AsI64);
        Assert.Equal("verschachtelt: 21\n", output.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Gate_program_computes_the_right_answer()
    {
        // sumTo(10) = 55, gcd(48,18) = 6, max(55,6) = 55, add(55,0) = 55.
        // Das ist M6s erster echter Beweis, dass die Pipeline nicht nur übersetzt, sondern rechnet.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "examples", "arith.lyr"), Encoding.UTF8);
        Assert.Equal(55, Run(source).AsI64);
    }

    // ------------------------------------------------------------------ 2) Rechnen

    [Theory]
    [InlineData("return 1 + 2 * 3;", 7)]                  // Präzedenz
    [InlineData("return (1 + 2) * 3;", 9)]
    [InlineData("return 7 / 2;", 3)]                      // Ganzzahldivision schneidet ab
    [InlineData("return -7 / 2;", -3)]                    // Richtung Null, nicht Richtung -unendlich
    [InlineData("return -7 % 2;", -1)]                    // Rest trägt das Vorzeichen des Dividenden
    [InlineData("return 7 % -2;", 1)]
    [InlineData("return 1 << 10;", 1024)]
    [InlineData("return -16 >> 2;", -4)]                  // arithmetischer Shift bei signed
    [InlineData("return 12 & 10;", 8)]
    [InlineData("return 12 | 10;", 14)]
    [InlineData("return 12 ^ 10;", 6)]
    [InlineData("return ~0;", -1)]
    [InlineData("return -(-5);", 5)]
    public void Integer_arithmetic(string body, long expected) => Assert.Equal(expected, Eval(body));

    [Fact]
    public void Signed_min_divided_by_minus_one_wraps()
    {
        // Zweierkomplement hat kein positives Gegenstück zu MinValue. .NET wirft hier; Lyric
        // wickelt um wie bei jeder anderen Ganzzahl-Operation (Sprache.md §6.6).
        Assert.Equal(long.MinValue, Eval("let m: int = -9223372036854775807 - 1; return m / -1;"));
    }

    [Theory]
    // Sprache.md §6.6: der Schiebebetrag wird modulo der OPERANDENBREITE genommen. Vorher maskierte
    // die VM bei 64 und normalisierte auf die Zielbreite — eine Mischform, die dieselbe Regel je
    // nach Typ verschieden ausfallen ließ: `1 << 9` ergab bei int8 0, bei int64 aber 2.
    [InlineData("let a: int8 = 1; let s: int8 = 9; return (a << s) as int;", 2)]     // 9 mod 8 = 1
    [InlineData("let a: int32 = 1; let s: int32 = 33; return (a << s) as int;", 2)]  // 33 mod 32 = 1
    [InlineData("let a: int = 1; let s: int = 65; return a << s;", 2)]               // 65 mod 64 = 1
    [InlineData("let a: int8 = 1; let s: int8 = 7; return (a << s) as int;", -128)]  // Vorzeichenbit
    public void Shift_count_is_taken_modulo_the_operand_width(string body, long expected) =>
        Assert.Equal(expected, Eval(body));

    [Fact]
    public void Narrow_integers_wrap_at_their_own_width()
    {
        // Ohne Breiten-Normalisierung nach jeder Operation käme hier 200 heraus statt -56.
        Assert.Equal(-56, Eval("let a: int8 = 100; let b: int8 = 100; return (a + b) as int;"));
    }

    [Fact]
    public void Unsigned_comparison_is_not_signed_comparison()
    {
        // Als u64 gelesen ist 0xFFFF… der größte Wert, als i64 wäre es -1. Das Tag am Opcode
        // entscheidet — genau dafür trägt es den Operandentyp und nicht den Ergebnistyp.
        Assert.Equal(1, Eval("let big: uint = 18446744073709551615; let one: uint = 1; " +
                             "return if (big > one) 1 else 0;"));
    }

    // ------------------------------------------------------------------ 3) Konvertierung

    [Theory]
    [InlineData("let n: int = 300; return n as int8 as int;", 44)]      // 300 & 0xFF = 44
    [InlineData("let n: int = -1; return n as uint8 as int;", 255)]
    [InlineData("let f: float = 3.9; return f as int;", 3)]            // Richtung Null
    [InlineData("let f: float = -3.9; return f as int;", -3)]
    [InlineData("let n: int32 = 7; return n as int64 as int;", 7)]
    public void Conversions(string body, long expected) => Assert.Equal(expected, Eval(body));

    [Fact]
    public void Float_to_int_saturates_instead_of_being_undefined()
    {
        // WASMs trunc_sat-Verhalten. Die Alternative wäre "undefiniert wie in C" — dann lieferte
        // dieselbe .lyrbc-Datei auf zwei Runtimes verschiedene Ergebnisse, und ADR-013s Versprechen
        // einer zweiten Implementierung wäre nichts wert.
        Assert.Equal(long.MaxValue, Eval("let f: float = 1e30; return f as int;"));
        Assert.Equal(long.MinValue, Eval("let f: float = -1e30; return f as int;"));
    }

    [Fact]
    public void Float32_arithmetic_uses_single_precision()
    {
        // 2^24 ist die erste ganze Zahl, ab der f32 nicht mehr jede zählen kann: 16777216 + 1
        // bleibt 16777216. In doppelter Genauigkeit gerechnet käme 16777217 heraus, und der
        // Vergleich schlüge fehl. Der Test unterscheidet also wirklich die Rechenbreite.
        Assert.Equal(1, Eval("""
            let big: float32 = 16777216.0f32;
            let plusOne: float32 = big + 1.0f32;
            return if (plusOne == big) 1 else 0;
            """));
    }

    // ------------------------------------------------------------------ 4) Kontrollfluss

    [Theory]
    [InlineData("var i = 0; var s = 0; while (i < 5) { s += i; i += 1; } return s;", 10)]
    [InlineData("var i = 3; var s = 0; do { s += i; i -= 1; } while (i > 0); return s;", 6)]
    [InlineData("var i = 0; while (true) { i += 1; if (i > 3) { break; } } return i;", 4)]
    [InlineData("var i = 0; var s = 0; while (i < 5) { i += 1; if (i % 2 == 0) { continue; } s += i; } return s;", 9)]
    [InlineData("return if (1 < 2) 10 else 20;", 10)]
    [InlineData("return if (1 > 2) 10 else 20;", 20)]
    public void Control_flow(string body, long expected) => Assert.Equal(expected, Eval(body));

    [Fact]
    public void And_short_circuits_before_evaluating_the_right_side()
    {
        // Der Beweis läuft über einen Nebeneffekt, den es sonst nicht gibt: würde die rechte Seite
        // ausgewertet, gäbe es eine Division durch Null und damit einen panic statt einer 7.
        Assert.Equal(7, Eval("var d = 0; if (false && (10 / d) > 0) { return 1; } return 7;"));
    }

    [Fact]
    public void Or_short_circuits_before_evaluating_the_right_side()
    {
        Assert.Equal(7, Eval("var d = 0; if (true || (10 / d) > 0) { return 7; } return 1;"));
    }

    [Fact]
    public void Recursion_and_forward_calls()
    {
        Assert.Equal(3628800, Run("""
            fn fact(n: int): int {
                if (n <= 1) { return 1; }
                return n * fact(n - 1);
            }
            fn main(): int { return fact(10); }
            """).AsI64);
    }

    [Fact]
    public void Postfix_and_prefix_increment_differ()
    {
        // i++ liefert den alten Wert, ++i den neuen. Beide schreiben denselben Slot.
        Assert.Equal(0, Eval("var i = 0; let old = i++; return old;"));
        Assert.Equal(1, Eval("var i = 0; let now = ++i; return now;"));
    }

    // ------------------------------------------------------------------ 4b) Objekte

    private const string Counter = "class Counter { value: int, step: int }\n";

    [Fact]
    public void An_object_carries_its_fields()
    {
        Assert.Equal(10, Run(Counter +
            "fn main(): int { let c = Counter { value = 10, step = 5 }; return c.value; }").AsI64);
        Assert.Equal(5, Run(Counter +
            "fn main(): int { let c = Counter { value = 10, step = 5 }; return c.step; }").AsI64);
    }

    [Fact]
    public void A_field_can_be_assigned_and_compound_assigned()
    {
        Assert.Equal(3, Run(Counter +
            "fn main(): int { let c = Counter { value = 1, step = 2 }; c.value = 3; return c.value; }").AsI64);
        Assert.Equal(3, Run(Counter +
            "fn main(): int { let c = Counter { value = 1, step = 2 }; c.value += c.step; return c.value; }").AsI64);
    }

    /// <summary>
    /// <b>Der Test, der P1 von P4 (Structs) unterscheidet.</b> Eine Klasse ist ein Referenz-Typ
    /// (Sprache.md §3.3): zwei Namen für dasselbe Objekt sehen einander. Käme später versehentlich
    /// eine Kopie beim Zuweisen dazu, fällt genau dieser Test — und nur dieser.
    /// </summary>
    [Fact]
    public void Assignment_copies_the_reference_not_the_object()
    {
        Assert.Equal(99, Run(Counter +
            """
            fn main(): int {
                let c = Counter { value = 1, step = 0 };
                let alias = c;
                alias.value = 99;
                return c.value;
            }
            """).AsI64);
    }

    /// <summary>Dasselbe über eine Funktionsgrenze: das Argument ist die Referenz, also wirkt die
    /// Mutation beim Aufrufer. Ohne diesen Fall wäre „Referenz-Semantik" nur lokal gezeigt.</summary>
    [Fact]
    public void An_object_passed_to_a_function_is_mutated_in_place()
    {
        Assert.Equal(7, Run(Counter +
            """
            fn bump(c: Counter) {
                c.value += c.step;
            }

            fn main(): int {
                let c = Counter { value = 4, step = 3 };
                bump(c);
                return c.value;
            }
            """).AsI64);
    }

    [Fact]
    public void A_field_of_class_type_nests()
    {
        Assert.Equal(42, Run(
            """
            class Inner { value: int }
            class Outer { inner: Inner }

            fn main(): int {
                let outer = Outer { inner = Inner { value = 42 } };
                return outer.inner.value;
            }
            """).AsI64);
    }

    /// <summary>Zwei getrennt angelegte Objekte teilen nichts — die Gegenprobe zum Alias-Test, sonst
    /// wäre ein globaler Speicher pro Typ von den Tests oben nicht zu unterscheiden.</summary>
    [Fact]
    public void Two_instances_are_independent()
    {
        Assert.Equal(1, Run(Counter +
            """
            fn main(): int {
                let a = Counter { value = 1, step = 0 };
                let b = Counter { value = 2, step = 0 };
                b.value = 50;
                return a.value;
            }
            """).AsI64);
    }

    // ------------------------------------------------------------------ 4c) Methoden (ADR-014)

    private const string Acc = """
        class Acc {
            total: int,

            static fn new(start: int): Acc { return Acc { total = start }; }
            fn get(): int { return this.total; }
            fn add(n: int) { this.total += n; }
            fn addTwice(n: int) { this.add(n); this.add(n); }
        }

        """;

    [Fact]
    public void A_static_factory_constructs_and_an_instance_method_reads()
    {
        Assert.Equal(7, Run(Acc + "fn main(): int { return Acc.new(7).get(); }").AsI64);
    }

    /// <summary>Der Empfänger ist Parameter 0 — eine Methode mutiert dasselbe Objekt, das der
    /// Aufrufer hält. Ohne die richtige Argument-Reihenfolge käme hier Unsinn heraus, und zwar
    /// stiller Unsinn: beide Argumente sind Zahlen.</summary>
    [Fact]
    public void An_instance_method_mutates_the_receiver()
    {
        Assert.Equal(10, Run(Acc +
            "fn main(): int { let a = Acc.new(7); a.add(3); return a.get(); }").AsI64);
    }

    /// <summary>Methode ruft Methode auf demselben <c>this</c>. Prüft, dass der Empfänger im
    /// Rumpf ein gewöhnlicher Wert ist und weitergereicht werden kann.</summary>
    [Fact]
    public void A_method_can_call_another_method_on_this()
    {
        Assert.Equal(11, Run(Acc +
            "fn main(): int { let a = Acc.new(5); a.addTwice(3); return a.get(); }").AsI64);
    }

    /// <summary>Zwei Instanzen, dieselbe Methode: der Empfänger entscheidet, nicht die Funktion.
    /// Fällt dieser Test, teilen sich alle Instanzen versehentlich einen Zustand.</summary>
    [Fact]
    public void Methods_act_on_their_own_receiver()
    {
        Assert.Equal(1, Run(Acc +
            """
            fn main(): int {
                let a = Acc.new(1);
                let b = Acc.new(2);
                b.add(50);
                return a.get();
            }
            """).AsI64);
    }

    // ------------------------------------------------------------------ 4d) Arrays (ADR-016)

    [Theory]
    [InlineData("let xs = [3, 7, 1]; return xs[1];", 7)]
    [InlineData("let xs = [3, 7, 1]; return xs.length;", 3)]
    [InlineData("let xs = [0] * 4; return xs.length;", 4)]        // Default-Array
    [InlineData("let n = 5; let xs = [0] * n; return xs.length;", 5)]  // Laenge zur Laufzeit
    [InlineData("let xs = [0] * 0; return xs.length;", 0)]        // leeres Array ist gueltig
    [InlineData("let xs = [1, 2] + [3]; return xs[2];", 3)]
    [InlineData("let xs = [1, 2] + [3]; return xs.length;", 3)]
    [InlineData("let xs = [7] * 3; return xs[0] + xs[1] + xs[2];", 21)]
    [InlineData("var xs = [1, 2, 3]; xs[1] = 9; return xs[1];", 9)]
    [InlineData("var xs = [1, 2, 3]; xs[1] += 9; return xs[1];", 11)]
    public void Arrays_behave(string body, long expected) => Assert.Equal(expected, Eval(body));

    /// <summary>Konkatenation liefert ein <b>neues</b> Array — <c>T[]</c> wächst nicht, also darf
    /// der Operand nicht mitverändert werden.</summary>
    [Fact]
    public void Concatenation_leaves_its_operands_alone()
    {
        Assert.Equal(2, Eval("let xs = [1, 2]; let ys = xs + [3]; return xs.length;"));
    }

    /// <summary>Ein Array ist eine Referenz (wie eine Klasse): zwei Namen, ein Speicher.</summary>
    [Fact]
    public void An_array_is_a_reference()
    {
        Assert.Equal(9, Eval("var xs = [1, 2]; var ys = xs; ys[0] = 9; return xs[0];"));
    }

    /// <summary>
    /// Ein Element-Index ist ein <b>Laufzeitwert</b> — anders als Typ- und Feldindizes kann der
    /// Loader ihn nicht prüfen (ADR-013/ADR-016). Eine Verletzung ist deshalb ein <c>panic</c>
    /// (§9) mit Backtrace, kein Ladefehler und erst recht kein stiller Speicherzugriff.
    /// </summary>
    [Theory]
    [InlineData("let xs = [1, 2]; return xs[2];")]
    [InlineData("let xs = [1, 2]; return xs[-1];")]
    [InlineData("let xs = [0] * 0; return xs[0];")]
    [InlineData("var xs = [1, 2]; xs[5] = 0; return 0;")]
    public void An_index_outside_the_array_panics(string body)
    {
        var panic = RunExpectingPanic($"fn main(): int {{ {body} }}");
        Assert.Equal(VmDiagnostics.IndexOutOfRange, panic.Code);
        Assert.NotEmpty(panic.CallStack);
    }

    [Fact]
    public void A_negative_repetition_count_panics()
    {
        var panic = RunExpectingPanic("fn main(): int { let n = -1; let xs = [0] * n; return 0; }");
        Assert.Equal(VmDiagnostics.IndexOutOfRange, panic.Code);
    }

    // ------------------------------------------------------------------ 4e) Optionals (§7)

    private const string Find = "fn find(x: int): ?int { if (x > 0) { return x; } return null; }\n";

    [Theory]
    [InlineData("return find(7) ?? 0;", 7)]
    [InlineData("return find(-1) ?? 100;", 100)]     // rechte Seite nur bei "kein Wert"
    [InlineData("return find(7)!;", 7)]
    [InlineData("let m = find(3); if (m != null) { return m; } return 0;", 3)]   // Narrowing
    [InlineData("let m = find(-1); if (m == null) { return 42; } return 0;", 42)]
    [InlineData("let m = find(-1); if (m != null) { return m; } return 0;", 0)]
    public void Optionals_behave(string body, long expected) =>
        Assert.Equal(expected,
            Run(Find + $"fn wrap(): int {{ {body} }}\nfn main(): int {{ return wrap(); }}").AsI64);

    /// <summary>
    /// Der Kern der Darstellungsentscheidung: <c>?int</c> muss <b>alle</b> <c>int</c>-Werte tragen
    /// können. Wäre irgendein Bitmuster als „null" reserviert — 0 oder −1 sind die üblichen
    /// Kandidaten —, wäre genau dieser Wert je nach Runtime mal ein Wert und mal keiner.
    /// Bytecode.md §5 verbietet das, dieser Test hält es fest.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    public void An_optional_int_can_carry_every_int(long value)
    {
        var source = $"fn wrap(): ?int {{ return {value}; }}\n" +
                     "fn main(): int { let x = wrap(); if (x != null) { return 1; } return 0; }";
        Assert.Equal(1, Run(source).AsI64);
        Assert.Equal(value, Run($"fn wrap(): ?int {{ return {value}; }}\n" +
                                "fn main(): int { return wrap()!; }").AsI64);
    }

    /// <summary>Die rechte Seite von <c>??</c> wird <b>nicht</b> ausgewertet, wenn links ein Wert
    /// steht — sonst wäre es kein Kurzschluss. Nachgewiesen über eine Division durch Null, die
    /// sonst panickt.</summary>
    [Fact]
    public void Coalescing_does_not_evaluate_its_right_side_when_there_is_a_value()
    {
        Assert.Equal(7, Run(Find + "fn boom(): int { return 1 / 0; }\n" +
                            "fn wrap(): int { return find(7) ?? boom(); }\n" +
                            "fn main(): int { return wrap(); }").AsI64);
    }

    [Fact]
    public void Force_unwrapping_nothing_panics()
    {
        var panic = RunExpectingPanic(Find + "fn main(): int { return find(-1)!; }");
        Assert.Equal(VmDiagnostics.NullDereference, panic.Code);
        Assert.NotEmpty(panic.CallStack);
    }

    // ------------------------------------------------------------------ 4f) Enums (§3.4)

    private const string ShapeEnum = """
        enum Shape {
            Circle(int),
            Rect { w: int, h: int },
            Empty;

            fn area(): int {
                return match (this) {
                    Circle(r) => r * r,
                    Rect { w, h } => w * h,
                    Empty => 0,
                };
            }
        }

        """;

    [Theory]
    [InlineData("return Shape.Circle(5).area();", 25)]                       // Tuple-Variante
    [InlineData("let s: Shape = Shape.Rect { w = 3, h = 4 }; return s.area();", 12)] // Struct-Variante
    [InlineData("return Shape.Empty.area();", 0)]                            // Unit-Variante
    public void Enum_variants_dispatch_through_match(string body, long expected) =>
        Assert.Equal(expected, Run(ShapeEnum + $"fn wrap(): int {{ {body} }}\nfn main(): int {{ return wrap(); }}").AsI64);

    /// <summary>Jede Variante trägt ihre eigenen Felder — der Payload der einen darf beim Lesen der
    /// anderen nicht durchschlagen. Das ist die Invariante hinter „ein Layout pro Variante".</summary>
    [Fact]
    public void Variants_keep_their_own_payload()
    {
        Assert.Equal(37, Run(ShapeEnum +
            """
            fn main(): int {
                let a = Shape.Circle(5);
                let b: Shape = Shape.Rect { w = 3, h = 4 };
                return a.area() + b.area();
            }
            """).AsI64);
    }

    /// <summary><c>match</c> als <b>Statement</b> — derselbe Code wie beim Ausdruck, nur ohne
    /// Ergebnis-Slot.</summary>
    [Fact]
    public void Match_works_as_a_statement()
    {
        Assert.Equal(9, Run(ShapeEnum +
            """
            fn main(): int {
                var total = 0;
                let s = Shape.Circle(3);
                match (s) {
                    Circle(r) => { total = r * r; },
                    Rect { w, h } => { total = 1; },
                    Empty => { total = 2; },
                }
                return total;
            }
            """).AsI64);
    }

    // ------------------------------------------------------------------ 5) Laufzeitfehler

    [Fact]
    public void Division_by_zero_panics_with_a_backtrace()
    {
        // Ein gebrochener Vertrag ist ein panic (Sprache.md §9), kein eigener VM-Fehlerweg
        // daneben — sonst gäbe es drei Fehlermechanismen statt zwei.
        var ex = RunExpectingPanic("""
            fn divide(a: int, b: int): int { return a / b; }
            fn main(): int { var zero = 0; return divide(1, zero); }
            """);

        Assert.Equal(VmDiagnostics.DivisionByZero, ex.Code);
        // Innerste Funktion zuerst — der Backtrace zeigt, wo es knallte und wer dorthin führte.
        Assert.Equal(new[] { "main.divide", "main.main" }, ex.CallStack);
    }

    [Fact]
    public void Remainder_by_zero_panics()
    {
        var ex = RunExpectingPanic("fn main(): int { var d = 0; return 10 % d; }");
        Assert.Equal(VmDiagnostics.DivisionByZero, ex.Code);
    }

    [Fact]
    public void Float_division_by_zero_is_infinity_not_an_error()
    {
        // IEEE 754: keine Ausnahme, sondern Inf. Nur Ganzzahlen kennen den Fehler.
        // (`main` muss int liefern — Sprache.md §11 —, also wird drinnen verglichen.)
        Assert.Equal(1, Eval("var d = 0.0; let r = 1.0 / d; return if (r > 1.0e308) 1 else 0;"));
    }

    [Fact]
    public void Runaway_recursion_is_reported_instead_of_crashing_the_process()
    {
        // Mit .NET-Rekursion im Interpreter wäre das ein StackOverflowException — und die kann
        // man in .NET nicht abfangen, der Prozess stirbt. Deshalb ein expliziter Frame-Stack.
        var ex = RunExpectingPanic("""
            fn down(n: int): int { return down(n + 1); }
            fn main(): int { return down(0); }
            """);
        Assert.Equal(VmDiagnostics.CallDepthExceeded, ex.Code);
    }

    [Fact]
    public void A_module_without_main_has_no_entry_point()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("lib.lyr", "pub fn helper(): int { return 1; }");
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        var binding = comp.Resolve();
        var types = Semantics.Analyze(comp, binding, de);
        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true)!;

        Assert.Null(ir.EntryFunction);

        var module = BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir));
        Assert.Null(module.Start);

        var ex = Assert.Throws<LyricRuntimeException>(() => Interpreter.Run(module));
        Assert.Equal(VmDiagnostics.NoEntryPoint, ex.Code);
    }

    // ------------------------------------------------------------------ 6) Start-Sektion

    [Fact]
    public void Start_section_survives_the_round_trip()
    {
        // Ohne diese Sektion müsste eine Runtime den Einstieg über eine Namenskonvention raten —
        // und eine zweite Implementierung, die nur die Spec kennt, könnte ihn gar nicht finden.
        var module = BytecodeReader.ReadOrThrow(BytecodeWriter.Write(
            LowerOnly("fn helper(): int { return 1; } fn main(): int { return helper(); }")));

        Assert.NotNull(module.Start);
        Assert.Equal("main.main", module.Functions[module.Start!.Value].Name);
    }

    private static Lyric.Ir.IrModule LowerOnly(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        var binding = comp.Resolve();
        var types = Semantics.Analyze(comp, binding, de);
        Assert.False(de.HasErrors);
        return ModuleLowerer.Lower(comp, binding, types, de, verify: true)!;
    }

    // ------------------------------------------------------------------ P5b: ?. und ??=

    /// <summary><c>??=</c> weist nur zu, wenn nichts da ist — und wertet die rechte Seite auch
    /// nur dann aus.</summary>
    [Theory]
    [InlineData("null", 5)]
    [InlineData("1", 1)]
    public void Coalescing_assign_only_fills_an_empty_optional(string initial, long expected) =>
        Assert.Equal(expected, Run($"fn main(): int {{ var x: ?int = {initial}; x ??= 5; return x!; }}").AsI64);

    [Fact]
    public void Coalescing_assign_does_not_evaluate_its_right_side_when_full()
    {
        // Kurzschluss-Nachweis ueber eine sonst ausloesende Division durch Null — dieselbe Form,
        // mit der P2b schon '??' belegt hat.
        Assert.Equal(1, Run("""
            fn main(): int {
                var zero = 0;
                var x: ?int = 1;
                x ??= 1 / zero;
                return x!;
            }
            """).AsI64);
    }

    /// <summary><c>?.</c> greift nur zu, wenn der Traeger einen Wert hat; das Ergebnis ist immer
    /// ein Optional (Sprache.md §7).</summary>
    [Theory]
    [InlineData("null", 0)]
    [InlineData("P { n = 7 }", 7)]
    public void Optional_chaining_skips_the_access_when_empty(string initial, long expected) =>
        Assert.Equal(expected, Run($$"""
            class P { n: int }

            fn main(): int {
                let p: ?P = {{initial}};
                let n: ?int = p?.n;
                return n ?? 0;
            }
            """).AsI64);

    // ------------------------------------------------------------------ P5b: string-Ops, panic

    /// <summary><c>+</c> und <c>*</c> auf <c>string</c> sind eingebaute Semantik (§6.5), aber
    /// kein Opcode: sie lowern zu Calls in <c>std.string</c>, sonst waere <c>add</c> polymorph.</summary>
    [Fact]
    public void String_concatenation_lowers_to_a_call()
    {
        var (_, output) = RunWithStdlib("""
            import std.io.console;
            fn main(): int { console.print("a" + "b" + "c"); return 0; }
            """);
        Assert.Equal("abc", output);
    }

    [Theory]
    [InlineData("3", "ababab")]
    [InlineData("0", "")]
    [InlineData("0 - 1", "")]   // negativ ist kein Fehlerfall — die Spec kennt keinen
    public void String_repetition_lowers_to_a_call(string count, string expected)
    {
        var (_, output) = RunWithStdlib($$"""
            import std.io.console;
            fn main(): int { console.print("ab" * ({{count}})); return 0; }
            """);
        Assert.Equal(expected, output);
    }

    /// <summary><c>panic</c> ist ein Sprach-Built-in mit Rueckgabetyp <c>never</c> (§9): nicht
    /// catchbar, beendet die VM mit Backtrace.</summary>
    [Fact]
    public void Panic_aborts_with_its_message_and_a_backtrace()
    {
        var panic = Assert.Throws<LyricPanic>(() => RunWithStdlib("""
            fn deep(): int { panic("kaputt"); }
            fn main(): int { return deep(); }
            """));

        Assert.Equal(VmDiagnostics.Panicked, panic.Code);
        Assert.Equal("kaputt", panic.Message);
        // Der Backtrace nennt beide Frames, innerster zuerst.
        Assert.Equal(["main.deep", "main.main"], panic.CallStack);
    }

    [Fact]
    public void Code_after_a_panic_is_unreachable_but_the_other_branch_still_runs()
    {
        // 'panic' versiegelt seinen Block — der Rueckgabewert von LowerStmt muss das melden,
        // sonst versucht der Aufrufer, denselben Block ein zweites Mal zu versiegeln.
        Assert.Equal(5, RunWithStdlib("""
            fn f(n: int): int {
                if (n < 0) { panic("negativ"); }
                return n;
            }
            fn main(): int { return f(5); }
            """).Result.AsI64);
    }

    // ------------------------------------------------------------------ P5b: Defaults, params

    /// <summary>
    /// Default-Werte werden an der <b>Aufrufstelle</b> materialisiert, nicht beim Callee — die IR
    /// kennt keine optionalen Parameter, und nach dem Lowering ist ein Aufruf ein Aufruf.
    /// </summary>
    [Theory]
    [InlineData("f(1)", 3)]        // b faellt weg -> Default 2
    [InlineData("f(1, 10)", 11)]   // b angegeben -> Default ungenutzt
    public void A_default_fills_an_omitted_trailing_argument(string call, long expected) =>
        Assert.Equal(expected, Run($$"""
            fn f(a: int, b: int = 2): int { return a + b; }
            fn main(): int { return {{call}}; }
            """).AsI64);

    [Fact]
    public void Several_defaults_fill_from_the_right()
    {
        Assert.Equal(6, Run("""
            fn f(a: int, b: int = 2, c: int = 3): int { return a + b + c; }
            fn main(): int { return f(1); }
            """).AsI64);
    }

    [Fact]
    public void A_default_is_evaluated_per_call_not_once()
    {
        // An der Aufrufstelle heisst: zweimal aufgerufen, zweimal ausgewertet. Waere der Default
        // einmal beim Callee gelowert, teilten sich beide Aufrufe ein Objekt.
        Assert.Equal(2, Run("""
            class Cell { n: int }
            fn make(c: Cell = Cell { n = 0 }): int { c.n += 1; return c.n; }
            fn main(): int { make(); make(); return make() + 1; }
            """).AsI64);
    }

    /// <summary><c>params</c> sammelt den Rest in ein Array (§3.1) — auch das eine
    /// Aufrufstellen-Transformation: der Callee sieht ein gewoehnliches <c>T[]</c>.</summary>
    [Theory]
    [InlineData("sum(1, 2, 3)", 6)]
    [InlineData("sum()", 0)]        // leeres Array, kein Sonderfall
    [InlineData("sum(5)", 5)]
    public void Params_collects_the_remaining_arguments(string call, long expected) =>
        Assert.Equal(expected, Run($$"""
            fn sum(params xs: int[]): int {
                var total = 0;
                var i = 0;
                while (i < xs.length) { total += xs[i]; i += 1; }
                return total;
            }
            fn main(): int { return {{call}}; }
            """).AsI64);

    [Fact]
    public void Params_follows_the_fixed_parameters()
    {
        Assert.Equal(7, Run("""
            fn tag(n: int, params xs: int[]): int { return n * 3 + xs.length; }
            fn main(): int { return tag(2, 5); }
            """).AsI64);
    }

    private const string VariadicSum = """
        fn sum(params xs: int[]): int {
            var total = 0;
            var i = 0;
            while (i < xs.length) { total += xs[i]; i += 1; }
            return total;
        }
        """;

    /// <summary>
    /// <b>Der Fall, der die Regel begruendet</b>: ohne Durchreichen kann eine variadische Funktion
    /// an keine andere delegieren. Genau solche Huellen bauen C#s <c>WriteLine</c>-Ueberladungen
    /// intern.
    /// </summary>
    [Fact]
    public void A_variadic_function_can_forward_its_own_params()
    {
        Assert.Equal(6, Run(VariadicSum + """

            fn logged(params xs: int[]): int { return sum(xs); }
            fn main(): int { return logged(1, 2, 3); }
            """).AsI64);
    }

    [Fact]
    public void A_ready_made_array_is_passed_as_the_array_itself()
    {
        // Nicht als EIN Element: 4+5+6, nicht 1 (die Laenge eines Arrays mit einem Element).
        Assert.Equal(15, Run(VariadicSum + """

            fn main(): int { let a = [4, 5, 6]; return sum(a); }
            """).AsI64);
    }

    /// <summary>
    /// Die Eindeutigkeit, die C# ueber Ueberladungsaufloesung herstellen muss und Lyric ueber den
    /// Typ: bei <c>params xs: int[][]</c> ist ein Element <c>int[]</c> und das Array
    /// <c>int[][]</c>. Beide Aufrufe liefern 1 — aber aus verschiedenen Gruenden, und genau das
    /// ist der Punkt.
    /// </summary>
    [Theory]
    [InlineData("inner", 1)]           // int[] -> ein Element, Array der Laenge 1
    [InlineData("[inner, inner]", 2)]  // int[][] -> das Array selbst, Laenge 2
    public void The_argument_type_decides_element_versus_array(string argument, long expected) =>
        Assert.Equal(expected, Run($$"""
            fn count(params xs: int[][]): int { return xs.length; }

            fn main(): int {
                let inner = [1, 2];
                return count({{argument}});
            }
            """).AsI64);

    // ------------------------------------------------------------------ P5c: Konstanten

    [Fact]
    public void A_module_level_let_is_a_global_slot()
    {
        Assert.Equal(3, Run("""
            let pi = 3;
            fn main(): int { return pi; }
            """).AsI64);
    }

    [Fact]
    public void A_static_let_is_the_same_mechanism()
    {
        Assert.Equal(7, Run("""
            class V { static let Z: int = 7; }
            fn main(): int { return V.Z; }
            """).AsI64);
    }

    [Fact]
    public void An_initializer_may_read_an_earlier_constant()
    {
        Assert.Equal(5, Run("""
            let a = 2;
            let b = a + 3;
            fn main(): int { return b; }
            """).AsI64);
    }

    [Fact]
    public void A_function_reads_a_constant_declared_after_it()
    {
        // Aus einem Rumpf ist jede Konstante lesbar, egal wo sie steht — dann ist die Init-Phase
        // laengst vorbei. Nur INNERHALB eines Initialisierers gilt die Reihenfolge.
        Assert.Equal(4, Run("""
            fn f(): int { return k; }
            let k = 4;
            fn main(): int { return f(); }
            """).AsI64);
    }

    [Fact]
    public void An_initializer_can_build_an_object()
    {
        // Der Fall, wegen dem es eine Init-FUNKTION ist und keine Werte in der Sektion: ein
        // Initialisierer ist ein Ausdruck, kein Literal (ADR-014).
        Assert.Equal(9, Run("""
            class C { n: int }
            let cell = C { n = 9 };
            fn main(): int { return cell.n; }
            """).AsI64);
    }

    [Fact]
    public void The_documented_static_let_example_works()
    {
        // Doku.md §10.3 — der Fall, fuer den ADR-014 'static let' ueberhaupt eingefuehrt hat.
        Assert.Equal(50, Run("""
            class Enemy {
                name: string,
                hp: int,

                static let BASE_HP: int = 10;

                static fn new(level: int): Enemy {
                    return Enemy { name = "goblin", hp = Enemy.BASE_HP * level };
                }
            }

            fn main(): int { let e = Enemy.new(5); return e.hp; }
            """).AsI64);
    }

    // ------------------------------------------------------------------ P6: Closures

    [Fact]
    public void A_lambda_can_be_called_immediately() =>
        Assert.Equal(3, Run("fn main(): int { let f = (x: int) => x + 1; return f(2); }").AsI64);

    [Fact]
    public void A_captured_let_is_copied_into_the_environment() =>
        Assert.Equal(7, Run("""
            fn main(): int { let k = 5; let f = (x: int) => x + k; return f(2); }
            """).AsI64);

    [Fact]
    public void A_closure_outlives_the_call_that_made_it() =>
        // Der Kern von ADR-018: 'n' lebt in einer Zelle, nicht im Frame von mk — sonst waere es
        // hier weg.
        Assert.Equal(2, Run("""
            fn mk(): fn() -> int { var n = 0; return (): int => { n += 1; return n; }; }
            fn main(): int { let c = mk(); c(); return c(); }
            """).AsI64);

    [Fact]
    public void Two_closures_share_one_captured_variable() =>
        // Die Gegenprobe zum Test darueber: teilen heisst teilen. Bei zwei Zellen stuende hier 1.
        Assert.Equal(21, Run("""
            fn main(): int {
                var n = 1;
                let inc = (): int => { n += 10; return n; };
                let get = (): int => n;
                inc();
                inc();
                return get();
            }
            """).AsI64);

    [Fact]
    public void The_enclosing_function_sees_what_the_closure_wrote() =>
        // Die andere Richtung derselben Zelle — ohne diesen Test bliebe „geteilt" die halbe Aussage.
        Assert.Equal(30, Run("""
            fn main(): int { var n = 0; let set = () => { n = 30; }; set(); return n; }
            """).AsI64);

    [Fact]
    public void A_closure_can_be_passed_as_an_argument() =>
        Assert.Equal(12, Run("""
            fn ap(f: fn(int) -> int, v: int): int { return f(v); }
            fn main(): int { let m = 3; return ap((x: int) => x * m, 4); }
            """).AsI64);

    [Fact]
    public void A_lambda_inside_a_lambda_reaches_the_outer_capture() =>
        // Verschachtelt: 'a' liegt im Environment der aeusseren Closure, und die innere liest es
        // von dort — nicht aus einem Slot, den es in ihrem Frame nicht gibt.
        Assert.Equal(8, Run("""
            fn main(): int { let a = 7; let f = (): int => { let g = (): int => a + 1; return g(); }; return f(); }
            """).AsI64);

    [Fact]
    public void A_closure_without_captures_needs_no_environment() =>
        // Kein newobj im erzeugten Code — der Wert ist reiner Funktionsindex. Gemessen wird das
        // Ergebnis; dass keine Allokation stattfand, haelt der Disassembler fest.
        Assert.Equal(9, Run("""
            fn main(): int { let f = (x: int) => x * 3; return f(3); }
            """).AsI64);

    [Fact]
    public void An_array_of_function_values_runs()
    {
        // Der Fall, fuer den die Typ-Klammerung eingefuehrt wurde (Sprache.md §4): vorher liess
        // sich dieser Typ nicht hinschreiben, obwohl es ihn gab.
        Assert.Equal(31, Run("""
            fn main(): int {
                let fs: (fn(int) -> int)[] = [(x: int) => x + 1, (x: int) => x * 2];
                return fs[0](10) + fs[1](10);
            }
            """).AsI64);
    }

    [Fact]
    public void A_parenthesized_type_is_the_type_itself() =>
        Assert.Equal(7, Run("fn main(): int { let a: (int) = 7; return a; }").AsI64);

    // ------------------------------------------------------------------ P7: Coroutinen

    /// <summary>Wie <see cref="Run"/>, aber mit Stdlib: der Sprungverteiler einer Coroutine ruft
    /// <c>std.core.coroutineEnded</c>, und den bindet erst der Modulpfad.</summary>
    private static LyrValue Coroutine(string source) => RunWithStdlib(source).Result;

    private static LyricPanic PanicFromCoroutine(string source) =>
        Assert.Throws<LyricPanic>(() => RunWithStdlib(source));

    [Fact]
    public void A_coroutine_resumes_where_it_left_off() =>
        // Der Kern von §8: 'n' ueberlebt das 'yield'. Ohne erhaltenen Zustand kaeme dreimal die 0.
        Assert.Equal(2, Coroutine("""
            fn counter(): Coroutine<int> { var n = 0; while (true) { yield n; n += 1; } }
            fn main(): int { let c = counter(); resume c; resume c; return resume c; }
            """).AsI64);

    [Fact]
    public void Each_yield_is_its_own_resume_point() =>
        Assert.Equal(30, Coroutine("""
            fn three(): Coroutine<int> { yield 10; yield 20; yield 30; }
            fn main(): int { let t = three(); let a = resume t; let b = resume t; return a + b; }
            """).AsI64);

    [Fact]
    public void Two_coroutines_of_the_same_kind_have_separate_state() =>
        // Die Gegenprobe: der Zustand haengt am WERT, nicht an der Funktion. Bei geteiltem
        // Zustand stuende hier 3.
        Assert.Equal(2, Coroutine("""
            fn counter(): Coroutine<int> { var n = 0; while (true) { yield n; n += 1; } }
            fn main(): int {
                let a = counter();
                let b = counter();
                resume a; resume a;
                resume b;
                return resume a;
            }
            """).AsI64);

    [Fact]
    public void A_coroutine_parameter_survives_the_first_yield() =>
        // Parameter liegen im Zustandsobjekt wie jedes Local — die Fabrik schreibt sie beim
        // Erzeugen hinein.
        Assert.Equal(14, Coroutine("""
            fn steps(by: int): Coroutine<int> { var n = 0; while (true) { yield n; n += by; } }
            fn main(): int { let s = steps(7); resume s; resume s; return resume s; }
            """).AsI64);

    [Fact]
    public void Resuming_a_finished_coroutine_is_an_error()
    {
        // §8: der resume, bei dem der Rumpf auslaeuft, hat keinen Wert zu liefern und meldet
        // sich. Bis Throwable-Typen aus der Stdlib kommen (M8) als Panic.
        var panic = PanicFromCoroutine("""
            fn two(): Coroutine<int> { yield 1; yield 2; }
            fn main(): int { let c = two(); resume c; resume c; return resume c; }
            """);

        Assert.Contains("already finished", panic.Message);
    }

    // ------------------------------------------------------------------ P8: Generics

    [Fact]
    public void A_generic_function_runs() =>
        Assert.Equal(7, Run("""
            fn id<T>(x: T): T { return x; }
            fn main(): int { return id(7); }
            """).AsI64);

    [Fact]
    public void Two_type_arguments_do_not_interfere() =>
        Assert.Equal(5, Run("""
            fn id<T>(x: T): T { return x; }
            fn main(): int { let s = id("x"); return id(5); }
            """).AsI64);

    [Fact]
    public void A_generic_function_can_call_a_generic_function() =>
        // 'id' wird aus 'twice<int>' heraus angefordert, und welches T gemeint ist, weiss nur die
        // Substitution der rufenden Instanz.
        Assert.Equal(4, Run("""
            fn id<T>(x: T): T { return x; }
            fn twice<T>(x: T): T { return id(id(x)); }
            fn main(): int { return twice(4); }
            """).AsI64);

    [Fact]
    public void A_generic_function_can_recurse() =>
        // Die Instanz findet ihre eigene Id vor — deshalb wird sie bei der Anforderung vergeben
        // und nicht erst beim Lowern.
        Assert.Equal(3, Run("""
            fn down<T>(x: T, n: int): int { if (n <= 0) { return 0; } return 1 + down(x, n - 1); }
            fn main(): int { return down("a", 3); }
            """).AsI64);

    [Fact]
    public void A_generic_type_has_one_layout_per_instance() =>
        Assert.Equal(3, Run("""
            class Box<T> { v: T }
            fn main(): int { let a = Box<int> { v = 3 }; let s = Box<string> { v = "x" }; return a.v; }
            """).AsI64);

    [Fact]
    public void A_method_of_a_generic_type_is_instantiated_per_type() =>
        // Der Rueckgabetyp ist T — er kann nur aus der INSTANZ kommen, nicht aus der Definition.
        Assert.Equal(5, Run("""
            class Box<T> { v: T, fn get(): T { return this.v; } }
            fn main(): int { let a = Box<int> { v = 5 }; let s = Box<string> { v = "x" }; return a.get(); }
            """).AsI64);

    [Fact]
    public void A_generic_struct_lowers_like_any_other() =>
        // Generisch UND Wert-Semantik: 'Pair<int>' geht durch denselben Layout-Pfad wie jedes
        // andere struct (P4) — die Instanziierung aendert daran nichts.
        Assert.Equal(5, Run("""
            struct Pair<T> { a: T, b: T, fn first(): T { return this.a; } }
            fn main(): int { let p = Pair<int> { a = 5, b = 3 }; return p.first(); }
            """).AsI64);

    [Fact]
    public void A_generic_interface_dispatches_dynamically() =>
        // Der Baustein, auf dem 'Iterator<T>' aufsetzt: Konformanz zu einem generischen Interface,
        // Zuweisung an dessen Typ, und ein callvirt darueber.
        Assert.Equal(7, Run("""
            interface Src<T> { fn next(): ?T; }
            class Ones :: [Src<int>] { fn next(): ?int { return 7; } }
            fn take(s: Src<int>): int { return s.next() ?? 0; }
            fn main(): int { let o = Ones { }; return take(o); }
            """).AsI64);

    // ------------------------------------------------------------------ P8c: for-in

    [Fact]
    public void For_in_walks_an_exclusive_range() =>
        Assert.Equal(6, Iterating("fn main(): int { var s = 0; for (n in 0..4) { s += n; } return s; }"));

    [Fact]
    public void For_in_walks_an_inclusive_range() =>
        // Der inklusive Bereich endet eins spaeter — die Umrechnung passiert beim Bauen des
        // Adapters, damit es nur EINEN RangeIterator gibt.
        Assert.Equal(10, Iterating("fn main(): int { var s = 0; for (n in 1..=4) { s += n; } return s; }"));

    [Fact]
    public void For_in_walks_an_array() =>
        Assert.Equal(33, Iterating("""
            fn main(): int { let xs = [10, 20, 3]; var s = 0; for (x in xs) { s += x; } return s; }
            """));

    [Fact]
    public void Break_and_continue_work_inside_for_in() =>
        // Die Schleife ist ein gewoehnlicher LoopScope — 'break' und 'continue' brauchen deshalb
        // keinen Sonderfall.
        Assert.Equal(8, Iterating("""
            fn main(): int {
                var s = 0;
                for (n in 0..5) { if (n == 2) { continue; } s += n; }
                return s;
            }
            """));

    [Fact]
    public void Two_loops_over_the_same_array_do_not_interfere() =>
        // Der Index gehoert dem ITERATOR und nicht dem Array. Bei geteiltem Zustand kaeme hier 6.
        Assert.Equal(12, Iterating("""
            fn main(): int {
                let xs = [1, 2, 3];
                var s = 0;
                for (a in xs) { s += a; }
                for (b in xs) { s += b; }
                return s;
            }
            """));

    [Fact]
    public void A_user_defined_iterator_is_used_directly() =>
        // Kein Adapter: der Typ erfuellt 'Iterator<T>' selbst, also wird er genommen, wie er ist.
        Assert.Equal(3, Iterating("""
            import std.iter { Iterator };
            class UpTo :: [Iterator<int>] {
                current: int,
                last: int,
                pub mut fn next(): ?int {
                    if (this.current > this.last) { return null; }
                    let v = this.current;
                    this.current = this.current + 1;
                    return v;
                }
            }
            fn main(): int {
                var n = 0;
                for (x in UpTo { current = 1, last = 2 }) { n += x; }
                return n;
            }
            """));

    /// <summary>'for-in' baut seinen Iterator aus std.iter — ohne Modulpfad gibt es keinen.</summary>
    private static long Iterating(string source) => RunWithStdlib(source).Result.AsI64;
}
