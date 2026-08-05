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
}
