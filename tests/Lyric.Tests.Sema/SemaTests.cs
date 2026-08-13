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

    // Typ des Initializers der LETZTEN Bindung über alle Top-Level-Funktionen.
    private static (LyrType type, DiagnosticEngine de) LastInit(string program)
    {
        var (types, de, module) = Check(program);
        var init = module.Declarations.OfType<FunctionDecl>()
            .Where(f => f.Body is not null)
            .SelectMany(f => Bindings(f.Body!.Statements))
            .Last().Initializer!;
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

    // --- Slice 2b: Calls / Member / Struct-Init / composite ---

    private static void AssertNamed(string name, LyrType t) => Assert.Equal(name, Assert.IsType<NamedRef>(t).Symbol.Name);

    [Fact]
    public void Call_checks_arguments_and_yields_return_type()
    {
        var (t, de) = LastInit("fn add(a: int, b: int): int { return a + b; } fn t() { let x = add(1, 2); }");
        Assert.False(de.HasErrors);
        AssertType(LyrType.Int, t);
    }

    [Fact]
    public void Call_with_wrong_arity_or_type_is_reported()
    {
        Assert.Contains(Check("fn f(a: int): int { return a; } fn t() { let x = f(1, 2); }").de.Diagnostics, d => d.Code == "LYR-SEM0014");
        Assert.Contains(Check("fn f(a: int): int { return a; } fn t() { let x = f(true); }").de.Diagnostics, d => d.Code == "LYR-SEM0001");
    }

    [Fact]
    public void Calling_a_non_function_is_reported()
    {
        Assert.Contains(Check("fn t() { let x = 5(); }").de.Diagnostics, d => d.Code == "LYR-SEM0013");
    }

    [Fact]
    public void Field_and_method_access()
    {
        AssertType(LyrType.Int, LastInit("struct P { x: int, } fn t(p: P) { let y = p.x; }").type);
        AssertType(LyrType.Int, LastInit("struct P { x: int, fn get(): int { return this.x; } } fn t(p: P) { let y = p.get(); }").type);
    }

    [Fact]
    public void Optional_member_access_yields_optional()
    {
        var (t, de) = LastInit("struct P { x: int, } fn t(p: ?P) { let y = p?.x; }");
        Assert.False(de.HasErrors);
        Assert.True(LyrType.Equal(new Optional(LyrType.Int), t));
    }

    [Fact]
    public void Unknown_member_is_reported()
    {
        Assert.Contains(Check("struct P { x: int, } fn t(p: P) { let y = p.nope; }").de.Diagnostics, d => d.Code == "LYR-SEM0012");
    }

    [Fact]
    public void Struct_init_yields_the_named_type()
    {
        var (t, de) = LastInit("struct P { x: int, y: int, } fn t() { let p = P { x = 1, y = 2 }; }");
        Assert.False(de.HasErrors);
        AssertNamed("P", t);
    }

    [Fact]
    public void Struct_init_checks_field_names_and_types()
    {
        Assert.Contains(Check("struct P { x: int, } fn t() { let p = P { z = 1 }; }").de.Diagnostics, d => d.Code == "LYR-SEM0015");
        Assert.Contains(Check("struct P { x: int, } fn t() { let p = P { x = true }; }").de.Diagnostics, d => d.Code == "LYR-SEM0001");
    }

    [Fact]
    public void Enum_variant_construction()
    {
        AssertNamed("Sh", LastInit("enum Sh { Circle(float), Empty; } fn t() { let a = Sh.Circle(2.5); }").type);   // tuple variant als Konstruktor
        AssertNamed("Sh", LastInit("enum Sh { Circle(float), Empty; } fn t() { let a = Sh.Empty; }").type);         // Unit-Variante
    }

    [Fact]
    public void If_expression_unifies_branches()
    {
        AssertType(LyrType.Int, LastInit("fn t() { let x = if (true) 1 else 2; }").type);
        Assert.Contains(Check("fn t() { let x = if (true) 1 else \"a\"; }").de.Diagnostics, d => d.Code == "LYR-SEM0016");
    }

    [Fact]
    public void Match_expression_unifies_arms()
    {
        AssertType(LyrType.String, LastInit("fn t(n: int) { let x = match (n) { 0 => \"a\", _ => \"b\" }; }").type);
    }

    [Fact]
    public void Match_binds_the_whole_scrutinee_in_a_simple_binding()
    {
        var (t, de) = LastInit("fn t(n: int) { let r = match (n) { v => v }; }");
        Assert.False(de.HasErrors);
        AssertType(LyrType.Int, t); // v bindet den int-Scrutinee, Arm-Body v → int
    }

    [Fact]
    public void Lambda_with_annotated_params_has_a_function_type()
    {
        var fn = Assert.IsType<FnType>(LastInit("fn t() { let f = (x: int) => x + 1; }").type);
        AssertType(LyrType.Int, fn.Parameters.Single());
        AssertType(LyrType.Int, fn.Return);
    }

    // --- Slice 3a: Flow (Return-Coverage / DAA / Narrowing) ---

    [Fact]
    public void Missing_return_is_reported()
    {
        Assert.Contains(Check("fn f(): int { }").de.Diagnostics, d => d.Code == "LYR-SEM0017");
        Assert.Contains(Check("fn f(c: bool): int { if (c) { return 1; } }").de.Diagnostics, d => d.Code == "LYR-SEM0017");
    }

    [Fact]
    public void Full_return_coverage_passes()
    {
        Assert.False(Check("fn f(c: bool): int { if (c) { return 1; } else { return 2; } }").de.HasErrors);
        Assert.False(Check("fn f(): int { while (true) { } }").de.HasErrors); // divergiert
        Assert.False(Check("fn f() { }").de.HasErrors);                       // void
    }

    [Fact]
    public void Use_of_unassigned_variable_is_reported()
    {
        Assert.Contains(Check("fn f() { var x: int; let y = x; }").de.Diagnostics, d => d.Code == "LYR-SEM0018");
        Assert.Contains(Check("fn f(c: bool) { var x: int; if (c) { x = 1; } let y = x; }").de.Diagnostics, d => d.Code == "LYR-SEM0018");
    }

    [Fact]
    public void Definite_assignment_passes()
    {
        Assert.False(Check("fn f() { var x: int; x = 5; let y = x; }").de.HasErrors);
        Assert.False(Check("fn f(c: bool) { var x: int; if (c) { x = 1; } else { x = 2; } let y = x; }").de.HasErrors);
        Assert.False(Check("fn f(c: bool): int { var x: int; if (c) { return 0; } x = 1; return x; }").de.HasErrors);
    }

    [Fact]
    public void Narrowing_allows_use_in_then_branch()
    {
        Assert.False(Check("fn f(p: ?int) { if (p != null) { let x = p + 1; } }").de.HasErrors);
        Assert.False(Check("struct P { field: int, } fn f(p: ?P) { if (p != null) { let x = p.field; } }").de.HasErrors);
    }

    [Fact]
    public void Optional_without_narrowing_is_rejected()
    {
        Assert.Contains(Check("fn f(p: ?int) { let x = p + 1; }").de.Diagnostics, d => d.Code == "LYR-SEM0003");
    }

    [Fact]
    public void Early_exit_narrows_after_the_if()
    {
        Assert.False(Check("fn f(p: ?int): int { if (p == null) { return 0; } return p + 1; }").de.HasErrors);
    }

    [Fact]
    public void Reassignment_invalidates_narrowing()
    {
        Assert.Contains(Check("fn f(q: ?int) { var p = q; if (p != null) { p = null; let x = p + 1; } }").de.Diagnostics, d => d.Code == "LYR-SEM0003");
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
