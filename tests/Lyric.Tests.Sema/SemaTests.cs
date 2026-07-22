using Lyric.AST;
using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Xunit;

namespace Lyric.Tests.Sema;

/// <summary>
/// Typprüfung (M3-Slice 2a). Die Tests zurren die vereinbarten Entscheidungen fest:
/// ①A strikte Numerik, ②a Literal-Fit, `+`/`*` für string/T[], ④ Numerik-Casts,
/// ⑤ Nullable-Operatoren (ohne Flow-Narrowing).
/// </summary>
public class SemaTests
{
    private static (TypeResult types, DiagnosticEngine de, Module module) Check(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        var module = new Parser(sm, id, de).ParseModule();
        comp.AddModule(module);
        var binding = comp.Resolve();
        var types = new TypeChecker(comp, binding, de).Check();
        return (types, de, module);
    }

    private static List<BindingStmt> Bindings(IEnumerable<Stmt> stmts)
    {
        var acc = new List<BindingStmt>();
        void Walk(IEnumerable<Stmt> ss)
        {
            foreach (var s in ss)
                switch (s)
                {
                    case BindingStmt b: acc.Add(b); break;
                    case Block bl: Walk(bl.Statements); break;
                    case ForInStmt f: Walk(f.Body.Statements); break;
                    case WhileStmt w: Walk(w.Body.Statements); break;
                    case DoWhileStmt d: Walk(d.Body.Statements); break;
                    case IfStmt i: Walk(i.Then.Statements); if (i.Else is Block eb) Walk(eb.Statements); break;
                }
        }
        Walk(stmts);
        return acc;
    }

    // Typ des Initializers der LETZTEN Bindung in der ersten Funktion.
    private static (LyrType type, DiagnosticEngine de) LastInit(string function)
    {
        var (types, de, module) = Check(function);
        var fn = module.Declarations.OfType<FunctionDecl>().First();
        var init = Bindings(fn.Body!.Statements).Last().Initializer!;
        return (types.TypeOf(init), de);
    }

    private static LyrType Prim(PrimitiveKind k) => new PrimitiveType(k);
    private static LyrType IntArr => new ArrayOf(LyrType.Int, null);
    private static void AssertType(LyrType expected, LyrType actual) =>
        Assert.True(LyrType.Equal(expected, actual), $"expected '{TypeFacts.Display(expected)}', got '{TypeFacts.Display(actual)}'");

    // --- ①A Numerik strikt + ②a Literal-Fit ---

    [Fact]
    public void Int_arithmetic_is_int()
    {
        var (t, de) = LastInit("fn t() { let x = 1 + 2 * 3; }");
        Assert.False(de.HasErrors);
        AssertType(LyrType.Int, t);
    }

    [Fact]
    public void Suffix_pins_the_type()
    {
        var (t, de) = LastInit("fn t() { let x = 1i32 + 2i32; }");
        Assert.False(de.HasErrors);
        AssertType(Prim(PrimitiveKind.Int32), t);
    }

    [Fact]
    public void Mixed_sized_arithmetic_is_rejected()
    {
        var (_, de) = LastInit("fn t(a: int, b: int8) { let x = a + b; }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0003"); // strikt: kein implizites Widening
    }

    [Fact]
    public void Untyped_literal_adapts_to_the_other_operand()
    {
        var (t, de) = LastInit("fn t(a: int8) { let x = a + 1; }");
        Assert.False(de.HasErrors);
        AssertType(Prim(PrimitiveKind.Int8), t); // '1' passt sich int8 an
    }

    [Fact]
    public void Literal_fits_annotated_target()
    {
        Assert.False(Check("fn t() { let x: int8 = 5; }").de.HasErrors);
    }

    [Fact]
    public void Literal_out_of_range_is_rejected()
    {
        Assert.Contains(Check("fn t() { let x: int8 = 300; }").de.Diagnostics, d => d.Code == "LYR-SEM0001");
    }

    [Fact]
    public void Int_literal_widens_to_float()
    {
        Assert.False(Check("fn t() { let x: float = 3; }").de.HasErrors);
    }

    // --- `+`/`*` für string und T[] ---

    [Fact]
    public void String_concat_and_repeat()
    {
        AssertType(LyrType.String, LastInit("fn t() { let x = \"a\" + \"b\"; }").type);
        AssertType(LyrType.String, LastInit("fn t() { let x = \"ab\" * 3; }").type);
    }

