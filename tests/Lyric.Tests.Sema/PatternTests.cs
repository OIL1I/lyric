using Lyric.AST;
using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Xunit;

namespace Lyric.Tests.Sema;

/// <summary>
/// Pattern-Match voll — M4-Slice 2 (Sprache.md §5/§6.3). Enum-Payload-Destructuring mit
/// echten Typen (Tuple-/Struct-Varianten, generisch substituiert), Struct-/Tuple-
/// Destructuring, Or-Pattern-Konsistenz, Literal-/Range-Pattern-Typprüfung,
/// Exhaustivität (SEM0050, D4 pragmatisch), Block-Arm-Regel im match-Ausdruck (SEM0033)
/// und kontextuelle/qualifizierte Enum-Varianten-Konstruktion (§3.4).
/// </summary>
public class PatternTests
{
    private const string Prelude = """
        enum Shape {
            Circle(float),
            Rectangle(float, float),
            Triangle { a: float, b: float, c: float },
            Empty
        }
        enum Opt<T> { Some(T), None }
        enum Res<T> { Okv { v: T }, Err }
        enum Other { Red }
        enum Mix { I(int), S(string) }
        struct Point { x: int, y: int }
        """;

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
                    case IfStmt i: Walk(i.Then.Statements); if (i.Else is Block eb) Walk(eb.Statements); break;
                }
        }
        Walk(stmts);
        return acc;
    }

    // Typ des Initializers der LETZTEN Bindung über alle Top-Level-Funktionen.
    private static (LyrType type, DiagnosticEngine de) LastInit(string body)
    {
        var (types, de, module) = Check(Prelude + "\n" + body);
        var init = module.Declarations.OfType<FunctionDecl>()
            .Where(f => f.Body is not null)
            .SelectMany(f => Bindings(f.Body!.Statements))
            .Last().Initializer!;
        return (types.TypeOf(init), de);
    }

    private static DiagnosticEngine Diags(string body) => Check(Prelude + "\n" + body).de;

    private static void AssertType(LyrType expected, LyrType actual) =>
        Assert.True(LyrType.Equal(expected, actual), $"expected '{TypeFacts.Display(expected)}', got '{TypeFacts.Display(actual)}'");

    private static void AssertClean(DiagnosticEngine de) =>
        Assert.False(de.HasErrors, string.Join("; ", de.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));

    // --- Enum-Payload-Destructuring bindet mit echten Typen ---

    [Fact]
    public void Tuple_variant_payload_binds_with_real_type()
    {
        var (t, de) = LastInit("fn u(s: Shape) { let x = match (s) { Circle(r) => r, _ => 0.0 }; }");
        AssertClean(de);
        AssertType(LyrType.Float, t);
    }

    [Fact]
    public void Struct_variant_fields_bind_with_real_types()
    {
        var (t, de) = LastInit("fn u(s: Shape) { let x = match (s) { Triangle { a, b, c } => a + b + c, _ => 0.0 }; }");
        AssertClean(de);
        AssertType(LyrType.Float, t);
    }

    [Fact]
    public void Field_pattern_with_explicit_subpattern_binds_the_rest()
    {
        var (t, de) = LastInit("fn u(s: Shape) { let x = match (s) { Triangle { a = 3.0, b, c } => b, _ => 0.0 }; }");
        AssertClean(de);
        AssertType(LyrType.Float, t);
    }

    [Fact]
    public void Generic_enum_payload_is_substituted()
    {
        var (t, de) = LastInit("fn u(o: Opt<int>) { let x = match (o) { Some(v) => v, None => 0 }; }");
        AssertClean(de);
        AssertType(LyrType.Int, t); // Some(T) → T=int
    }

    [Fact]
    public void Generic_struct_variant_payload_is_substituted()
    {
        var (t, de) = LastInit("fn u(r: Res<int>) { let x = match (r) { Okv { v } => v, Err => 0 }; }");
        AssertClean(de);
        AssertType(LyrType.Int, t); // Okv { v: T } → T=int
    }

    [Fact]
    public void Qualified_variant_pattern_resolves_against_scrutinee()
    {
        var (t, de) = LastInit("fn u(s: Shape) { let x = match (s) { Shape.Circle(r) => r, _ => 0.0 }; }");
        AssertClean(de);
        AssertType(LyrType.Float, t);
    }

    [Fact]
    public void Unit_variant_name_matches_without_binding()
    {
        var de = Diags("fn u(s: Shape) { let x = match (s) { Empty => 1.0, _ => 2.0 }; }");
        AssertClean(de);
    }

    // --- Tuple-/Struct-Destructuring ---

    [Fact]
    public void Tuple_pattern_destructures_and_is_exhaustive()
    {
        var (t, de) = LastInit("fn u(p: (int, string)) { let x = match (p) { (a, b) => a }; }");
        AssertClean(de); // irrefutabel → kein SEM0050
        AssertType(LyrType.Int, t);
    }

    [Fact]
    public void Struct_destructuring_binds_field_types()
    {
        var (t, de) = LastInit("fn u(pt: Point) { let x = match (pt) { Point { x, y } => x + y }; }");
        AssertClean(de);
        AssertType(LyrType.Int, t);
    }

    // --- Optional im match (Doku §6): null-Arm plus Inner-Bindung ---

    [Fact]
    public void Optional_binding_arm_narrows_to_inner_type()
    {
        var (t, de) = LastInit("fn u(m: ?int) { let x = match (m) { null => 0, v => v }; }");
        AssertClean(de);
        AssertType(LyrType.Int, t); // v bindet int, nicht ?int
    }

    [Fact]
    public void Optional_match_without_null_arm_is_not_exhaustive()
    {
        var de = Diags("fn u(m: ?int) { let x = match (m) { v => v }; }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0050" && d.Message.Contains("null"));
    }

    // --- Literal-/Range-Patterns werden typgeprüft ---

    [Fact]
    public void Literal_pattern_type_mismatch_is_reported()
    {
        var de = Diags("fn u(n: int) { let x = match (n) { \"a\" => 1, _ => 2 }; }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0029");
    }

    [Fact]
    public void Null_pattern_on_non_optional_is_reported()
    {
        var de = Diags("fn u(n: int) { let x = match (n) { null => 1, _ => 2 }; }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0029");
    }

    [Fact]
    public void Range_pattern_on_matching_numeric_is_clean()
    {
        var de = Diags("fn u(n: int) { let x = match (n) { 0..=9 => 1, _ => 2 }; }");
        AssertClean(de);
    }

    [Fact]
    public void Char_range_pattern_is_clean()
    {
        var de = Diags("fn u(c: char) { let x = match (c) { 'a'..='z' => 1, _ => 0 }; }");
        AssertClean(de);
    }

    [Fact]
    public void Range_pattern_on_string_is_reported()
    {
        var de = Diags("fn u(s: string) { let x = match (s) { 0..=9 => 1, _ => 2 }; }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0029");
    }

    // --- Varianten-Fehler ---

    [Fact]
    public void Unknown_variant_is_reported()
    {
        var de = Diags("fn u(s: Shape) { match (s) { Nope(x) => { }, _ => { } } }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0031");
    }

    [Fact]
    public void Wrong_payload_arity_is_reported()
    {
        var de = Diags("fn u(s: Shape) { match (s) { Circle(a, b) => { }, _ => { } } }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0031");
    }

    [Fact]
    public void Struct_pattern_on_tuple_variant_is_reported()
    {
        var de = Diags("fn u(s: Shape) { match (s) { Circle { r } => { }, _ => { } } }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0031");
    }

    [Fact]
    public void Unknown_field_in_variant_pattern_is_reported()
    {
        var de = Diags("fn u(s: Shape) { match (s) { Triangle { d } => { }, _ => { } } }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0031");
    }

    [Fact]
    public void Bare_name_of_payload_variant_is_reported()
    {
        var de = Diags("fn u(s: Shape) { match (s) { Circle => { }, _ => { } } }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0031");
    }

    [Fact]
    public void Wrong_enum_prefix_is_reported()
    {
        var de = Diags("fn u(s: Shape) { let x = match (s) { Other.Red => 1, _ => 2 }; }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0029");
    }

    [Fact]
    public void Tuple_pattern_on_non_tuple_is_reported()
    {
        var de = Diags("fn u(n: int) { let x = match (n) { (a, b) => 1, _ => 2 }; }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0029");
    }

    // --- Or-Patterns: Bindungs-Konsistenz ---

    [Fact]
    public void Or_pattern_with_consistent_binding_is_clean()
    {
        var (t, de) = LastInit("fn u(s: Shape) { let x = match (s) { Circle(r) | Rectangle(r, _) => r, _ => 0.0 }; }");
        AssertClean(de);
        AssertType(LyrType.Float, t);
    }

    [Fact]
    public void Or_pattern_with_missing_binding_is_reported()
    {
        var de = Diags("fn u(s: Shape) { match (s) { Circle(r) | Empty => { }, _ => { } } }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0032");
    }

    [Fact]
    public void Or_pattern_with_conflicting_types_is_reported()
    {
        var de = Diags("fn u(m: Mix) { match (m) { I(v) | S(v) => { } } }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0032");
    }

    // --- Exhaustivität (SEM0050, D4) ---

    [Fact]
    public void Missing_variants_are_reported_by_name()
    {
        var de = Diags("fn u(s: Shape) { match (s) { Circle(r) => { } } }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0050"
            && d.Message.Contains("Rectangle") && d.Message.Contains("Triangle") && d.Message.Contains("Empty"));
    }

    [Fact]
    public void All_variants_covered_is_exhaustive_without_wildcard()
    {
        var de = Diags("""
            fn u(s: Shape) {
                match (s) {
                    Circle(_) => { }
                    Rectangle(_, _) => { }
                    Triangle { } => { }
                    Empty => { }
                }
            }
            """);
        AssertClean(de);
    }

    [Fact]
    public void Or_pattern_coverage_counts_each_alternative()
    {
        var de = Diags("fn u(s: Shape) { match (s) { Circle(_) | Rectangle(_, _) | Triangle { } | Empty => { } } }");
        AssertClean(de);
    }

    [Fact]
    public void Guarded_arm_does_not_count_for_coverage()
    {
        var de = Diags("""
            fn u(s: Shape) {
                match (s) {
                    Circle(r) if r > 0.0 => { }
                    Rectangle(_, _) => { }
                    Triangle { } => { }
                    Empty => { }
                }
            }
            """);
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0050" && d.Message.Contains("Circle"));
    }

    [Fact]
    public void Refutable_payload_does_not_cover_the_variant()
    {
        var de = Diags("""
            fn u(s: Shape) {
                match (s) {
                    Circle(1.0) => { }
                    Rectangle(_, _) => { }
                    Triangle { } => { }
                    Empty => { }
                }
            }
            """);
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0050" && d.Message.Contains("Circle"));
    }

    [Fact]
    public void Bool_match_needs_both_values()
    {
        var de = Diags("fn u(b: bool) { let x = match (b) { true => 1 }; }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0050" && d.Message.Contains("false"));
    }

    [Fact]
    public void Bool_match_with_both_values_is_exhaustive()
    {
        var de = Diags("fn u(b: bool) { let x = match (b) { true => 1, false => 2 }; }");
        AssertClean(de);
    }

    [Fact]
    public void Open_type_match_needs_a_default_arm()
    {
        var de = Diags("fn u(n: int) { let x = match (n) { 0 => 1, 1 => 2 }; }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0050");
    }

    // --- Block-Arme (Block-Wert-Regel): im match-Ausdruck nur mit return/throw ---

    [Fact]
    public void Diverging_block_arm_in_match_expression_is_allowed()
    {
        var de = Diags("""
            fn u(s: Shape): float {
                return match (s) {
                    Triangle { a, b, c } => { return a; },
                    _ => 0.0,
                };
            }
            """);
        AssertClean(de);
    }

    [Fact]
    public void Non_diverging_block_arm_in_match_expression_is_reported()
    {
        var de = Diags("fn u(n: int) { let x = match (n) { 0 => { let y = 1; }, _ => 2 }; }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0033");
    }

    [Fact]
    public void Block_arms_in_match_statement_are_unrestricted()
    {
        var de = Diags("fn u(n: int) { match (n) { _ => { let y = 1; } } }");
        AssertClean(de);
    }

    // --- Exhaustivität speist Return-Coverage und DAA ---

    [Fact]
    public void Exhaustive_match_counts_for_return_coverage()
    {
        var de = Diags("""
            fn u(s: Shape): int {
                match (s) {
                    Circle(_) => { return 1; }
                    Rectangle(_, _) => { return 2; }
                    Triangle { } => { return 3; }
                    Empty => { return 4; }
                }
            }
            """);
        AssertClean(de); // kein SEM0017
    }

    [Fact]
    public void Exhaustive_match_arms_count_for_definite_assignment()
    {
        var de = Diags("""
            fn u(b: bool): int {
                var x: int;
                match (b) {
                    true => { x = 1; }
                    false => { x = 2; }
                }
                return x;
            }
            """);
        AssertClean(de); // kein SEM0018
    }

    // --- Enum-Varianten-Konstruktion (§3.4): qualifiziert + kontextuell ---

    [Fact]
    public void Contextual_variant_construction_from_declared_binding()
    {
        var (t, de) = LastInit("fn u() { let s: Shape = Triangle { a = 1.0, b = 1.0, c = 1.0 }; }");
        AssertClean(de);
        Assert.Equal("Shape", TypeFacts.Display(t));
    }

    [Fact]
    public void Contextual_variant_construction_in_array_literal()
    {
        var de = Diags("fn u() { let xs: Shape[] = [Shape.Circle(1.0), Triangle { a = 1.0, b = 1.0, c = 1.0 }]; }");
        AssertClean(de);
    }

    [Fact]
    public void Contextual_variant_construction_in_return_position()
    {
        var de = Diags("fn mk(): Shape { return Triangle { a = 1.0, b = 1.0, c = 1.0 }; }");
        AssertClean(de);
    }

    [Fact]
    public void Qualified_struct_variant_construction()
    {
        var (t, de) = LastInit("fn u() { let s = Shape.Triangle { a = 1.0, b = 1.0, c = 1.0 }; }");
        AssertClean(de);
        Assert.Equal("Shape", TypeFacts.Display(t));
    }

    [Fact]
    public void Generic_contextual_variant_substitutes_fields()
    {
        var de = Diags("fn u() { let r: Res<int> = Okv { v = 5 }; }");
        AssertClean(de);
    }

    [Fact]
    public void Generic_contextual_variant_checks_field_types()
    {
        var de = Diags("fn u() { let r: Res<int> = Okv { v = \"x\" }; }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0001");
    }

    [Fact]
    public void Variant_construction_field_types_are_checked()
    {
        var de = Diags("fn u() { let s: Shape = Triangle { a = \"x\", b = 1.0, c = 1.0 }; }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0001");
    }

    [Fact]
    public void Unknown_contextual_name_stays_unknown_type()
    {
        var de = Diags("fn u() { let s: Shape = Blob { a = 1.0 }; }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0011");
    }

    [Fact]
    public void Bare_struct_variant_as_value_is_reported()
    {
        var de = Diags("fn u() { let t = Shape.Triangle; }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0031");
    }

    [Fact]
    public void Struct_init_on_tuple_variant_is_reported()
    {
        var de = Diags("fn u() { let s: Shape = Circle { r = 1.0 }; }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0031");
    }

    // --- Leeres Array-Literal nimmt den Kontext-Typ ---

    [Fact]
    public void Empty_array_literal_takes_expected_element_type()
    {
        var (t, de) = LastInit("fn u() { let xs: int[] = []; }");
        AssertClean(de);
        AssertType(new ArrayOf(LyrType.Int, null), t);
    }
}
