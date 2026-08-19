using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Xunit;

namespace Lyric.Tests.Sema;

/// <summary>
/// <c>opaque type</c> (v1.15): the alias keeps its identity — nothing converts implicitly, the
/// explicit <c>as</c> to exactly the underlying and back is the one door, equality works within
/// one alias, and everything else (arithmetic, ordering, constraints, f-strings) stays walled
/// off. The wall is the point: a forged or leaked handle is a compile error, not a bug report.
/// </summary>
public class OpaqueTypeTests
{
    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static DiagnosticEngine Check(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de)
        {
            ModuleLoader = StdlibLoader.ForRoot(Path.Combine(RepoRoot(), "stdlib"), sm, de),
        };

        comp.AddModule(new Parser(sm, id, de).ParseModule());
        Semantics.Analyze(comp, comp.Resolve(), de);
        return de;
    }

    private const string Entity = "opaque type Entity = int;\n\n";

    [Fact]
    public void The_explicit_cast_crosses_in_both_directions()
    {
        var de = Check(Entity +
            """
            fn main(): int {
                let e = 42 as Entity;
                return e as int;
            }
            """);
        Assert.False(de.HasErrors, string.Join("\n", de.Diagnostics));
    }

    [Fact]
    public void Nothing_converts_implicitly()
    {
        var de = Check(Entity +
            """
            fn main(): int {
                let e: Entity = 42;
                let n: int = 5 as Entity;
                return n;
            }
            """);
        Assert.Equal(2, de.Diagnostics.Count(d => d.Code == "LYR-SEM0001"));
    }

    [Fact]
    public void Arithmetic_and_ordering_never_reach_the_underlying()
    {
        var de = Check(Entity +
            """
            fn main(): int {
                let e = 1 as Entity;
                let s = e + 1;
                let o = e < e;
                return 0;
            }
            """);
        Assert.Equal(2, de.Diagnostics.Count(d => d.Code == "LYR-SEM0003"));
    }

    [Fact]
    public void Equality_works_within_one_alias_and_not_across_two()
    {
        var clean = Check(Entity +
            """
            fn main(): int {
                let a = 1 as Entity;
                let b = 2 as Entity;
                return if (a == b) 1 else 0;
            }
            """);
        Assert.False(clean.HasErrors, string.Join("\n", clean.Diagnostics));

        var mixed = Check(Entity +
            """
            opaque type Handle = int;

            fn main(): int {
                let a = 1 as Entity;
                let h = 1 as Handle;
                return if (a == h) 1 else 0;
            }
            """);
        Assert.Contains(mixed.Diagnostics, d => d.Code == "LYR-SEM0003");
    }

    [Fact]
    public void Two_aliases_of_the_same_underlying_are_two_types()
    {
        // Not even 'as' jumps sideways; the way across is through the underlying, visibly.
        var de = Check(Entity +
            """
            opaque type Handle = int;

            fn main(): int {
                let a = 1 as Entity;
                let h = a as Handle;
                return 0;
            }
            """);
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0006");
    }

    [Fact]
    public void A_constraint_is_not_satisfied_through_the_wall()
    {
        // Map<Entity, V> needs Hashable and Equatable; the alias walls its underlying's
        // conformances off. Without this the map would compile and the lowering would crash on
        // a member the type does not have.
        var de = Check(Entity +
            """
            import std.collections { Map };

            fn main(): int {
                let m = Map<Entity, int>.empty();
                return 0;
            }
            """);
        Assert.True(de.HasErrors, "an opaque alias must not satisfy its underlying's constraints");
    }

    [Fact]
    public void An_f_string_does_not_leak_the_underlying()
    {
        var de = Check(Entity +
            """
            fn main(): int {
                let e = 5 as Entity;
                let s = f"{e}";
                return 0;
            }
            """);
        var error = Assert.Single(de.Diagnostics, d => d.Code == "LYR-SEM0006");
        Assert.Contains("opaque", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plain_alias_stays_transparent()
    {
        // The counter-check: without 'opaque' nothing changed.
        var de = Check(
            """
            type Meters = int;

            fn main(): int {
                let m: Meters = 42;
                return m + 1;
            }
            """);
        Assert.False(de.HasErrors, string.Join("\n", de.Diagnostics));
    }
}
