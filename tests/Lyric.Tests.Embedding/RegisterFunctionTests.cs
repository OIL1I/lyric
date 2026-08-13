using System.Runtime.CompilerServices;
using Lyric.Core;
using Lyric.Embedding;

namespace Lyric.Tests.Embedding;

/// <summary>
/// <c>RegisterFunction</c>: the host makes a .NET function visible to its script.
///
/// <para>THE SEAM WAS ALREADY THERE. Natives are bound BY NAME AT LOAD TIME, and the signature comes
/// from a bodyless <c>pub fn</c> declaration. This produces that declaration rather than putting a
/// second route beside it; the only difference from the stdlib is that the file lives in memory.</para>
///
/// <para>THE SCRIPT HAS TO IMPORT <c>host</c>. The documentation used to show a script calling
/// <c>playSound("hit")</c> without an import; the language knows no implicit namespace, and introducing
/// one for exactly one kind of function would be a special route.</para>
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

    // ------------------------------------------------------------------ calling

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

    /// <summary>As a namespace too: <c>host.name(...)</c>, as for every other module.</summary>
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
    /// The host really sees something rather than only a return value. A side effect is the actual purpose
    /// of <c>RegisterFunction</c>: <c>playSound</c> returns nothing.
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

    /// <summary>The round trip: host to script to host to script to host.</summary>
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

    // ------------------------------------------------------------------ the generated declaration

    /// <summary>
    /// The generated source is the best answer to "what signature does my function have in Lyric?": it
    /// stands there as Lyric code and is exactly what the script compiles against.
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
    /// The same set of functions gives the same source regardless of the registration order, and therefore
    /// the same bytes. Reproducible output is required; a module whose content depended on a call order
    /// would be the one place where that no longer held.
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

        // And a script importing it anyway gets the ordinary diagnostic.
        var thrown = Assert.Throws<EmbeddingException>(
            () => vm.Compile("import host;\nfn main(): int { return 0; }", "mod"));

        Assert.Contains(thrown.Diagnostics, d => d.Code == "LYR-RES0003");
    }

    // ------------------------------------------------------------------ what gets rejected

    /// <summary>
    /// No silent overwriting: which of two registrations of the same name wins would otherwise be a
    /// question of order, and the host never notices.
    /// </summary>
    [Fact]
    public void Registering_the_same_name_twice_is_an_error()
    {
        var vm = Vm();
        vm.RegisterFunction("f", () => 1L);

        var thrown = Assert.Throws<ArgumentException>(() => vm.RegisterFunction("f", () => 2L));
        Assert.Contains("already registered", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>A type that cannot cross the boundary is rejected AT REGISTRATION rather than only when a
    /// script calls it.</summary>
    [Fact]
    public void A_parameter_type_that_cannot_cross_is_refused_at_registration()
    {
        var vm = Vm();

        var thrown = Assert.Throws<ArgumentException>(
            () => vm.RegisterFunction("nimmt", (int[] xs) => xs.Length));

        Assert.Contains("cannot cross the boundary", thrown.Message, StringComparison.Ordinal);

        // The message names the way out: 'RegisterType<T>'.
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

    // ------------------------------------------------------------------ faults of the host

    /// <summary>
    /// When the host throws in its own function, it gets its own type back rather than the shell of the
    /// reflection and rather than a Lyric panic.
    ///
    /// <para>The distinction is the point: "my script is broken", "my script has a bug" and "MY CODE
    /// failed" are three different messages to three different people.</para>
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
    /// Host functions belong to the VM rather than to the process. Without this test everything above
    /// would stay green if the registry were static, and one mod would see the other's functions.
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
    /// A host function costs NO capability: the host put it there itself. That is the model — the levels
    /// apply to the stdlib, and what goes beyond it the host decides case by case.
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
