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
/// `std.collections` — `Indexable&lt;T&gt;` and `List&lt;T&gt;`.
///
/// <para>`List&lt;T&gt;` IS WRITTEN IN LYRIC, NOT NATIVELY. Natives are registered monomorphically, so
/// a generic one would need a marshalling layer. That a standard library can express its own
/// containers is the more interesting statement anyway.</para>
///
/// <para>THE BACKING IS `(?T)[]`, so a slot can be emptied: `pop` really releases its value instead of
/// merely letting it disappear behind `count`. That also solves the creation — a `T[]` of length n
/// would need n values of type T, and Lyric has no `default(T)`; for `?T` there is one.</para>
///
/// <para>The first version was wrong in two places and this file did not notice: `get` checked against
/// `data.length` rather than against `count` and therefore returned leftovers from the doubling, and
/// `pop` left its value in the slot. The growth test only checked that nothing was MISSING, not that
/// nothing was IN EXCESS. The five tests under "bounds and release" close exactly that
/// direction.</para>
/// </summary>
public class CollectionTests
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

    private const string Head = "import std.collections { List };\n";

    // ------------------------------------------------------------------ List<T>

    [Fact]
    public void A_list_starts_empty() =>
        Assert.Equal(0, Run(Head + "fn main(): int { return List<int>.empty().length(); }"));

    [Fact]
    public void Push_appends_and_counts() =>
        Assert.Equal(3, Run(Head + """
            fn main(): int {
                let xs = List<int>.empty();
                xs.push(1);
                xs.push(2);
                xs.push(3);
                return xs.length();
            }
            """));

    [Fact]
    public void Growth_preserves_every_element()
    {
        // The growth test. With 100 elements the array doubles seven times; if an element were lost or
        // overwritten in the process, the sum would not match. A test with three elements would stay
        // green even with the doubling broken.
        Assert.Equal(4950, Run(Head + """
            fn main(): int {
                let xs = List<int>.empty();
                var i = 0;
                while (i < 100) { xs.push(i); i = i + 1; }

                var sum = 0;
                var k = 0;
                while (k < xs.length()) { sum = sum + xs.get(k); k = k + 1; }
                return sum;
            }
            """));
    }

    [Fact]
    public void Pop_returns_the_last_value_and_shrinks() =>
        Assert.Equal(21, Run(Head + """
            fn main(): int {
                let xs = List<int>.empty();
                xs.push(1);
                xs.push(2);
                let last = xs.pop() ?? 0;
                return last * 10 + xs.length();
            }
            """));

    [Fact]
    public void Pop_on_an_empty_list_is_null_not_a_panic() =>
        // An empty list is an ordinary state rather than a programming error, unlike an index out of
        // range. Hence `?T` and no `panic`.
        Assert.Equal(7, Run(Head + """
            fn main(): int {
                let xs = List<int>.empty();
                return xs.pop() ?? 7;
            }
            """));

    [Fact]
    public void A_list_of_strings_works_too() =>
        // A second instantiation: with only one the test would stay green even if the monomorphization
        // ignored the element type.
        Assert.Equal(2, Run("""
            import std.collections { List };
            fn main(): int {
                let xs = List<string>.empty();
                xs.push("a");
                xs.push("b");
                return xs.length();
            }
            """));

    // ------------------------------------------------------------------ Indexable<T>

    [Fact]
    public void A_list_can_be_read_with_brackets() =>
        // `[i]` on a non-array goes through `Indexable<T>.get`, the same division of labour as `for-in`
        // over `Iterator<T>`. The compiler knows exactly ONE built-in indexable form, the array.
        Assert.Equal(20, Run(Head + """
            fn main(): int {
                let xs = List<int>.empty();
                xs.push(10);
                xs.push(20);
                return xs[1];
            }
            """));

    [Fact]
    public void A_list_can_be_written_with_brackets() =>
        // This works because `let` binds the name rather than the content; under the other rule
        // `Indexable<T>` would have to reproduce a special case.
        Assert.Equal(5, Run(Head + """
            fn main(): int {
                let xs = List<int>.empty();
                xs.push(10);
                xs[0] = 5;
                return xs[0];
            }
            """));

    [Fact]
    public void An_array_still_uses_the_builtin_path() =>
        // The counter-check: the Indexable path must not have taken over the built-in array indexing. An
        // array does not implement `Indexable<T>` — it is the built-in form, and `ldelem` stays an array
        // access without a method call.
        Assert.Equal(9, Run("fn main(): int { let xs = [1, 9, 3]; return xs[1]; }"));

    [Fact]
    public void A_user_type_can_implement_Indexable() =>
        // Not only the stdlib: the interface is open to anyone. Without this test it would not be held
        // that `[i]` really hangs on the interface rather than on `List<T>`.
        Assert.Equal(42, Run("""
            import std.collections { Indexable };

            class Doubler :: [Indexable<int>] {
                base: int,
                pub fn get(index: int): int { return this.base * index; }
                pub mut fn set(index: int, value: int): void { this.base = value; }
            }

            fn main(): int {
                let d = Doubler { base = 21 };
                return d[2];
            }
            """));

    // ------------------------------------------------------------------ bounds and release

    // These five tests did NOT exist in the first version, and two faults survived there: 'get' checked
    // against 'data.length' rather than against 'count' and returned leftovers from the doubling, and
    // 'pop' left its value in the slot. The growth test above only checked that nothing was MISSING, not
    // that nothing was IN EXCESS.

    [Fact]
    public void Reading_past_the_end_panics_even_when_capacity_is_larger()
    {
        // After three pushes the capacity is 4: index 3 lies inside the array but outside the list. This
        // is exactly where the old version returned a leftover.
        var panic = Assert.Throws<LyricPanic>(() => Run(Head + """
            fn main(): int {
                let xs = List<int>.empty();
                xs.push(10);
                xs.push(20);
                xs.push(30);
                return xs[3];
            }
            """));

        Assert.Contains("out of range", panic.Message);
    }

    [Fact]
    public void A_popped_value_is_gone_not_just_hidden() =>
        // 'pop' empties the slot. Otherwise the list keeps the object alive and a 'get' at this position
        // reads it back although it has been removed.
        Assert.Throws<LyricPanic>(() => Run(Head + """
            fn main(): int {
                let xs = List<int>.empty();
                xs.push(10);
                xs.push(20);
                let v = xs.pop();
                return xs[1];
            }
            """));

    [Fact]
    public void A_negative_index_panics() =>
        Assert.Throws<LyricPanic>(() => Run(Head + """
            fn main(): int { let xs = List<int>.empty(); xs.push(1); return xs[0 - 1]; }
            """));

    [Fact]
    public void Push_after_pop_overwrites_instead_of_appending() =>
        // 'count' is the truth rather than 'data.length'. The other way round the list would grow at the
        // wrong place after a pop.
        Assert.Equal(99, Run(Head + """
            fn main(): int {
                let xs = List<int>.empty();
                xs.push(10);
                xs.push(20);
                let v = xs.pop();
                xs.push(99);
                return xs[1] + (xs.length() - 2);
            }
            """));

    [Fact]
    public void Capacity_grows_by_doubling_and_shrinks_by_halving()
    {
        // The slack does not stay: 100 elements give a capacity of 128, and after 95 pops there are 5
        // elements with a capacity of 16.
        //
        // The threshold is a QUARTER rather than a half, so a list oscillating around the boundary does
        // not copy on every push and pop.
        Assert.Equal(128, Run(Head + """
            fn main(): int {
                let xs = List<int>.empty();
                var i = 0;
                while (i < 100) { xs.push(i); i = i + 1; }
                return xs.capacity();
            }
            """));

        Assert.Equal(16, Run(Head + """
            fn main(): int {
                let xs = List<int>.empty();
                var i = 0;
                while (i < 100) { xs.push(i); i = i + 1; }
                var k = 0;
                while (k < 95) { let v = xs.pop(); k = k + 1; }
                return xs.capacity();
            }
            """));
    }

    [Fact]
    public void A_small_list_keeps_a_floor_capacity() =>
        // Below four slots the copying is not worth it: the gain would be a few dozen bytes.
        Assert.Equal(4, Run(Head + """
            fn main(): int {
                let xs = List<int>.empty();
                xs.push(1);
                let v = xs.pop();
                return xs.capacity();
            }
            """));

    // ------------------------------------------------------------------ Iterable<T>

    [Fact]
    public void A_list_can_be_walked_with_for_in() =>
        // 'for-in' asks FORWARDS: when the carrier satisfies 'Iterable<T>', 'iter()' is called. The
        // compiler does NOT search backwards for an iterator taking the list as its source — that would
        // be ambiguous with two candidates and would have to scan every visible module.
        Assert.Equal(6, Run(Head + """
            fn main(): int {
                let xs = List<int>.empty();
                xs.push(1);
                xs.push(2);
                xs.push(3);
                var sum = 0;
                for (x in xs) { sum = sum + x; }
                return sum;
            }
            """));

    [Fact]
    public void Two_loops_over_the_same_list_do_not_interfere() =>
        // The reason for the split in two. Were 'List<T>' its own 'Iterator<T>', it would have a built-in
        // progress: the inner loop would advance the outer one and the result would be 3 instead of 9.
        // 'iter()' yields a fresh cursor on every call.
        Assert.Equal(9, Run(Head + """
            fn main(): int {
                let xs = List<int>.empty();
                xs.push(1);
                xs.push(2);
                xs.push(3);
                var n = 0;
                for (a in xs) {
                    for (b in xs) { n = n + 1; }
                }
                return n;
            }
            """));

    [Fact]
    public void An_empty_list_iterates_zero_times() =>
        Assert.Equal(0, Run(Head + """
            fn main(): int {
                let xs = List<int>.empty();
                var n = 0;
                for (x in xs) { n = n + 1; }
                return n;
            }
            """));

    [Fact]
    public void A_user_type_can_implement_Iterable() =>
        // As with 'Indexable': the interface is open to anyone, not only to the stdlib. Without this test
        // 'for-in' might hang on 'List<T>' rather than on the interface.
        Assert.Equal(3, Run("""
            import std.iter { Iterator, Iterable };

            class Once :: [Iterator<int>] {
                done: bool,
                pub mut fn next(): ?int {
                    if (this.done) { return null; }
                    this.done = true;
                    return 3;
                }
            }

            class Source :: [Iterable<int>] {
                pub fn iter(): Iterator<int> { return Once { done = false }; }
            }

            fn main(): int {
                let s = Source { };
                var sum = 0;
                for (x in s) { sum = sum + x; }
                return sum;
            }
            """));

    [Fact]
    public void A_plain_iterator_still_works_directly() =>
        // The counter-check: the Iterable path must not have displaced the old one. A value satisfying
        // 'Iterator<T>' itself is still used directly.
        Assert.Equal(3, Run("""
            import std.iter { Iterator };

            class Once :: [Iterator<int>] {
                done: bool,
                pub mut fn next(): ?int {
                    if (this.done) { return null; }
                    this.done = true;
                    return 3;
                }
            }

            fn main(): int {
                let o = Once { done = false };
                var sum = 0;
                for (x in o) { sum = sum + x; }
                return sum;
            }
            """));

    // ------------------------------------------------------------------ clear and toArray
    //
    // 'toArray' is the interesting case: its return type is 'T[]' and the backing is a '(?T)[]', and
    // there is NO reinterpretation between the two — '!' unwraps a single value rather than an array
    // element by element. Writing 'return result!;' is LYR-SEM0005.

    [Fact]
    public void ToArray_copies_the_used_slots_and_nothing_more() =>
        // Three elements, sum 17: length AND content in one number, so no part of it can be off
        // unnoticed.
        Assert.Equal(3017, Run("""
            import std.collections;

            fn main(): int {
                var xs = collections.List<int>.empty();
                xs.push(3);
                xs.push(5);
                xs.push(9);

                let a = xs.toArray();
                return a.length * 1000 + a[0] + a[1] + a[2];
            }
            """));

    /// <summary>
    /// The length is <c>count</c> rather than <c>capacity</c>. After three <c>push</c> calls four slots
    /// stand ready, and an array with four elements would be the fault <c>get</c> already made.
    /// </summary>
    [Fact]
    public void ToArray_uses_count_and_not_capacity() =>
        Assert.Equal(3, Run("""
            import std.collections;

            fn main(): int {
                var xs = collections.List<int>.empty();
                xs.push(1);
                xs.push(2);
                xs.push(3);
                return xs.toArray().length;
            }
            """));

    /// <summary>The empty list has no first element to build a <c>T[]</c> from and is caught beforehand.
    /// Without this test exactly that branch would stay unchecked.</summary>
    [Fact]
    public void ToArray_on_an_empty_list_is_an_empty_array() =>
        Assert.Equal(0, Run("""
            import std.collections;

            fn main(): int {
                let xs = collections.List<int>.empty();
                return xs.toArray().length;
            }
            """));

    /// <summary>The copy is a copy: changing it does not change the list.</summary>
    [Fact]
    public void ToArray_returns_a_copy() =>
        Assert.Equal(1, Run("""
            import std.collections;

            fn main(): int {
                var xs = collections.List<int>.empty();
                xs.push(1);

                let a = xs.toArray();
                a[0] = 99;
                return xs.get(0);
            }
            """));

    [Fact]
    public void Clear_empties_the_list() =>
        Assert.Equal(0, Run("""
            import std.collections;

            fn main(): int {
                var xs = collections.List<int>.empty();
                xs.push(1);
                xs.push(2);
                xs.clear();
                return xs.length();
            }
            """));

    /// <summary>
    /// <c>clear</c> keeps the backing array (v1.14): a buffer emptied once per frame must not
    /// reallocate on every refill. The VALUES are still released — the slots are nulled, so
    /// nothing inserted stays alive — but that is invisible from Lyric; what is visible is the
    /// kept capacity.
    /// </summary>
    [Fact]
    public void Clear_keeps_the_capacity_for_reuse() =>
        Assert.Equal(4, Run("""
            import std.collections;

            fn main(): int {
                var xs = collections.List<int>.empty();
                xs.push(1);
                xs.push(2);
                xs.push(3);
                xs.clear();
                return xs.capacity();
            }
            """));

    /// <summary>And afterwards it is usable again: an emptied backing must be no special case for
    /// <c>push</c>.</summary>
    [Fact]
    public void A_cleared_list_still_grows() =>
        Assert.Equal(7, Run("""
            import std.collections;

            fn main(): int {
                var xs = collections.List<int>.empty();
                xs.push(1);
                xs.clear();
                xs.push(7);
                return xs.get(0);
            }
            """));
}
