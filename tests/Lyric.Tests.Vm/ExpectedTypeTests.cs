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
/// Where a construction may take its instance from the surrounding type.
///
/// <para>Two causes, not one. An ARGUMENT position had no expected type at all, while a binding, a
/// return and a field had one — so <c>f(Opt.Some(5))</c> failed where
/// <c>let o: Opt&lt;int&gt; = Opt.Some(5)</c> worked. And a generic STRUCT initializer read no
/// context anywhere, not even an annotation: <c>let p: P&lt;int&gt; = P { v = 1 }</c> was
/// <c>cannot assign 'P&lt;&gt;' to 'P&lt;int&gt;'</c>, while the enum struct variant beside it had
/// always read it.</para>
///
/// <para>The counter-checks carry this file. An expectation decides WHICH INSTANCE is built, and an
/// instance built from the wrong arguments puts a value in a slot of another type — the failure that
/// is a verifier finding in Debug and a silently wrong answer in Release. Every test that accepts
/// something here has one beside it that must still be refused.</para>
/// </summary>
public class ExpectedTypeTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static (Compilation Comp, DiagnosticEngine De) Front(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de)
        {
            ModuleLoader = StdlibLoader.ForRoot(Path.Combine(RepoRoot(), "stdlib"), sm, de),
        };
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        return (comp, de);
    }

    private static long Run(string source)
    {
        var (comp, de) = Front(source);
        var binding = comp.Resolve();
        var types = Semantics.Analyze(comp, binding, de);

        var writer = new StringWriter();
        de.RenderText(writer);
        Assert.False(de.HasErrors, "source did not compile:\n" + writer);

        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true);
        Assert.NotNull(ir);
        return Interpreter.Run(BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir!)),
            NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null)).AsI64;
    }

    private static string[] Errors(string source)
    {
        var (comp, de) = Front(source);
        Semantics.Analyze(comp, comp.Resolve(), de);
        Assert.True(de.HasErrors, "the program compiled, but the test expects it to be refused");
        return de.Diagnostics.Select(d => d.Code).ToArray();
    }

    private const string Opt = "enum Opt<T> { None, Some(T); }\n";
    private const string Ev = "enum Ev<T> { Miss, Hit { at: T }; }\n";

    // ------------------------------------------------------------------ an enum in an argument

    [Fact]
    public void A_tuple_variant_takes_its_instance_from_the_parameter() =>
        Assert.Equal(5, Run(Opt + """
            fn f(o: Opt<int>): int { return match (o) { Opt.Some(v) => v, Opt.None => 0, }; }
            fn main(): int { return f(Opt.Some(5)); }
            """));

    [Fact]
    public void A_unit_variant_takes_its_instance_from_the_parameter() =>
        Assert.Equal(3, Run(Opt + """
            fn f(o: Opt<int>): int { return match (o) { Opt.Some(v) => v, Opt.None => 3, }; }
            fn main(): int { return f(Opt.None); }
            """));

    [Fact]
    public void A_struct_variant_takes_its_instance_from_the_parameter() =>
        Assert.Equal(4, Run(Ev + """
            fn f(e: Ev<int>): int { return match (e) { Ev.Hit { at } => at, Ev.Miss => 0, }; }
            fn main(): int { return f(Ev.Hit { at = 4 }); }
            """));

    [Fact]
    public void A_variadic_parameter_carries_its_element_type()
    {
        // The expectation of a 'params' parameter is the ELEMENT, not the array.
        Assert.Equal(2, Run(Opt + """
            fn f(params os: Opt<int>[]): int { return os.length; }
            fn main(): int { return f(Opt.Some(1), Opt.None); }
            """));
    }

    // ------------------------------------------------------------------ a struct initializer

    [Fact]
    public void A_generic_struct_takes_its_instance_from_an_annotation() =>
        Assert.Equal(1, Run("struct P<T> { v: T, }\nfn main(): int { let p: P<int> = P { v = 1 }; return p.v; }"));

    [Fact]
    public void A_generic_struct_takes_its_instance_from_a_parameter() =>
        Assert.Equal(6, Run("""
            struct P<T> { v: T, }
            fn f(p: P<int>): int { return p.v; }
            fn main(): int { return f(P { v = 6 }); }
            """));

    [Fact]
    public void A_generic_struct_takes_its_instance_from_a_return_type() =>
        Assert.Equal(8, Run("""
            struct P<T> { v: T, }
            fn g(): P<int> { return P { v = 8 }; }
            fn main(): int { return g().v; }
            """));

    [Fact]
    public void A_generic_class_takes_its_instance_from_an_annotation() =>
        Assert.Equal(2, Run("class C<T> { v: T, }\nfn main(): int { let c: C<int> = C { v = 2 }; return c.v; }"));

    [Fact]
    public void Written_type_arguments_still_win()
    {
        // Written beats context, as for an enum variant: it says itself which instance is meant, and
        // that has to hold where there is no context at all.
        Assert.Equal(9, Run("struct P<T> { v: T, }\nfn main(): int { let p = P<int> { v = 9 }; return p.v; }"));
    }

    // ------------------------------------------------------------------ what must stay refused

    [Fact]
    public void A_field_value_of_the_wrong_type_is_still_refused() =>
        Assert.Contains("LYR-SEM0001", Errors(
            "struct P<T> { v: T, }\nfn main(): int { let p: P<string> = P { v = 1 }; return 0; }"));

    [Fact]
    public void A_context_naming_another_type_gives_no_arguments()
    {
        // The definition must MATCH. Taking the arguments of 'Box<int>' for a 'Pair { … }' would
        // silently build an instance nobody asked for.
        Assert.Contains("LYR-SEM0026", Errors("""
            struct Box<T> { v: T, }
            struct Pair<T> { a: T, }
            fn main(): int { let b: Box<int> = Pair { a = 1 }; return 0; }
            """));
    }

    [Fact]
    public void Without_any_context_a_generic_struct_is_still_refused()
    {
        // There is no inference from the field VALUES; the context is read, the '1' is not.
        Assert.Contains("LYR-SEM0026", Errors("struct P<T> { v: T, }\nfn main(): int { let p = P { v = 1 }; return 0; }"));
    }

    [Fact]
    public void The_arity_is_still_checked() =>
        Assert.Contains("LYR-SEM0026", Errors("""
            struct P<A, B> { a: A, b: B, }
            fn main(): int { let p: P<int> = P { a = 1, b = 2 }; return 0; }
            """));

    [Fact]
    public void A_written_instance_that_does_not_fit_the_parameter_is_still_refused() =>
        Assert.Contains("LYR-SEM0001", Errors(Opt + """
            fn f(o: Opt<int>): int { return 0; }
            fn main(): int { return f(Opt<string>.Some("x")); }
            """));

    [Fact]
    public void A_payload_of_the_wrong_type_for_the_context_is_still_refused() =>
        Assert.Contains("LYR-SEM0001", Errors(Opt + """
            fn f(o: Opt<int>): int { return 0; }
            fn main(): int { return f(Opt.Some("x")); }
            """));

    // ------------------------------------------------------------------ inference is not bypassed

    [Fact]
    public void An_open_parameter_type_is_no_expectation()
    {
        // 'Opt<T>' states nothing about the instance — it is the question the inference answers from
        // this argument. Offering it would answer that question with itself.
        Assert.Contains("LYR-SEM0063", Errors(Opt + """
            fn f<T>(o: Opt<T>): int { return 0; }
            fn main(): int { return f(Opt.None); }
            """));
    }

    [Fact]
    public void A_generic_function_still_infers_from_a_written_instance() =>
        Assert.Equal(7, Run(Opt + """
            fn f<T>(o: Opt<T>): int { return 7; }
            fn main(): int { return f(Opt<int>.Some(1)); }
            """));

    [Fact]
    public void A_concrete_parameter_of_a_generic_function_is_still_an_expectation()
    {
        // One parameter open, the other concrete: the expectation is per parameter, not per call.
        Assert.Equal(3, Run(Opt + """
            fn f<T>(x: T, o: Opt<int>): int { return match (o) { Opt.Some(v) => v, Opt.None => 0, }; }
            fn main(): int { return f("ignored", Opt.Some(3)); }
            """));
    }

    [Fact]
    public void Ordinary_inference_is_untouched() =>
        Assert.Equal(5, Run("fn id<T>(x: T): T { return x; }\nfn main(): int { return id(5); }"));

    [Fact]
    public void A_written_type_argument_against_a_wrong_value_still_errors() =>
        Assert.Contains("LYR-SEM0001", Errors(
            "fn id<T>(x: T): T { return x; }\nfn main(): int { let s = id<int>(\"x\"); return 0; }"));

    // ------------------------------------------------------------------ the load-bearing promise

    [Fact]
    public void Two_instances_in_one_program_keep_their_own_layouts()
    {
        // The reason the counter-checks above matter: if the context could build the wrong instance,
        // an i64 would land in a string slot. Both come from the context here, not from a written
        // argument.
        Assert.Equal(1, Run("""
            import std.string { length };
            struct P<T> { v: T, }
            fn takesInt(p: P<int>): int { return p.v; }
            fn takesString(p: P<string>): int { return length(p.v); }
            fn main(): int { return takesInt(P { v = 4 }) - takesString(P { v = "abc" }); }
            """));
    }
}
