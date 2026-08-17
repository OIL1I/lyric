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
/// <c>x as T</c> on types conforming to <c>Into&lt;T&gt;</c>, end to end.
///
/// <para>The last of the four operator slices, and the one with the tightest boundary: explicit
/// only, ONE target per type — <c>into</c> is a member name and a type has one member of a name —
/// and the numeric casts keep their opcodes untouchable.</para>
/// </summary>
public class OperatorIntoTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static long Run(string source)
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
        Assert.False(de.HasErrors, "source did not compile: " + writer);

        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true);
        Assert.NotNull(ir);
        return Interpreter.Run(BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir!)),
            NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null)).AsI64;
    }

    private const string Temperatures = """
        import std.core { Into };

        struct Fahrenheit { degrees: int, }

        struct Celsius :: [Into<Fahrenheit>] {
            degrees: int,
            fn into(): Fahrenheit {
                return Fahrenheit { degrees = this.degrees * 9 / 5 + 32 };
            }
        }

        """;

    [Fact]
    public void A_cast_converts_through_the_conformance()
    {
        Assert.Equal(212, Run(Temperatures + """
            fn main(): int {
                let boiling = Celsius { degrees = 100 };
                let f = boiling as Fahrenheit;
                return f.degrees;
            }
            """));
    }

    [Fact]
    public void The_cast_and_the_written_call_agree()
    {
        Assert.Equal(1, Run(Temperatures + """
            fn main(): int {
                let c = Celsius { degrees = 37 };
                let viaCast = c as Fahrenheit;
                let viaCall = c.into();
                return if (viaCast.degrees == viaCall.degrees) 1 else 0;
            }
            """));
    }

    [Fact]
    public void A_conversion_out_of_a_builtin_is_stopped_by_the_orphan_rule()
    {
        // 'extend int :: [Into<Cents>]' is LYR-SEM0041: neither 'int' nor 'Into' is declared in
        // the extending module, and the orphan rule does not look into type arguments — 'Cents'
        // being local does not rescue it. A conversion OUT of a builtin therefore has no home; a
        // named function takes its place. Measured here so the limit has an address.
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", """
            import std.core { Into };

            struct Cents { value: int, }

            extend int :: [Into<Cents>] {
                fn into(): Cents { return Cents { value = this * 100 }; }
            }

            fn main(): int {
                let five = 5 as Cents;
                return five.value;
            }
            """);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de)
        {
            ModuleLoader = StdlibLoader.ForRoot(Path.Combine(RepoRoot(), "stdlib"), sm, de),
        };
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        Semantics.Analyze(comp, comp.Resolve(), de);

        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0041");
    }

    [Fact]
    public void Conversions_chain()
    {
        Assert.Equal(1, Run("""
            import std.core { Into };

            struct B { n: int, }
            struct C { n: int, }

            struct A :: [Into<B>] {
                n: int,
                fn into(): B { return B { n = this.n + 1 }; }
            }

            extend B :: [Into<C>] {
                fn into(): C { return C { n = this.n + 1 }; }
            }

            fn main(): int {
                let a = A { n = 0 };
                let c = (a as B) as C;
                return if (c.n == 2) 1 else 0;
            }
            """));
    }

    [Fact]
    public void The_operand_is_evaluated_once()
    {
        Assert.Equal(1, Run(Temperatures + """
            fn main(): int {
                var made = 0;
                let make = (): Celsius => {
                    made = made + 1;
                    return Celsius { degrees = 0 };
                };
                let f = make() as Fahrenheit;
                return if (f.degrees == 32) made else -1;
            }
            """));
    }

    [Fact]
    public void Numeric_casts_are_untouched()
    {
        Assert.Equal(1, Run("""
            fn main(): int {
                let f = 1 as float;
                let n = 2.9 as int;
                let c = 'a' as int;
                return if (f == 1.0 && n == 2 && c == 97) 1 else 0;
            }
            """));
    }
}
