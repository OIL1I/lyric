using System.Runtime.CompilerServices;
using System.Text;
using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Xunit;

namespace Lyric.Tests.Sema;

/// <summary>
/// Complete programs (e2e/&lt;name&gt;.lyr) through the whole pipeline: parse, resolve, typecheck, flow,
/// rules. Valid programs have to run through without errors, negative programs have to report the
/// expected code.
/// </summary>
public class E2ETests
{
    private static string ProgramDir([CallerFilePath] string thisFile = "")
        => Path.Combine(Path.GetDirectoryName(thisFile)!, "e2e");

    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static DiagnosticEngine Analyze(string name)
    {
        var source = File.ReadAllText(Path.Combine(ProgramDir(), name), Encoding.UTF8);
        var sm = new SourceManager();
        var id = sm.AddVirtual(name, source);
        var de = new DiagnosticEngine(sm);

        // With the stdlib on the module path: since an unfindable module is an error (LYR-RES0003), this
        // harness has to see the same world as 'lyric check'. Before that every stdlib import here was
        // silently opaque, and every use of the imported names went unchecked.
        var comp = new Compilation(sm, de)
        {
            ModuleLoader = StdlibLoader.ForRoot(Path.Combine(RepoRoot(), "stdlib"), sm, de),
        };
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        var binding = comp.Resolve();
        Semantics.Analyze(comp, binding, de);
        return de;
    }

    private static string Errors(DiagnosticEngine de) =>
        string.Join("; ", de.Diagnostics.Select(d => $"{d.Code}: {d.Message}"));

    [Theory]
    [InlineData("arithmetic.lyr")]
    [InlineData("nullable.lyr")]
    [InlineData("struct_methods.lyr")]
    [InlineData("class_player.lyr")]
    [InlineData("interface_impl.lyr")]
    [InlineData("enum_ops.lyr")]
    [InlineData("control_flow.lyr")]
    [InlineData("strings.lyr")]
    [InlineData("factory.lyr")]
    [InlineData("main_args.lyr")]
    [InlineData("bank.lyr")]
    [InlineData("fibonacci.lyr")]
    [InlineData("inventory.lyr")]
    [InlineData("shapes.lyr")]
    public void Valid_program_checks_clean(string name)
    {
        var de = Analyze(name);
        Assert.False(de.HasErrors, $"{name} should check clean but got: {Errors(de)}");
    }

    /// <summary>
    /// Programs importing a stdlib module that does not exist yet. That used to go unnoticed: an
    /// unfindable module counted as "external and opaque", and every use of the imported names was
    /// silently unchecked. Since <c>LYR-RES0003</c> it is an error, and these fixtures show it.
    ///
    /// <para>They stand here rather than in the list above, because they are VALID LYRIC — the library is
    /// missing, not the language. When the module appears they move back; that this test then fails is
    /// the reminder.</para>
    ///
    /// <para>That is exactly what happened with <c>shapes.lyr</c>: <c>std.math</c> exists, the program
    /// runs, and the line moved from this list into the one for clean programs. The test did its job — it
    /// reported that the expectation no longer held rather than silently claiming the module was
    /// missing.</para>
    /// </summary>
    [Theory]
    [InlineData("imports.lyr", "std.io")]
    public void Program_waiting_on_a_stdlib_module_reports_it(string name, string missing)
    {
        var de = Analyze(name);
        Assert.Contains(de.Diagnostics, d =>
            d.Code == "LYR-RES0003" && d.Message.Contains(missing, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("neg_no_return.lyr", "LYR-SEM0017")]
    [InlineData("neg_immutable.lyr", "LYR-SEM0019")]
    [InlineData("neg_bad_main.lyr", "LYR-SEM0021")]
    [InlineData("neg_mut_free.lyr", "LYR-SEM0023")]
    [InlineData("neg_type_mismatch.lyr", "LYR-SEM0001")]
    [InlineData("neg_unassigned.lyr", "LYR-SEM0018")]
    [InlineData("neg_missing_impl.lyr", "LYR-SEM0020")]
    [InlineData("neg_nonexhaustive.lyr", "LYR-SEM0050")]
    [InlineData("neg_unhandled_throw.lyr", "LYR-SEM0034")]
    [InlineData("neg_yield_outside.lyr", "LYR-SEM0038")]
    [InlineData("neg_orphan.lyr", "LYR-SEM0041")]
    [InlineData("neg_bad_impl.lyr", "LYR-SEM0042")]
    public void Negative_program_reports(string name, string code)
    {
        Assert.Contains(Analyze(name).Diagnostics, d => d.Code == code);
    }
}
