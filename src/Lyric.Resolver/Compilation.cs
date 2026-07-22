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

    public IReadOnlyList<ModuleSymbol> Modules => _modules;
    public SymbolTable Builtins => _builtins;

    /// <summary>Registriert ein Modul. Der Pfad kommt aus dem Header, sonst aus
    /// <paramref name="name"/>, sonst "main" (Single-File-Default).</summary>
    public ModuleSymbol AddModule(Module ast, string? name = null)
    {
        var path = ast.Header is not null ? ast.Header.Segments
                 : name is not null ? name.Split('.')
                 : ["main"];
        var members = new SymbolTable(_builtins); // Parent = Builtins → 'int' & Co. via Lookup-Kette
        var symbol = new ModuleSymbol(path, members, ast);
        _modules.Add(symbol);
        _asts[symbol] = ast;
        return symbol;
    }

    public Module AstOf(ModuleSymbol module) => _asts[module];

    public ModuleSymbol? FindModule(string[] path) =>
        _modules.FirstOrDefault(m => m.Path.Length == path.Length && m.Path.SequenceEqual(path));

    /// <summary>Löst alle Module auf und liefert die Binding-Seitentabelle. Fehler
    /// gehen als LYR-RES#### an die DiagnosticEngine.</summary>
    public BindingResult Resolve() => new Resolver(this, _sm, _de).Run();
}
