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
/// The pipeline source to AST to symbols to types to IR to <c>.lyrbc</c> bytes, as a library.
///
/// <para>One front end for the whole toolchain. With a copy of the preamble per command, only one
/// of them wired up the <see cref="Compilation.ModuleLoader"/>, and <c>check</c> silently treated
/// every standard library import as opaque.</para>
///
/// <para>This class NEVER RENDERS ITSELF. It collects into
/// <see cref="CompileResult.Diagnostics"/> and leaves the output to the caller;
/// <see cref="DiagnosticEngine.RenderText"/> renders the whole collection each time, so two calls
/// would be duplicate messages.</para>
/// </summary>
public static class SourceCompiler
{
    /// <summary>
    /// Everything a build does except writing the bytes. The basis of <c>lyrc check</c>.
    ///
    /// <para>Lowering is part of it: a limit the backend cannot express is reported as
    /// <c>LYR-IR0001</c>, and stopping after the sema would let <c>check</c> answer 'ok' for a
    /// program that <c>build</c> rejects.</para>
    /// </summary>
    public static CompileResult Check(string path, CompilerOptions? options = null) =>
        Check(ScriptSource.FromDisk(path), options);

    /// <summary>Up to the mid-level IR. The basis of <c>lyrc lower</c>.</summary>
    public static CompileResult Lower(string path, CompilerOptions? options = null) =>
        Lower(ScriptSource.FromDisk(path), options);

    /// <summary>Up to the <c>.lyrbc</c> bytes. The basis of <c>lyrc build</c> and of
    /// <c>lyric run</c> on a source file.</summary>
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

        // The module loader times itself: Compilation.Resolve loads the imported modules
        // internally, so the load/resolve boundary is not observable from outside. The wrapper
        // subtracts its own duration from the resolve time.
        var loaderTime = TimeSpan.Zero;
        var loaded = new List<string>();
        var stdlib = StdlibLoader.ForRoot(options.StdlibRoot ?? StdlibLoader.DefaultRoot(),
            sources, diagnostics);

        // Supplied modules first, then disk. Chained rather than a second loader mechanism:
        // 'Compilation' knows exactly one delegate.
        var provided = options.NativeModules;
        if (provided is { Count: > 0 })
        {
            var fromDisk = stdlib;
            stdlib = modulePath =>
            {
                var name = string.Join('.', modulePath);
                if (!provided.TryGetValue(name, out var text)) return fromDisk(modulePath);

                var id = sources.AddVirtual(name, text);
                return (new Parser(sources, id, diagnostics).ParseModule(), true);
            };
        }

        var compilation = new Compilation(sources, diagnostics)
        {
            // The standard library is ordinary Lyric source and is loaded on demand.
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
        compilation.AddModule(entry, source.ModuleName);

        report?.BeginPhase(Phase.Load);
        var resolveStarted = Stopwatch.GetTimestamp();
        var binding = compilation.Resolve();
        var resolveTime = Stopwatch.GetElapsedTime(resolveStarted);
        report?.EndPhase(loaderTime);
        report?.ReportPhase(Phase.Resolve, ModuleCount(loaded), resolveTime - loaderTime);

        report?.BeginPhase(Phase.Check, ModuleCount(loaded));
        var types = Semantics.Analyze(compilation, binding, diagnostics);
        report?.EndPhase();

        // On a faulty AST any lowering result would be guesswork.
        if (diagnostics.HasErrors)
            return new CompileResult(sources, diagnostics, null, null);

        // Lowering limits arrive as LYR-IR0001 in the same engine and are rendered with file, line and
        // column like any other error.
        //
        // verify:false plus the separate VerifyOrThrow call is not a behaviour change:
        // ModuleLowerer.VerifyByDefault still decides whether verification runs. The split exists
        // so the two durations can be measured separately.
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

        // Everything a build does except turning the IR into bytes. Writing them is mechanical and
        // cannot fail on the program, so stopping here answers the same question a build answers.
        if (stage == Stage.Check)
            return new CompileResult(sources, diagnostics, ir, null);

        report?.BeginPhase(Phase.Emit, FunctionCount(ir));
        var bytes = BytecodeWriter.Write(ir, options.SourceMap
            ? new SourceMapContext(sources, source.BaseDirectory)
            : null);
        report?.EndPhase();

        return new CompileResult(sources, diagnostics, ir, bytes);
    }

    private static string ModuleCount(List<string> loaded) =>
        loaded.Count == 0 ? "1 module" : $"{loaded.Count + 1} modules";

    private static string FunctionCount(IrModule ir) =>
        ir.Functions.Count == 1 ? "1 function" : $"{ir.Functions.Count} functions";

    /// <summary>
    /// Reads a file into a fresh <see cref="SourceManager"/>: the shared preamble of the debug
    /// commands <c>tokenize</c> and <c>parse</c>, which branch off before the resolver and do not
    /// go through <see cref="Run"/>.
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
/// What a compiler run needs besides the source file.
///
/// <para>A record rather than a growing parameter list.</para>
/// </summary>
public sealed record CompilerOptions
{
    /// <summary>Where the standard library lives. <c>null</c> means
    /// <c>StdlibLoader.DefaultRoot()</c>: <c>LYRIC_STDLIB</c> or the directory next to the
    /// binary.</summary>
    public string? StdlibRoot { get; init; }

    /// <summary>Where phase reports go. <c>null</c> means nobody is listening, and the compiler
    /// then runs with no output dependency at all, which is what the embedding API needs.
    /// benutzbar haelt.</summary>
    public TerminalOutput? Progress { get; init; }

    /// <summary>
    /// Additional NATIVE modules that do not live on disk, by module path.
    ///
    /// <para>Used by <c>LangVm.RegisterFunction</c>: a host function needs a DECLARATION for the
    /// compiler to know its signature, exactly like every standard library native, which stands as
    /// a bodyless <c>pub fn</c> in a <c>.lyr</c> file. The only difference is that this file lives
    /// in memory.</para>
    ///
    /// <para>They are consulted BEFORE the standard library, so a host module hides one of the same name
    /// on disk rather than the other way round: the host decides what its script sees.</para>
    /// </summary>
    public IReadOnlyDictionary<string, string>? NativeModules { get; init; }

    /// <summary>
    /// Whether the SourceMap section is written. On by default: a panic that names a line is worth
    /// the bytes, and the moment it is needed is the moment nobody planned for it.
    ///
    /// <para>Turning it off produces exactly the file a build produced before the section existed,
    /// which is what makes stripping a decision with no other consequence.</para>
    /// </summary>
    public bool SourceMap { get; init; } = true;
}

/// <summary>
/// What a compiler run leaves behind. <see cref="Ir"/> and <see cref="Bytes"/> are <c>null</c>
/// when the requested stage was not reached or was not requested at all;
/// </summary>
public sealed record CompileResult(
    SourceManager Sources,
    DiagnosticEngine Diagnostics,
    IrModule? Ir,
    byte[]? Bytes)
{
    /// <summary>No error was reported. Warnings do not count.</summary>
    public bool Ok => !Diagnostics.HasErrors;

    /// <summary>Renders every diagnostic exactly once and reports whether the run was clean.
    /// </summary>
    public bool Render(TextWriter error)
    {
        Diagnostics.RenderText(error);
        return Ok;
    }
}
