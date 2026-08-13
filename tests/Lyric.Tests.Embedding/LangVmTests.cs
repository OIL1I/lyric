using System.Runtime.CompilerServices;
using Lyric.Core;
using Lyric.Embedding;

namespace Lyric.Tests.Embedding;

/// <summary>
/// The embedding API: loading, executing, sandboxing.
///
/// <para>THE CAPABILITY PART IS THE ONLY SECURITY-RELEVANT ONE, and it therefore stands in pairs — every
/// operation once granted, once denied. A test for the allowed case alone would stay green if the check
/// failed, and that is exactly the fault nobody notices: it works, after all.</para>
/// </summary>
public class LangVmTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string Stdlib => Path.Combine(RepoRoot(), "stdlib");

    private static HostOptions With(Capability capabilities = Capability.None,
        TextWriter? output = null) =>
        new() { Capabilities = capabilities, StdlibRoot = Stdlib, Output = output };

    // ------------------------------------------------------------------ compiling and running

    [Fact]
    public void A_script_compiled_from_memory_runs_and_returns_its_exit_code()
    {
        var vm = new LangVm(With());
        var module = vm.Compile("fn main(): int { return 42; }", "mod");

        Assert.True(module.HasEntryPoint);
        Assert.Equal(42, vm.Run(module));
    }

    /// <summary>
    /// The host's module name lands in the diagnostics. Without it an error message from a mod carries no
    /// hint which mod is meant.
    /// </summary>
    [Fact]
    public void The_host_chosen_module_name_reaches_the_diagnostics()
    {
        var vm = new LangVm(With());
        var thrown = Assert.Throws<EmbeddingException>(
            () => vm.Compile("fn main(): int { return kaputt; }", "enemy-mod"));

        Assert.NotEmpty(thrown.Diagnostics);
        Assert.Contains(thrown.Diagnostics, d => d.Code == "LYR-SEM0002");
        Assert.Contains("enemy-mod", thrown.Message);
    }

    /// <summary>
    /// The diagnostics arrive as DATA rather than as pre-rendered text: a host shows them in its own
    /// interface and needs the code and the position rather than a string to parse back.
    /// </summary>
    [Fact]
    public void Diagnostics_arrive_as_data_with_code_and_position()
    {
        var vm = new LangVm(With());
        var thrown = Assert.Throws<EmbeddingException>(
            () => vm.Compile("fn main(): int { return \"text\"; }", "mod"));

        var diagnostic = thrown.Diagnostics.First(d => d.Severity == Severity.Error);
        Assert.StartsWith("LYR-", diagnostic.Code, StringComparison.Ordinal);
        Assert.True(diagnostic.Span.End > diagnostic.Span.Start);
    }

    /// <summary>
    /// A module without an entry point is a LIBRARY rather than a program: valid bytecode with nothing to
    /// execute. Exactly the normal case for embedded code, and the reason the Start section has always
    /// been optional.
    /// </summary>
    [Fact]
    public void A_module_without_an_entry_point_compiles_but_does_not_run()
    {
        var vm = new LangVm(With());
        var module = vm.Compile("pub fn onStart(): int { return 7; }", "lib");

        Assert.False(module.HasEntryPoint);
        Assert.NotEmpty(module.Bytes);
        Assert.Throws<ScriptException>(() => vm.Run(module));
    }

    [Fact]
    public void A_script_writes_to_the_writer_the_host_supplied()
    {
        var output = new StringWriter();
        var vm = new LangVm(With(output: output));
        vm.Run(vm.Compile("""
            import std.io.console { println };
            fn main(): int { println("aus dem Skript"); return 0; }
            """, "mod"));

        Assert.Equal("aus dem Skript", output.ToString().ReplaceLineEndings("\n").Trim());
    }

    // ------------------------------------------------------------------ Capabilities

    private const string ReadsAFile = """
        import std.io.file { exists };
        fn main(): int { return if (exists("nope.txt")) 1 else 0; }
        """;

    /// <summary>
    /// THE DEFAULT IS THE PROMISE. The documentation says embed mode is sandboxed by default;
    /// without this test that is a sentence in a file. A <c>new LangVm()</c> without arguments has to
    /// reject file access.
    /// </summary>
    [Fact]
    public void The_default_vm_is_a_sandbox()
    {
        var vm = new LangVm(new HostOptions { StdlibRoot = Stdlib });

        Assert.Equal(Capability.None, vm.Capabilities);

        var denied = Assert.Throws<ScriptException>(() => vm.Run(vm.Compile(ReadsAFile, "mod")));
        Assert.Equal("LYR-CAP0001", denied.Code);
    }

    [Fact]
    public void A_denied_capability_stops_the_script_before_it_runs()
    {
        var output = new StringWriter();
        var vm = new LangVm(With(Capability.None, output));

        Assert.Throws<ScriptException>(() => vm.Run(vm.Compile("""
            import std.io.file { exists };
            import std.io.console { println };
            fn main(): int {
                println("das darf nicht erscheinen");
                return if (exists("nope.txt")) 1 else 0;
            }
            """, "mod")));

        // The actual point: not THAT it fails but that no line ran beforehand. A check at the first file
        // call rather than at load time would also be "safe" — and would have let the script do whatever
        // it wanted until then.
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public void The_granted_capability_lets_the_same_script_through()
    {
        var vm = new LangVm(With(Capability.FileAccess));
        Assert.Equal(0, vm.Run(vm.Compile(ReadsAFile, "mod")));
    }

    /// <summary>
    /// Two VMs in the same process share nothing.
    ///
    /// <para>Without this test everything above would stay green if the registry or the mask were static,
    /// and in a host with several mods that would be the fault that shows only when one mod inherits
    /// another's rights.</para>
    /// </summary>
    [Fact]
    public void Two_vms_in_one_process_do_not_share_their_capabilities()
    {
        var erlaubt = new LangVm(With(Capability.FileAccess));
        var verboten = new LangVm(With(Capability.None));

        Assert.Equal(0, erlaubt.Run(erlaubt.Compile(ReadsAFile, "a")));
        Assert.Throws<ScriptException>(() => verboten.Run(verboten.Compile(ReadsAFile, "b")));

        // And still afterwards: the order must change nothing.
        Assert.Equal(0, erlaubt.Run(erlaubt.Compile(ReadsAFile, "c")));
    }

    /// <summary>And the outputs of two VMs do not run into each other.</summary>
    [Fact]
    public void Two_vms_in_one_process_do_not_share_their_output()
    {
        var a = new StringWriter();
        var b = new StringWriter();
        var first = new LangVm(With(output: a));
        var second = new LangVm(With(output: b));

        const string Source = """
            import std.io.console { println };
            fn main(): int { println("X"); return 0; }
            """;

        first.Run(first.Compile(Source, "a"));

        Assert.Equal("X", a.ToString().ReplaceLineEndings("\n").Trim());
        Assert.Equal("", b.ToString());
        _ = second;
    }

    // ------------------------------------------------------------------ from disk

    [Fact]
    public void RunScript_compiles_and_runs_a_file_in_one_step()
    {
        var vm = new LangVm(With());
        Assert.Equal(0, vm.RunScript(Path.Combine(RepoRoot(), "examples", "hello.lyr")));
    }
}
