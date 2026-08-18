using System.Runtime.CompilerServices;
using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Embedding;

namespace Lyric.Tests.Embedding;

/// <summary>
/// The host surface of the attribute milestone: what a C# host learns from a script's rows, and
/// how it calls what it learned about.
///
/// <para>The three questions, in the order a host asks them. What is this module (before
/// instantiating)? Which functions did the script mark for me (instead of a name convention)?
/// What shape does a script-declared type have (fields with names — the component case)?</para>
/// </summary>
public class AttributeQueryTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static LangVm Vm() => new(new HostOptions
    {
        StdlibRoot = Path.Combine(RepoRoot(), "stdlib"),
        Capabilities = Capability.None,
    });

    private const string Game = """
        import std.core { OnModule, OnType, OnFunction };

        pub struct Plugin :: [OnModule] { name: string, api: int }
        pub struct Component :: [OnType] { }
        pub struct System :: [OnFunction] { order: int = 0 }

        @Component
        pub struct Health { value: int, max: int }

        @System { order = 10 }
        pub fn damageTick(dt: float): int { return 1; }

        @System
        pub fn lateTick(dt: float): int { return 2; }

        pub fn helper(): int { return 3; }
        """;

    [Fact]
    public void A_module_row_is_readable_before_instantiation()
    {
        var module = Vm().Compile("@Plugin { name = \"mymod\", api = 2 }\nmodule mymod;\n" + Game, "mymod");

        // No Instantiate anywhere: the decision whether to load foreign bytes hangs exactly here.
        var row = Assert.Single(module.Attributes.OnModule);
        Assert.Equal("Plugin", row.Attribute);
        Assert.Equal("mymod", row.Value("name")?.Text);
        Assert.Equal(2, row.Value("api")?.AsInt);
    }

    [Fact]
    public void Attributed_functions_are_enumerable_and_callable_through_the_use()
    {
        var vm = Vm();
        var instance = vm.Instantiate(vm.Compile(Game, "game"));

        var systems = instance.Attributes.OnFunctions("System")
            .OrderBy(use => use.Value("order")?.AsInt)
            .ToArray();

        Assert.Equal(2, systems.Length);
        // 'lateTick' wrote nothing; the row carries the default, so ordering by 'order' works
        // without a host-side fallback.
        Assert.Equal("game.lateTick", systems[0].TargetName);
        Assert.Equal(0, systems[0].Value("order")?.AsInt);
        Assert.Equal("game.damageTick", systems[1].TargetName);
        Assert.Equal(10, systems[1].Value("order")?.AsInt);

        // The use is the handle: no name resolution stands between enumeration and call.
        Assert.Equal(2L, instance.Call<long>(systems[0], 0.16));
        Assert.Equal(1L, instance.Call<long>(systems[1], 0.16));
    }

    [Fact]
    public void A_component_type_reports_its_shape()
    {
        var module = Vm().Compile(Game, "game");

        var health = Assert.Single(module.Attributes.OnTypes("Component"));
        Assert.Equal("Health", health.TargetName);

        var fields = module.Attributes.FieldsOf(health.Target);
        Assert.NotNull(fields);
        Assert.Equal(["value", "max"], fields.Select(f => f.Name));
        Assert.All(fields, f => Assert.Equal(TypeTag.I64, f.Type.Tag));
    }

    [Fact]
    public void An_attribute_nobody_used_answers_empty_not_throwing()
    {
        var module = Vm().Compile(Game, "game");

        Assert.Empty(module.Attributes.OnFunctions("Nope"));
        Assert.Empty(module.Attributes.OnTypes("Nope"));
        Assert.Empty(module.Attributes.OnModule);
    }

    [Fact]
    public void Calling_a_type_row_is_a_script_exception_not_a_crash()
    {
        var vm = Vm();
        var instance = vm.Instantiate(vm.Compile(Game, "game"));
        var component = instance.Attributes.OnTypes("Component").Single();

        var error = Assert.Throws<ScriptException>(() => instance.CallVoid(component));
        Assert.Contains("no function to call", error.Message);
    }

    [Fact]
    public void The_wrong_arity_through_a_use_is_the_same_error_as_by_name()
    {
        var vm = Vm();
        var instance = vm.Instantiate(vm.Compile(Game, "game"));
        var system = instance.Attributes.OnFunctions("System").First();

        var error = Assert.Throws<ScriptException>(() => instance.Call<long>(system));
        Assert.Equal("LYR-EMB0007", error.Code);
    }
}
