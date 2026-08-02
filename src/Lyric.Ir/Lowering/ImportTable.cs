using Lyric.Resolver;

namespace Lyric.Ir.Lowering;

/// <summary>
/// Die nativen Deklarationen der geladenen Stdlib, und welche davon dieses Modul tatsächlich
/// benutzt.
///
/// <para><b>Interniert wird bei Bedarf, nicht auf Vorrat.</b> Wer nur <c>println</c> aufruft, soll
/// keine Import-Tabelle mit <c>print</c> und <c>eprintln</c> bekommen — die Runtime müsste sonst
/// auch die binden und das Modul ablehnen, wenn eine davon fehlt. Die Reihenfolge ergibt sich aus
/// der Lowering-Reihenfolge und ist damit deterministisch (ADR-013).</para>
///
/// <para>Zwei Zugriffswege, weil es zwei Arten von Aufrufern gibt: der Nutzer ruft über ein
/// aufgelöstes <see cref="FunctionSymbol"/> auf, das f-String-Lowering über einen
/// <b>festen Namen</b> — es referenziert <c>std.string.concat</c>, ohne dass jemand
/// <c>std.string</c> importiert hätte. Dasselbe Modell wie Roslyns Verweis auf
/// <c>String.Concat</c>.</para>
/// </summary>
internal sealed class ImportTable
{
    private readonly Dictionary<FunctionSymbol, IrImport> _declared = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, IrImport> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImportId> _assigned = new(StringComparer.Ordinal);
    private readonly List<IrImport> _used = new();

    public List<IrImport> Used => _used;

    public void Declare(FunctionSymbol symbol, IrImport import)
    {
        _declared[symbol] = import;
        _byName[import.Name] = import;
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
