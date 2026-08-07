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
/// `std.collections` — `Indexable&lt;T&gt;` und `List&lt;T&gt;` (M8/S5).
///
/// <para><b>`List&lt;T&gt;` ist in Lyric geschrieben, nicht nativ.</b> Die ROADMAP sah einen Hook
/// auf `System.Collections.Generic.List&lt;&gt;` vor; dagegen sprach, dass Natives monomorph
/// registriert werden — ein generischer bräuchte eine Marshalling-Schicht, und die gehört zu
/// M10. Dass eine Stdlib ihre eigenen Container ausdrücken kann, ist ohnehin die interessantere
/// Aussage.</para>
///
/// <para><b>Das Wachsen kommt ohne `newArray&lt;T&gt;(n)` aus</b> — das gibt es nicht, weil ein
/// `T[]` der Länge n auch n Werte vom Typ T bräuchte und Lyric kein `default(T)` hat. Stattdessen
/// verdoppelt `data = data + data`: was jenseits von `count` steht, wird nie gelesen. Der Test
/// über viele `push`-Aufrufe hält genau das fest.</para>
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

    private const string Head = "import std.collections { List, emptyList };\n";

    // ------------------------------------------------------------------ List<T>

    [Fact]
    public void A_list_starts_empty() =>
        Assert.Equal(0, Run(Head + "fn main(): int { return emptyList<int>().length(); }"));

    [Fact]
    public void Push_appends_and_counts() =>
        Assert.Equal(3, Run(Head + """
            fn main(): int {
                let xs = emptyList<int>();
                xs.push(1);
                xs.push(2);
                xs.push(3);
                return xs.length();
            }
            """));

    [Fact]
    public void Growth_preserves_every_element()
    {
        // DER Test des Wachstums. Bei 100 Elementen verdoppelt sich das Array siebenmal; würde
        // dabei ein Element verlorengehen oder überschrieben, stimmte die Summe nicht. Ein Test
        // mit drei Elementen bliebe grün, auch wenn das Verdoppeln kaputt wäre.
        Assert.Equal(4950, Run(Head + """
            fn main(): int {
                let xs = emptyList<int>();
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
                let xs = emptyList<int>();
                xs.push(1);
                xs.push(2);
                let last = xs.pop() ?? 0;
                return last * 10 + xs.length();
            }
            """));

    [Fact]
    public void Pop_on_an_empty_list_is_null_not_a_panic() =>
        // Eine leere Liste ist ein gewöhnlicher Zustand, kein Programmierfehler — anders als ein
        // Index daneben. Deshalb `?T` und kein `panic`.
        Assert.Equal(7, Run(Head + """
            fn main(): int {
                let xs = emptyList<int>();
                return xs.pop() ?? 7;
            }
            """));

    [Fact]
    public void A_list_of_strings_works_too() =>
        // Zweite Instanziierung: mit nur einer bliebe der Test auch grün, wenn die
        // Monomorphisierung den Elementtyp ignorierte.
        Assert.Equal(2, Run("""
            import std.collections { List, emptyList };
            fn main(): int {
                let xs = emptyList<string>();
                xs.push("a");
                xs.push("b");
                return xs.length();
            }
            """));

    // ------------------------------------------------------------------ Indexable<T>

    [Fact]
    public void A_list_can_be_read_with_brackets() =>
        // `[i]` auf einem Nicht-Array läuft über `Indexable<T>.get` — dieselbe Arbeitsteilung
        // wie `for-in` über `Iterator<T>`. Der Compiler kennt genau EINE eingebaute indizierbare
        // Form, das Array.
        Assert.Equal(20, Run(Head + """
            fn main(): int {
                let xs = emptyList<int>();
                xs.push(10);
                xs.push(20);
                return xs[1];
            }
            """));

    [Fact]
    public void A_list_can_be_written_with_brackets() =>
        // Das geht nur, weil `let` seit ADR-020 den Namen bindet und nicht den Inhalt. Unter der
        // alten Regel hätte `Indexable<T>` eine Sonderregel nachbilden müssen.
        Assert.Equal(5, Run(Head + """
            fn main(): int {
                let xs = emptyList<int>();
                xs.push(10);
                xs[0] = 5;
                return xs[0];
            }
            """));

    [Fact]
    public void An_array_still_uses_the_builtin_path() =>
        // Die Gegenprobe: der Indexable-Pfad darf die eingebaute Array-Indizierung nicht
        // übernommen haben. Ein Array implementiert `Indexable<T>` nicht — es ist die eingebaute
        // Form, und `ldelem` bleibt ein Array-Zugriff ohne Methodenaufruf.
        Assert.Equal(9, Run("fn main(): int { let xs = [1, 9, 3]; return xs[1]; }"));

    [Fact]
    public void A_user_type_can_implement_Indexable() =>
        // Nicht nur die Stdlib: das Interface steht jedem offen. Ohne diesen Test wäre nicht
        // festgehalten, dass `[i]` wirklich am Interface hängt und nicht an `List<T>`.
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
}
