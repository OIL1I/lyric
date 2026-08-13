using Lyric.Core;
using Lyric.Ir;
using static Lyric.Tests.Ir.BrokenIr;

namespace Lyric.Tests.Ir;

/// <summary>
/// Tests für <see cref="IrVerifier"/>. Aufbau:
/// <list type="number">
/// <item><b>Positiv</b> — jede gültige Fixture läuft befundfrei durch. Das ist gleichzeitig das
/// Regressionsnetz des Verifiers und die Validierung der Fixtures selbst.</item>
/// <item><b>Negativ</b> — eine Invariante pro Test, jeweils ein einziger Defekt.</item>
/// <item><b>Robustheit</b> — Bail-out ohne Kaskade, Isolation zwischen Funktionen, Determinismus,
/// und dass der Verifier auf malformed IR nie selbst crasht.</item>
/// </list>
///
/// Assertions laufen über <b>Substrings</b>, nicht über Snapshots: der Wortlaut der Befunde wird
/// sich ändern, und Goldens würden bei jeder Formulierungs-Verbesserung rot.
/// </summary>
public class VerifierTests
{
    // ------------------------------------------------------------------ helpers

    private static void AssertFinding(IrModule module, string expected)
    {
        var findings = IrVerifier.Verify(module);
        Assert.True(
            findings.Any(f => f.Contains(expected, StringComparison.Ordinal)),
            $"expected a finding containing:\n  {expected}\nbut got {findings.Count} finding(s):\n  " +
            string.Join("\n  ", findings));
    }

    private static void AssertClean(IrModule module)
    {
        var findings = IrVerifier.Verify(module);
        Assert.True(findings.Count == 0,
            "expected no findings, got:\n  " + string.Join("\n  ", findings));
    }

    private static IReadOnlyList<string> FindingsOf(IrModule module) => IrVerifier.Verify(module);

    /// <summary>Ein-Block-void-Funktion mit nacktem <c>ret</c> — Träger für Typ-Defekte, die
    /// keinen weiteren Kontext brauchen.</summary>
    private static IrModule VoidFn(List<IrLocal> locals, List<IrTemp> temps, List<IrOp> insts)
        => Module(Fn("main.f", VoidT, 0, locals, temps,
            new List<IrBlock> { Block(0, insts, new Return(null, Sp)) }));

    // ------------------------------------------------------------------ 1) Positiv

    [Theory]
    [MemberData(nameof(ValidFixtures))]
    public void Valid_fixtures_verify_without_findings(string name) => AssertClean(Fixtures.Build(name));

