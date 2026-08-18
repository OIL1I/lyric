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
        var parsedEntry = ParsedModule.Parse(sources, id, diagnostics);
        var entry = parsedEntry.Ast;
        report?.EndPhase();

        // The module loader times itself: Compilation.Resolve loads the imported modules
        // internally, so the load/resolve boundary is not observable from outside. The wrapper
        // subtracts its own duration from the resolve time.
        var loaderTime = TimeSpan.Zero;
        var loaded = new List<string>();

        var stdlib = BuildModuleLoader(sources, diagnostics, options, source.BaseDirectory);

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
        compilation.AddModule(entry, source.ModuleName, documentation: parsedEntry.Documentation);

        report?.BeginPhase(Phase.Load);
        var resolveStarted = Stopwatch.GetTimestamp();
        var binding = compilation.Resolve();
        var resolveTime = Stopwatch.GetElapsedTime(resolveStarted);
        report?.EndPhase(loaderTime);
        report?.ReportPhase(Phase.Resolve, ModuleCount(loaded), resolveTime - loaderTime);

        report?.BeginPhase(Phase.Check, ModuleCount(loaded));
        var types = Semantics.Analyze(compilation, binding, diagnostics);
        report?.EndPhase();

        // From here on every exit carries it. A program WITH errors still has a model, and that is
        // the case an editor cares about most: the text under a cursor is usually mid-edit.
        var model = new SemanticModel(compilation, entry, binding, types);

        // On a faulty AST any lowering result would be guesswork.
        if (diagnostics.HasErrors)
            return new CompileResult(sources, diagnostics, null, null, model);

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
            return new CompileResult(sources, diagnostics, ir, null, model);

        if (ModuleLowerer.VerifyByDefault)
        {
            report?.BeginPhase(Phase.Verify, FunctionCount(ir));
            IrVerifier.VerifyOrThrow(ir);
            report?.EndPhase();
        }

        // Everything a build does except turning the IR into bytes. Writing them is mechanical and
        // cannot fail on the program, so stopping here answers the same question a build answers.
        if (stage == Stage.Check)
            return new CompileResult(sources, diagnostics, ir, null, model);

        report?.BeginPhase(Phase.Emit, FunctionCount(ir));
        var bytes = BytecodeWriter.Write(ir, options.SourceMap
            ? new SourceMapContext(sources, source.BaseDirectory)
            : null);
        report?.EndPhase();

        return new CompileResult(sources, diagnostics, ir, bytes, model);
    }

    /// <summary>
    /// The module loader every run uses: 'std' from the standard library, a segment the host
    /// claimed from one of its native roots, everything else from the program's directory.
    ///
    /// <para>The split is by module path rather than by trying one root and then the other. A
    /// precedence rule would let a file at '&lt;program&gt;/std/io/console.lyr' shadow the standard
    /// library silently, and silently is the part that makes it a trap.</para>
    /// </summary>
    private static Func<string[], LoadedModule?> BuildModuleLoader(
        SourceManager sources, DiagnosticEngine diagnostics, CompilerOptions options,
        string fallbackSourceRoot)
    {
        var fromStdlib = StdlibLoader.ForRoot(options.StdlibRoot ?? StdlibLoader.DefaultRoot(),
            sources, diagnostics, options.SourceOverlay);
        var fromProject = StdlibLoader.ForProject(
            options.SourceRoot ?? fallbackSourceRoot, sources, diagnostics, options.SourceOverlay);

        // Roots the host declares native, keyed by the segment they own. An SDK ships its
        // declarations as .lyr files and says which prefix they live under.
        var nativeRoots = options.NativeRoots?.ToDictionary(
            entry => entry.Key,
            entry => StdlibLoader.ForRoot(entry.Value, sources, diagnostics, options.SourceOverlay),
            StringComparer.Ordinal);

        var loader = (string[] modulePath) =>
        {
            if (modulePath is ["std", ..]) return fromStdlib(modulePath);

            if (nativeRoots is not null && modulePath.Length > 0
                && nativeRoots.TryGetValue(modulePath[0], out var native))
                return native(modulePath);

            return fromProject(modulePath);
        };

        // Supplied modules first, then disk. Chained rather than a second loader mechanism:
        // 'Compilation' knows exactly one delegate.
        var provided = options.NativeModules;
        if (provided is { Count: > 0 })
        {
            var fromDisk = loader;
            loader = modulePath =>
            {
                var name = string.Join('.', modulePath);
                if (!provided.TryGetValue(name, out var text)) return fromDisk(modulePath);

                var id = sources.AddVirtual(name, text);
                var parsed = ParsedModule.Parse(sources, id, diagnostics);
                return new LoadedModule(parsed.Ast, IsNative: true, parsed.Documentation);
            };
        }

        return loader;
    }

    /// <summary>
    /// Everything <see cref="Check(ScriptSource, CompilerOptions?)"/> does, over every root of a
    /// project at once: ONE compilation, one source manager, one model.
    ///
    /// <para>One compilation rather than one per root, because symbols are identity objects. An
    /// editor asking "who uses this function" needs the uses in OTHER files bound to the same
    /// symbol the declaration produced, and two compilations of the same text produce two symbol
    /// worlds with nothing to compare.</para>
    ///
    /// <para>Every root is registered before anything resolves, so an import between two roots
    /// finds the module already there instead of reading its file a second time. A root's name
    /// comes from its header when it has one, and from the caller otherwise —
    /// <see cref="ScriptSource.ModuleName"/> is where a caller says what a headerless file is
    /// called, and for a file under the source root that must be the same derivation the import
    /// path makes in the other direction, or the import will not find it.</para>
    ///
    /// <para>Checked as a workspace, not as an executable: two roots may both declare 'main',
    /// because each is the entry point of its own program (see
    /// <see cref="Semantics.Analyze"/>).</para>
    /// </summary>
    public static CompileResult CheckProject(
        IReadOnlyList<ScriptSource> roots, CompilerOptions? options = null)
    {
        options ??= new CompilerOptions();
        var sources = new SourceManager();
        var diagnostics = new DiagnosticEngine(sources);

        var loader = BuildModuleLoader(sources, diagnostics, options,
            roots.Count > 0 ? roots[0].BaseDirectory : Directory.GetCurrentDirectory());

        var compilation = new Compilation(sources, diagnostics) { ModuleLoader = loader };

        Module? entry = null;
        foreach (var root in roots)
        {
            if (root.Open(sources, diagnostics) is not { } id) continue;

            var parsed = ParsedModule.Parse(sources, id, diagnostics);

            // The header wins over the caller's derivation. A file whose header disagrees with its
            // path registers under the name it claims; an import of the path-derived name then
            // reloads the file and reports the mismatch (LYR-RES0006), the same way it would were
            // the file not a root.
            var name = parsed.Ast.Header is not null ? null : root.ModuleName;

            compilation.AddModule(parsed.Ast, name, documentation: parsed.Documentation);
            entry ??= parsed.Ast;
        }

        // No root could even be opened. The diagnostics say why, and there is no module to hang a
        // model on.
        if (entry is null) return new CompileResult(sources, diagnostics, null, null);

        var binding = compilation.Resolve();
        var types = Semantics.Analyze(compilation, binding, diagnostics, singleProgram: false);

        var model = new SemanticModel(compilation, entry, binding, types);

        if (diagnostics.HasErrors)
            return new CompileResult(sources, diagnostics, null, null, model);

        var ir = ModuleLowerer.Lower(compilation, binding, types, diagnostics, verify: false);
        if (ir is null) return new CompileResult(sources, diagnostics, null, null, model);

        if (ModuleLowerer.VerifyByDefault) IrVerifier.VerifyOrThrow(ir);

        return new CompileResult(sources, diagnostics, ir, null, model);
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
/// <summary>
/// The front end's answer about a program, as opposed to its output.
///
/// <para>The three tables belong together and are useless apart: a symbol from
/// <see cref="Binding"/> is looked up in <see cref="Types"/>, and both are keyed by nodes that only
/// <see cref="Compilation"/> can hand out. Passing them as one value keeps a caller from holding a
/// binding table from one run beside a type table from another.</para>
/// </summary>
/// <param name="Entry">The module that was compiled, as opposed to the ones it imported.</param>
public sealed record SemanticModel(
    Compilation Compilation,
    Module Entry,
    BindingResult Binding,
    TypeResult Types)
{
    /// <summary>
    /// What was written above each declaration, across every module that was read.
    ///
    /// <para>Forwarded rather than stored: the compilation gathers it as modules arrive, and a copy
    /// here would be a second table to keep in step with the first.</para>
    /// </summary>
    public DocumentationTable Documentation => Compilation.Documentation;
}

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
    /// Directories whose modules may declare functions WITHOUT A BODY, keyed by the module path
    /// segment they own: <c>["engine"] = "…/sdk"</c> makes <c>engine.input</c> read
    /// <c>…/sdk/engine/input.lyr</c> and treats what it finds as native declarations.
    ///
    /// <para>For an SDK that ships its surface as <c>.lyr</c> files rather than as generated
    /// strings. The host still supplies the implementations, under the same qualified names.</para>
    ///
    /// <para>A ROOT is declared native, never a file: <c>Compilation.IsNative</c> follows the origin
    /// of a module and not its content, or naming a file well enough would be a way into the host.
    /// The segment a root owns is taken out of the program's own directory, which is what makes the
    /// answer unambiguous instead of a matter of precedence.</para>
    /// </summary>
    public IReadOnlyDictionary<string, string>? NativeRoots { get; init; }

    /// <summary>
    /// Where the program's own modules are looked up. <c>null</c> means the directory of the entry
    /// file, which is what a program without a project file gets.
    ///
    /// <para>Filled from <see cref="ProjectFile"/> by the tools that read one. Deliberately not
    /// discovered here: a script being compiled must not be able to widen what the compiler looks
    /// at by placing a file beside itself, so the decision belongs to the caller.</para>
    /// </summary>
    public string? SourceRoot { get; init; }

    /// <summary>
    /// Text to use instead of what lies on disk, by absolute file path. A module found at one of
    /// these paths is read from here, and one that exists only here is found all the same.
    ///
    /// <para>An editor holds the authoritative text of every file it has open, saved or not. Without
    /// this, a program is compiled against its own buffer and against the SAVED version of every
    /// module it imports, and the two disagree for as long as an edit is unsaved.</para>
    ///
    /// <para>Supply it with a comparer that matches the platform's idea of path equality; it is
    /// looked up with <see cref="Path.GetFullPath(string)"/> applied to the candidate.</para>
    /// </summary>
    public IReadOnlyDictionary<string, string>? SourceOverlay { get; init; }

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
/// <param name="Model">
/// What the front end knew when it was done: the modules, the resolved names, the types. It is
/// <c>null</c> only when the run stopped before the sema, which happens when the source could not
/// be opened at all.
///
/// <para>Carried out rather than dropped because a batch compile is not the only caller. Everything
/// a tool can say ABOUT a program rather than about its bytes — a type under the cursor, the
/// declaration a name refers to — is in these three tables, and rebuilding them outside would mean
/// running the front end a second time and getting a second answer to the same question.</para>
/// </param>
public sealed record CompileResult(
    SourceManager Sources,
    DiagnosticEngine Diagnostics,
    IrModule? Ir,
    byte[]? Bytes,
    SemanticModel? Model = null)
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
