namespace Lyric.Embedding;

/// <summary>
/// What a script may do with a host type.
///
/// <para>There is no field access: a host type has no type-table entry, so there is no field
/// index. <see cref="Getter"/> produces a method, read in Lyric as <c>e.name()</c>.</para>
///
/// <para>The receiver is the first parameter of the delegate, the same position it occupies in
/// every other method.</para>
/// </summary>
public sealed class HostTypeBuilder<T> where T : class
{
    private readonly List<(string Name, Delegate Implementation, bool Mutates)> _methods = [];

    internal HostTypeBuilder() { }

    /// <summary>A method: <c>e.damage(5)</c>.</summary>
    /// <param name="mutates">Whether it changes the receiver. Controls the <c>mut</c> in the
    /// generated declaration.</param>
    public HostTypeBuilder<T> Method(string name, Delegate implementation, bool mutates = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(implementation);
        _methods.Add((name, implementation, mutates));
        return this;
    }

    /// <summary>A read: <c>e.name()</c>. The same as a parameterless <see cref="Method"/>.
    /// </summary>
    public HostTypeBuilder<T> Getter<TValue>(string name, Func<T, TValue> read) =>
        Method(name, read);

    internal IReadOnlyList<(string Name, Delegate Implementation, bool Mutates)> Methods
        => _methods;
}