    public static TheoryData<string> ValidFixtures
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in Fixtures.AllNames) data.Add(name);
            return data;
        }
    }

    [Fact]
    public void Verify_is_deterministic()
    {
        // Modul mit mehreren Befunden: die Reihenfolge muss über Läufe stabil sein.
        var first = FindingsOf(Broken_for_determinism());
        var second = FindingsOf(Broken_for_determinism());
        Assert.Equal(first, second);
        Assert.True(first.Count > 1, "fixture should produce more than one finding");

        static IrModule Broken_for_determinism() => Mutate("diamond", m =>
        {
            m.Functions[0].Blocks[0].Insts[2] = new BinOp(T(2), IrBinKind.Add, I32, T(0), T(1), Sp);
            m.Functions[0].Blocks[3].Terminator = new Return(null, Sp);
        });
    }

    [Fact]
    public void VerifyOrThrow_passes_valid_ir() => IrVerifier.VerifyOrThrow(Fixtures.Build("loop"));

    [Fact]
    public void VerifyOrThrow_throws_and_names_the_finding()
    {
        var module = Mutate("single_block", m => m.Functions[0].Blocks[0].Terminator = new Return(null, Sp));
        var ex = Assert.Throws<InternalCompilationException>(() => IrVerifier.VerifyOrThrow(module));
        Assert.Contains("'ret' carries no value", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- 2a) Phase 0: Tabellen

    [Fact]
    public void Locals_table_must_be_dense() =>
        AssertFinding(
            Mutate("single_block", m => m.Functions[0].Locals[0] = new IrLocal(L(5), "a", I64)),
            "locals table not dense at index 0: found l5");

    [Fact]
    public void Local_must_not_have_type_void() =>
        AssertFinding(
            Mutate("single_block", m => m.Functions[0].Locals[0] = new IrLocal(L(0), "a", VoidT)),
            "local l0 (a) has type void");

    [Fact]
    public void Temps_table_must_be_dense() =>
        AssertFinding(
            Mutate("single_block", m => m.Functions[0].Temps[1] = new IrTemp(T(7), I64)),
            "temps table not dense at index 1: found t7");

    [Fact]
    public void Temp_must_not_have_type_void() =>
        AssertFinding(
            Mutate("single_block", m => m.Functions[0].Temps[0] = new IrTemp(T(0), VoidT)),
            "temp t0 has type void");

    [Fact]
    public void ParamCount_must_fit_the_locals_table() =>
        AssertFinding(
            Module(Fn("main.f", VoidT, 3, new List<IrLocal>(), new List<IrTemp>(),
                new List<IrBlock> { Block(0, new List<IrOp>(), new Return(null, Sp)) })),
            "paramCount 3 out of range (locals: 0)");

    [Fact]
    public void Dest_must_be_in_the_temp_table() =>
        AssertFinding(
            Mutate("void_store", m =>
                m.Functions[0].Blocks[0].Insts[0] = new Const(T(9), I64, new IntConst(0), Sp)),
            "dest t9 is not in the temp table");

    [Fact]
    public void Temp_must_not_be_defined_twice() =>
        AssertFinding(
            Mutate("single_block", m =>
                m.Functions[0].Blocks[0].Insts[1] = new LoadLocal(T(0), L(1), I64, Sp)),
            "t0 is defined more than once (first at bb0: #0)");

    [Fact]
    public void Declared_temp_must_be_defined() =>
        AssertFinding(
            Mutate("single_block", m => m.Functions[0].Temps.Add(new IrTemp(T(3), I64))),
            "t3 is declared in the temp table but never defined");

    [Fact]
    public void Unused_definition_is_legal()
    {
        // Gegenprobe zum Test darüber: eine verworfene Definition ist KEIN Fehler — `foo();` bei
        // `foo(): int` erzeugt genau das.
        AssertClean(VoidFn(
            new List<IrLocal>(),
            new List<IrTemp> { new(T(0), I64) },
            new List<IrOp> { new Const(T(0), I64, new IntConst(7), Sp) }));
    }

    // ------------------------------------------------------------- 2b) Phase 1: CFG-Form

    [Fact]
    public void Function_must_have_a_block() =>
        AssertFinding(
            Module(Fn("main.f", VoidT, 0, new List<IrLocal>(), new List<IrTemp>(), new List<IrBlock>())),
            "no blocks");

    [Fact]
    public void Block_ids_must_be_unique() =>
        AssertFinding(
            Module(Fn("main.f", VoidT, 0, new List<IrLocal>(), new List<IrTemp>(), new List<IrBlock>
            {
                Block(0, new List<IrOp>(), new Return(null, Sp)),
                Block(0, new List<IrOp>(), new Return(null, Sp)),
            })),
            "duplicate block id bb0");

    [Fact]
    public void Block_table_must_be_dense() =>
        AssertFinding(
            Module(Fn("main.f", VoidT, 0, new List<IrLocal>(), new List<IrTemp>(), new List<IrBlock>
            {
                Block(0, new List<IrOp>(), new Branch(B(5), Sp)),
                Block(5, new List<IrOp>(), new Return(null, Sp)),
            })),
            "block table not dense at index 1: found bb5");

    [Fact]
    public void Entry_must_exist() =>
        AssertFinding(
            Mutate("single_block", m => m.Functions[0].Entry = B(9)),
            "entry block bb9 does not exist");

    [Fact]
    public void Entry_must_be_the_first_block() =>
        AssertFinding(
            Mutate("diamond", m => m.Functions[0].Entry = B(2)),
            "entry is bb2, expected the first block bb0");

    [Fact]
    public void Block_must_have_a_terminator() =>
        AssertFinding(
            Mutate("single_block", m => m.Functions[0].Blocks[0].Terminator = null),
            "bb0: has no terminator");

    [Fact]
    public void Branch_target_must_exist() =>
        AssertFinding(
            Mutate("diamond", m =>
                m.Functions[0].Blocks[0].Terminator = new CondBranch(T(2), B(1), B(9), Sp)),
            "bb0: terminator: branches to unknown block bb9");

    [Fact]
    public void Entry_must_not_have_predecessors()
    {
        // Kein Bail-out: die Availability-Analyse bleibt gültig, deshalb genau ein Befund und
        // keine Folgefehler aus Phase 3.
        var module = Mutate("diamond", m => m.Functions[0].Blocks[1].Terminator = new Branch(B(0), Sp));
        var findings = FindingsOf(module);
        Assert.Single(findings);
        Assert.Contains("entry bb0 has predecessors bb1", findings[0], StringComparison.Ordinal);
    }

    // ------------------------------------------------------ 2b') Objekte und Typ-Tabelle

    /// <summary>Ein Modul mit genau einem Typ <c>P { x: i32 }</c> und einer void-Funktion, deren
    /// Instruktionen der Test vorgibt. Träger für die Objekt-Defekte.</summary>
    private static IrModule WithPoint(List<IrTemp> temps, List<IrOp> insts, List<IrTypeDef>? types = null)
        => ModuleWithTypes(
            types ?? new List<IrTypeDef> { TypeDef("P", ("x", I32)) },
            Fn("main.f", VoidT, 0, new List<IrLocal>(), temps,
                new List<IrBlock> { Block(0, insts, new Return(null, Sp)) }));

    [Fact]
    public void Newobj_type_must_exist() =>
        AssertFinding(
            WithPoint(
                new List<IrTemp> { new(T(0), Ref(9)) },
                new List<IrOp> { new NewObject(T(0), Ty(9), new IrRefType(Ty(9)), Sp) }),
            "newobj references type ty9 which is out of range");

    [Fact]
    public void Field_index_must_be_inside_the_type() =>
        AssertFinding(
            WithPoint(
                new List<IrTemp> { new(T(0), Ref(0)), new(T(1), I32) },
                new List<IrOp>
                {
                    new NewObject(T(0), Ty(0), new IrRefType(Ty(0)), Sp),
                    new LoadField(T(1), T(0), Ty(0), Fld(3), I32, Sp),
                }),
            "loadfield references field #3 of type ty0 'P', which has 1 field(s)");

    [Fact]
    public void Storefield_value_must_match_the_declared_field_type() =>
        AssertFinding(
            WithPoint(
                new List<IrTemp> { new(T(0), Ref(0)), new(T(1), Str) },
                new List<IrOp>
                {
                    new NewObject(T(0), Ty(0), new IrRefType(Ty(0)), Sp),
                    new Const(T(1), Str, new StringConst("nope"), Sp),
                    new StoreField(T(0), Ty(0), Fld(0), T(1), Sp),
                }),
            "storefield into ty0#0 takes i32, but t1 is string");

    /// <summary>
    /// Der Objekt-Operand muss eine Referenz auf <b>genau</b> den Typ sein, den die Instruktion
    /// nennt. Beide zu tragen ist Absicht (Bytecode.md §5) — laufen sie auseinander, prüft der
    /// Bytecode-Leser den Feldindex später gegen das falsche Layout.
    /// </summary>
    [Fact]
    public void Field_access_needs_a_reference_to_the_named_type() =>
        AssertFinding(
            WithPoint(
                new List<IrTemp> { new(T(0), I32), new(T(1), I32) },
                new List<IrOp>
                {
                    new Const(T(0), I32, new IntConst(0), Sp),
                    new LoadField(T(1), T(0), Ty(0), Fld(0), I32, Sp),
                }),
            "loadfield expects t0 to hold type ty0, found i32");

    [Fact]
    public void A_void_field_is_reported() =>
        AssertFinding(
            WithPoint(new List<IrTemp>(), new List<IrOp>(),
                new List<IrTypeDef> { TypeDef("P", ("nothing", VoidT)) }),
            "type ty0 'P': field #0 'nothing' is void");

    [Fact]
    public void A_field_type_referencing_an_unknown_type_is_reported() =>
        AssertFinding(
            WithPoint(new List<IrTemp>(), new List<IrOp>(),
                new List<IrTypeDef> { TypeDef("P", ("other", Ref(4))) }),
            "field #0 'other' references type ty4, which is out of range");

    /// <summary>Ein Typ darf sich selbst als Feldtyp nennen — <c>class Node { next: Node }</c> ist
    /// gültig. Der Gegentest zu den beiden Bereichs-Befunden oben: die Prüfung darf Rekursion nicht
    /// mit einem Fehler verwechseln.</summary>
    [Fact]
    public void A_self_referential_type_is_clean() =>
        AssertClean(WithPoint(new List<IrTemp>(), new List<IrOp>(),
            new List<IrTypeDef> { TypeDef("Node", ("payload", I32), ("next", Ref(0))) }));

    // ------------------------------------------------------ 2c) Phase 2: Erreichbarkeit

    [Fact]
    public void Unreachable_block_is_reported()
    {
        var module = Mutate("diamond", m =>
            m.Functions[0].Blocks.Add(Block(4, new List<IrOp>(), new Unreachable(Sp))));
        var findings = FindingsOf(module);
        Assert.Single(findings);
        Assert.Contains("bb4: unreachable from entry bb0", findings[0], StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- 2d) Phase 3: Def/Use

    [Fact]
    public void Use_before_definition_in_the_same_block()
    {
        var module = Mutate("single_block", m =>
        {
            var insts = m.Functions[0].Blocks[0].Insts;
            (insts[0], insts[2]) = (insts[2], insts[0]); // BinOp vor seine Loads ziehen
        });
        AssertFinding(module, "uses t0 before its definition (defined at bb0: #2)");
    }

    [Fact]
    public void Use_from_a_sibling_branch_is_not_dominated()
    {
        // t1 wird nur in bb1 definiert, in bb2 benutzt. Über den Pfad bb0 -> bb2 ist es nicht
        // verfügbar; zur Laufzeit wäre das ein Read auf einen uninitialisierten Slot.
        //
        // Was dieser Test festnagelt: availIn kommt aus dem availOut der Prädecessoren, nicht aus
        // "alle Temps der Funktion" (die schwache Variante, die nur die Tabellen-Existenz prüft).
        // Die MEET-Operation pinnt er nicht — bb2 hat nur einen Prädecessor, dort ist Vereinigung
        // gleich Schnittmenge. Das macht erst Use_reachable_only_through_the_back_edge.
        var module = Module(Fn("main.g", I64, 0,
            new List<IrLocal>(),
            new List<IrTemp> { new(T(0), Bool), new(T(1), I64), new(T(2), I64), new(T(3), I64) },
            new List<IrBlock>
            {
                Block(0, new List<IrOp> { new Const(T(0), Bool, new BoolConst(true), Sp) },
                    new CondBranch(T(0), B(1), B(2), Sp)),
                Block(1, new List<IrOp> { new Const(T(1), I64, new IntConst(1), Sp) },
                    new Branch(B(3), Sp)),
                Block(2, new List<IrOp> { new BinOp(T(2), IrBinKind.Add, I64, T(1), T(1), Sp) },
                    new Branch(B(3), Sp)),
                Block(3, new List<IrOp> { new Const(T(3), I64, new IntConst(0), Sp) },
                    new Return(T(3), Sp)),
            }));

        AssertFinding(module, "bb2: #0: uses t1 before its definition (defined at bb1: #0)");
    }

    [Fact]
    public void Use_reachable_only_through_the_back_edge_is_not_dominated()
    {
        // Der schärfste Test des Availability-Dataflows: t7 wird im Loop-Body (bb2) definiert und
        // im Header (bb1) benutzt. Über die Back-Edge ist es verfügbar, über bb0 -> bb1 beim
        // ersten Durchlauf nicht. Nur die SCHNITTMENGE über die Prädecessoren fängt das —
        // mit einer Vereinigung ("kann verfügbar sein") würde es durchrutschen.
        var module = Mutate("loop", m =>
            m.Functions[0].Blocks[1].Insts[2] = new BinOp(T(4), IrBinKind.Lt, Bool, T(2), T(7), Sp));

        var findings = FindingsOf(module);
        Assert.Single(findings);
        Assert.Contains("bb1: #2: uses t7 before its definition (defined at bb2: #2)",
            findings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Operand_outside_the_temp_table_does_not_crash_the_type_checks()
    {
        // Der Punkt dieses Tests: der Verifier meldet und macht weiter, statt beim Tabellen-
        // Lookup in eine IndexOutOfRangeException zu laufen.
        var module = Mutate("single_block", m =>
            m.Functions[0].Blocks[0].Terminator = new Return(T(9), Sp));
        AssertFinding(module, "bb0: terminator: uses t9, which is not in the temp table");
    }

    // -------------------------------------------------- 2e) Phase 3: Typen — Const

    [Fact]
    public void Const_type_must_match_the_temp_table() =>
        AssertFinding(
            VoidFn(new List<IrLocal>(), new List<IrTemp> { new(T(0), I32) },
                new List<IrOp> { new Const(T(0), I64, new IntConst(0), Sp) }),
            "const declares type i64 but t0 is i32 in the temp table");

    [Fact]
    public void Const_value_kind_must_match_its_type() =>
        AssertFinding(
            VoidFn(new List<IrLocal>(), new List<IrTemp> { new(T(0), Bool) },
                new List<IrOp> { new Const(T(0), Bool, new IntConst(0), Sp) }),
            "integer const does not match type bool");

    [Fact]
    public void Integer_const_must_fit_its_width() =>
        AssertFinding(
            VoidFn(new List<IrLocal>(), new List<IrTemp> { new(T(0), U8) },
                new List<IrOp> { new Const(T(0), U8, new IntConst(300), Sp) }),
            "integer const 300 does not fit the bit pattern of u8");

    [Fact]
    public void Negative_integer_const_is_two_complement_zero_extended()
    {
        // -1 als i64 ist 0xFFFF_FFFF_FFFF_FFFF. Gegenprobe zum Width-Check: die Kodierung ist
        // das Bitmuster, nicht der vorzeichenbehaftete Wertebereich.
        AssertClean(VoidFn(new List<IrLocal>(), new List<IrTemp> { new(T(0), I64) },
            new List<IrOp> { new Const(T(0), I64, new IntConst(ulong.MaxValue), Sp) }));

        // Dasselbe Bitmuster passt aber nicht in i8.
        AssertFinding(
            VoidFn(new List<IrLocal>(), new List<IrTemp> { new(T(0), new IrScalarType(IrScalar.I8)) },
                new List<IrOp>
                {
                    new Const(T(0), new IrScalarType(IrScalar.I8), new IntConst(ulong.MaxValue), Sp)
                }),
            "does not fit the bit pattern of i8");
    }

    [Fact]
    public void Float_const_must_be_representable_in_f32() =>
        AssertFinding(
            VoidFn(new List<IrLocal>(), new List<IrTemp> { new(T(0), F32) },
                new List<IrOp> { new Const(T(0), F32, new FloatConst(0.1), Sp) }),
            "is not exactly representable as f32");

    [Theory]
    [InlineData(0.5)]                 // exakt in f32
    [InlineData(double.NaN)]          // nicht endlich -> ausgenommen
    [InlineData(double.PositiveInfinity)]
    public void Representable_float_const_is_clean(double value) =>
        AssertClean(VoidFn(new List<IrLocal>(), new List<IrTemp> { new(T(0), F32) },
            new List<IrOp> { new Const(T(0), F32, new FloatConst(value), Sp) }));

    [Theory]
    [InlineData(0x110000)] // über dem Unicode-Maximum
    [InlineData(0xD800)]   // Surrogat ist kein Unicode Scalar Value
    [InlineData(-1)]
    public void Char_const_must_be_a_unicode_scalar_value(int codePoint) =>
        AssertFinding(
            VoidFn(new List<IrLocal>(), new List<IrTemp> { new(T(0), CharT) },
                new List<IrOp> { new Const(T(0), CharT, new CharConst(codePoint), Sp) }),
            "is not a Unicode scalar value");

    // -------------------------------------------------- 2f) Phase 3: Typen — BinOp/UnOp

    [Fact]
    public void BinOp_operands_must_have_the_same_type() =>
        AssertFinding(
            VoidFn(new List<IrLocal>(),
                new List<IrTemp> { new(T(0), I64), new(T(1), I32), new(T(2), I64) },
                new List<IrOp>
                {
                    new Const(T(0), I64, new IntConst(0), Sp),
                    new Const(T(1), I32, new IntConst(0), Sp),
                    new BinOp(T(2), IrBinKind.Add, I64, T(0), T(1), Sp),
                }),
            "operand types differ: t0 is i64, t1 is i32");

    [Fact]
    public void Comparison_must_produce_bool() =>
        AssertFinding(
            Mutate("comparison", m =>
                m.Functions[0].Blocks[0].Insts[2] = new BinOp(T(2), IrBinKind.Lt, I64, T(0), T(1), Sp)),
            "comparison must produce bool");

    [Fact]
    public void Arithmetic_result_must_have_the_operand_type() =>
        AssertFinding(
            Mutate("single_block", m =>
                m.Functions[0].Blocks[0].Insts[2] = new BinOp(T(2), IrBinKind.Add, I32, T(0), T(1), Sp)),
            "add result must have the operand type i64");

    [Fact]
    public void Ordering_comparison_on_non_numeric_type()
    {
        // Sprache.md §6.5: Vergleiche verlangen denselben NUMERISCHEN Typ. 'string' hat in v1
        // keine Ordnung.
        //
        // Hier stand 'char' — seit ADR-022 zaehlt er zur Numerik und HAT eine Ordnung ('c < 'z''
        // ist der Punkt der Aenderung). Der Test bleibt, weil die Verifier-Regel bleibt; nur der
        // Zeuge musste getauscht werden.
        AssertFinding(
            VoidFn(new List<IrLocal>(),
                new List<IrTemp> { new(T(0), Str), new(T(1), Str), new(T(2), Bool) },
                new List<IrOp>
                {
                    new Const(T(0), Str, new StringConst("a"), Sp),
                    new Const(T(1), Str, new StringConst("b"), Sp),
                    new BinOp(T(2), IrBinKind.Lt, Bool, T(0), T(1), Sp),
                }),
            "ordering comparison lt on non-numeric type string");
    }

    [Fact]
    public void Equality_comparison_on_string_is_legal() =>
        AssertClean(VoidFn(new List<IrLocal>(),
            new List<IrTemp> { new(T(0), Str), new(T(1), Str), new(T(2), Bool) },
            new List<IrOp>
            {
                new Const(T(0), Str, new StringConst("a"), Sp),
                new Const(T(1), Str, new StringConst("b"), Sp),
                new BinOp(T(2), IrBinKind.Eq, Bool, T(0), T(1), Sp),
            }));

    [Fact]
    public void String_concatenation_is_not_a_binop() =>
        AssertFinding(
            VoidFn(new List<IrLocal>(),
                new List<IrTemp> { new(T(0), Str), new(T(1), Str), new(T(2), Str) },
                new List<IrOp>
                {
                    new Const(T(0), Str, new StringConst("a"), Sp),
                    new Const(T(1), Str, new StringConst("b"), Sp),
                    new BinOp(T(2), IrBinKind.Add, Str, T(0), T(1), Sp),
                }),
            "add on non-numeric type string (string concatenation/repetition lowers to a call, not a binop)");

    [Fact]
    public void Bitwise_op_on_float_is_rejected() =>
        AssertFinding(
            VoidFn(new List<IrLocal>(),
                new List<IrTemp> { new(T(0), F64), new(T(1), F64), new(T(2), F64) },
                new List<IrOp>
                {
                    new Const(T(0), F64, new FloatConst(1.0), Sp),
                    new Const(T(1), F64, new FloatConst(2.0), Sp),
                    new BinOp(T(2), IrBinKind.BitAnd, F64, T(0), T(1), Sp),
                }),
            "and on non-integer type f64");

    [Fact]
    public void Logical_not_requires_bool() =>
        AssertFinding(
            VoidFn(new List<IrLocal>(), new List<IrTemp> { new(T(0), I64), new(T(1), I64) },
                new List<IrOp>
                {
                    new Const(T(0), I64, new IntConst(0), Sp),
                    new UnOp(T(1), IrUnKind.Not, I64, T(0), Sp),
                }),
            "not on non-bool type i64");

    [Fact]
    public void Bitnot_requires_integer() =>
        AssertFinding(
            VoidFn(new List<IrLocal>(), new List<IrTemp> { new(T(0), Bool), new(T(1), Bool) },
                new List<IrOp>
                {
                    new Const(T(0), Bool, new BoolConst(true), Sp),
                    new UnOp(T(1), IrUnKind.BitNot, Bool, T(0), Sp),
                }),
            "bitnot on non-integer type bool");

    // ------------------------------------------------ 2g) Phase 3: Typen — Convert/Local

    [Fact]
    public void Convert_from_type_must_match_the_operand() =>
        AssertFinding(
            Mutate("convert", m =>
                m.Functions[0].Blocks[0].Insts[1] = new Lyric.Ir.Convert(T(1), F64, I64, T(0), Sp)),
            "convert declares from-type f64 but t0 is i32");

    [Fact]
    public void Identity_convert_is_rejected() =>
        AssertFinding(
            VoidFn(new List<IrLocal> { new(L(0), "x", I64) },
                new List<IrTemp> { new(T(0), I64), new(T(1), I64) },
                new List<IrOp>
                {
                    new LoadLocal(T(0), L(0), I64, Sp),
                    new Lyric.Ir.Convert(T(1), I64, I64, T(0), Sp),
                }),
            "identity convert i64 -> i64");

    [Fact]
    public void Convert_is_numeric_to_numeric_only() =>
        AssertFinding(
            VoidFn(new List<IrLocal> { new(L(0), "b", Bool) },
                new List<IrTemp> { new(T(0), Bool), new(T(1), I64) },
                new List<IrOp>
                {
                    new LoadLocal(T(0), L(0), Bool, Sp),
                    new Lyric.Ir.Convert(T(1), Bool, I64, T(0), Sp),
                }),
            "convert bool -> i64 is not numeric<->numeric");

    [Fact]
    public void Load_type_must_match_the_local() =>
        AssertFinding(
            Mutate("single_block", m =>
                m.Functions[0].Blocks[0].Insts[0] = new LoadLocal(T(0), L(0), I32, Sp)),
            "load declares type i32 but l0 is i64");

    [Fact]
    public void Store_type_must_match_the_local() =>
        AssertFinding(
            VoidFn(new List<IrLocal> { new(L(0), "n", I64) },
                new List<IrTemp> { new(T(0), Bool) },
                new List<IrOp>
                {
                    new Const(T(0), Bool, new BoolConst(true), Sp),
                    new StoreLocal(L(0), T(0), Sp),
                }),
            "store of t0 (bool) into l0 (i64)");

    [Fact]
    public void Load_from_unknown_local() =>
        AssertFinding(
            VoidFn(new List<IrLocal>(), new List<IrTemp> { new(T(0), I64) },
                new List<IrOp> { new LoadLocal(T(0), L(4), I64, Sp) }),
            "load from unknown local l4");

    [Fact]
    public void Store_to_unknown_local() =>
        AssertFinding(
            VoidFn(new List<IrLocal>(), new List<IrTemp> { new(T(0), I64) },
                new List<IrOp>
                {
                    new Const(T(0), I64, new IntConst(0), Sp),
                    new StoreLocal(L(4), T(0), Sp),
                }),
            "store to unknown local l4");

    // ------------------------------------------------------- 2h) Phase 3: Typen — Call

    /// <summary>f0 = <c>main.take(x: bool) -> void</c>, f1 = <c>main.double(n: i64) -> i64</c>.
    /// Beide sind selbst wohlgeformt; <paramref name="callerInsts"/> liefert den defekten Call.</summary>
    private static IrModule WithCallees(List<IrTemp> callerTemps, List<IrOp> callerInsts)
    {
        var take = Fn("main.take", VoidT, 1,
            new List<IrLocal> { new(L(0), "x", Bool) }, new List<IrTemp>(),
            new List<IrBlock> { Block(0, new List<IrOp>(), new Return(null, Sp)) });

        var dbl = Fn("main.double", I64, 1,
            new List<IrLocal> { new(L(0), "n", I64) }, new List<IrTemp> { new(T(0), I64) },
            new List<IrBlock>
            {
                Block(0, new List<IrOp> { new LoadLocal(T(0), L(0), I64, Sp) }, new Return(T(0), Sp))
            });

        var caller = Fn("main.main", VoidT, 0, new List<IrLocal>(), callerTemps,
            new List<IrBlock> { Block(0, callerInsts, new Return(null, Sp)) });

        return Module(take, dbl, caller);
    }

    [Fact]
    public void Call_target_must_be_in_range() =>
        AssertFinding(
            WithCallees(new List<IrTemp> { new(T(0), I64) },
                new List<IrOp>
                {
                    new Const(T(0), I64, new IntConst(0), Sp),
                    new Call(null, F(9), new[] { T(0) }, Sp),
                }),
            "call target f9 is out of range (module has 3 function(s))");

    [Fact]
    public void Call_arity_must_match() =>
        AssertFinding(
            WithCallees(new List<IrTemp> { new(T(0), Bool) },
                new List<IrOp>
                {
                    new Const(T(0), Bool, new BoolConst(true), Sp),
                    new Call(null, F(0), new[] { T(0), T(0) }, Sp),
                }),
            "call to main.take passes 2 arg(s), expected 1");

    [Fact]
    public void Call_argument_types_must_match_the_callee_parameters() =>
        AssertFinding(
            WithCallees(new List<IrTemp> { new(T(0), I64) },
                new List<IrOp>
                {
                    new Const(T(0), I64, new IntConst(0), Sp),
                    new Call(null, F(0), new[] { T(0) }, Sp),
                }),
            "call to main.take: arg 0 is i64, expected bool");

    [Fact]
    public void Void_call_must_not_have_a_dest() =>
        AssertFinding(
            WithCallees(new List<IrTemp> { new(T(0), Bool), new(T(1), I64) },
                new List<IrOp>
                {
                    new Const(T(0), Bool, new BoolConst(true), Sp),
                    new Call(T(1), F(0), new[] { T(0) }, Sp),
                }),
            "call to void function main.take must not have a dest (found t1)");

    [Fact]
    public void Non_void_call_must_have_a_dest() =>
        AssertFinding(
            WithCallees(new List<IrTemp> { new(T(0), I64) },
                new List<IrOp>
                {
                    new Const(T(0), I64, new IntConst(0), Sp),
                    new Call(null, F(1), new[] { T(0) }, Sp),
                }),
            "call to main.double returning i64 must have a dest");

    [Fact]
    public void Call_dest_type_must_match_the_callee_return_type() =>
        AssertFinding(
            WithCallees(new List<IrTemp> { new(T(0), I64), new(T(1), Bool) },
                new List<IrOp>
                {
                    new Const(T(0), I64, new IntConst(0), Sp),
                    new Call(T(1), F(1), new[] { T(0) }, Sp),
                }),
            "call dest t1 is bool but main.double returns i64");

    // ------------------------------------------------- 2i) Phase 3: Typen — Terminatoren

    [Fact]
    public void Void_function_must_not_return_a_value() =>
        AssertFinding(
            Mutate("void_store", m => m.Functions[0].Blocks[0].Terminator = new Return(T(0), Sp)),
            "void function returns a value (t0)");

    [Fact]
    public void Non_void_function_must_return_a_value() =>
        AssertFinding(
            Mutate("single_block", m => m.Functions[0].Blocks[0].Terminator = new Return(null, Sp)),
            "function returns i64 but 'ret' carries no value");

    [Fact]
    public void Return_type_must_match_the_signature() =>
        AssertFinding(
            Mutate("comparison", m => m.Functions[0].Blocks[0].Terminator = new Return(T(0), Sp)),
            "returns t0 (i64), expected bool");

    [Fact]
    public void CondBranch_condition_must_be_bool() =>
        AssertFinding(
            Mutate("diamond", m =>
                m.Functions[0].Blocks[0].Terminator = new CondBranch(T(0), B(1), B(2), Sp)),
            "condition t0 is i64, must be bool");

    // ------------------------------------------------------------- 2j) Modul-Ebene

    [Fact]
    public void Function_names_must_be_unique()
    {
        var body = () => Block(0, new List<IrOp>(), new Return(null, Sp));
        var module = Module(
            Fn("main.f", VoidT, 0, new List<IrLocal>(), new List<IrTemp>(), new List<IrBlock> { body() }),
            Fn("main.f", VoidT, 0, new List<IrLocal>(), new List<IrTemp>(), new List<IrBlock> { body() }));

        AssertFinding(module, "main.f: duplicate function name");
    }

    // ------------------------------------------------------------------ 3) Robustheit

    [Fact]
    public void Broken_table_bails_out_without_a_cascade()
    {
        // t0 auf void zu setzen würde ohne Bail-out zusätzlich load-, binop- und store-Befunde
        // auslösen — genau die Folgefehler, die die Phasen-Architektur verhindert.
        var module = Mutate("diamond", m => m.Functions[0].Temps[0] = new IrTemp(T(0), VoidT));
        var findings = FindingsOf(module);
        Assert.Single(findings);
        Assert.Contains("temp t0 has type void", findings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_broken_function_does_not_suppress_the_next_one()
    {
        var broken = Fn("main.bad", VoidT, 0, new List<IrLocal>(), new List<IrTemp>(),
            new List<IrBlock>()); // no blocks -> Bail-out in Phase 1
        var good = Fixtures.Build("single_block").Functions[0]; // main.add, wohlgeformt

        var findings = FindingsOf(Module(broken, good));

        Assert.Single(findings);
        Assert.StartsWith("main.bad:", findings[0], StringComparison.Ordinal);
        Assert.DoesNotContain(findings, f => f.StartsWith("main.add:", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_op_type_throws_instead_of_passing_silently()
    {
        // Ein unbekannter Instruktionstyp heißt "der Verifier ist veraltet" — eine andere
        // Bug-Klasse als "die IR ist kaputt", und deshalb ein Wurf statt eines Befunds.
        var module = VoidFn(new List<IrLocal>(), new List<IrTemp>(),
            new List<IrOp> { new UnknownOp(Sp) });

        var ex = Assert.Throws<InternalCompilationException>(() => IrVerifier.Verify(module));
        Assert.Contains("unhandled op UnknownOp", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_terminator_type_throws_instead_of_passing_silently()
    {
        var module = Module(Fn("main.f", VoidT, 0, new List<IrLocal>(), new List<IrTemp>(),
            new List<IrBlock> { Block(0, new List<IrOp>(), new UnknownTerminator(Sp)) }));

        var ex = Assert.Throws<InternalCompilationException>(() => IrVerifier.Verify(module));
        Assert.Contains("unhandled terminator UnknownTerminator", ex.Message, StringComparison.Ordinal);
    }
}
