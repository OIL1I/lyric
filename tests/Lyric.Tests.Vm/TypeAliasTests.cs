using System.Runtime.CompilerServices;
using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Ir;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Lyric.Vm;

namespace Lyric.Tests.Vm;

/// <summary>
/// A <c>type</c> alias in every position a type may stand.
///
/// <para>The sema always replaced an alias by what it names; the lowering did not, and reached
/// <c>InternNonGeneric</c> with a symbol that has no layout — <c>LYR-IR0001</c> as a return type and
/// as a field type, while a parameter and a local annotation worked. One question, two answers.</para>
///
/// <para>Run rather than checked: an alias that resolves to the wrong type still type-checks against
/// itself, and only the result says which type actually arrived.</para>
/// </summary>
public class TypeAliasTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static IrModule Lower(string source)
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
        Assert.False(de.HasErrors, "source did not compile:\n" + writer);

        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true);
        Assert.NotNull(ir);
        return ir!;
    }

    private static long Run(string source) =>
        Interpreter.Run(BytecodeReader.ReadOrThrow(BytecodeWriter.Write(Lower(source))),
            NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null)).AsI64;

    // ------------------------------------------------------------------ the positions

    [Fact]
    public void An_alias_as_a_return_type()
    {
        Assert.Equal(42, Run("type Id = int;\nfn f(): Id { return 41; }\nfn main(): int { return f() + 1; }"));
    }

    [Fact]
    public void An_alias_as_a_field_type_of_a_struct()
    {
        Assert.Equal(7, Run("""
            type Id = int;
            struct H { id: Id, }
            fn main(): int { let h = H { id = 7 }; return h.id; }
            """));
    }

    [Fact]
    public void An_alias_as_a_field_type_of_a_class()
    {
        Assert.Equal(4, Run("""
            type Id = int;
            class K { id: Id, }
            fn main(): int { let k = K { id = 4 }; return k.id; }
            """));
    }

    [Fact]
    public void An_alias_as_a_parameter_type_and_a_local_annotation()
    {
        // These two worked before; they stand here so a fix that moved the gap rather than closing
        // it shows up.
        Assert.Equal(2, Run("""
            type Id = int;
            fn f(x: Id): int { let y: Id = x; return y; }
            fn main(): int { return f(2); }
            """));
    }

    [Fact]
    public void An_alias_over_a_struct_carries_its_layout()
    {
        Assert.Equal(9, Run("""
            struct P { v: int, }
            type Point = P;
            fn make(): Point { return P { v = 9 }; }
            fn main(): int { return make().v; }
            """));
    }

    [Fact]
    public void An_alias_of_an_alias_resolves_through()
    {
        Assert.Equal(3, Run("type A = int;\ntype B = A;\nfn f(): B { return 3; }\nfn main(): int { return f(); }"));
    }

    [Fact]
    public void An_alias_as_an_array_element()
    {
        Assert.Equal(2, Run("type Id = int;\nfn f(): Id[] { return [2]; }\nfn main(): int { return f()[0]; }"));
    }

    [Fact]
    public void An_alias_inside_an_optional()
    {
        Assert.Equal(6, Run("type Id = int;\nfn f(): ?Id { return 6; }\nfn main(): int { return f()!; }"));
    }

    [Fact]
    public void An_alias_as_a_type_argument()
    {
        Assert.Equal(5, Run("""
            import std.collections { List, emptyList };
            type Id = int;
            fn main(): int { let xs = emptyList<Id>(); xs.push(5); return xs.get(0); }
            """));
    }

    [Fact]
    public void An_alias_naming_a_generic_instance()
    {
        Assert.Equal(8, Run("""
            struct Box<T> { v: T, }
            type IntBox = Box<int>;
            fn f(b: IntBox): int { return b.v; }
            fn main(): int { return f(Box<int> { v = 8 }); }
            """));
    }

    // ------------------------------------------------------------------ what reaches the bytecode

    [Fact]
    public void An_alias_leaves_no_trace_in_the_lowered_module()
    {
        // The point of the fix: an alias is a NAME for a type, so it gets no table entry of its own.
        // A module carrying 'Id' as a type would mean the erasure only looked complete.
        var ir = Lower("""
            type Id = int;
            struct H { id: Id, }
            fn f(): Id { return 1; }
            fn main(): int { let h = H { id = 1 }; return h.id + f(); }
            """);

        var dump = IrPrinter.Dump(ir);
        Assert.DoesNotContain("Id", dump);
    }

    [Fact]
    public void An_alias_and_the_type_it_names_intern_as_one()
    {
        // 'List<Id>' and 'List<int>' are the same instance. Two entries would be two layouts for one
        // type, which is the asymmetry that makes a value arrive in the wrong slot.
        var ir = Lower("""
            import std.collections { List, emptyList };
            type Id = int;
            fn a(): int { let xs = emptyList<Id>(); xs.push(1); return xs.get(0); }
            fn b(): int { let ys = emptyList<int>(); ys.push(2); return ys.get(0); }
            fn main(): int { return a() + b(); }
            """);

        var lists = ir.Types.Count(t => t.Name.Contains("List", StringComparison.Ordinal));
        Assert.Equal(1, lists);
    }
}
