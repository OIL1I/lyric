using System.Diagnostics;
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
    public static CompileResult Check(string path, CompilerOptions? options = null) =>
        Check(ScriptSource.FromDisk(path), options);

    /// <summary>Bis zur Mid-IR. Der Unterbau von <c>lyrc lower</c>.</summary>
    public static CompileResult Lower(string path, CompilerOptions? options = null) =>
        Lower(ScriptSource.FromDisk(path), options);

    /// <summary>Bis zu den <c>.lyrbc</c>-Bytes. Der Unterbau von <c>lyrc build</c> und von
    /// <c>lyric run</c> auf einer Quelldatei.</summary>
    public static CompileResult Compile(string path, CompilerOptions? options = null) =>
        Compile(ScriptSource.FromDisk(path), options);

    /// <inheritdoc cref="Check(string, CompilerOptions?)"/>
    public static CompileResult Check(ScriptSource source, CompilerOptions? options = null) =>
        Run(source, Stage.Check, options ?? new CompilerOptions());

    /// <inheritdoc cref="Lower(string, CompilerOptions?)"/>
    public static CompileResult Lower(ScriptSource source, CompilerOptions? options = null) =>
        Run(source, Stage.Lower, options ?? new CompilerOptions());

    /// <inheritdoc cref="Compile(string, CompilerOptions?)"/>
    public static CompileResult Compile(ScriptSource source, CompilerOptions? options = null) =>
        Run(source, Stage.Emit, options ?? new CompilerOptions());

    private enum Stage { Check, Lower, Emit }

    private static CompileResult Run(ScriptSource source, Stage stage, CompilerOptions options)
    {
        var report = options.Progress;
        var sources = new SourceManager();
        var diagnostics = new DiagnosticEngine(sources);

        report?.BeginPhase(Phase.Read, source.DisplayName);
        if (source.Open(sources, diagnostics) is not { } id)
        {
            report?.EndPhase();
            return new CompileResult(sources, diagnostics, null, null);
        }
        report?.EndPhase();

        report?.BeginPhase(Phase.Parse, source.DisplayName);
        var entry = new Parser(sources, id, diagnostics).ParseModule();
        report?.EndPhase();

        // Der Modul-Lader misst sich selbst. Compilation.Resolve laedt die importierten Module
        // intern, die Grenze Load/Resolve ist von aussen also nicht beobachtbar — statt dafuer
        // Lyric.Resolver aufzubohren, zieht die Huelle ihre eigene Dauer von der Resolve-Zeit ab.
        // Die Huelle ist zugleich der Ort, an dem Modulnamen fuer die Anzeige entstehen; ADR-012s
        // Source-Root wird spaeter durch dieselbe Naht laufen.
        var loaderTime = TimeSpan.Zero;
        var loaded = new List<string>();
        var stdlib = StdlibLoader.ForRoot(options.StdlibRoot ?? StdlibLoader.DefaultRoot(),
            sources, diagnostics);

        var compilation = new Compilation(sources, diagnostics)
        {
            // Source-first: die Stdlib ist gewoehnlicher Lyric-Quelltext und wird bei Bedarf geladen.
            ModuleLoader = modulePath =>
            {
                var name = string.Join('.', modulePath);
                report?.UpdateDetail(name);

                var started = Stopwatch.GetTimestamp();
                var result = stdlib(modulePath);
                loaderTime += Stopwatch.GetElapsedTime(started);

                if (result is not null) loaded.Add(name);
                return result;
            },
        };
        compilation.AddModule(entry);

        report?.BeginPhase(Phase.Load);
        var resolveStarted = Stopwatch.GetTimestamp();
        var binding = compilation.Resolve();
        var resolveTime = Stopwatch.GetElapsedTime(resolveStarted);
        report?.EndPhase(loaderTime);
        report?.ReportPhase(Phase.Resolve, ModuleCount(loaded), resolveTime - loaderTime);

        report?.BeginPhase(Phase.Check, ModuleCount(loaded));
        var types = Semantics.Analyze(compilation, binding, diagnostics);
        report?.EndPhase();

        // Auf fehlerhaftem AST waere jedes Lowering-Ergebnis Raten.
        if (stage == Stage.Check || diagnostics.HasErrors)
            return new CompileResult(sources, diagnostics, null, null);

        // Scope-Grenzen des Lowerings kommen als LYR-IR0001 in dieselbe Engine und werden mit
        // Datei/Zeile/Spalte gerendert wie jeder andere Fehler auch.
        //
        // verify:false und der separate VerifyOrThrow-Aufruf sind KEINE Verhaltensaenderung:
        // ModuleLowerer.VerifyByDefault entscheidet weiterhin, ob geprueft wird. Die Trennung
        // existiert, damit die beiden Zeiten getrennt messbar sind — STATUS.md behauptet seit M5,
        // der Verifier sei ~90 % der Lowering-Zeit, und dafuer gab es bisher keine Quelle.
        report?.BeginPhase(Phase.Lower);
        var ir = ModuleLowerer.Lower(compilation, binding, types, diagnostics, verify: false);
        if (ir is not null) report?.UpdateDetail(FunctionCount(ir));
        report?.EndPhase();
        if (ir is null || stage == Stage.Lower)
            return new CompileResult(sources, diagnostics, ir, null);

        if (ModuleLowerer.VerifyByDefault)
        {
            report?.BeginPhase(Phase.Verify, FunctionCount(ir));
            IrVerifier.VerifyOrThrow(ir);
            report?.EndPhase();
        }

        report?.BeginPhase(Phase.Emit, FunctionCount(ir));
        var bytes = BytecodeWriter.Write(ir);
        report?.EndPhase();

        return new CompileResult(sources, diagnostics, ir, bytes);
    }

    private static string ModuleCount(List<string> loaded) =>
        loaded.Count == 0 ? "1 module" : $"{loaded.Count + 1} modules";

    private static string FunctionCount(IrModule ir) =>
        ir.Functions.Count == 1 ? "1 function" : $"{ir.Functions.Count} functions";

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
/// Was ein Compiler-Lauf ausser der Quelldatei noch braucht.
///
/// <para>Ein Record statt einer wachsenden Parameterliste: <c>--stdlib</c> ist heute die einzige
/// Stellschraube, aber ADR-012s Source-Root fuer User-Module kommt hier dazu, sobald
/// Mehrdatei-Programme moeglich sind.</para>
/// </summary>
public sealed record CompilerOptions
{
    /// <summary>Wo die Stdlib liegt. <c>null</c> = <c>StdlibLoader.DefaultRoot()</c>, also
    /// <c>LYRIC_STDLIB</c> oder das Verzeichnis neben dem Binary.</summary>
    public string? StdlibRoot { get; init; }

    /// <summary>Wohin Phasenmeldungen gehen. <c>null</c> = niemand hoert zu; der Compiler laeuft
    /// dann ohne jede Ausgabe-Abhaengigkeit, was die Bibliothek fuer M10s Embedding-API
    /// benutzbar haelt.</summary>
    public TerminalOutput? Progress { get; init; }
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
