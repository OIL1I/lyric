using System.Runtime.CompilerServices;
using Lyric.Core;
using Lyric.Embedding;

namespace Lyric.Tests.Embedding;

/// <summary>
/// <c>Call&lt;T&gt;</c> und die Skalar-Marshalling-Schicht (M10/E2).
///
/// <para><b>Die Wandlungen stehen als Matrix da und nicht als Beispielliste.</b> Dieselbe
/// Entscheidung wie bei <c>AgreementTests</c>, und aus demselben Grund: dort wurden vier Abstuerze
/// allesamt <i>durch Zufall</i> gefunden, beim Bauen von etwas anderem, das zufaellig danebenlag.
/// Vier Zufaelle sind kein Zufall, sondern eine strukturelle Luecke — und eine Grenze, ueber die
/// vierzehn Typen laufen, ist genau so eine.</para>
/// </summary>
public class CallTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static ScriptInstance Instance(string source, Capability capabilities = Capability.None)
    {
        var vm = new LangVm(new HostOptions
        {
            StdlibRoot = Path.Combine(RepoRoot(), "stdlib"),
            Capabilities = capabilities,
        });
        return vm.Instantiate(vm.Compile(source, "mod"));
    }

    // ------------------------------------------------------------------ rufen

    [Fact]
    public void A_function_is_called_by_its_unqualified_name() =>
        Assert.Equal(30, Instance("pub fn add(a: int, b: int): int { return a + b; }")
            .Call<long>("add", 10, 20));

    /// <summary>
    /// Der Grund fuer die Qualifizierung: die Funktionstabelle eines Moduls traegt auch alles,
    /// was aus der Stdlib mitgezogen wurde. Ohne den Modul-Praefix faende <c>length</c> ebenso gut
    /// <c>std.string.length</c> — und zwar je nach Reihenfolge mal so und mal so.
    /// </summary>
    [Fact]
    public void A_stdlib_function_of_the_same_name_is_not_reachable()
    {
        var instance = Instance("""
            import std.string { length };
            pub fn length2(s: string): int { return length(s) * 2; }
            """);

        Assert.True(instance.Defines("length2"));
        Assert.False(instance.Defines("length"));

        var thrown = Assert.Throws<ScriptException>(() => instance.Call<long>("length", "abc"));
        Assert.Equal("LYR-EMB0006", thrown.Code);
    }

    [Fact]
    public void A_missing_function_says_so()
    {
        var thrown = Assert.Throws<ScriptException>(
            () => Instance("pub fn a(): int { return 1; }").Call<long>("b"));

        Assert.Equal("LYR-EMB0006", thrown.Code);
        Assert.Contains("has no function 'b'", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wrong_argument_count_says_so()
    {
        var thrown = Assert.Throws<ScriptException>(
            () => Instance("pub fn add(a: int, b: int): int { return a + b; }")
                .Call<long>("add", 1));

        Assert.Equal("LYR-EMB0007", thrown.Code);
        Assert.Contains("takes 2 argument(s), got 1", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_void_function_is_called_through_CallVoid()
    {
        var output = new StringWriter();
        var vm = new LangVm(new HostOptions
        {
            StdlibRoot = Path.Combine(RepoRoot(), "stdlib"),
            Output = output,
        });
        var instance = vm.Instantiate(vm.Compile("""
            import std.io.console { println };
            pub fn shout(what: string) { println(what); }
            """, "mod"));

        instance.CallVoid("shout", "hallo");

        Assert.Equal("hallo", output.ToString().ReplaceLineEndings("\n").Trim());
    }

    /// <summary>Ein <c>void</c> hat keinen Wert, und ein stiller <c>default(T)</c> verstellte dem
    /// Host den Blick darauf, dass er die Signatur falsch gelesen hat.</summary>
    [Fact]
    public void Asking_a_void_function_for_a_value_is_an_error()
    {
        var thrown = Assert.Throws<ScriptException>(
            () => Instance("pub fn nichts() { }").Call<long>("nichts"));

        Assert.Equal("LYR-EMB0002", thrown.Code);
    }

    // ------------------------------------------------------------------ Zustand

    /// <summary>
    /// <b>Der eigentliche Unterschied zwischen <c>Run</c> und <c>Call</c>.</b> Die Modul-Konstante
    /// wird einmal berechnet, und jeder Aufruf danach sieht denselben Stand. Ein <c>Call</c>, das
    /// jedes Mal neu laedt, waere ein Programmstart mit anderem Namen.
    /// </summary>
    [Fact]
    public void Module_state_survives_between_calls()
    {
        var instance = Instance("""
            class Zaehler { stand: int = 0 }

            let z = Zaehler { };

            pub fn hoch(): int {
                z.stand = z.stand + 1;
                return z.stand;
            }
            """);

        Assert.Equal(1, instance.Call<long>("hoch"));
        Assert.Equal(2, instance.Call<long>("hoch"));
        Assert.Equal(3, instance.Call<long>("hoch"));
    }

    /// <summary>
    /// Und zwei Instanzen desselben Moduls teilen nichts. Ohne diesen Test bliebe der obige gruen,
    /// wenn die Globals statisch waeren — und in einem Host mit zwei Mods waere genau das der
    /// Fehler, der erst auffaellt, wenn einer den Zaehler des anderen hochzaehlt.
    /// </summary>
    [Fact]
    public void Two_instances_of_the_same_module_do_not_share_state()
    {
        const string Source = """
            class Zaehler { stand: int = 0 }
            let z = Zaehler { };
            pub fn hoch(): int { z.stand = z.stand + 1; return z.stand; }
            """;

        var a = Instance(Source);
        var b = Instance(Source);

        Assert.Equal(1, a.Call<long>("hoch"));
        Assert.Equal(2, a.Call<long>("hoch"));
        Assert.Equal(1, b.Call<long>("hoch"));
    }

    /// <summary>Ein Modul ohne Einstiegspunkt ist hier der <b>Normalfall</b> — genau dafuer ist
    /// die Start-Sektion optional (siehe <c>examples/embedded.lyr</c>).</summary>
    [Fact]
    public void A_library_module_without_main_can_be_instantiated_and_called()
    {
        var instance = Instance("pub fn onStart(): int { return 7; }");

        Assert.False(instance.Module.HasEntryPoint);
        Assert.Equal(7, instance.Call<long>("onStart"));
    }

    /// <summary>Ein <c>panic</c> im gerufenen Code kommt als <see cref="ScriptPanicException"/>
    /// an — die Unterscheidung aus §17.1 haelt auch ueber die Host-Grenze.</summary>
    [Fact]
    public void A_panic_inside_a_called_function_reaches_the_host_as_a_panic()
    {
        var thrown = Assert.Throws<ScriptPanicException>(
            () => Instance("pub fn teile(n: int): int { return n / 0; }").Call<long>("teile", 1));

        Assert.StartsWith("LYR-VM", thrown.Code, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ Marshalling-Matrix

    /// <summary>
    /// Jeder Skalartyp einmal hin und zurueck, mit den Randwerten, an denen es schon einmal brach:
    /// <c>uint64.MaxValue</c> (der f-String-Bug aus M8b/S7), die Grenzen der schmalen Typen, und
    /// ein Codepoint jenseits von ASCII.
    /// </summary>
    [Theory]
    [InlineData("int8", "int8", (sbyte)-128)]
    [InlineData("int8", "int8", (sbyte)127)]
    [InlineData("int16", "int16", (short)-32768)]
    [InlineData("int32", "int32", int.MinValue)]
    [InlineData("int", "int", long.MaxValue)]
    [InlineData("int", "int", long.MinValue)]
    [InlineData("uint8", "uint8", (byte)255)]
    [InlineData("uint16", "uint16", (ushort)65535)]
    [InlineData("uint32", "uint32", uint.MaxValue)]
    [InlineData("bool", "bool", true)]
    [InlineData("bool", "bool", false)]
    [InlineData("char", "char", 'x')]
    [InlineData("char", "char", 'ß')]
    [InlineData("string", "string", "hallo")]
    [InlineData("string", "string", "")]
    [InlineData("float", "float", 3.5)]
    [InlineData("float", "float", -0.0)]
    public void A_value_survives_the_round_trip(string lyricType, string _, object value)
    {
        var instance = Instance($"pub fn durch(x: {lyricType}): {lyricType} {{ return x; }}");

        var back = value switch
        {
            sbyte v => (object)instance.Call<sbyte>("durch", v),
            short v => instance.Call<short>("durch", v),
            int v => instance.Call<int>("durch", v),
            long v => instance.Call<long>("durch", v),
            byte v => instance.Call<byte>("durch", v),
            ushort v => instance.Call<ushort>("durch", v),
            uint v => instance.Call<uint>("durch", v),
            bool v => instance.Call<bool>("durch", v),
            char v => instance.Call<char>("durch", v),
            string v => instance.Call<string>("durch", v),
            double v => instance.Call<double>("durch", v),
            _ => throw new InvalidOperationException("unhandled case in the test itself"),
        };

        Assert.Equal(value, back);
    }

    /// <summary>
    /// <c>uint</c> ist 64 Bit breit, und der groesste Wert passt in kein <c>long</c>. Genau dieser
    /// Wert hat in M8b/S7 einen f-String zu <c>-1</c> gemacht; ueber die Host-Grenze ist er
    /// derselbe Fallstrick, und er steht deshalb eigens hier.
    /// </summary>
    [Fact]
    public void The_largest_uint_survives_the_round_trip() =>
        Assert.Equal(ulong.MaxValue,
            Instance("pub fn durch(x: uint): uint { return x; }")
                .Call<ulong>("durch", ulong.MaxValue));

    // ------------------------------------------------------------------ was NICHT durchgeht

    /// <summary>
    /// <b>Verlustfrei oder gar nicht.</b> <c>300</c> als <c>int8</c> waere <c>44</c>, und das
    /// merkt niemand — bis drei Ebenen spaeter eine Zahl nicht stimmt. Innerhalb von Lyric wickelt
    /// Arithmetik definiert um (§6.6); das ist eine Rechnung des Programms und etwas anderes als
    /// eine stille Umdeutung beim Uebergeben.
    /// </summary>
    [Theory]
    [InlineData("int8", 300)]
    [InlineData("int8", -300)]
    [InlineData("uint8", -1)]
    [InlineData("int16", 70000)]
    [InlineData("uint32", -1)]
    public void A_value_that_does_not_fit_is_refused_instead_of_truncated(string type, int value)
    {
        var thrown = Assert.Throws<ScriptException>(
            () => Instance($"pub fn durch(x: {type}): {type} {{ return x; }}")
                .Call<long>("durch", value));

        Assert.Equal("LYR-EMB0004", thrown.Code);
    }

    /// <summary>Ein Bruch, der als Ganzzahl ankommen soll, verlöre seinen Nachkommateil.</summary>
    [Fact]
    public void A_fractional_value_is_refused_for_an_integer_parameter()
    {
        var thrown = Assert.Throws<ScriptException>(
            () => Instance("pub fn durch(x: int): int { return x; }").Call<long>("durch", 3.5));

        Assert.Equal("LYR-EMB0005", thrown.Code);
    }

    [Theory]
    [InlineData("int", "nicht eine Zahl")]
    [InlineData("string", 5)]
    [InlineData("bool", 1)]
    [InlineData("char", "x")]
    public void A_value_of_the_wrong_shape_is_refused(string type, object value)
    {
        var thrown = Assert.Throws<ScriptException>(
            () => Instance($"pub fn durch(x: {type}): {type} {{ return x; }}")
                .CallVoid("durch", value));

        Assert.Equal("LYR-EMB0005", thrown.Code);
    }

    /// <summary>
    /// Ganzzahl → <c>float</c> geht, und zwar bewusst: <c>3</c> fuer einen <c>float</c>-Parameter
    /// ist das, was ein Host schreibt, und es ist verlustfrei. Die Gegenrichtung ist es nicht und
    /// steht oben als Fehlerfall.
    /// </summary>
    [Fact]
    public void An_integer_widens_to_a_float_parameter() =>
        Assert.Equal(6.0, Instance("pub fn doppelt(x: float): float { return x * 2.0; }")
            .Call<double>("doppelt", 3));

    /// <summary>
    /// Was E2 <b>nicht</b> kann, sagt es auch: Arrays, Optionals und Objekte bleiben draussen.
    /// Ein Objekt haette ein Layout, und das nach aussen zu geben machte die Feldreihenfolge zum
    /// oeffentlichen Vertrag — dann koennte die Erreichbarkeitsanalyse nichts mehr streichen.
    /// </summary>
    [Theory]
    [InlineData("int[]")]
    [InlineData("?int")]
    public void A_type_that_cannot_cross_the_boundary_says_so(string type)
    {
        var thrown = Assert.Throws<ScriptException>(
            () => Instance($"pub fn durch(x: {type}): int {{ return 0; }}")
                .Call<long>("durch", 1));

        Assert.Equal("LYR-EMB0001", thrown.Code);
        Assert.Contains("scalars and strings only", thrown.Message, StringComparison.Ordinal);
    }
}
