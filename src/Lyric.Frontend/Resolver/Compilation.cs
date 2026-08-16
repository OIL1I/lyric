using Lyric.AST;
using Lyric.Core;

namespace Lyric.Resolver;

/// <summary>
/// Holds the parsed modules of a translation unit and drives resolution.
///
/// Single-file first: usually one module, but several are possible, and then their imports resolve
/// against each other. Modules outside the compilation (the standard library) count as
/// extern/opak.
/// </summary>
public sealed class Compilation
{
    private readonly SourceManager _sm;
    private readonly DiagnosticEngine _de;
    private readonly SymbolTable _builtins = BuiltinTypes.CreateScope();
    private readonly List<ModuleSymbol> _modules = new();
    private readonly Dictionary<ModuleSymbol, Module> _asts = new(ReferenceEqualityComparer.Instance);

    public Compilation(SourceManager sm, DiagnosticEngine de)
    {
        _sm = sm;
        _de = de;
    }

    private readonly HashSet<ModuleSymbol> _native = new(ReferenceEqualityComparer.Instance);

    public IReadOnlyList<ModuleSymbol> Modules => _modules;
    public SymbolTable Builtins => _builtins;
    public ExtensionRegistry Extensions { get; } = new();

    /// <summary>
    /// Loads a module that is not yet in the compilation. Returns <c>null</c> when the path does
    /// not exist.
    ///
    /// <para>A delegate rather than a fixed dependency, so <c>Lyric.Resolver</c> need not
    /// reference <c>Lyric.Parsing</c>. The implementation lives where the parser lives.</para>
    /// </summary>
    public Func<string[], (AST.Module Ast, bool IsNative)?>? ModuleLoader { get; set; }

    /// <summary>
    /// Does the module come from the standard library? Only there is a bodyless function an import
    /// declaration; anywhere else it is an error (<c>LYR-SEM0051</c>).
    ///
    /// <para>The property follows the ORIGIN, not the content.
    /// a user could obtain native functions by naming their module <c>std.foo</c>.</para>
    /// </summary>
    public bool IsNative(ModuleSymbol module) => _native.Contains(module);

    /// <summary>Does module <paramref name="from"/> see declarations from <paramref name="to"/>?
    /// The same module, or an import of <paramref name="to"/>.</summary>
    public bool Sees(ModuleSymbol from, ModuleSymbol to)
    {
        if (ReferenceEquals(from, to)) return true;

        // 'std.core' is always visible without an import. It is the module the language itself
        // uses: 'panic' and 'coroutineEnded' live there and are bound by the compiler, in a
        // dispatcher nobody wrote.
        //
        // The 'Display' extensions for the built-ins hang there too, so without this rule a
        // program would have to import 'std.core' just to satisfy the constraint of
        // does. The same model as Roslyn's well-known members.
        if (to.FullName == "std.core") return true;
        foreach (var decl in AstOf(from).Declarations)
            if (decl is ImportDecl imp && FindModule(imp.Path) is { } t && ReferenceEquals(t, to))
                return true;
        return false;
    }

    /// <summary>
    /// Registers a module. A <paramref name="name"/> given by the caller wins; otherwise the header
    /// decides, and without one the module is "main".
    ///
    /// <para>The caller wins because it knows something the file does not: a loaded module was found
    /// at a path, and a host that supplies a name needs that name to call back in. When the header
    /// disagrees with a path it was loaded from, the mismatch is reported before this is
    /// reached — here the name is simply the authority.</para>
    /// </summary>
    public ModuleSymbol AddModule(Module ast, string? name = null, bool isNative = false)
    {
        var path = name is not null ? name.Split('.')
                 : ast.Header is not null ? ast.Header.Segments
                 : ["main"];
        var members = new SymbolTable(_builtins); // the parent is the builtins, so 'int' and friends resolve through the lookup chain
        var symbol = new ModuleSymbol(path, members, ast);
        _modules.Add(symbol);
        _asts[symbol] = ast;
        if (isNative) _native.Add(symbol);
        return symbol;
    }

    /// <summary>
    /// Loads everything an <c>import</c> requires and that is not there yet, transitively.
    ///
    /// <para>It runs BEFORE resolution rather than during it, or the module list would grow while
    /// the resolver iterates over it. The loop is index-based because <c>_modules</c> grows in its
    /// body.</para>
    ///
    /// <para>A cycle terminates by itself: only what <see cref="FindModule"/> does not yet know is
    /// loaded, and registration happens before a module's own imports are examined.</para>
    /// </summary>
    /// <summary>
    /// Modules the COMPILER itself needs, regardless of what the user imports: the f-string
    /// lowering calls <c>std.string.concat</c> and the <c>fromXxx</c> converters.
    /// </summary>
    private static readonly string[][] WellKnownModules =
        [["std", "string"], ["std", "core"], ["std", "iter"], ["std", "fmt"],
            ["std", "collections"]];

    private void LoadImportedModules()
    {
        if (ModuleLoader is null) return;

        foreach (var path in WellKnownModules)
        {
            if (FindModule(path) is not null) continue;
            if (ModuleLoader(path) is not { } wellKnown) continue; // without a stdlib path this does not apply
            AddModule(wellKnown.Ast, string.Join('.', path), wellKnown.IsNative);
        }

        for (var i = 0; i < _modules.Count; i++)
        {
            foreach (var decl in AstOf(_modules[i]).Declarations)
            {
                if (decl is not ImportDecl import) continue;
                if (FindModule(import.Path) is not null) continue;
                if (ModuleLoader(import.Path) is not { } loaded) continue;

                var name = string.Join('.', import.Path);

                // A file found at a path must agree that it lives there. Without the check the
                // module registers under the name its header claims, the import that pulled it in
                // still finds nothing, and the message says "cannot find" about a file that was
                // just read — and a second importer loads it a second time.
                if (loaded.Ast.Header is { } header && !header.Segments.SequenceEqual(import.Path))
                    _de.Report("LYR-RES0006", Severity.Error, header.Span,
                        $"this file was loaded as '{name}' but declares module "
                        + $"'{string.Join('.', header.Segments)}'");

                AddModule(loaded.Ast, name, loaded.IsNative);
            }
        }
    }

    public Module AstOf(ModuleSymbol module) => _asts[module];

    public ModuleSymbol? FindModule(string[] path) =>
        _modules.FirstOrDefault(m => m.Path.Length == path.Length && m.Path.SequenceEqual(path));

    /// <summary>Resolves every module and returns the binding side table. Errors go to the
    /// diagnostic engine as LYR-RES####.</summary>
    public BindingResult Resolve()
    {
        LoadImportedModules();
        return new Resolver(this, _sm, _de).Run();
    }
}
