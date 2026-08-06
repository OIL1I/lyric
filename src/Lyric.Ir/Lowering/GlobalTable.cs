using Lyric.AST;
using Lyric.Core;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Ir.Lowering;

/// <summary>
/// Ordnet jedem Modul-<c>let</c> und jedem <c>static let</c> einen globalen Slot zu.
///
/// <para><b>Anders als <see cref="TypeTable"/> und <see cref="ImportTable"/> wird hier
/// <i>vollstaendig</i> vorab gesammelt, nicht bei Bedarf interniert.</b> Der Grund ist die
/// Init-Funktion: sie muss jeden Slot fuellen, auch einen, den kein Programmpfad je liest. Ein
/// ungefuellter Slot waere ein Wert ohne Wert, und §6.6 kennt keinen.</para>
///
/// <para>Die Reihenfolge ist Modul- dann Deklarationsreihenfolge und damit deterministisch
/// (ADR-013). Sie ist zugleich die <b>Initialisierungsreihenfolge</b>: ein Global darf ein
/// frueher deklariertes benutzen, ein spaeteres nicht. Das ist keine Einschraenkung, die diese
/// Implementierung erfindet — es ist die einzige Ordnung, die ohne Abhaengigkeitsanalyse
/// auskommt, und C# (Feld-Initialisierer) sowie Go (ohne dessen Sortierung) machen es genauso.</para>
/// </summary>
internal sealed class GlobalTable
{
    private readonly Dictionary<GlobalSymbol, GlobalId> _assigned =
        new(ReferenceEqualityComparer.Instance);

    private readonly List<IrGlobal> _defs = new();

    /// <summary>Der Initialisierer je Slot, in derselben Reihenfolge — Quelle fuer die
    /// Init-Funktion.</summary>
    private readonly List<(GlobalSymbol Symbol, BindingStmt Binding, ModuleSymbol Module)> _pending = new();

    public List<IrGlobal> Defs => _defs;

    public IReadOnlyList<(GlobalSymbol Symbol, BindingStmt Binding, ModuleSymbol Module)> Pending
        => _pending;

    public bool IsEmpty => _defs.Count == 0;

    /// <summary>
    /// Sammelt alle Globals einer Compilation. Laeuft <b>vor</b> dem Funktions-Lowering: ein
    /// Funktionsrumpf darf ein Global lesen, das erst spaeter im Quelltext steht.
    /// </summary>
    public void Collect(Compilation compilation, TypeResult types, TypeTable typeTable)
    {
        foreach (var module in compilation.Modules)
        {
            // Native Module (die Stdlib) deklarieren nur Signaturen; sie haben nichts zu fuellen.
            if (compilation.IsNative(module)) continue;

            foreach (var decl in compilation.AstOf(module).Declarations)
            {
                switch (decl)
                {
                    case GlobalBindingDecl global
                        when module.Members.LookupLocal(global.Binding.Name) is GlobalSymbol symbol:
                        Add(symbol, global.Binding, module, symbol.Name, types, typeTable);
                        break;

                    // 'static let' auf einem Typ ist derselbe Mechanismus — nur der Name traegt
                    // den Typ, damit 'Player.MAX' und 'Wall.MAX' nicht zusammenfallen.
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

        // Ohne Initialisierer bliebe der Slot leer. Auf Modulebene erlaubt die Grammatik nur
        // 'let' (§2.3), und ein 'let' ohne Wert ist auf Top-Level nicht sinnvoll auffuellbar:
        // es gibt keinen spaeteren Punkt, an dem eine Zuweisung stehen koennte.
        if (binding.Initializer is null)
            throw new UnsupportedConstructException(
                $"the constant '{name}' has no initializer; a module-level 'let' needs one",
                binding.Span);

        var type = typeTable.Lower(types.TypeOfGlobal(symbol), binding.Span);

        _assigned[symbol] = new GlobalId(_defs.Count);
        _defs.Add(new IrGlobal(name, type));
        _pending.Add((symbol, binding, module));
    }

    /// <summary>Slot und Typ eines Globals. Ein unbekanntes Symbol ist ein Lowering-Bug: gesammelt
    /// wurde vollstaendig, bevor die erste Funktion gelowert wurde.</summary>
    public (GlobalId Id, IrType Type) Resolve(GlobalSymbol symbol, Span span)
    {
        if (_assigned.TryGetValue(symbol, out var id)) return (id, _defs[id.Value].Type);

        throw new UnsupportedConstructException(
            $"the constant '{symbol.Name}' was not collected (is it declared outside this compilation?)",
            span);
    }

    public GlobalId IdOf(GlobalSymbol symbol) => _assigned[symbol];
}
