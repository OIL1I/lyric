namespace Lyric.Embedding;

/// <summary>
/// Was ein Skript mit einem Host-Typ tun darf (M10/E4b).
///
/// <para><b>Es gibt kein <c>Field</c>.</b> <c>Doku.md</c> §21 versprach
/// <c>builder.Field("x", v =&gt; v.X)</c>, und das ist nicht baubar: ein Feldzugriff braucht ein
/// <c>ldfld</c>, und ein Host-Typ hat keinen Typtabellen-Eintrag, aus dem ein Feldindex kaeme
/// (ADR-026). <see cref="Getter"/> ist die ehrliche Form — in Lyric liest sie sich als
/// <c>e.name()</c> und nicht als <c>e.name</c>. Properties als Zucker fuer Methoden nennt ADR-003
/// als moegliche post-v1-Ergaenzung; bis dahin ist ein Host-„Feld" eine Methode.</para>
///
/// <para><b>Der Empfaenger ist der erste Parameter des Delegaten</b> — dieselbe Konvention wie im
/// Lowering jeder anderen Methode (ADR-014). Was der Host schreibt, ist damit genau das, was die
/// VM aufruft.</para>
/// </summary>
public sealed class HostTypeBuilder<T> where T : class
{
    private readonly List<(string Name, Delegate Implementation, bool Mutates)> _methods = [];

    internal HostTypeBuilder() { }

    /// <summary>Eine Methode: <c>e.damage(5)</c>.</summary>
    /// <param name="mutates">Ob sie den Empfaenger aendert. Wirkt sich auf das <c>mut</c> in der
    /// erzeugten Deklaration aus — bei einer <c>class</c> ist es laut Doku §10.2 ohnehin nur ein
    /// Lesbarkeits-Marker, aber ein falscher waere trotzdem eine falsche Aussage.</param>
    public HostTypeBuilder<T> Method(string name, Delegate implementation, bool mutates = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(implementation);
        _methods.Add((name, implementation, mutates));
        return this;
    }

    /// <summary>
    /// Ein lesender Zugriff: <c>e.name()</c>.
    ///
    /// <para>Dasselbe wie eine parameterlose <see cref="Method"/> — der eigene Name steht dafuer,
    /// dass der Host beim Schreiben sieht, was er meint. Dass es in Lyric trotzdem ein Aufruf mit
    /// Klammern ist, steht oben.</para>
    /// </summary>
    public HostTypeBuilder<T> Getter<TValue>(string name, Func<T, TValue> read) =>
        Method(name, read);

    internal IReadOnlyList<(string Name, Delegate Implementation, bool Mutates)> Methods
        => _methods;
}
