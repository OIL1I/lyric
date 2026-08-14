using Lyric.AST;
using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Xunit;

namespace Lyric.Tests.Resolver;

/// <summary>
/// Direct assertions against the resolver contract: the symbol structure, cross-module imports, cycles
/// and type name bindings in the <see cref="BindingResult"/>.
/// </summary>
public class ResolverTests
{
    private static (Compilation comp, DiagnosticEngine de, BindingResult binding) Resolve(
        params (string name, string source)[] files)
    {
        var sm = new SourceManager();
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        foreach (var (name, source) in files)
        {
            var id = sm.AddVirtual(name + ".lyr", source);
            comp.AddModule(new Parser(sm, id, de).ParseModule(), name);
        }
        return (comp, de, comp.Resolve());
    }

    // --- declarations ---

    [Fact]
    public void Top_level_symbols_are_declared()
    {
        var (comp, de, _) = Resolve(("m", "struct Point { x: int, } fn f(): int { return 0; } let g = 1;"));
        Assert.False(de.HasErrors);
        var m = comp.Modules[0];
        Assert.IsType<TypeSymbol>(m.Members.LookupLocal("Point"));
        Assert.IsType<FunctionSymbol>(m.Members.LookupLocal("f"));
        Assert.IsType<GlobalSymbol>(m.Members.LookupLocal("g"));
    }

    [Fact]
    public void Type_members_live_in_the_type_scope()
    {
        var (comp, _, _) = Resolve(("m", "struct P { x: int, fn go(): int { return this.x; } }"));
        var p = Assert.IsType<TypeSymbol>(comp.Modules[0].Members.LookupLocal("P"));
        Assert.IsType<FieldSymbol>(p.Members.LookupLocal("x"));
        Assert.IsType<FunctionSymbol>(p.Members.LookupLocal("go"));
    }

    [Fact]
    public void Duplicate_top_level_declaration_reports()
    {
        var (_, de, _) = Resolve(("m", "fn f(): int { return 0; } fn f(): int { return 1; }"));
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-RES0001");
    }

    [Fact]
    public void Visibility_is_captured()
    {
        var (comp, _, _) = Resolve(("m", "pub fn a(): int { return 0; } fn b(): int { return 0; }"));
        var m = comp.Modules[0];
        Assert.Equal(Visibility.Public, ((FunctionSymbol)m.Members.LookupLocal("a")!).Visibility);
        Assert.Equal(Visibility.Module, ((FunctionSymbol)m.Members.LookupLocal("b")!).Visibility);
    }

    // --- type name binding through the BindingResult ---

    [Fact]
    public void Builtin_and_local_type_names_bind()
    {
        var (comp, de, binding) = Resolve(("m", "struct S { x: int, y: S, }"));
        Assert.False(de.HasErrors);
        var s = (TypeSymbol)comp.Modules[0].Members.LookupLocal("S")!;
        var decl = (StructDecl)s.Declaration!;
        var xType = ((FieldDecl)decl.Members[0]).Type; // int
        var yType = ((FieldDecl)decl.Members[1]).Type; // S

        var xSym = Assert.IsType<TypeSymbol>(binding.Resolve(xType));
        Assert.Equal(TypeSymbolKind.Builtin, xSym.Kind);
        Assert.Equal("int", xSym.Name);
        Assert.Same(s, binding.Resolve(yType)); // 'y: S' binds to the S symbol
    }

    [Fact]
    public void Unknown_type_reports_and_binds_error()
    {
        var (comp, de, binding) = Resolve(("m", "struct W { thing: Nope, }"));
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-RES0002");
        var w = (TypeSymbol)comp.Modules[0].Members.LookupLocal("W")!;
        var t = ((FieldDecl)((StructDecl)w.Declaration!).Members[0]).Type;
        Assert.IsType<ErrorSymbol>(binding.Resolve(t));
    }

    // --- imports: cross-module and external ---

    [Fact]
    public void Selective_import_binds_to_real_symbol_across_modules()
    {
        var (comp, de, _) = Resolve(
            ("lib", "pub fn greet(): int { return 0; }"),
            ("app", "import lib { greet }; fn main(): int { return 0; }"));
        Assert.False(de.HasErrors);
        var app = comp.Modules.First(m => m.FullName == "app");
        var ib = Assert.IsType<ImportBindingSymbol>(app.Members.LookupLocal("greet"));
        Assert.IsType<FunctionSymbol>(ib.Target);
    }

    [Fact]
    public void Namespace_import_of_external_module_is_opaque()
    {
        var (comp, _, _) = Resolve(("m", "import std.io; fn main(): int { return 0; }"));
        Assert.IsType<ExternalSymbol>(comp.Modules[0].Members.LookupLocal("io")); // std.io is not in the compilation
    }

    [Fact]
    public void Import_of_missing_or_private_symbol_reports()
    {
        var (_, missing, _) = Resolve(
            ("lib", "pub fn a(): int { return 0; }"),
            ("app", "import lib { nope };"));
        Assert.Contains(missing.Diagnostics, d => d.Code == "LYR-RES0004");

        var (_, priv, _) = Resolve(
            ("lib", "fn secret(): int { return 0; }"),
            ("app", "import lib { secret };"));
        Assert.Contains(priv.Diagnostics, d => d.Code == "LYR-RES0004");
    }

    [Fact]
    public void Import_cycle_is_detected()
    {
        var (_, de, _) = Resolve(("a", "import b;"), ("b", "import a;"));
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-RES0005");
    }

    // --- robustness ---

    [Theory]
    [InlineData("")]
    [InlineData("import ;")]
    [InlineData("struct { }")]
    [InlineData("fn (): int")]
    public void Resolver_never_throws_on_garbage(string source)
    {
        Assert.Null(Record.Exception(() => Resolve(("m", source))));
    }
}
