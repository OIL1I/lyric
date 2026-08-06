namespace Lyric.Ir.Lowering;

/// <summary>
/// Vergibt die <see cref="FunctionId"/>s der Funktionen, die erst <b>waehrend</b> des Lowerings
/// entstehen: angehobene Lambdas (ADR-018), Coroutine-Rumpfe (§8) und monomorphisierte Instanzen
/// (§12).
///
/// <para><b>Warum gemeinsam und nicht je Tabelle ein eigener Bereich.</b> Alle drei wachsen
/// gleichzeitig und unbegrenzt: eine Instanz kann ein Lambda enthalten, ein Lambda eine generische
/// Funktion rufen. Getrennte Bereiche muessten ihre Groesse vorher kennen — und die steht erst
/// fest, wenn alles gelowert ist. Ein gemeinsamer Zaehler loest das, weil die Id dann in der
/// Reihenfolge der Anforderung vergeben wird und nicht in der einer Kategorie.</para>
///
/// <para>Die Position in der Funktionsliste <b>ist</b> die Id (ADR-013), also wird am Ende nach ihr
/// sortiert. Deterministisch bleibt das, weil die Anforderungsreihenfolge es ist.</para>
/// </summary>
internal sealed class FunctionIds
{
    private int _next;

    public FunctionIds(int first) => _next = first;

    public FunctionId Next() => new(_next++);
}
