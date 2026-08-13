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
/// `Equatable&lt;T&gt;`, `Hashable&lt;T&gt;` and `Ordered&lt;T&gt;` in `std.core`.
///
/// <para>WHY GENERIC. A `fn equals(other: Equatable)` demands an interface VALUE, and a scalar cannot
/// be one — a fat pointer needs a reference. `extend int :: [Equatable]` would have been impossible
/// and with it `Map&lt;int, V&gt;`. With `Equatable&lt;T&gt;` the concrete type stands on both
/// sides.</para>
///
/// <para>WHY `Hashable` DOES NOT INHERIT FROM `Equatable`. Lyric has no interface inheritance; the
/// grammar provides no conformance list for an `InterfaceDecl`. Whoever needs both demands both.</para>
///
/// <para>This file checks the conformance THROUGH A CONSTRAINT rather than by a direct method call. The
/// difference is the whole purpose: a direct call would work even if the type did not satisfy the
/// interface at all.</para>
/// </summary>
public class CoreInterfaceTests
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

    private const string Head = """
        import std.core { Equatable, Hashable, Ordered };

        pub fn eq<T :: [Equatable<T>]>(a: T, b: T): bool { return a.equals(b); }
        pub fn hash<T :: [Hashable<T>]>(v: T): int { return v.hash(); }
        pub fn cmp<T :: [Ordered<T>]>(a: T, b: T): int { return a.compare(b); }

        """;

    // ------------------------------------------------------- conformance of the builtins

    /// <summary>
    /// Every builtin meant to satisfy `Equatable` does, checked through the constraint.
    /// </summary>
    /// <remarks>A theory rather than five separate tests: adding a type means adding a line. Without the
    /// table a missing `extend` line would show only when someone used the type as a map key.</remarks>
    [Theory]
    [InlineData("3", "3", 1)]
    [InlineData("3", "4", 0)]
    [InlineData("'a'", "'a'", 1)]
    [InlineData("'a'", "'b'", 0)]
    [InlineData("true", "true", 1)]
    [InlineData("true", "false", 0)]
    [InlineData("1.5", "1.5", 1)]
    [InlineData("1.5", "2.5", 0)]
    [InlineData("\"ab\"", "\"ab\"", 1)]
    [InlineData("\"ab\"", "\"ba\"", 0)]
    public void Every_builtin_satisfies_Equatable(string a, string b, long expected) =>
        Assert.Equal(expected, Run(Head +
            $"fn main(): int {{ return if (eq({a}, {b})) 1 else 0; }}"));

    [Theory]
    [InlineData("1", "2", -1)]
    [InlineData("2", "1", 1)]
    [InlineData("2", "2", 0)]
    [InlineData("'a'", "'b'", -1)]
    [InlineData("1.5", "2.5", -1)]
    [InlineData("\"a\"", "\"b\"", -1)]
    [InlineData("\"b\"", "\"a\"", 1)]
    [InlineData("\"ab\"", "\"ab\"", 0)]
    public void Every_ordered_builtin_compares(string a, string b, long expected) =>
        Assert.Equal(expected, Run(Head +
            $"fn main(): int {{ return cmp({a}, {b}); }}"));

    [Fact]
    public void A_prefix_sorts_before_the_longer_string() =>
        // The same beginning, different lengths — the case a naive loop over the shorter of the two
        // forgets.
        Assert.Equal(-1, Run(Head + "fn main(): int { return cmp(\"ab\", \"abc\"); }"));

    // ------------------------------------------------------------------ Hash

    /// <summary>
    /// The invariant of every hash table: equal values yield the same hash.
    ///
    /// <para>No compiler checks it, and without it `Map` builds a table that cannot find a key it stored
    /// itself. The converse does not hold — two different values MAY collide.</para>
    /// </summary>
    [Theory]
    [InlineData("42")]
    [InlineData("'x'")]
    [InlineData("true")]
    [InlineData("\"hallo welt\"")]
    [InlineData("\"\"")]
    public void Equal_values_hash_equally(string value) =>
        Assert.Equal(1, Run(Head +
            $"fn main(): int {{ return if (hash({value}) == hash({value})) 1 else 0; }}"));

    [Fact]
    public void The_string_hash_distinguishes_similar_strings() =>
        // A hash that always yields the same value satisfies the invariant above and is still worthless.
        // Two strings differing in one character are the sharpest cheap test for that.
        Assert.Equal(1, Run(Head +
            "fn main(): int { return if (hash(\"hallo\") != hash(\"hallp\")) 1 else 0; }"));

    [Fact]
    public void The_string_hash_depends_on_order() =>
        // A sum over the characters would be equal here. FNV-1a is not.
        Assert.Equal(1, Run(Head +
            "fn main(): int { return if (hash(\"ab\") != hash(\"ba\")) 1 else 0; }"));

    // ------------------------------------------------------------------ Nutzertypen

    private const string UserType = """
        pub struct Punkt :: [Equatable<Punkt>, Hashable<Punkt>] {
            x: int,
            y: int,
            fn equals(other: Punkt): bool { return this.x == other.x && this.y == other.y; }
            fn hash(): int { return this.x * 31 + this.y; }
        }

        """;

    [Fact]
    public void A_user_struct_satisfies_both_interfaces() =>
        // The case this is all for: a user type as a map key. Two constraints on one type parameter, each
        // with its own type argument.
        Assert.Equal(1, Run(Head + UserType + """
            fn main(): int {
                let a = Punkt { x = 1, y = 2 };
                let b = Punkt { x = 1, y = 2 };
                return if (eq(a, b) && hash(a) == hash(b)) 1 else 0;
            }
            """));

    [Fact]
    public void A_user_struct_distinguishes_different_values() =>
        Assert.Equal(0, Run(Head + UserType + """
            fn main(): int {
                return if (eq(Punkt { x = 1, y = 2 }, Punkt { x = 9, y = 2 })) 1 else 0;
            }
            """));

    // ------------------------------------------------- the counter-check: does the constraint apply?

    /// <summary>
    /// A type without conformance is REJECTED; otherwise every test above would be worthless.
    ///
    /// <para>Green tests are no proof that anything is checked. Only this one shows that the constraint
    /// has an effect at all: a struct without <c>:: [Equatable&lt;…&gt;]</c> has to trigger
    /// <c>LYR-SEM0028</c>.</para>
    ///
    /// <para><c>float</c> as <c>Hashable</c> is the second case, and there the rejection is DELIBERATE:
    /// <c>NaN != NaN</c> would mean a key cannot find itself.</para>
    /// </summary>
    [Theory]
    [InlineData("eq(Ohne { x = 1 }, Ohne { x = 1 })", "Equatable")]
    [InlineData("hash(1.5) == 0", "Hashable")]
    public void A_type_without_conformance_is_rejected(string call, string expected)
    {
        var diagnostics = Diagnostics(Head + """
            pub struct Ohne { x: int }

            """ + $"fn main(): int {{ return if ({call}) 1 else 0; }}");

        Assert.Contains("LYR-SEM0028", diagnostics);
        Assert.Contains(expected, diagnostics);
    }

    /// <summary>Compiles and returns the diagnostics as text, for the cases meant to fail.
    /// <see cref="Run"/> is no good for that: it claims nothing fails.</summary>
    private static string Diagnostics(string source)
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

        var writer = new StringWriter();
        de.RenderText(writer);
        return writer.ToString();
    }

    [Theory]
    [InlineData("1", "1")]
    [InlineData("1", "2")]
    public void Uint_satisfies_the_interfaces(string a, string b) =>
        // uint was missing entirely in the first version, noticed only through the counter-check above,
        // which reported it as 'does not satisfy'. Without uint there would be no Map<uint, V>.
        Assert.Equal(1, Run(Head + $$"""
            fn main(): int {
                let a: uint = {{a}};
                let b: uint = {{b}};
                let ok = eq(a, a) && hash(a) == hash(a) && cmp(a, b) <= 0;
                return if (ok) 1 else 0;
            }
            """));
}
