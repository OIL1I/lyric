using Lyric.AST;

namespace Lyric.Resolver;

/// <summary>
/// Registriert `extend`-Blöcke (Sprache.md §3.6). Extend-Methoden werden NICHT in die
/// Member-Tabelle des Ziel-Typs gemerget (das könnte die import-gebundene Sichtbarkeit
/// nicht ausdrücken), sondern hier gesammelt — C#-Extension-Method-Modell. Der TypeChecker
/// konsultiert die Registry beim Member-Lookup und filtert nach Sichtbarkeit (deklarierendes
/// Modul == aktuelles Modul oder von ihm importiert).
/// </summary>
public sealed class ExtensionRegistry
{
    private readonly List<ExtensionBlock> _blocks = new();
    private readonly Dictionary<TypeSymbol, List<ExtensionMethod>> _byTarget = new(ReferenceEqualityComparer.Instance);

    public IReadOnlyList<ExtensionBlock> Blocks => _blocks;

    public void Add(ExtensionBlock block) => _blocks.Add(block);

    /// <summary>Nach der Ziel-Auflösung (Resolver-Pass 3) den Lookup-Index aufbauen.</summary>
    public void BuildIndex()
    {
        _byTarget.Clear();
        foreach (var b in _blocks)
        {
            if (b.Target is null) continue;
            if (!_byTarget.TryGetValue(b.Target, out var list))
                _byTarget[b.Target] = list = new();
            foreach (var m in b.Methods)
                list.Add(new ExtensionMethod(m, b.Module));
        }
    }

    /// <summary>Alle für <paramref name="target"/> registrierten Extension-Methoden
    /// (ungefiltert — die Sichtbarkeitsprüfung macht der Aufrufer).</summary>
    public IReadOnlyList<ExtensionMethod> MethodsFor(TypeSymbol target) =>
        _byTarget.TryGetValue(target, out var list) ? list : [];
}

/// <summary>Ein `extend`-Block mit seinen Methoden-Symbolen und dem deklarierenden Modul.
/// <see cref="Target"/> wird erst in Resolver-Pass 3 gesetzt (null = unauflösbar/unsupported).</summary>
public sealed class ExtensionBlock
{
    public ExtendDecl Decl { get; }
    public ModuleSymbol Module { get; }
    public SymbolTable MethodScope { get; } // FunctionSymbols der Extend-Methoden (für Body-Check + Cross-Calls)
    public FunctionSymbol[] Methods { get; }
    public TypeSymbol? Target { get; set; }

    public ExtensionBlock(ExtendDecl decl, ModuleSymbol module, SymbolTable methodScope, FunctionSymbol[] methods)
    {
        Decl = decl;
        Module = module;
        MethodScope = methodScope;
        Methods = methods;
    }
}

public readonly record struct ExtensionMethod(FunctionSymbol Symbol, ModuleSymbol Module);
