using System.Runtime.CompilerServices;
using Lyric.Core;
using Lyric.Embedding;

namespace Lyric.Tests.Embedding;

/// <summary>
/// <c>RegisterFunction</c> (M10/E3): der Host macht eine .NET-Funktion fuer sein Skript sichtbar.
///
/// <para><b>Der Seam ist seit M6 da.</b> Natives werden <b>beim Laden ueber den Namen</b>
/// gebunden, und die Signatur kommt aus einer bodylosen <c>pub fn</c>-Deklaration. E3 erzeugt
/// diese Deklaration, statt einen zweiten Weg danebenzustellen — der Unterschied zur Stdlib ist
/// allein, dass die Datei im Speicher liegt.</para>
///
/// <para><b>Das Skript muss <c>host</c> importieren.</b> <c>Doku.md</c> §21 zeigte bis heute ein
/// Skript, das <c>playSound("hit")</c> ohne Import ruft; §2.2 kennt keinen impliziten Namensraum,
/// und einen fuer genau eine Sorte Funktion einzufuehren waere ein Sonderweg.</para>
/// </summary>
public class RegisterFunctionTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static LangVm Vm(TextWriter? output = null) => new(new HostOptions
    {
        StdlibRoot = Path.Combine(RepoRoot(), "stdlib"),
        Output = output,
    });

    // ------------------------------------------------------------------ rufen

    [Fact]
    public void A_script_calls_a_registered_host_function()
    {
        var vm = Vm();
        vm.RegisterFunction("verdoppeln", (long n) => n * 2);

        var instance = vm.Instantiate(vm.Compile("""
            import host { verdoppeln };
            pub fn los(n: int): int { return verdoppeln(n) + 1; }
            """, "mod"));

        Assert.Equal(21, instance.Call<long>("los", 10));
    }

    /// <summary>Auch als Namensraum — <c>host.name(...)</c>, wie bei jedem anderen Modul.</summary>
    [Fact]
    public void The_host_module_works_as_a_namespace_import()
    {
        var vm = Vm();
        vm.RegisterFunction("grussformel", (string wen) => $"hallo, {wen}");

        var instance = vm.Instantiate(vm.Compile("""
            import host;
            pub fn los(): string { return host.grussformel("Ada"); }
            """, "mod"));

        Assert.Equal("hallo, Ada", instance.Call<string>("los"));
    }

    /// <summary>
    /// Der Host sieht wirklich etwas — nicht nur einen Rueckgabewert. Ein Seiteneffekt ist der
    /// eigentliche Zweck von <c>RegisterFunction</c>: <c>playSound</c> gibt nichts zurueck.
    /// </summary>
    [Fact]
    public void A_void_host_function_is_called_for_its_effect()
    {
        var gesehen = new List<string>();
        var vm = Vm();
        vm.RegisterFunction("merken", (string was) => gesehen.Add(was));

        var instance = vm.Instantiate(vm.Compile("""
            import host { merken };
            pub fn los() { merken("eins"); merken("zwei"); }
            """, "mod"));

        instance.CallVoid("los");

        Assert.Equal(["eins", "zwei"], gesehen);
    }

    /// <summary>Der Rundweg: Host → Skript → Host → Skript → Host.</summary>
    [Fact]
    public void A_value_survives_host_to_script_to_host()
    {
        var vm = Vm();
        vm.RegisterFunction("quadrat", (double x) => x * x);

        var instance = vm.Instantiate(vm.Compile("""
            import host { quadrat };
            pub fn flaeche(r: float): float { return quadrat(r) * 3.0; }
            """, "mod"));

        Assert.Equal(12.0, instance.Call<double>("flaeche", 2.0));
    }

    // ------------------------------------------------------------------ die erzeugte Deklaration

    /// <summary>
    /// Der erzeugte Quelltext ist die beste Antwort auf „welche Signatur hat meine Funktion in
    /// Lyric?" — er steht als Lyric-Code da und ist genau das, wogegen das Skript uebersetzt.
    /// </summary>
    [Fact]
    public void The_generated_declaration_names_the_lyric_types()
    {
        var vm = Vm();
        vm.RegisterFunction("mischen", (int a, string b, bool c) => (char)'x');

        var source = vm.HostModuleSource;

        Assert.NotNull(source);
        Assert.Contains("module host;", source, StringComparison.Ordinal);
        Assert.Contains("pub fn mischen(a: int32, b: string, c: bool): char;", source,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Derselbe Satz Funktionen ergibt denselben Quelltext, unabhaengig von der
    /// Registrierungsreihenfolge — und damit dieselben Bytes. ADR-013 verlangt reproduzierbare
    /// Ausgabe; ein Modul, dessen Inhalt an einer Aufrufreihenfolge haengt, waere die eine
    /// Stelle, an der das nicht mehr gilt.
    /// </summary>
    [Fact]
    public void The_generated_module_does_not_depend_on_registration_order()
    {
        var a = Vm();
        a.RegisterFunction("eins", () => 1L);
        a.RegisterFunction("zwei", () => 2L);

        var b = Vm();
        b.RegisterFunction("zwei", () => 2L);
        b.RegisterFunction("eins", () => 1L);

        Assert.Equal(a.HostModuleSource, b.HostModuleSource);
    }

    [Fact]
    public void Without_a_registration_there_is_no_host_module()
    {
        var vm = Vm();
        Assert.Null(vm.HostModuleSource);

        // Und ein Skript, das es trotzdem importiert, bekommt die gewoehnliche Diagnose.
        var thrown = Assert.Throws<EmbeddingException>(
            () => vm.Compile("import host;\nfn main(): int { return 0; }", "mod"));

        Assert.Contains(thrown.Diagnostics, d => d.Code == "LYR-RES0003");
    }

    // ------------------------------------------------------------------ was abgelehnt wird

    /// <summary>
    /// Kein stilles Ueberschreiben: welche von zwei Registrierungen desselben Namens gewinnt,
    /// waere sonst eine Frage der Reihenfolge — und der Host merkt es nie.
    /// </summary>
    [Fact]
    public void Registering_the_same_name_twice_is_an_error()
    {
        var vm = Vm();
        vm.RegisterFunction("f", () => 1L);

        var thrown = Assert.Throws<ArgumentException>(() => vm.RegisterFunction("f", () => 2L));
        Assert.Contains("already registered", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>Ein Typ, der die Grenze nicht ueberqueren kann, wird <b>bei der Registrierung</b>
    /// abgelehnt — nicht erst, wenn ein Skript ihn ruft.</summary>
    [Fact]
    public void A_parameter_type_that_cannot_cross_is_refused_at_registration()
    {
        var vm = Vm();

        var thrown = Assert.Throws<ArgumentException>(
            () => vm.RegisterFunction("nimmt", (int[] xs) => xs.Length));

        Assert.Contains("cannot cross the boundary", thrown.Message, StringComparison.Ordinal);

        // Die Meldung nennt den Ausweg. Sie sagte bis E4 "Host objects come in E4" — seit es sie
        // gibt, nennt sie 'RegisterType<T>', und das ist die nuetzlichere Auskunft.
        Assert.Contains("RegisterType", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_return_type_that_cannot_cross_is_refused_at_registration()
    {
        var vm = Vm();

        var thrown = Assert.Throws<ArgumentException>(
            () => vm.RegisterFunction("liefert", () => new object()));

        Assert.Contains("the return value", thrown.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ Fehler des Hosts

    /// <summary>
    /// Wirft der Host in seiner eigenen Funktion, bekommt er seinen Typ zurueck — nicht die Huelle
    /// der Reflection und nicht einen Lyric-Panic.
    ///
    /// <para>Die Unterscheidung ist der Punkt: „mein Skript ist kaputt", „mein Skript hat einen
    /// Bug" und „<b>mein Code</b> ist gescheitert" sind drei verschiedene Nachrichten an drei
    /// verschiedene Leute.</para>
    /// </summary>
    [Fact]
    public void An_exception_from_the_host_function_keeps_its_own_type()
    {
        var vm = Vm();
        vm.RegisterFunction("kaputt", (Func<long, long>)(_ =>
            throw new InvalidOperationException("die Engine schlief")));

        var instance = vm.Instantiate(vm.Compile("""
            import host { kaputt };
            pub fn los(): int { return kaputt(1); }
            """, "mod"));

        var thrown = Assert.Throws<HostFunctionException>(() => instance.Call<long>("los"));

        Assert.Equal("kaputt", thrown.Function);
        Assert.IsType<InvalidOperationException>(thrown.InnerException);
        Assert.Contains("die Engine schlief", thrown.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ Isolation

    /// <summary>
    /// Host-Funktionen gehoeren der VM, nicht dem Prozess. Ohne diesen Test bliebe alles oben
    /// gruen, wenn die Registry statisch waere — und ein Mod saehe die Funktionen des anderen.
    /// </summary>
    [Fact]
    public void Host_functions_do_not_leak_into_another_vm()
    {
        var mit = Vm();
        mit.RegisterFunction("geheim", () => 42L);

        var ohne = Vm();

        Assert.NotNull(mit.HostModuleSource);
        Assert.Null(ohne.HostModuleSource);

        const string Source = """
            import host { geheim };
            pub fn los(): int { return geheim(); }
            """;

        Assert.Equal(42, mit.Instantiate(mit.Compile(Source, "a")).Call<long>("los"));
        Assert.Throws<EmbeddingException>(() => ohne.Compile(Source, "b"));
    }

    /// <summary>
    /// Eine Host-Funktion kostet <b>keine</b> Capability — der Host hat sie ja selbst
    /// hingestellt. Genau das ist das Modell aus ADR-007: die Stufen gelten der Stdlib, und was
    /// darueber hinaus geht, entscheidet der Host einzeln.
    /// </summary>
    [Fact]
    public void A_host_function_needs_no_capability()
    {
        var vm = Vm();
        vm.RegisterFunction("darfIch", () => true);

        Assert.Equal(Capability.None, vm.Capabilities);
        Assert.True(vm.Instantiate(vm.Compile("""
            import host { darfIch };
            pub fn los(): bool { return darfIch(); }
            """, "mod")).Call<bool>("los"));
    }
}
