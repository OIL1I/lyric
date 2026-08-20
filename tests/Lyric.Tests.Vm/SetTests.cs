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
/// `Set&lt;T&gt;` and the additions to `Map` and `List`.
///
/// <para>`Set` IS NOT BUILT AS A `Map&lt;T, bool&gt;`, although that would be shorter: it wastes an array,
/// and `add(x)` would read as `set(x, true)`, a spelling that would need explaining at every call site.
/// Duplicating the probing logic is the more honest
/// choice.</para>
///
/// <para>The iteration order depends on the hashes and is UNSPECIFIED. The tests here therefore check
/// sums and counts, never a position: a test relying on today's hash order would be a promise the
/// library does not make.</para>
/// </summary>
public class SetTests
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

    private const string Head =
        "import std.collections { Set, union, intersect, difference, isSubset };\n";

    // ------------------------------------------------------------------ Grundlagen

    [Fact]
    public void A_set_starts_empty() =>
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let s = Set<int>.empty();
                return if (s.length() == 0 && s.isEmpty()) 1 else 0;
            }
            """));

    [Fact]
    public void Add_reports_whether_the_value_was_new() =>
        // The return value is no decoration: "was that already in?" is the question one would otherwise
        // put a 'contains' in front of, probing twice.
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let s = Set<int>.empty();
                let ersteMal = s.add(7);
                let zweiteMal = s.add(7);
                return if (ersteMal && !zweiteMal && s.length() == 1) 1 else 0;
            }
            """));

    [Fact]
    public void Remove_reports_whether_the_value_was_there() =>
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let s = Set<int>.empty();
                s.add(7);
                let weg = s.remove(7);
                let nochmal = s.remove(7);
                return if (weg && !nochmal && s.isEmpty()) 1 else 0;
            }
            """));

    [Fact]
    public void Everything_survives_resizing() =>
        // Two hundred values across several doublings; afterwards all have to still be there and none
        // counted twice.
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let s = Set<int>.empty();
                var i = 0;
                while (i < 200) { s.add(i); i = i + 1; }

                var fehlend = 0;
                i = 0;
                while (i < 200) {
                    if (!s.contains(i)) { fehlend = fehlend + 1; }
                    i = i + 1;
                }
                return if (s.length() == 200 && fehlend == 0) 1 else 0;
            }
            """));

    [Fact]
    public void Iteration_visits_every_element_exactly_once() =>
        // Sum AND count: the sum alone would not find an element visited twice if another were missing in
        // exchange.
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let s = Set<int>.empty();
                var i = 1;
                while (i <= 10) { s.add(i); i = i + 1; }

                var summe = 0;
                var anzahl = 0;
                for (v in s.iter()) { summe = summe + v; anzahl = anzahl + 1; }

                return if (summe == 55 && anzahl == 10) 1 else 0;
            }
            """));

    // ------------------------------------------------------------ Mengenoperationen

    [Fact]
    public void Union_contains_both_sides_without_duplicates() =>
        Assert.Equal(4, Run(Head + """
            fn main(): int {
                let a = Set<int>.empty();
                a.add(1); a.add(2);
                let b = Set<int>.empty();
                b.add(2); b.add(3); b.add(4);
                return union(a, b).length();
            }
            """));

    [Fact]
    public void Intersect_keeps_only_what_both_have() =>
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let a = Set<int>.empty();
                a.add(1); a.add(2);
                let b = Set<int>.empty();
                b.add(2); b.add(3);
                let s = intersect(a, b);
                return if (s.length() == 1 && s.contains(2)) 1 else 0;
            }
            """));

    [Fact]
    public void Intersect_is_symmetric_regardless_of_size() =>
        // The implementation runs over the SMALLER set. Without this test it would stay unchecked whether
        // both directions yield the same; the swap is exactly the place one gets wrong.
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let klein = Set<int>.empty();
                klein.add(5);
                let gross = Set<int>.empty();
                var i = 0;
                while (i < 20) { gross.add(i); i = i + 1; }

                let a = intersect(klein, gross);
                let b = intersect(gross, klein);
                return if (a.length() == 1 && b.length() == 1 && a.contains(5) && b.contains(5))
                    1 else 0;
            }
            """));

    [Fact]
    public void Difference_removes_what_the_other_has() =>
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let a = Set<int>.empty();
                a.add(1); a.add(2); a.add(3);
                let b = Set<int>.empty();
                b.add(2);
                let s = difference(a, b);
                return if (s.length() == 2 && s.contains(1) && s.contains(3) && !s.contains(2))
                    1 else 0;
            }
            """));

    [Fact]
    public void The_operations_do_not_modify_their_arguments() =>
        // All three yield a NEW set. One silently changing its argument would no longer be usable in a
        // chain.
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let a = Set<int>.empty();
                a.add(1); a.add(2);
                let b = Set<int>.empty();
                b.add(2); b.add(3);

                union(a, b);
                intersect(a, b);
                difference(a, b);

                return if (a.length() == 2 && b.length() == 2) 1 else 0;
            }
            """));

    [Fact]
    public void IsSubset_checks_containment_in_both_directions() =>
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let klein = Set<int>.empty();
                klein.add(1);
                let gross = Set<int>.empty();
                gross.add(1); gross.add(2);
                return if (isSubset(klein, gross) && !isSubset(gross, klein)) 1 else 0;
            }
            """));

    // --------------------------------------------------- additions to Map and List

    /// <summary>
    /// A `Map` can be walked.
    ///
    /// <para>Until this point that did NOT work: one could put something in and fetch it deliberately but
    /// not know what was in it. `keys` and `values` were missing, because they hung on the same compiler
    /// fault as `Set.iter()`.</para>
    ///
    /// <para>`entries(): Iterator&lt;(K, V)&gt;` is still missing — a tuple as the type argument of a
    /// generic interface is not lowerable.</para>
    /// </summary>
    [Fact]
    public void A_map_can_be_walked_by_keys_and_values() =>
        Assert.Equal(1, Run("""
            import std.collections { Map, keys, values };

            fn main(): int {
                let m = Map<string, int>.empty();
                m.set("a", 1); m.set("b", 2); m.set("c", 3);

                var anzahlKeys = 0;
                for (k in keys(m)) { anzahlKeys = anzahlKeys + 1; }

                var summe = 0;
                var anzahlValues = 0;
                for (v in values(m)) { summe = summe + v; anzahlValues = anzahlValues + 1; }

                return if (anzahlKeys == 3 && anzahlValues == 3 && summe == 6) 1 else 0;
            }
            """));

    [Fact]
    public void A_removed_entry_does_not_show_up_in_the_walk() =>
        // The tombstones must not appear while walking: 'states[i] == 1' is the condition, and '!= 0'
        // would be the obvious wrong one.
        Assert.Equal(1, Run("""
            import std.collections { Map, keys };

            fn main(): int {
                let m = Map<int, int>.empty();
                var i = 0;
                while (i < 10) { m.set(i, i); i = i + 1; }
                i = 0;
                while (i < 10) { m.remove(i); i = i + 2; }

                var gesehen = 0;
                for (k in keys(m)) { gesehen = gesehen + 1; }
                return if (gesehen == 5 && m.length() == 5) 1 else 0;
            }
            """));

    [Fact]
    public void A_list_knows_whether_it_is_empty() =>
        // Map and Set had this from their first day, List did not.
        Assert.Equal(1, Run("""
            import std.collections { List };

            fn main(): int {
                let xs = List<int>.empty();
                let vorher = xs.isEmpty();
                xs.push(1);
                return if (vorher && !xs.isEmpty()) 1 else 0;
            }
            """));

    [Fact]
    public void A_list_can_be_searched() =>
        // Free functions rather than methods: they need 'Equatable<T>', and that constraint on the CLASS
        // would make 'List<T>' unusable for every type without equality, including where nobody searches.
        Assert.Equal(1, Run("""
            import std.collections { List, listContains, listIndexOf };

            fn main(): int {
                let xs = List<int>.empty();
                xs.push(10); xs.push(20); xs.push(30);

                let hat = listContains(xs, 20);
                let fehlt = listContains(xs, 99);
                let pos = listIndexOf(xs, 30) ?? -1;
                let keine = listIndexOf(xs, 99) ?? -1;

                return if (hat && !fehlt && pos == 2 && keine == -1) 1 else 0;
            }
            """));

    /// <summary>
    /// The compiler fault that blocked `Set`, in twelve lines.
    ///
    /// <para>A class with a CONSTRAINED type parameter whose method returns `Iterator&lt;T&gt;` was not
    /// lowerable — but only once it was INSTANTIATED. `List&lt;T&gt;` has the same method and worked,
    /// because its `T` carries no constraint.</para>
    ///
    /// <para>`LowerWithOwner` was a partial copy of the type resolution and too short for the THIRD time:
    /// first the bare case, then `?T`, then `T[]` — and a generic type as a return type was still
    /// missing.</para>
    /// </summary>
    [Fact]
    public void A_constrained_generic_class_may_return_an_interface_instance() =>
        Assert.Equal(3, Run("""
            import std.iter { Iterator, ArrayIterator };
            import std.core { Hashable, Equatable };

            pub class Box<T :: [Hashable<T>, Equatable<T>]> {
                xs: T[],
                pub fn iter(): Iterator<T> {
                    return ArrayIterator<T> { source = this.xs, index = 0 };
                }
            }

            fn main(): int {
                let b = Box<int> { xs = [1, 2, 3] };
                var n = 0;
                for (v in b.iter()) { n = n + 1; }
                return n;
            }
            """));
}
