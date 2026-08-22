using Lyric.AST;
using Lyric.Core;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Ir.Lowering;

/// <summary>
/// Assigns a global slot to every module <c>let</c> and every <c>static let</c>.
///
/// <para>Unlike <see cref="TypeTable"/> and <see cref="ImportTable"/>, everything is collected UP
/// FRONT here rather than interned on demand. The reason is the init function: it has to fill every
/// slot, including one no program path ever reads. An unfilled slot would be a value without a value,
/// and the language has none.</para>
///
/// <para>The order is dependency order across modules — every module after the ones it imports —
/// then declaration order within one, and therefore deterministic. It is at the same time the
/// INITIALIZATION ORDER: a global may use one initialized earlier, which is anything its own module
/// declared before it and anything from a module it imports. The sema decides the same question on
/// the same order (<c>LYR-SEM0057</c>); one walk answers both, or they would drift.</para>
/// </summary>
internal sealed class GlobalTable
{
    private readonly Dictionary<GlobalSymbol, GlobalId> _assigned =
        new(ReferenceEqualityComparer.Instance);

    private readonly List<IrGlobal> _defs = new();

    /// <summary>The initializer per slot, in the same order; the source for the init function.</summary>
    private readonly List<(GlobalSymbol Symbol, BindingStmt Binding, ModuleSymbol Module)> _pending = new();

    public List<IrGlobal> Defs => _defs;

    public IReadOnlyList<(GlobalSymbol Symbol, BindingStmt Binding, ModuleSymbol Module)> Pending
        => _pending;

    public bool IsEmpty => _defs.Count == 0;

    /// <summary>
    /// Collects all globals of a compilation. Runs BEFORE the function lowering: a function body may
    /// read a global that stands later in the source.
    /// </summary>
    public void Collect(Compilation compilation, TypeResult types, TypeTable typeTable)
    {
        foreach (var module in compilation.InitializationOrder())
        {
            // Native modules are NOT skipped. "They only declare signatures" holds for bodyless 'fn',
            // but a 'pub let pi: float = 3.14…' has a value, and that has to go into the Globals section
            // like any other.

            foreach (var decl in compilation.AstOf(module).Declarations)
            {
                switch (decl)
                {
                    case GlobalBindingDecl global
                        when module.Members.LookupLocal(global.Binding.Name) is GlobalSymbol symbol:
                        Add(symbol, global.Binding, module, symbol.Name, types, typeTable);
                        break;

                    // A 'static let' on a type is the same mechanism; only the name carries the type, so
                    // 'Player.MAX' and 'Wall.MAX' do not collide.
                    case ClassDecl or StructDecl or EnumDecl:
                        CollectStatics(decl, module, types, typeTable);
                        break;
                }
            }
        }
    }

    private void CollectStatics(Decl decl, ModuleSymbol module, TypeResult types,
        TypeTable typeTable)
    {
        var (typeName, members) = decl switch
        {
            ClassDecl c => (c.Name, c.Members),
            StructDecl v => (v.Name, v.Members),
            _ => (null, null),
        };

        if (typeName is null || members is null) return;
        if (module.Members.LookupLocal(typeName) is not TypeSymbol owner) return;

        foreach (var member in members)
        {
            if (member is not StaticBindingDecl binding) continue;
            if (owner.Members.LookupLocal(binding.Binding.Name) is not GlobalSymbol symbol) continue;

            Add(symbol, binding.Binding, module, $"{typeName}.{symbol.Name}", types, typeTable);
        }
    }

    private void Add(GlobalSymbol symbol, BindingStmt binding, ModuleSymbol module, string name,
        TypeResult types, TypeTable typeTable)
    {
        if (_assigned.ContainsKey(symbol)) return;

        // Without an initializer the slot would stay empty. At module level the grammar allows 'let'
        // only, and a 'let' without a value cannot sensibly be filled in later: there is no later point
        // at which an assignment could stand.
        if (binding.Initializer is null)
            throw new UnsupportedConstructException(
                $"the constant '{name}' has no initializer; a module-level 'let' needs one",
                binding.Span);

        var type = typeTable.Lower(types.TypeOfGlobal(symbol), binding.Span);

        _assigned[symbol] = new GlobalId(_defs.Count);
        _defs.Add(new IrGlobal(name, type));
        _pending.Add((symbol, binding, module));
    }

    /// <summary>
    /// A slot no declaration stands behind — the hidden result buffers of struct-returning
    /// natives live here. No entry in <see cref="Pending"/>: the initialization is injected as
    /// IR after the lowering, because the buffer is an object, not an expression.
    /// </summary>
    public GlobalId DeclareSynthetic(string name, IrType type)
    {
        var id = new GlobalId(_defs.Count);
        _defs.Add(new IrGlobal(name, type));
        return id;
    }

    /// <summary>The slot and type of a global. An unknown symbol is a lowering bug: collection was
    /// complete before the first function was lowered.</summary>
    public (GlobalId Id, IrType Type) Resolve(GlobalSymbol symbol, Span span)
    {
        if (_assigned.TryGetValue(symbol, out var id)) return (id, _defs[id.Value].Type);

        throw new UnsupportedConstructException(
            $"the constant '{symbol.Name}' was not collected (is it declared outside this compilation?)",
            span);
    }

    public GlobalId IdOf(GlobalSymbol symbol) => _assigned[symbol];
}
