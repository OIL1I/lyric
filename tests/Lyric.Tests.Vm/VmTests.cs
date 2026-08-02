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

        var ir = ModuleLowerer.Lower(comp, types, de, verify: true);
        Assert.NotNull(ir);

        var module = BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir!));
        return Interpreter.Run(module);
    }

    /// <summary>Kürzel: der Rumpf wird in ein `main` gepackt.</summary>
    private static long Eval(string body) => Run($"fn main(): int {{ {body} }}").AsI64;

    private static LyricRuntimeException RunExpectingError(string source) =>
        Assert.Throws<LyricRuntimeException>(() => Run(source));

    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    // ------------------------------------------------------------------ 1) Gate-Programm

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
        // wickelt um wie bei jeder anderen Ganzzahl-Operation — ein Absturz wäre die schlechtere
        // Antwort. (Steht noch nicht in Sprache.md.)
        Assert.Equal(long.MinValue, Eval("let m: int = -9223372036854775807 - 1; return m / -1;"));
    }

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
        // ausgewertet, gäbe es eine Division durch Null und damit LYR-VM0002 statt einer 7.
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

    // ------------------------------------------------------------------ 5) Laufzeitfehler

    [Fact]
    public void Division_by_zero_is_a_runtime_error()
    {
        var ex = RunExpectingError("fn main(): int { var d = 0; return 10 / d; }");
        Assert.Equal(VmDiagnostics.DivisionByZero, ex.Code);
    }

    [Fact]
    public void Remainder_by_zero_is_a_runtime_error()
    {
        var ex = RunExpectingError("fn main(): int { var d = 0; return 10 % d; }");
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
        var ex = RunExpectingError("""
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
        var types = Semantics.Analyze(comp, comp.Resolve(), de);
        var ir = ModuleLowerer.Lower(comp, types, de, verify: true)!;

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
        var types = Semantics.Analyze(comp, comp.Resolve(), de);
        Assert.False(de.HasErrors);
        return ModuleLowerer.Lower(comp, types, de, verify: true)!;
    }
}
