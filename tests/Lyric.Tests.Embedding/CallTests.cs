using System.Runtime.CompilerServices;
using Lyric.Core;
using Lyric.Embedding;

namespace Lyric.Tests.Embedding;

/// <summary>
/// <c>Call&lt;T&gt;</c> and the scalar marshalling layer.
///
/// <para>THE CONVERSIONS STAND AS A MATRIX rather than as a list of examples. The same decision as in
/// <c>AgreementTests</c> and for the same reason: four crashes there were all found BY ACCIDENT, while
/// building something else that happened to lie next to them. Four accidents are no accident but a
/// structural gap — and a boundary fourteen types cross is exactly such a place.</para>
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

    // ------------------------------------------------------------------ calling

    [Fact]
    public void A_function_is_called_by_its_unqualified_name() =>
        Assert.Equal(30, Instance("pub fn add(a: int, b: int): int { return a + b; }")
            .Call<long>("add", 10, 20));

    /// <summary>
    /// The reason for the qualification: a module's function table also carries everything dragged in
    /// from the stdlib. Without the module prefix, <c>length</c> would find <c>std.string.length</c> just
    /// as well, and depending on the order sometimes one and sometimes the other.
    /// </summary>
    [Fact]
    public void A_stdlib_function_of_the_same_name_is_not_reachable()
    {
        var instance = Instance("""
            import std.string as strings;
            pub fn length2(s: string): int { return s.length() * 2; }
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

    /// <summary>A <c>void</c> has no value, and a silent <c>default(T)</c> would hide from the host that
    /// it read the signature wrongly.</summary>
    [Fact]
    public void Asking_a_void_function_for_a_value_is_an_error()
    {
        var thrown = Assert.Throws<ScriptException>(
            () => Instance("pub fn nichts() { }").Call<long>("nichts"));

        Assert.Equal("LYR-EMB0002", thrown.Code);
    }

    // ------------------------------------------------------------------ state

    /// <summary>
    /// The actual difference between <c>Run</c> and <c>Call</c>. The module constant is computed once and
    /// every call afterwards sees the same state. A <c>Call</c> that reloaded every time would be a
    /// program start under a different name.
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
    /// And two instances of the same module share nothing. Without this test the one above would stay
    /// green if the globals were static — and in a host with two mods that would be the fault that shows
    /// only when one increments the other's counter.
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

    /// <summary>A module without an entry point is the NORMAL CASE here, which is exactly why the Start
    /// section is optional (see <c>examples/embedded.lyr</c>).</summary>
    [Fact]
    public void A_library_module_without_main_can_be_instantiated_and_called()
    {
        var instance = Instance("pub fn onStart(): int { return 7; }");

        Assert.False(instance.Module.HasEntryPoint);
        Assert.Equal(7, instance.Call<long>("onStart"));
    }

    /// <summary>A <c>panic</c> in the called code arrives as a <see cref="ScriptPanicException"/>: the
    /// distinction holds across the host boundary too.</summary>
    [Fact]
    public void A_panic_inside_a_called_function_reaches_the_host_as_a_panic()
    {
        var thrown = Assert.Throws<ScriptPanicException>(
            () => Instance("pub fn teile(n: int): int { return n / 0; }").Call<long>("teile", 1));

        Assert.StartsWith("LYR-VM", thrown.Code, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the marshalling matrix

    /// <summary>
    /// Every scalar type once there and back, with the edge values it broke at before:
    /// <c>uint64.MaxValue</c>, the bounds of the narrow types, and a code point beyond ASCII.
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
    /// <c>uint</c> is 64 bits wide and its largest value fits into no <c>long</c>. Exactly that value once
    /// turned an f-string into <c>-1</c>; across the host boundary it is the same trap and therefore
    /// stands here separately.
    /// </summary>
    [Fact]
    public void The_largest_uint_survives_the_round_trip() =>
        Assert.Equal(ulong.MaxValue,
            Instance("pub fn durch(x: uint): uint { return x; }")
                .Call<ulong>("durch", ulong.MaxValue));

    // ------------------------------------------------------------------ what does NOT pass

    /// <summary>
    /// LOSSLESS OR NOT AT ALL. <c>300</c> as an <c>int8</c> would be <c>44</c>, and nobody notices that
    /// until a number is wrong three levels later. Inside Lyric arithmetic wraps in a defined way; that is
    /// a computation of the program and something other than a silent reinterpretation while passing.
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

    /// <summary>A fraction meant to arrive as an integer would lose its fractional part.</summary>
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
    /// An integer to a <c>float</c> works, deliberately: <c>3</c> for a <c>float</c> parameter is what a
    /// host writes, and it is lossless. The other direction is not and stands above as an error case.
    /// </summary>
    [Fact]
    public void An_integer_widens_to_a_float_parameter() =>
        Assert.Equal(6.0, Instance("pub fn doppelt(x: float): float { return x * 2.0; }")
            .Call<double>("doppelt", 3));

    /// <summary>
    /// What this layer cannot do it says as well: arrays, optionals and objects stay outside. An object
    /// would have a layout, and handing that outwards would make the field order a public contract — the
    /// reachability analysis could then delete nothing.
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
