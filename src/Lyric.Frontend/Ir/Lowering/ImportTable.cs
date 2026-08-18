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
/// A struct RETURN. The wire form is void plus a trailing out-parameter: a hidden module global
/// holds one instance of the struct, the call passes it last, the host fills its slots, and the
/// call site copies the value out — a copy the scalarizer dissolves when it never escapes. The
/// language's value semantics are what make the shared buffer safe: any binding copies.
/// </summary>
internal readonly record struct ImportReturn(TypeId Struct, IrType[] Fields);

/// <summary>The declared shape of a native whose signature was transformed on the wire.</summary>
internal sealed record ImportShape(ImportParam[] Params, ImportReturn? Return);

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
    private readonly Dictionary<string, ImportShape> _shapes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GlobalId> _buffers = new(StringComparer.Ordinal);
    private readonly List<IrImport> _used = new();

    public List<IrImport> Used => _used;

    /// <summary>The hidden out-buffers created so far: one global per struct-returning import
    /// that has a call site. The module lowerer injects their construction into the global
    /// initializer at the end.</summary>
    public List<(GlobalId Global, TypeId Type)> ResultBuffers { get; } = new();

    public void Declare(FunctionSymbol symbol, IrImport import, ImportShape? shape = null)
    {
        _declared[symbol] = import;
        _byName[import.Name] = import;
        if (shape is not null) _shapes[import.Name] = shape;
    }

    /// <summary>The declared shape of an interned import, or <c>null</c> where none was recorded
    /// — runtime helpers and generated host functions, whose parameters are all plain.</summary>
    public ImportShape? ShapeOf(ImportId id) =>
        _shapes.TryGetValue(_used[id.Value].Name, out var shape) ? shape : null;

    /// <summary>The hidden global holding this import's result buffer, created on first use.</summary>
    public GlobalId ResultBuffer(ImportId id, GlobalTable globals)
    {
        var name = _used[id.Value].Name;
        if (_buffers.TryGetValue(name, out var existing)) return existing;

        var structType = _shapes[name].Return!.Value.Struct;
        var global = globals.DeclareSynthetic($"<out:{name}>", new IrStructType(structType));
        _buffers[name] = global;
        ResultBuffers.Add((global, structType));
        return global;
    }

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
