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
/// Format-Specs in f-Strings — `std.fmt` (M8/S3).
///
/// <para>`f"{avg:N2}"` wird zu `std.fmt.formatFloat(avg, "N2")`. Die Spec-Sprache ist die von
/// .NET, wie `Sprache.md` §2.2 es verlangt, und sie wird unverändert durchgereicht — Lyric
/// erfindet keine eigene Notation daneben.</para>
///
/// <para><b>Zwei Zusicherungen sind wichtiger als die Formate selbst</b>: dass ohne Spec
/// weiterhin die `fromXxx`-Wandler laufen (ein Format-Aufruf, der nur den Standard nachbaut, wäre
/// ein zweiter Weg zu demselben Ergebnis), und dass die Ausgabe <b>invariant</b> ist. Eine Zahl,
/// die unter deutscher Locale anders aussieht als unter englischer, ist kein
/// Formatierungsdetail, sondern ein Programm, das sich je nach Rechner anders verhält.</para>
/// </summary>
public class FormatTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string Out(string body)
    {
        var source = "import std.io.console { println };\nfn main(): int { " + body + " return 0; }";
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

        var diagnostics = new StringWriter();
        de.RenderText(diagnostics);
        Assert.False(de.HasErrors, "source did not compile: " + diagnostics);

        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true);
        Assert.NotNull(ir);

        var output = new StringWriter();
        Interpreter.Run(BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir!)),
            NativeRegistry.CreateDefault(output, TextWriter.Null));
        return output.ToString().ReplaceLineEndings("\n");
    }

    // ------------------------------------------------------------------ Zahlen

    [Fact]
    public void N_rounds_to_the_given_digits() =>
        // Der Fall, auf den 'examples/stats.lyr' seit M6 wartet.
        Assert.Equal("12.35\n", Out("let avg = 12.3456; println(f\"{avg:N2}\");"));

    [Fact]
    public void F_is_fixed_point() =>
        Assert.Equal("12.346\n", Out("let avg = 12.3456; println(f\"{avg:F3}\");"));

    [Fact]
    public void D_pads_an_integer_with_zeroes() =>
        Assert.Equal("00042\n", Out("let n = 42; println(f\"{n:D5}\");"));

    [Fact]
    public void X_is_hexadecimal() =>
        Assert.Equal("2A\n", Out("let n = 42; println(f\"{n:X}\");"));

    [Fact]
    public void The_thousands_separator_is_invariant() =>
        // Invariant heißt: Punkt als Dezimaltrenner, Komma als Tausendertrenner — überall.
        // Unter deutscher Locale wäre es umgekehrt, und dasselbe Programm gäbe auf zwei
        // Rechnern zwei verschiedene Zahlen aus.
        Assert.Equal("1,234.57\n", Out("let x = 1234.567; println(f\"{x:N2}\");"));

    // ------------------------------------------------------------------ ohne Spec

    [Fact]
    public void Without_a_spec_the_plain_converter_still_runs() =>
        // Die wichtigste Zusicherung der Datei. Liefe hier ebenfalls std.fmt, gäbe es zwei Wege
        // zu demselben Ergebnis — genau das, was CONTRIBUTING §Rule 2 verbietet.
        Assert.Equal("42\n", Out("let n = 42; println(f\"{n}\");"));

    [Fact]
    public void A_program_without_format_specs_does_not_pull_in_std_fmt()
    {
        // Dieselbe Regel wie bei den Display-Extensions (S1a): im Bytecode steht nur, was
        // benutzt wird. 'std.fmt' ist ein Well-Known-Modul und wird IMMER geladen — geladen zu
        // werden und im Modul zu landen sind zwei verschiedene Dinge.
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", "fn main(): int { let n = 42; return n; }");
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de)
        {
            ModuleLoader = StdlibLoader.ForRoot(Path.Combine(RepoRoot(), "stdlib"), sm, de),
        };
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        var binding = comp.Resolve();
        var ir = ModuleLowerer.Lower(comp, binding, Semantics.Analyze(comp, binding, de), de,
            verify: true);

        Assert.NotNull(ir);
        Assert.DoesNotContain(ir!.Imports, i => i.Name.StartsWith("std.fmt", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ andere Typen

    [Fact]
    public void A_string_spec_is_a_width() =>
        // .NET kennt für Strings keine Standardformate. Statt eine zweite Notation zu erfinden,
        // ist die Spec hier schlicht eine Breite — positiv füllt rechts, negativ links.
        Assert.Equal("ab   |\n", Out("let s = \"ab\"; println(f\"{s:5}|\");"));

    [Fact]
    public void A_negative_width_pads_on_the_left() =>
        Assert.Equal("   ab|\n", Out("let s = \"ab\"; println(f\"{s:-5}|\");"));

    [Fact]
    public void Bool_and_char_take_a_width_too() =>
        Assert.Equal("true |x    |\n", Out("let b = true; let c = 'x'; println(f\"{b:5}|{c:5}|\");"));

    // ------------------------------------------------------------------ was schiefgehen darf

    [Fact]
    public void An_invalid_spec_panics_with_the_spec_in_the_message()
    {
        // Kein stilles Ausweichen auf die Standarddarstellung: die Spec steht als Literal im
        // Quelltext und hängt nicht von der Eingabe ab. Ein '{x:Q9}' ist falsch geschrieben,
        // nicht unglücklich gelaufen — und ein Fallback trüge den Tippfehler bis in die Ausgabe.
        var panic = Assert.Throws<LyricPanic>(() =>
            Out("let n = 42; println(f\"{n:Q9}\");"));

        Assert.Contains("Q9", panic.Message);
    }
}
