using System.Runtime.CompilerServices;
using System.Text;
using Lyric.Core;
using Lyric.Ir;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Ir;

/// <summary>
/// Tests für das Lowering AST → IR (M5/P4).
///
/// <para><b>Golden-Tests sind das Rückgrat</b>: Quelltext rein, IR-Dump raus, gegen Snapshot.
/// Quelle und Erwartung liegen als Paar in <c>golden/lowering/&lt;name&gt;.lyr</c> und
/// <c>.ir</c> — dasselbe Muster wie die Lexer-Goldens.</para>
///
/// <para><b>Der Verifier läuft in jedem dieser Tests mit.</b> <see cref="ModuleLowerer.Lower"/>
/// ruft <see cref="IrVerifier.VerifyOrThrow"/>, ein Befund wirft also, bevor überhaupt
/// verglichen wird. Damit sind die 74 Verifier-Testfälle erstmals gegen echtes Lowering scharf
/// statt nur gegen handgebaute Fixtures — das war der eigentliche Zweck von P3.</para>
///
/// <para>Die Unit-Tests darunter nageln die Invarianten fest, die man im Dump zwar sehen, aber
/// leicht übersehen kann (Blockdichte, Parameter-Konvention, verworfener toter Code).</para>
/// </summary>
public class LoweringTests
{
    // ------------------------------------------------------------------ Helfer

    /// <summary>Quelltext → IR. Bricht ab, wenn die Sema meckert: auf fehlerhaftem AST wäre
    /// jedes Lowering-Ergebnis Raten.</summary>
    private static IrModule Lower(string source, bool verify = true)
    {
        var (ir, de) = TryLower(source, verify);
        Assert.True(ir is not null, "lowering reported diagnostics:\n" + Render(de));
        return ir!;
    }

    /// <summary>Wie <see cref="Lower"/>, akzeptiert aber gemeldete Scope-Grenzen. Bricht weiterhin
    /// bei Sema-Fehlern ab: auf fehlerhaftem AST wäre jedes Lowering-Ergebnis Raten.</summary>
    private static (IrModule? Ir, DiagnosticEngine De) TryLower(string source, bool verify = true)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        var binding = comp.Resolve();
        var types = Semantics.Analyze(comp, binding, de);

