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
/// `Equatable&lt;T&gt;`, `Hashable&lt;T&gt;` und `Ordered&lt;T&gt;` in `std.core` — ADR-024.
///
/// <para><b>Warum generisch.</b> Die erste Fassung des ADR schrieb `fn equals(other: Equatable)`.
/// Das verlangt einen Interface-<i>Wert</i>, und ein Skalar kann keiner sein — ein Fat Pointer
/// braucht eine Referenz. `extend int :: [Equatable]` wäre unmöglich gewesen und damit
/// `Map&lt;int, V&gt;` auch. Mit `Equatable&lt;T&gt;` steht auf beiden Seiten der konkrete Typ.</para>
///
/// <para><b>Warum `Hashable` nicht von `Equatable` erbt.</b> Lyric kennt keine
/// Interface-Vererbung; die Grammatik sieht für `InterfaceDecl` keine Konformanzliste vor. Auch
/// das stand falsch im ADR und ist dort korrigiert. Wer beides braucht, verlangt beides.</para>
///
/// <para>Diese Datei prüft die Konformanz <b>über einen Constraint</b> und nicht durch direkten
/// Methodenaufruf. Der Unterschied ist der ganze Zweck: ein direkter Aufruf würde auch dann
/// funktionieren, wenn der Typ das Interface gar nicht erfüllte.</para>
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

    // ------------------------------------------------------- Konformanz der Builtins

    /// <summary>
    /// Jeder Builtin, der `Equatable` erfüllen soll, erfüllt es — geprüft über den Constraint.
    /// </summary>
    /// <remarks>Eine Theory und keine fünf Einzeltests: wer einen Typ ergänzt, ergänzt eine
    /// Zeile. Ohne die Tabelle wäre eine fehlende `extend`-Zeile erst dann aufgefallen, wenn
    /// jemand den Typ als Map-Schlüssel benutzt.</remarks>
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
        // Gleicher Anfang, verschiedene Länge — der Fall, den eine naive Schleife über den
        // kürzeren von beiden vergisst.
        Assert.Equal(-1, Run(Head + "fn main(): int { return cmp(\"ab\", \"abc\"); }"));

    // ------------------------------------------------------------------ Hash

    /// <summary>
    /// <b>Die Invariante jeder Hash-Tabelle</b>: gleiche Werte liefern denselben Hash.
    ///
    /// <para>Kein Compiler prüft sie, und ohne sie baut `Map` eine Tabelle, die einen Schlüssel
    /// nicht wiederfindet, den sie selbst abgelegt hat. Umgekehrt gilt sie nicht — zwei
    /// verschiedene Werte <i>dürfen</i> kollidieren.</para>
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
        // Ein Hash, der immer dasselbe liefert, erfüllt die Invariante oben und ist trotzdem
        // wertlos. Zwei Strings, die sich in einem Zeichen unterscheiden, sind der schärfste
        // billige Test dafür.
        Assert.Equal(1, Run(Head +
            "fn main(): int { return if (hash(\"hallo\") != hash(\"hallp\")) 1 else 0; }"));

    [Fact]
    public void The_string_hash_depends_on_order() =>
        // Eine Summe über die Zeichen wäre hier gleich. FNV-1a ist es nicht.
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
        // Der Fall, für den ADR-024 überhaupt da ist: ein Nutzertyp als Map-Schlüssel. Zwei
        // Constraints an einem Typ-Parameter, beide mit eigenem Typargument.
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

    // ------------------------------------------------- die Gegenprobe: greift der Constraint?

    /// <summary>
    /// Ein Typ ohne Konformanz wird <b>abgelehnt</b> — sonst wären alle Tests darüber wertlos.
    ///
    /// <para>28 grüne Tests beim ersten Lauf sind kein Beweis, dass geprüft wird. Erst dieser
    /// hier zeigt, dass der Constraint überhaupt eine Wirkung hat: ein Struct ohne
    /// <c>:: [Equatable&lt;…&gt;]</c> muss <c>LYR-SEM0028</c> auslösen.</para>
    ///
    /// <para><c>float</c> als <c>Hashable</c> ist der zweite Fall, und dort ist die Ablehnung
    /// <b>Absicht</b>: <c>NaN != NaN</c> hiesse, dass ein Schlüssel sich selbst nicht wiederfindet
    /// (siehe die Begründung in <c>std/core.lyr</c>).</para>
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

    /// <summary>Übersetzt und liefert die Diagnosen als Text — für die Fälle, die scheitern
    /// sollen. <see cref="Run"/> taugt dafür nicht: es behauptet, dass nichts scheitert.</summary>
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
        // uint fehlte in der ersten Fassung komplett — aufgefallen erst durch die Gegenprobe
        // oben, die es als 'erfüllt nicht' meldete. Ohne uint gäbe es kein Map<uint, V>.
        Assert.Equal(1, Run(Head + $$"""
            fn main(): int {
                let a: uint = {{a}};
                let b: uint = {{b}};
                let ok = eq(a, a) && hash(a) == hash(a) && cmp(a, b) <= 0;
                return if (ok) 1 else 0;
            }
            """));
}
