using Lyric.AST;
using Lyric.Core;

namespace Lyric.Resolver;

/// <summary>
/// Hält die geparsten Module einer Übersetzungseinheit und treibt die Auflösung an.
/// Single-file-first: meist ein Modul, aber mehrere sind möglich (dann lösen sich
/// Imports untereinander auf). Module außerhalb der Compilation (Stdlib) gelten als
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
    /// Lädt ein Modul, das noch nicht in der Compilation ist — die Stdlib. Liefert <c>null</c>,
    /// wenn es den Pfad nicht gibt.
    ///
    /// <para>Als Delegat statt als fester Abhängigkeit, damit <c>Lyric.Resolver</c> nicht
    /// <c>Lyric.Parsing</c> referenzieren muss. Die konkrete Implementierung
    /// (<c>StdlibLoader</c>) sitzt dort, wo der Parser lebt.</para>
    /// </summary>
    public Func<string[], (AST.Module Ast, bool IsNative)?>? ModuleLoader { get; set; }

    /// <summary>
    /// Stammt das Modul aus der Stdlib? Nur dort ist eine rumpflose Funktion eine
    /// Import-Deklaration; überall sonst ist sie ein Fehler (<c>LYR-SEM0051</c>).
    ///
    /// <para>Die Eigenschaft hängt an der <b>Herkunft</b>, nicht am Inhalt: sonst könnte sich ein
    /// User native Funktionen erschleichen, indem er sein Modul <c>std.foo</c> nennt.</para>
    /// </summary>
    public bool IsNative(ModuleSymbol module) => _native.Contains(module);

    /// <summary>Sieht Modul <paramref name="from"/> Deklarationen aus <paramref name="to"/>?
    /// Gleiches Modul oder ein Import von <paramref name="to"/> (§3.6-Sichtbarkeit).</summary>
    public bool Sees(ModuleSymbol from, ModuleSymbol to)
    {
        if (ReferenceEquals(from, to)) return true;
        foreach (var decl in AstOf(from).Declarations)
            if (decl is ImportDecl imp && FindModule(imp.Path) is { } t && ReferenceEquals(t, to))
                return true;
        return false;
    }

    /// <summary>Registriert ein Modul. Der Pfad kommt aus dem Header, sonst aus
    /// <paramref name="name"/>, sonst "main" (Single-File-Default).</summary>
    public ModuleSymbol AddModule(Module ast, string? name = null, bool isNative = false)
    {
        var path = ast.Header is not null ? ast.Header.Segments
                 : name is not null ? name.Split('.')
                 : ["main"];
        var members = new SymbolTable(_builtins); // Parent = Builtins → 'int' & Co. via Lookup-Kette
        var symbol = new ModuleSymbol(path, members, ast);
        _modules.Add(symbol);
        _asts[symbol] = ast;
        if (isNative) _native.Add(symbol);
        return symbol;
    }

    /// <summary>
    /// Lädt alles nach, was per <c>import</c> verlangt und noch nicht da ist — transitiv.
    ///
    /// <para>Läuft <b>vor</b> der Auflösung, nicht mittendrin: sonst würde die Modul-Liste
    /// wachsen, während der Resolver über sie iteriert. Die Schleife ist absichtlich
    /// indexbasiert, weil <c>_modules</c> in ihrem Rumpf wächst — neu geladene Module werden so
    /// selbst noch besucht.</para>
    ///
    /// <para>Ein Zyklus terminiert von allein: geladen wird nur, was <see cref="FindModule"/> noch
    /// nicht kennt, und registriert wird vor dem Betrachten der eigenen Imports.</para>
    /// </summary>
    /// <summary>
    /// Module, die der <b>Compiler selbst</b> braucht, unabhängig davon, was der Nutzer importiert:
    /// das f-String-Lowering ruft <c>std.string.concat</c> und die <c>fromXxx</c>-Wandler auf.
    /// Roslyn macht dasselbe mit seinen Well-Known-Members.
    /// </summary>
    private static readonly string[][] WellKnownModules =
        [["std", "string"], ["std", "core"], ["std", "iter"]];

    private void LoadImportedModules()
    {
        if (ModuleLoader is null) return;

        foreach (var path in WellKnownModules)
        {
            if (FindModule(path) is not null) continue;
            if (ModuleLoader(path) is not { } wellKnown) continue; // ohne Stdlib-Pfad: entfällt
            AddModule(wellKnown.Ast, string.Join('.', path), wellKnown.IsNative);
        }

        for (var i = 0; i < _modules.Count; i++)
        {
            foreach (var decl in AstOf(_modules[i]).Declarations)
            {
                if (decl is not ImportDecl import) continue;
                if (FindModule(import.Path) is not null) continue;
                if (ModuleLoader(import.Path) is not { } loaded) continue;

                AddModule(loaded.Ast, string.Join('.', import.Path), loaded.IsNative);
            }
        }
    }

    public Module AstOf(ModuleSymbol module) => _asts[module];

    public ModuleSymbol? FindModule(string[] path) =>
        _modules.FirstOrDefault(m => m.Path.Length == path.Length && m.Path.SequenceEqual(path));

    /// <summary>Löst alle Module auf und liefert die Binding-Seitentabelle. Fehler
    /// gehen als LYR-RES#### an die DiagnosticEngine.</summary>
    public BindingResult Resolve()
    {
        LoadImportedModules();
        return new Resolver(this, _sm, _de).Run();
    }
}
