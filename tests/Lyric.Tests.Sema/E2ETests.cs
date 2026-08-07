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

    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static DiagnosticEngine Analyze(string name)
    {
        var source = File.ReadAllText(Path.Combine(ProgramDir(), name), Encoding.UTF8);
        var sm = new SourceManager();
        var id = sm.AddVirtual(name, source);
        var de = new DiagnosticEngine(sm);

        // Mit Stdlib auf dem Modulpfad: seit ein unauffindbares Modul ein Fehler ist
        // (LYR-RES0003), muss dieser Harness dieselbe Welt sehen wie 'lyric check'. Vorher war
        // jeder Stdlib-Import hier stillschweigend opak — und damit war jede Verwendung der
        // importierten Namen ungeprueft, was diese Tests nicht bemerken konnten.
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
    /// Programme, die ein Stdlib-Modul importieren, das es noch nicht gibt. Bis M6-2 fiel das
    /// nicht auf — ein unauffindbares Modul galt als „extern/opak", und damit war stillschweigend
    /// auch jede Verwendung der importierten Namen ungeprüft. Seit <c>LYR-RES0003</c> ist es ein
    /// Fehler, und diese beiden Fixtures zeigen ihn.
    ///
    /// <para>Sie stehen hier statt in der Liste oben, weil sie <b>gültiges Lyric</b> sind — es
    /// fehlt die Bibliothek, nicht die Sprache. Wenn das Modul entsteht, wandern sie zurück; dass
    /// dieser Test dann fehlschlägt, ist die Erinnerung daran.</para>
    ///
    /// <para><b>Genau das ist mit <c>shapes.lyr</c> passiert</b> (M8/S7, 2026-08-07): <c>std.math</c>
    /// gibt es, das Programm läuft, und die Zeile ist aus dieser Liste in die der sauberen
    /// Programme gewandert. Der Test hat seine Aufgabe erfüllt — er hat gemeldet, dass die
    /// Erwartung nicht mehr stimmt, statt still weiter zu behaupten, das Modul fehle.</para>
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