    [Fact]
    public void List_concat_and_repeat()
    {
        AssertType(IntArr, LastInit("fn t() { let x = [1, 2] + [3, 4]; }").type);
        AssertType(IntArr, LastInit("fn t() { let x = [0] * 5; }").type);
    }

    [Fact]
    public void Adding_string_and_int_is_rejected()
    {
        Assert.Contains(LastInit("fn t() { let x = \"a\" + 1; }").de.Diagnostics, d => d.Code == "LYR-SEM0003");
    }

    // --- Nullable (⑤ Operatoren, ohne Narrowing) ---

    [Fact]
    public void Optional_widening_is_allowed()
    {
        Assert.False(Check("fn t() { let x: ?int = 5; }").de.HasErrors);
    }

    [Fact]
    public void Null_to_non_optional_is_rejected()
    {
        Assert.Contains(Check("fn t() { let x: int = null; }").de.Diagnostics, d => d.Code == "LYR-SEM0001");
    }

    [Fact]
    public void Coalesce_unwraps_optional()
    {
        var (t, de) = LastInit("fn t(p: ?int) { let x = p ?? 0; }");
        Assert.False(de.HasErrors);
        AssertType(LyrType.Int, t);
    }

    [Fact]
    public void Force_unwrap_optional_yields_inner()
    {
        AssertType(LyrType.Int, LastInit("fn t(p: ?int) { let x = p!; }").type);
    }

    [Fact]
    public void Force_unwrap_non_nullable_is_rejected()
    {
        Assert.Contains(LastInit("fn t(p: int) { let x = p!; }").de.Diagnostics, d => d.Code == "LYR-SEM0005");
    }

    // --- ④ Casts ---

    [Fact]
    public void Numeric_cast_is_allowed()
    {
        AssertType(LyrType.Float, LastInit("fn t() { let x = 1 as float; }").type);
    }

    [Fact]
    public void Non_numeric_cast_is_rejected()
    {
        Assert.Contains(LastInit("fn t() { let x = true as int; }").de.Diagnostics, d => d.Code == "LYR-SEM0006");
    }

    // --- Vergleiche / Logik / Bedingungen ---

    [Fact]
    public void Comparison_and_logic_are_bool()
    {
        AssertType(LyrType.Bool, LastInit("fn t(a: int, b: int) { let x = a < b; }").type);
        AssertType(LyrType.Bool, LastInit("fn t(a: bool, b: bool) { let x = a && b; }").type);
    }

    [Fact]
    public void Non_bool_condition_is_rejected()
    {
        Assert.Contains(Check("fn t(a: int) { if (a) { } }").de.Diagnostics, d => d.Code == "LYR-SEM0004");
    }

    // --- Index / for-in / Inferenz-Fluss ---

    [Fact]
    public void Array_index_yields_element_type()
    {
        AssertType(LyrType.Int, LastInit("fn t(a: int[]) { let x = a[0]; }").type);
    }

    [Fact]
    public void For_in_binds_element_type()
    {
        var (t, de) = LastInit("fn t(a: int[]) { for (i in a) { let y = i; } }");
        Assert.False(de.HasErrors);
        AssertType(LyrType.Int, t); // y = i, i ist das Array-Element
    }

    // --- Fehlerfälle ---

    [Fact]
    public void Unknown_identifier_is_reported()
    {
        Assert.Contains(LastInit("fn t() { let x = missing; }").de.Diagnostics, d => d.Code == "LYR-SEM0002");
    }

    [Fact]
    public void Array_element_mismatch_is_reported()
    {
        Assert.Contains(Check("fn t() { let x = [1, \"a\"]; }").de.Diagnostics, d => d.Code == "LYR-SEM0009");
    }

    [Fact]
    public void Return_type_mismatch_is_reported()
    {
        Assert.Contains(Check("fn t(): int { return true; }").de.Diagnostics, d => d.Code == "LYR-SEM0001");
    }

    [Fact]
    public void Binding_without_type_or_init_is_reported()
    {
        Assert.Contains(Check("fn t() { let x; }").de.Diagnostics, d => d.Code == "LYR-SEM0010");
    }

    // --- Robustheit ---

    [Theory]
    [InlineData("fn t() { let x = ; }")]
    [InlineData("fn t() { let x = 1 +; }")]
    [InlineData("fn t(a: Nope) { let x = a + 1; }")]
    [InlineData("fn t() { return; }")]
    [InlineData("")]
    public void Checker_never_throws(string source)
    {
        Assert.Null(Record.Exception(() => Check(source)));
    }
}
