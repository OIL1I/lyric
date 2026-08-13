using Lyric.AST;

namespace Lyric.Resolver;

/// <summary>
/// Registers `extend` blocks. Extension methods are NOT merged into the target type's member
/// table, which could not express import-bound visibility; they are collected here, as in C#'s
/// extension method model. The type checker consults the registry during member lookup and
/// filters by visibility: the declaring module is the current one or imported by it.
/// </summary>
public sealed class ExtensionRegistry
{
    private readonly List<ExtensionBlock> _blocks = new();
    private readonly Dictionary<TypeSymbol, List<ExtensionMethod>> _byTarget = new(ReferenceEqualityComparer.Instance);

    public IReadOnlyList<ExtensionBlock> Blocks => _blocks;

    public void Add(ExtensionBlock block) => _blocks.Add(block);

    /// <summary>Builds the lookup index after target resolution.</summary>
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
    /// (unfiltered; the caller checks visibility).</summary>
    public IReadOnlyList<ExtensionMethod> MethodsFor(TypeSymbol target) =>
        _byTarget.TryGetValue(target, out var list) ? list : [];
}

/// <summary>An `extend` block with its method symbols and its declaring module.
/// <see cref="Target"/> is set in pass 3 (null when unresolvable).
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
