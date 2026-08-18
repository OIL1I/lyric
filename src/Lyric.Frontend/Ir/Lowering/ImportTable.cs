using Lyric.Resolver;

namespace Lyric.Ir.Lowering;

/// <summary>
/// How one DECLARED parameter of a native crosses the boundary.
///
/// <para>A struct parameter is FLATTENED: the import's wire signature carries its fields as
/// scalars, in field order, and the call site emits one field load per slot. The bytecode format
/// and the binder never see a struct — which is exactly why this costs no format change — and
/// the .lyr declaration stays the typed façade the script is checked against.</para>
/// </summary>
/// <param name="Declared">The parameter as declared, for diagnostics and the plain case.</param>
/// <param name="Struct">The interned struct type when flattened, else <c>null</c>.</param>
/// <param name="Fields">The field types in layout order; empty for a plain parameter.</param>
internal readonly record struct ImportParam(IrType Declared, TypeId? Struct, IrType[] Fields);

/// <summary>
/// The native declarations of the loaded stdlib, and which of them this module actually uses.
///
/// <para>INTERNED ON DEMAND, NOT IN ADVANCE. Whoever calls only <c>println</c> should not get an
/// import table with <c>print</c> and <c>eprintln</c>: the runtime would have to bind those too and
/// would reject the module if one were missing. The order follows the lowering order and is therefore
/// deterministic.</para>
///
/// <para>Two access paths, because there are two kinds of caller: the user calls through a resolved
/// <see cref="FunctionSymbol"/>, the f-string lowering through a FIXED NAME — it references
/// <c>std.string.concat</c> without anyone having imported <c>std.string</c>. The same model as
/// Roslyn's reference to <c>String.Concat</c>.</para>
/// </summary>
internal sealed class ImportTable
{
    private readonly Dictionary<FunctionSymbol, IrImport> _declared = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, IrImport> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImportId> _assigned = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImportParam[]> _shapes = new(StringComparer.Ordinal);
    private readonly List<IrImport> _used = new();

    public List<IrImport> Used => _used;

    public void Declare(FunctionSymbol symbol, IrImport import, ImportParam[]? shape = null)
    {
        _declared[symbol] = import;
        _byName[import.Name] = import;
        if (shape is not null) _shapes[import.Name] = shape;
    }

    /// <summary>The declared shape of an interned import, or <c>null</c> where none was recorded
    /// — runtime helpers and generated host functions, whose parameters are all plain.</summary>
    public ImportParam[]? ShapeOf(ImportId id) =>
        _shapes.TryGetValue(_used[id.Value].Name, out var shape) ? shape : null;

    public bool IsNative(FunctionSymbol symbol) => _declared.ContainsKey(symbol);

    public ImportId Intern(FunctionSymbol symbol) => Intern(_declared[symbol]);

    public bool TryFind(string name, out IrImport import) => _byName.TryGetValue(name, out import);

    public ImportId Intern(IrImport import)
    {
        if (_assigned.TryGetValue(import.Name, out var existing)) return existing;

        var id = new ImportId(_used.Count);
        _assigned[import.Name] = id;
        _used.Add(import);
        return id;
    }
}