        Assert.False(de.HasErrors, "source did not type-check:\n" + Render(de));
        return (ModuleLowerer.Lower(comp, binding, types, de, verify), de);
    }

    private static string Render(DiagnosticEngine de)
    {
        var writer = new StringWriter();
        de.RenderText(writer);
        return writer.ToString();
    }

    private static IrFunction Single(string source) => Assert.Single(Lower(source).Functions);

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");

    private static string GoldenDir([CallerFilePath] string thisFile = "")
        => Path.Combine(Path.GetDirectoryName(thisFile)!, "golden", "lowering");

    // ------------------------------------------------------------------ 1) Golden

    private static bool UpdateMode =>
        Environment.GetEnvironmentVariable("LYRIC_UPDATE_SNAPSHOTS") is "1" or "true";

    [Theory]
    [InlineData("arith")]           // Parameter, binop, ret — das Grundgerüst
    [InlineData("if_else")]         // beide Zweige fallen durch -> Merge-Block
    [InlineData("if_both_return")]  // beide Zweige returnen -> KEIN Merge-Block
    [InlineData("if_no_else")]      // ohne else ist der false-Zweig der Merge-Block
    [InlineData("while_loop")]      // Back-Edge, break, continue, verschachtelte ifs
    [InlineData("do_while")]        // continue springt zur Bedingung, nicht an den Body-Anfang
    [InlineData("if_expr")]         // if als Ausdruck über ein synthetisches Local
    [InlineData("short_circuit")]   // && und || als Kontrollfluss
    [InlineData("calls")]           // void-Call, Vorwärts-Call, Rekursion
    [InlineData("cast")]            // convert + elidierte Identität
    [InlineData("incdec")]          // ++/-- prä und post, compound assign
    [InlineData("objects")]         // newobj, Feld lesen/schreiben, Referenz-Semantik
    [InlineData("objects_nested")]  // Klasse als Feldtyp, plus ein rekursiver Typ
    [InlineData("methods")]         // Empfänger als Parameter 0, static-Fabrik, 'this'
    [InlineData("arrays")]          // Literal, [x]*n, xs+ys, Index lesend/schreibend, .length
    [InlineData("optionals")]       // null, ??, !, Flow-Narrowing
    [InlineData("enums")]           // Varianten, match, Tag-Dispatch, Pattern-Dekomposition
    [InlineData("interfaces")]      // mkiface, callvirt, vtable-Zeilen, Default vs. Ueberschreibung
    [InlineData("structs")]         // structcopy an den Bindepunkten, verschachtelter Wert-Typ
    public void Golden_lowering_matches_snapshot(string name)
    {
        var dir = GoldenDir();
        var sourcePath = Path.Combine(dir, name + ".lyr");
        var snapshotPath = Path.Combine(dir, name + ".ir");

        Assert.True(File.Exists(sourcePath), $"missing source fixture: {sourcePath}");
        var actual = Normalize(IrPrinter.Dump(Lower(File.ReadAllText(sourcePath, Encoding.UTF8))));

        if (UpdateMode)
        {
            File.WriteAllText(snapshotPath, actual, new UTF8Encoding(false));
            return;
        }

        Assert.True(File.Exists(snapshotPath),
            $"missing snapshot: {snapshotPath}\n" +
            "Run once with LYRIC_UPDATE_SNAPSHOTS=1 to generate it, then review and commit.");

        Assert.Equal(Normalize(File.ReadAllText(snapshotPath, Encoding.UTF8)), actual);
    }

    [Fact]
    public void Every_fixture_lowers_to_verifier_clean_ir()
    {
        // Explizit mit verify:false lowern und danach selbst prüfen — sonst würde der Test nur
        // wiederholen, dass ModuleLowerer den Verifier aufruft, statt dessen Ergebnis zu zeigen.
        foreach (var path in Directory.GetFiles(GoldenDir(), "*.lyr"))
        {
            var module = Lower(File.ReadAllText(path, Encoding.UTF8), verify: false);
            var findings = IrVerifier.Verify(module);
            Assert.True(findings.Count == 0,
                $"{Path.GetFileName(path)} produced malformed IR:\n  " + string.Join("\n  ", findings));
        }
    }

    [Fact]
    public void Gate_program_lowers_end_to_end()
    {
        // examples/arith.lyr ist das M5-Gate: bewusst stdlib-frei, damit es allein aus
        // M5-Mitteln compiliert. Bricht das, ist das Exit-Kriterium des Meilensteins verletzt.
        var path = Path.Combine(RepoRoot(), "examples", "arith.lyr");
        Assert.True(File.Exists(path), $"missing gate program: {path}");

        var module = Lower(File.ReadAllText(path, Encoding.UTF8));

        Assert.Equal(6, module.Functions.Count);
        Assert.Contains(module.Functions, f => f.Name == "main.main");
        Assert.Empty(IrVerifier.Verify(module));
    }

    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    // ------------------------------------------------------------- 1b) Source-first Stdlib

    /// <summary>Wie <see cref="TryLower"/>, aber mit der echten Stdlib auf dem Modulpfad — sie ist
    /// gewöhnlicher Lyric-Quelltext und wird beim Auflösen geladen.</summary>
    private static IrModule LowerWithStdlib(string source)
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
        return ir!;
    }

    [Fact]
    public void Stdlib_is_loaded_from_source_when_imported()
    {
        // Der Kern von „source-first": std.io.console ist ein normales Lyric-Modul, das beim
        // Auflösen geladen und typgeprüft wird — kein Sonderfall im Compiler.
        var module = LowerWithStdlib("""
            import std.io.console { println };
            fn main(): int { println("hi"); return 0; }
            """);

        var import = Assert.Single(module.Imports);
        Assert.Equal("std.io.console.println", import.Name);
        Assert.Equal(new IrScalarType(IrScalar.String), Assert.Single(import.ParamTypes));
        Assert.Equal(new IrScalarType(IrScalar.Void), import.ReturnType);
    }

    [Fact]
    public void Stdlib_signatures_are_enforced()
    {
        // Der Beweis, dass die Signatur wirklich ankommt: vorher war jedes Stdlib-Symbol opak und
        // `println(42)` wäre stillschweigend durchgegangen.
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr",
            "import std.io.console { println };\nfn main(): int { println(42); return 0; }");
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de)
        {
            ModuleLoader = StdlibLoader.ForRoot(Path.Combine(RepoRoot(), "stdlib"), sm, de),
        };
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        Semantics.Analyze(comp, comp.Resolve(), de);

        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0001");
    }

    [Fact]
    public void Interpolation_lowers_to_a_concat_chain()
    {
        var module = LowerWithStdlib("""
            fn main(): int { let n = 7; let s = f"n={n}!"; return 0; }
            """);

        // "n=" ++ fromInt(n) ++ "!" — zwei concat, ein Wandler. Nur die tatsächlich benutzten
        // Helfer stehen in der Tabelle, nicht alle deklarierten.
        Assert.Equal(new[] { "std.string.fromInt", "std.string.concat" },
            module.Imports.Select(i => i.Name).Distinct());
    }

    [Fact]
    public void Adjacent_text_segments_collapse_into_one_constant()
    {
        // f"ab" hat kein Loch: das Ergebnis ist eine schlichte Konstante, kein concat.
        var module = LowerWithStdlib("fn main(): int { let s = f\"ab\"; return 0; }");
        Assert.Empty(module.Imports);
    }

    [Fact]
    public void A_bodyless_function_outside_the_stdlib_is_an_error()
    {
        // Genau der Mechanismus, den die Stdlib nutzt — in User-Code muss er zu sein, sonst
        // deklariert sich jeder beliebige Natives.
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", "fn native(x: int): int;\nfn main(): int { return 0; }");
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        Semantics.Analyze(comp, comp.Resolve(), de);

        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0051");
    }

    // ------------------------------------------------------------------ 2) Invarianten

    [Fact]
    public void Block_ids_are_dense_and_entry_is_the_first_block()
    {
        var fn = Single("""
            fn f(limit: int): int {
                var acc = 0;
                var n = limit;
                while (n > 0) {
                    if (n % 2 == 0) { acc += n; } else { acc -= n; }
                    n -= 1;
                }
                return acc;
            }
            """);

        for (var i = 0; i < fn.Blocks.Count; i++)
            Assert.Equal(i, fn.Blocks[i].Id.Value);
        Assert.Equal(fn.Blocks[0].Id, fn.Entry);
        Assert.True(fn.Blocks.Count > 4, "fixture should produce a non-trivial CFG");
    }

    [Fact]
    public void First_locals_are_the_parameters_in_order()
    {
        var fn = Single("fn f(alpha: int, beta: bool): int { let gamma = 1; return alpha + gamma; }");

        Assert.Equal(2, fn.ParamCount);
        Assert.Equal("alpha", fn.Locals[0].Name);
        Assert.Equal("beta", fn.Locals[1].Name);
        Assert.Equal("gamma", fn.Locals[2].Name);
    }

    [Fact]
    public void Statements_after_a_return_are_dropped()
    {
        // Ein Block für den toten Code wäre unerreichbar — und der Verifier lehnt unerreichbare
        // Blöcke ab. Das Lowering muss die Statement-Liste abbrechen, statt hinterher aufzuräumen.
        var fn = Single("fn f(): int { return 1; let dead = 2; }");

        Assert.Single(fn.Blocks);
        Assert.DoesNotContain(fn.Locals, l => l.Name == "dead");
    }

    [Fact]
    public void If_with_both_arms_returning_creates_no_merge_block()
    {
        var fn = Single("fn f(n: int): int { if (n > 0) { return 1; } else { return 0; } }");

        // bb0 = Bedingung, bb1 = then, bb2 = else. Ein vierter Block wäre der unerreichbare Merge.
        Assert.Equal(3, fn.Blocks.Count);
    }

    [Fact]
    public void Void_function_gets_an_implicit_return()
    {
        var fn = Single("fn f(n: int) { var x = n; x += 1; }");

        var terminator = Assert.IsType<Return>(fn.Blocks[^1].Terminator);
        Assert.Null(terminator.Value);
    }

    [Fact]
    public void Identity_cast_is_elided()
    {
        var fn = Single("fn f(x: int): int { return x as int; }");

        Assert.DoesNotContain(fn.Blocks.SelectMany(b => b.Insts), op => op is Lyric.Ir.Convert);
    }

    [Fact]
    public void Widening_cast_emits_a_convert()
    {
        var fn = Single("fn f(x: int32): int64 { return x as int64; }");

        var convert = Assert.Single(fn.Blocks.SelectMany(b => b.Insts).OfType<Lyric.Ir.Convert>());
        Assert.Equal(new IrScalarType(IrScalar.I32), convert.From);
        Assert.Equal(new IrScalarType(IrScalar.I64), convert.To);
    }

    [Fact]
    public void Float32_literal_is_narrowed_by_the_lowering()
    {
        // Ein f32-Const, dessen Wert kein f32-Wert ist, wäre malformed. Die Verengung gehört ins
        // Lowering, damit der Wert im Bytecode deterministisch derselbe ist — sonst meldet es
        // der Verifier, und zwar zu Recht.
        var fn = Single("fn f(): float32 { return 0.1f32; }");

        var constant = Assert.Single(fn.Blocks.SelectMany(b => b.Insts).OfType<Const>());
        var value = Assert.IsType<FloatConst>(constant.Value);
        Assert.Equal((double)(float)0.1, value.Value);
    }

    [Fact]
    public void Short_circuit_routes_the_value_through_a_synthetic_local()
    {
        // Ein Temp darf nur einmal definiert werden, kann also nicht den Wert aus zwei Zweigen
        // tragen. Genau deshalb braucht diese IR kein Phi.
        var fn = Single("fn f(a: bool, b: bool): bool { return a && b; }");

        Assert.Contains(fn.Locals, l => l.Name.StartsWith("$and", StringComparison.Ordinal));
    }

    [Fact]
    public void Recursion_and_forward_calls_resolve()
    {
        var module = Lower("""
            fn fact(n: int): int {
                if (n <= 1) { return 1; }
                return n * fact(n - 1);
            }
            fn main(): int { return helper(); }
            fn helper(): int { return fact(3); }
            """);

        var calls = module.Functions.SelectMany(f => f.Blocks).SelectMany(b => b.Insts)
            .OfType<Call>().ToList();
        Assert.Equal(3, calls.Count);
        Assert.All(calls, c => Assert.InRange(c.Target.Value, 0, module.Functions.Count - 1));
    }

    [Fact]
    public void Short_circuit_inside_a_loop_condition_seals_the_right_block()
    {
        // '&&' erzeugt selbst Blöcke, der Cursor steht nach der Bedingung also nicht mehr auf dem
        // Cond-Block. Wer hier den Cond-Block statt des aktuellen versiegelt, baut einen Sprung
        // ins Leere — der Verifier fängt es, aber nur wenn der Fall überhaupt vorkommt.
        var fn = Single("""
            fn f(a: int, b: int): int {
                var x = a;
                var y = b;
                while (x > 0 && y > 0) {
                    x -= 1;
                    y -= 1;
                }
                return x;
            }
            """);

        Assert.All(fn.Blocks, b => Assert.NotNull(b.Terminator));
    }

    [Fact]
    public void Continue_in_a_do_while_jumps_to_the_condition()
    {
        // Nicht an den Body-Anfang: 'do' prüft am Ende, ein continue muss dort landen, sonst
        // wird die Bedingung übersprungen.
        var fn = Single("""
            fn f(n: int): int {
                var i = n;
                var seen = 0;
                do {
                    i -= 1;
                    if (i % 2 == 0) { continue; }
                    seen += 1;
                } while (i > 0);
                return seen;
            }
            """);

        // Genau ein Block wird von zwei Branches angesprungen: vom regulären Body-Ende und vom
        // continue. Das ist die Bedingung der do-while — erkennbar an ihrem CondBranch.
        var shared = fn.Blocks.Select(b => b.Terminator).OfType<Branch>()
            .GroupBy(br => br.Target)
            .Where(g => g.Count() >= 2)
            .Select(g => g.Key)
            .ToList();

        var target = Assert.Single(shared);
        Assert.IsType<CondBranch>(fn.Blocks[target.Value].Terminator);
    }

    [Fact]
    public void Lowering_is_deterministic()
    {
        const string source = """
            fn f(a: int, b: int): int {
                var acc = 0;
                if (a > b && a > 0) { acc = a; } else { acc = b; }
                while (acc > 0) { acc -= 1; }
                return if (acc == 0) 1 else 0;
            }
            """;

        Assert.Equal(IrPrinter.Dump(Lower(source)), IrPrinter.Dump(Lower(source)));
    }

    // ------------------------------------------------------------------ 3) Scope-Grenzen

    // Ein Typ, den die IR nicht kennt, ist die ERSTE Grenze, auf die man läuft — noch vor dem
    // Ausdruck, der ihn benutzt. Die Meldung benennt den Lyric-Typ, nicht den Ausdruck: das ist
    // die fundamentalere Aussage. (Arrays gehören seit P2 nicht mehr dazu.)
    [Theory]
    [InlineData("fn f(): int { let g = (x: int) => x + 1; return g(1); }", "type 'fn(int) -> int'")]
    [InlineData("fn f(): int { let t = (1, 2); return 0; }", "type '(int, int)'")]
    public void Non_scalar_types_are_reported_by_name(string source, string expected) =>
        AssertNotSupported(source, expected);

    // Konstrukte, deren Typ skalar ist — hier greift die Grenze erst am Ausdruck bzw. Statement.
    [Theory]
    [InlineData("fn f(): string { return \"a\" + \"b\"; }", "string concatenation")]
    // f-Strings lowern zu einer concat/fromXxx-Kette. Ohne Stdlib auf dem Modulpfad fehlen die
    // Helfer — die Meldung nennt genau den fehlenden, statt „f-Strings gehen nicht" zu behaupten.
    // Beim ersten Loch ist das der Wandler, noch vor dem concat.
    [InlineData("fn f(): string { return f\"n={1}\"; }", "std.string.fromInt")]
    // 'match' über einen Enum lowert seit P3b; über einen Skalar braucht es Literal-Muster, und
    // die sind eine eigene Ausbaustufe.
    [InlineData("fn f(n: int): int { return match (n) { 0 => 1, _ => 2 }; }", "is not an enum")]
    [InlineData("fn f(): int { var s = 0; for (i in 0..3) { s += i; } return s; }", "'for-in'")]
    public void Out_of_scope_constructs_report_where_and_what(string source, string expected) =>
        AssertNotSupported(source, expected);

    /// <summary>Was P4 nicht lowert, ist gültiges Lyric — also eine <b>Diagnose</b> mit
    /// Datei/Zeile/Spalte, kein Absturz. Der Code <c>LYR-IR0001</c> ist die stabile Kategorie
    /// („dieser Compiler-Stand kann das noch nicht"), das Konstrukt steht in der Nachricht.</summary>
    private static void AssertNotSupported(string source, string expected)
    {
        var (ir, de) = TryLower(source);

        Assert.Null(ir); // kein Teilergebnis: die FunctionIds wären verschoben
        var diagnostic = Assert.Single(de.Diagnostics);
        Assert.Equal("LYR-IR0001", diagnostic.Code);
        Assert.Equal(Severity.Error, diagnostic.Severity);
        Assert.Contains(expected, diagnostic.Message, StringComparison.Ordinal);

        // Der Span zeigt in die Quelldatei — genau das war vorher die raue Kante.
        Assert.True(diagnostic.Span.File.IsValid, "diagnostic has no source position");
        Assert.Contains("test.lyr:", Render(de), StringComparison.Ordinal);
    }

    /// <summary>
    /// Ein Typ, dessen Layout an einer Scope-Grenze scheitert, darf die Typ-Tabelle nicht
    /// beschädigen.
    ///
    /// <para><b>Das war ein Compiler-Absturz</b>, kein Schönheitsfehler: <c>Intern</c> trägt den
    /// Platzhalter ein, <i>bevor</i> es die Feldtypen lowert. Warf es danach — hier am Feld-Default
    /// —, blieb der Platzhalter stehen, und die nächste Funktion, die denselben Typ benutzt, las
    /// ein Layout mit <c>FieldNames == null</c>. <c>examples/bank.lyr</c> beendete sich damit über
    /// eine <c>NullReferenceException</c> statt mit einer Diagnose.</para>
    ///
    /// <para>Zwei Funktionen sind Pflicht: mit nur einer läuft der zweite Zugriff nie.</para>
    /// </summary>
    [Fact]
    public void A_type_whose_layout_fails_reports_once_and_does_not_corrupt_the_table()
    {
        // Ausloeser muss im LAYOUT scheitern, nicht an der Konstruktionsstelle — sonst prueft der
        // Test die Dedup nach Span statt die nach Typ. Frueher war es ein Feld-Default; der laeuft
        // seit P5 durch, also jetzt ein Tupel-Feld, das das Lowering noch nicht kennt.
        var (ir, de) = TryLower("""
            class Account {
                owner: string,
                pair: (int, int)
            }

            fn open(who: string): int { let a = Account { owner = who, pair = (1, 2) }; return 0; }
            fn main(): int { let a = Account { owner = "x", pair = (3, 4) }; return 0; }
            """);

        Assert.Null(ir);
        var diagnostic = Assert.Single(de.Diagnostics); // genau einmal, nicht je Funktion
        Assert.Equal("LYR-IR0001", diagnostic.Code);
    }

    [Fact]
    public void Generic_functions_are_skipped_and_calling_them_is_reported()
    {
        // Generics brauchen die Worklist-Monomorphisierung: pro konkretem Typargument-Tupel eine
        // Instanz, ausgehend von den Wurzeln. Bis dahin bekommen sie keine FunctionId.
        AssertNotSupported("""
            fn id<T>(x: T): T { return x; }
            fn main(): int { return id(1); }
            """, "call to 'id'");
    }

    [Fact]
    public void All_scope_limits_of_a_program_are_reported_in_one_run()
    {
        // Eine Meldung pro Aufruf wäre Schikane: wer drei nicht unterstützte Konstrukte benutzt,
        // soll sie in einem Durchlauf sehen. Deshalb sammelt das Lowering pro Funktion weiter.
        var (ir, de) = TryLower("""
            fn a(): int { let g = (x: int) => x + 1; return g(1); }
            fn b(): int { var s = 0; for (i in 0..3) { s += i; } return s; }
            fn c(): int { let t = (1, 2); return 0; }
            """);

        Assert.Null(ir);
        Assert.Equal(3, de.Diagnostics.Count);
        Assert.All(de.Diagnostics, d => Assert.Equal("LYR-IR0001", d.Code));
    }

    [Fact]
    public void Generic_function_alone_lowers_to_an_empty_module()
    {
        Assert.Empty(Lower("fn id<T>(x: T): T { return x; }").Functions);
    }
}
