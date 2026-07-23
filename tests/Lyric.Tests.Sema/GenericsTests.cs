using Lyric.AST;
using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Xunit;

namespace Lyric.Tests.Sema;

/// <summary>
/// Generics — M4-Slice 1a (Sprache.md §3.1/§4). Fundament: Typ-Params lösen auf
/// (kein RES0002 mehr), generische Instanzen substituieren Member-Typen (Stack&lt;int&gt;.value
/// → int), Member auf einem Typ-Param T kommen ausschließlich aus dessen Constraints (D2),
/// Arity wird geprüft. Konstruktion (Stack&lt;int&gt; { }) und Call-Inferenz sind Slice 1b.
/// </summary>
public class GenericsTests
{
    // Gemeinsame Definitionen; jeder Test hängt seinen eigenen Code an.
    private const string Prelude = """
        struct Box<T> { value: T }
        struct Vec<T> { items: T[] }
        interface Show { fn show(): string; }
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

    // --- Definitionen typen sauber (kein RES0002/Fehler) ---

    [Fact]
    public void Generic_struct_definition_checks_clean()
    {
        var de = Diags("");
        Assert.False(de.HasErrors, string.Join("; ", de.Diagnostics.Select(d => d.Code)));
    }

    [Fact]
    public void Generic_free_function_checks_clean()
    {
        var de = Diags("fn identity<T>(x: T): T { return x; }");
        Assert.False(de.HasErrors);
    }

    [Fact]
    public void Method_returning_type_param_checks_clean()
    {
        var de = Diags("struct Cell<T> { v: T, fn get(): T { return this.v; } }");
        Assert.False(de.HasErrors);
    }

    // --- Instanz-Member werden substituiert ---

    [Fact]
    public void Generic_instance_field_is_substituted()
    {
        var (t, de) = LastInit("fn u(b: Box<int>) { let v = b.value; }");
        Assert.False(de.HasErrors);
        AssertType(LyrType.Int, t); // Box<T>.value: T  →  int
    }

    [Fact]
    public void Substitution_reaches_into_array_fields()
    {
        var (t, de) = LastInit("fn u(v: Vec<int>) { let xs = v.items; }");
        Assert.False(de.HasErrors);
        AssertType(new ArrayOf(LyrType.Int, null), t); // Vec<T>.items: T[]  →  int[]
    }

    [Fact]
    public void Nested_generic_instance_substitutes_stepwise()
    {
        var (t, de) = LastInit("fn u(bb: Box<Box<int>>) { let inner = bb.value; }");
        Assert.False(de.HasErrors);
        // Box<Box<int>>.value: T  →  Box<int>
        var gi = Assert.IsType<GenericInstance>(t);
        Assert.Equal("Box", gi.Definition.Name);
        Assert.Single(gi.Arguments);
        AssertType(LyrType.Int, gi.Arguments[0]);
    }

    [Fact]
    public void Substituted_field_type_mismatch_is_rejected()
    {
        var de = Diags("fn u(b: Box<int>) { let s: string = b.value; }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0001"); // int → string
    }

    // --- Generische Instanzen sind invariant-gleich ---

    [Fact]
    public void Same_generic_instance_is_assignable()
    {
        var de = Diags("fn u(a: Box<int>) { let b: Box<int> = a; }");
        Assert.False(de.HasErrors);
    }

    [Fact]
    public void Different_type_argument_is_not_assignable()
    {
        var de = Diags("fn u(a: Box<int>) { let b: Box<string> = a; }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0001"); // Box<int> ≠ Box<string>
    }

    // --- Arity ---

    [Fact]
    public void Wrong_type_argument_count_is_rejected()
    {
        var de = Diags("fn u(b: Box<int, string>) { }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0026");
    }

    // --- Constraint-Member-Zugriff (D2) ---

    [Fact]
    public void Member_on_type_param_comes_from_constraint()
    {
        var (t, de) = LastInit("fn render<T :: [Show]>(x: T) { let s = x.show(); }");
        Assert.False(de.HasErrors);
        AssertType(LyrType.String, t); // Show.show(): string
    }

    [Fact]
    public void Member_on_unconstrained_type_param_is_rejected()
    {
        var de = Diags("fn bad<T>(x: T) { let y = x.nope(); }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0027");
    }

    [Fact]
    public void Member_not_in_constraint_is_rejected()
    {
        var de = Diags("fn bad<T :: [Show]>(x: T) { let y = x.missing(); }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0027");
    }
}
