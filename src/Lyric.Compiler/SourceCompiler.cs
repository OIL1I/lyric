using Lyric.AST;
using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Ir;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Compiler;

/// <summary>
/// Die Pipeline Quelle → AST → Symbole → Typen → IR → <c>.lyrbc</c>-Bytes, als Bibliothek.
///
/// <para><b>Ein</b> Front-End fuer die gesamte Toolchain (ADR-017). Der Grund steht im
/// Projekt-Gedaechtnis: als <c>run</c>, <c>lower</c> und <c>check</c> in der alten CLI je eine
/// eigene Kopie des Vorspanns hatten, verdrahtete nur eine davon den
/// <see cref="Compilation.ModuleLoader"/> — <c>check</c> hielt jeden Stdlib-Import fuer opak und
/// pruefte die Aufrufe deshalb <i>stumm gar nicht</i>. Drei Binaries waeren drei neue Gelegenheiten
/// fuer genau diesen Fehler.</para>
///
/// <para>Diese Klasse <b>rendert nie selbst</b>. Sie sammelt in
/// <see cref="CompileResult.Diagnostics"/> und ueberlaesst die Ausgabe dem Aufrufer —
/// <see cref="DiagnosticEngine.RenderText"/> gibt jedes Mal den vollstaendigen Bestand aus, zwei
/// Aufrufe waeren also doppelte Meldungen.</para>
/// </summary>
public static class SourceCompiler
{
    /// <summary>Resolve + Sema, ohne Lowering. Der Unterbau von <c>lyrc check</c>.</summary>
    public static CompileResult Check(string path) => Run(path, Stage.Check);

    /// <summary>Bis zur Mid-IR. Der Unterbau von <c>lyrc lower</c>.</summary>
    public static CompileResult Lower(string path) => Run(path, Stage.Lower);

    /// <summary>Bis zu den <c>.lyrbc</c>-Bytes. Der Unterbau von <c>lyrc build</c> und von
    /// <c>lyric run</c> auf einer Quelldatei.</summary>
    public static CompileResult Compile(string path) => Run(path, Stage.Emit);

    private enum Stage { Check, Lower, Emit }

    private static CompileResult Run(string path, Stage stage)
    {
        var sources = new SourceManager();
        var diagnostics = new DiagnosticEngine(sources);

        FileId id;
        try
        {
            id = sources.AddFromDisk(path);
        }
        catch
        {
            diagnostics.Report(CliDiagnostics.FileUnreadable, Severity.Error, default,
                $"failed to read file: {path}");
            return new CompileResult(sources, diagnostics, null, null);
        }

        var compilation = new Compilation(sources, diagnostics)
        {
            // Source-first: die Stdlib ist gewoehnlicher Lyric-Quelltext und wird bei Bedarf geladen.
            ModuleLoader = StdlibLoader.ForRoot(StdlibLoader.DefaultRoot(), sources, diagnostics),
        };
        compilation.AddModule(new Parser(sources, id, diagnostics).ParseModule());

        var binding = compilation.Resolve();
        var types = Semantics.Analyze(compilation, binding, diagnostics);

        // Auf fehlerhaftem AST waere jedes Lowering-Ergebnis Raten.
        if (stage == Stage.Check || diagnostics.HasErrors)
            return new CompileResult(sources, diagnostics, null, null);

        // Scope-Grenzen des Lowerings kommen als LYR-IR0001 in dieselbe Engine und werden mit
        // Datei/Zeile/Spalte gerendert wie jeder andere Fehler auch.
        var ir = ModuleLowerer.Lower(compilation, binding, types, diagnostics);
        if (ir is null || stage == Stage.Lower)
            return new CompileResult(sources, diagnostics, ir, null);

        return new CompileResult(sources, diagnostics, ir, BytecodeWriter.Write(ir));
    }

    /// <summary>
    /// Liest eine Datei in einen frischen <see cref="SourceManager"/> — der gemeinsame Vorspann
    /// der Debug-Kommandos <c>tokenize</c> und <c>parse</c>, die noch vor dem Resolver abbiegen
    /// und deshalb nicht durch <see cref="Run"/> laufen.
    /// </summary>
    public static (SourceManager Sources, DiagnosticEngine Diagnostics, FileId Id) Read(string path)
    {
        var sources = new SourceManager();
        var diagnostics = new DiagnosticEngine(sources);
        try
        {
            return (sources, diagnostics, sources.AddFromDisk(path));
        }
        catch
        {
            diagnostics.Report(CliDiagnostics.FileUnreadable, Severity.Error, default,
                $"failed to read file: {path}");
            return (sources, diagnostics, FileId.None);
        }
    }
}

/// <summary>
/// Was ein Compiler-Lauf hinterlaesst. <see cref="Ir"/> und <see cref="Bytes"/> sind
/// <c>null</c>, wenn die angeforderte Stufe nicht erreicht wurde <i>oder</i> gar nicht angefordert
/// war — <see cref="Ok"/> ist die Frage, die man stattdessen stellt.
/// </summary>
public sealed record CompileResult(
    SourceManager Sources,
    DiagnosticEngine Diagnostics,
    IrModule? Ir,
    byte[]? Bytes)
{
    /// <summary>Kein Fehler gemeldet. Warnungen zaehlen nicht.</summary>
    public bool Ok => !Diagnostics.HasErrors;

    /// <summary>Rendert alle Diagnosen genau einmal und liefert, ob der Lauf sauber war.</summary>
    public bool Render(TextWriter error)
    {
        Diagnostics.RenderText(error);
        return Ok;
    }
}
