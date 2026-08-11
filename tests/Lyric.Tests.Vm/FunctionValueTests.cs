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
/// Eine deklarierte Funktion als <b>Wert</b> — <c>map(o, verdoppeln)</c> statt
/// <c>map(o, (n: int) =&gt; verdoppeln(n))</c> — und ein Lambda in einer f-String-Interpolation.
///
/// <para>Beide Lücken wurden beim Bau von <c>std.option</c> (M8b/S9) gefunden, und beide trafen
/// dieselbe Stelle: jede Funktion höherer Ordnung in der Stdlib. Die eine zwang zum
/// Lambda-Umweg, die andere machte genau diesen Umweg an der häufigsten Schreibstelle zum
/// Syntaxfehler.</para>
///
/// <para><b>Die Funktion als Wert brauchte weder eine Instruktion noch einen Opcode.</b>
/// <c>MakeClosure</c> nimmt sein Environment seit P6 optional — der häufige Fall
/// <c>(x) =&gt; x &gt; 0</c> fängt nichts —, und die VM entscheidet am <c>HasEnvironment</c>-Bit,
/// ob Slot 0 belegt wird. Eine benannte Funktion ist eine Closure ohne Umgebung, mehr nicht.</para>
/// </summary>
public class FunctionValueTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static (long Exit, string Out) Run(string source)
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

        var diagnostics = new StringWriter();
        de.RenderText(diagnostics);
        Assert.False(de.HasErrors, "source did not compile: " + diagnostics);

        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true);
        Assert.NotNull(ir);

        var output = new StringWriter();
        var exit = Interpreter.Run(BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir!)),
            NativeRegistry.CreateDefault(output, TextWriter.Null)).AsI64;
        return (exit, output.ToString().ReplaceLineEndings("\n"));
    }

    // ------------------------------------------------------------------ Funktion als Wert

    [Fact]
    public void A_declared_function_can_be_passed_where_a_function_type_is_expected() =>
        Assert.Equal(16, Run("""
            import std.option { map };
            fn verdoppeln(n: int): int { return n * 2; }

            fn main(): int {
                let o: ?int = 8;
                return map(o, verdoppeln) ?? -1;
            }
            """).Exit);

    [Fact]
    public void A_declared_function_can_be_bound_to_a_local_of_function_type() =>
        Assert.Equal(42, Run("""
            fn verdoppeln(n: int): int { return n * 2; }

            fn main(): int {
                let f: fn(int) -> int = verdoppeln;
                return f(21);
            }
            """).Exit);

    /// <summary>
    /// Der Fall, an dem sich zeigt, dass wirklich <b>diese</b> Funktion gerufen wird und nicht
    /// irgendeine: zwei Kandidaten mit derselben Signatur. Mit nur einer Funktion bliebe der Test
    /// grün, wenn die <c>FunctionId</c> immer dieselbe wäre — dieselbe Lehre wie bei den
    /// Interface-Tests aus P3 und beim Dispatch-Test aus M8/S4.
    /// </summary>
    [Fact]
    public void The_reference_names_which_function_runs() =>
        Assert.Equal("2\n30\n", Run("""
            import std.io.console { println };
            import std.option { map };

            fn verdoppeln(n: int): int { return n * 2; }
            fn verdreissigfachen(n: int): int { return n * 30; }

            fn zeige(f: fn(int) -> int) {
                println(f"{map(1, f) ?? -1}");
            }

            fn main(): int {
                zeige(verdoppeln);
                zeige(verdreissigfachen);
                return 0;
            }
            """).Out);

    /// <summary>
    /// Eine so referenzierte Funktion darf der Erreichbarkeitsanalyse nicht zum Opfer fallen. Sie
    /// wird nirgends <b>gerufen</b> — sie wird nur weitergereicht, und der Aufruf steht als
    /// <c>callind</c> da, der seinen Zielnamen nicht kennt.
    ///
    /// <para><c>Reachability</c> behandelt <c>MakeClosure</c> bereits als Wurzel; der Test hält
    /// fest, dass das auch für diesen neuen Erzeuger von <c>MakeClosure</c> gilt. Ohne ihn wäre
    /// der Fehler ein Laufzeitabsturz beim Nutzer, nicht ein roter Test.</para>
    /// </summary>
    [Fact]
    public void A_function_used_only_as_a_value_survives_reachability_pruning() =>
        Assert.Equal(7, Run("""
            fn nurAlsWert(n: int): int { return n + 1; }

            fn anwenden(f: fn(int) -> int, x: int): int { return f(x); }

            fn main(): int {
                return anwenden(nurAlsWert, 6);
            }
            """).Exit);

    /// <summary>
    /// Eine generische Funktion als Wert wird <b>abgelehnt</b>, und zwar mit einer Meldung, die
    /// den Ausweg nennt. Die Typargumente hätten keine Aufrufstelle, aus der sie kommen könnten;
    /// still irgendeine Instanz zu nehmen wäre die gefährliche Antwort.
    /// </summary>
    [Fact]
    public void A_generic_function_as_a_value_is_rejected_with_a_reason()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", """
            fn identisch<T>(x: T): T { return x; }

            fn main(): int {
                let f: fn(int) -> int = identisch;
                return f(1);
            }
            """);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        var binding = comp.Resolve();
        var types = Semantics.Analyze(comp, binding, de);

        ModuleLowerer.Lower(comp, binding, types, de, verify: true);

        var diagnostic = Assert.Single(de.Diagnostics, d => d.Code == "LYR-IR0001");
        Assert.Contains("generic function", diagnostic.Message);
        Assert.Contains("lambda", diagnostic.Message);
    }

    // ------------------------------------------------------------------ Lambda im f-String

    /// <summary>
    /// <c>f"{map(o, (n: int) =&gt; n * 2)}"</c> war ein Syntaxfehler: das <c>:</c> der
    /// Parameter-Annotation wurde als Format-Spec-Trenner gelesen (§1.5). Der Lexer zählte
    /// geschweifte Klammern, aber keine runden — und genau die bringt ein Lambda mit.
    /// </summary>
    [Fact]
    public void A_lambda_inside_an_interpolation_parses() =>
        Assert.Equal("16\n", Run("""
            import std.io.console { println };
            import std.option { map };

            fn main(): int {
                let o: ?int = 8;
                println(f"{map(o, (n: int) => n * 2) ?? -1}");
                return 0;
            }
            """).Out);

    /// <summary>
    /// Die Gegenprobe, und sie ist die wichtigere: eine Format-Spec auf oberster Ebene muss
    /// weiterhin eine sein. Ein Fix, der das <c>:</c> gar nicht mehr als Trenner liest, wäre mit
    /// dem Test darüber allein grün — und hätte jede Format-Spec der Sprache stillgelegt.
    /// </summary>
    [Fact]
    public void A_format_spec_at_the_top_level_still_separates() =>
        Assert.Equal("3.14\n2.00\n", Run("""
            import std.io.console { println };

            fn main(): int {
                let xs = [1, 2, 3];
                println(f"{3.14159:N2}");
                println(f"{xs[1]:N2}");
                return 0;
            }
            """).Out);
}
