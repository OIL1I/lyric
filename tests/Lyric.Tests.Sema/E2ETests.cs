using System.Runtime.CompilerServices;
using System.Text;
using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Xunit;

namespace Lyric.Tests.Sema;

/// <summary>
/// M3-Exit: vollständige Programme (e2e/&lt;name&gt;.lyr) durch die ganze Pipeline —
/// parse → resolve → typecheck → flow → rules. Valide Programme müssen fehlerfrei
/// durchlaufen, Negativ-Programme den erwarteten Code melden.
/// </summary>
public class E2ETests
{
    private static string ProgramDir([CallerFilePath] string thisFile = "")
        => Path.Combine(Path.GetDirectoryName(thisFile)!, "e2e");

    private static DiagnosticEngine Analyze(string name)
    {
        var source = File.ReadAllText(Path.Combine(ProgramDir(), name), Encoding.UTF8);
        var sm = new SourceManager();
        var id = sm.AddVirtual(name, source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
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
    [InlineData("imports.lyr")]
    [InlineData("main_args.lyr")]
    [InlineData("shapes.lyr")]
    [InlineData("bank.lyr")]
    [InlineData("fibonacci.lyr")]
    public void Valid_program_checks_clean(string name)
    {
        var de = Analyze(name);
        Assert.False(de.HasErrors, $"{name} should check clean but got: {Errors(de)}");
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
    public void Negative_program_reports(string name, string code)
    {
        Assert.Contains(Analyze(name).Diagnostics, d => d.Code == code);
    }
}
