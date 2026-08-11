using System.Runtime.CompilerServices;
using Lyric.Core;
using Lyric.Embedding;

namespace Lyric.Tests.Embedding;

/// <summary>
/// Die Embedding-API (M10/E1): laden, ausfuehren, sandboxen.
///
/// <para><b>Der Capability-Teil ist der einzige sicherheitsrelevante</b>, und er steht deshalb
/// paarweise da — jede Operation einmal gewaehrt, einmal verweigert. Ein Test nur fuer den
/// erlaubten Fall bliebe gruen, wenn die Pruefung ausfiele, und das ist genau der Fehler, der
/// niemandem auffaellt: es funktioniert ja.</para>
/// </summary>
public class LangVmTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string Stdlib => Path.Combine(RepoRoot(), "stdlib");

    private static HostOptions With(Capability capabilities = Capability.None,
        TextWriter? output = null) =>
        new() { Capabilities = capabilities, StdlibRoot = Stdlib, Output = output };

    // ------------------------------------------------------------------ uebersetzen und laufen

    [Fact]
    public void A_script_compiled_from_memory_runs_and_returns_its_exit_code()
    {
        var vm = new LangVm(With());
        var module = vm.Compile("fn main(): int { return 42; }", "mod");

        Assert.True(module.HasEntryPoint);
        Assert.Equal(42, vm.Run(module));
    }

    /// <summary>
    /// Der Modulname des Hosts landet in den Diagnosen. Ohne ihn traegt eine Fehlermeldung aus
    /// einem Mod keinen Hinweis darauf, welcher Mod gemeint ist.
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
    /// Die Diagnosen kommen als <b>Daten</b> an, nicht als vorgerenderter Text: ein Host zeigt sie
    /// in seiner eigenen Oberflaeche und braucht Code und Position, keinen String zum Zurueckparsen.
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
    /// Ein Modul ohne Einstiegspunkt ist eine <b>Bibliothek</b>, kein Programm — gueltiger
    /// Bytecode, aber nichts auszufuehren. Genau der Normalfall fuer eingebetteten Code, und der
    /// Grund, warum die Start-Sektion seit jeher optional ist.
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
    /// <b>Die Voreinstellung ist die Zusage.</b> Doku §20.3 sagt „Embed-Mode default-sandbox";
    /// ohne diesen Test ist das ein Satz in einer Datei. <c>new LangVm()</c> ohne Argumente muss
    /// Dateizugriff ablehnen.
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

        // Der eigentliche Punkt: nicht DASS es scheitert, sondern dass vorher keine Zeile lief.
        // Eine Pruefung beim ersten Datei-Aufruf statt beim Laden waere ebenfalls "sicher" — und
        // haette das Skript bis dahin machen lassen, was es wollte.
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public void The_granted_capability_lets_the_same_script_through()
    {
        var vm = new LangVm(With(Capability.FileAccess));
        Assert.Equal(0, vm.Run(vm.Compile(ReadsAFile, "mod")));
    }

    /// <summary>
    /// Zwei VMs im selben Prozess teilen nichts.
    ///
    /// <para>Ohne diesen Test bliebe alles oben gruen, wenn Registry oder Maske statisch waeren —
    /// und in einem Host mit mehreren Mods waere genau das der Fehler, der erst auffaellt, wenn
    /// ein Mod die Rechte eines anderen erbt.</para>
    /// </summary>
    [Fact]
    public void Two_vms_in_one_process_do_not_share_their_capabilities()
    {
        var erlaubt = new LangVm(With(Capability.FileAccess));
        var verboten = new LangVm(With(Capability.None));

        Assert.Equal(0, erlaubt.Run(erlaubt.Compile(ReadsAFile, "a")));
        Assert.Throws<ScriptException>(() => verboten.Run(verboten.Compile(ReadsAFile, "b")));

        // Und danach immer noch — die Reihenfolge darf nichts aendern.
        Assert.Equal(0, erlaubt.Run(erlaubt.Compile(ReadsAFile, "c")));
    }

    /// <summary>Und die Ausgaben zweier VMs laufen nicht ineinander.</summary>
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

    // ------------------------------------------------------------------ von der Platte

    [Fact]
    public void RunScript_compiles_and_runs_a_file_in_one_step()
    {
        var vm = new LangVm(With());
        Assert.Equal(0, vm.RunScript(Path.Combine(RepoRoot(), "examples", "hello.lyr")));
    }
}
