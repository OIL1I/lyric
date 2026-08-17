namespace Lyric.AST;

/// <summary>
/// The documentation comments of a program, by the declaration they stand above.
///
/// <para>Keyed by node IDENTITY, like every other side table in this compiler. The parser collects
/// blocks by source offset, which is the only key available while there are no nodes yet — and an
/// offset counts within one file, so two modules both have one at 42. A compilation-wide table
/// keyed that way would answer with another module's documentation, which is the kind of wrong that
/// looks right.</para>
///
/// <para>Lives beside the AST rather than beside the parser: it is produced by parsing and read by
/// the resolver and by tools, and the layer both of those sit on is this one.</para>
/// </summary>
public sealed class DocumentationTable
{
    private readonly Dictionary<Node, string> _docs = new(ReferenceEqualityComparer.Instance);

    /// <summary>A table nothing was recorded in. For a caller that has no documentation to pass and
    /// wants none — the REPL builds its compilation this way.</summary>
    public static DocumentationTable Empty { get; } = new();

    public void Record(Node node, string documentation) => _docs[node] = documentation;

    /// <summary>The block written above <paramref name="node"/>, or <c>null</c> when there is
    /// none.</summary>
    public string? Of(Node node) => _docs.GetValueOrDefault(node);

    public int Count => _docs.Count;

    /// <summary>
    /// Takes over everything <paramref name="other"/> holds.
    ///
    /// <para>Used to gather the per-module tables into the one the compilation answers from. Nodes
    /// are unique per parse, so two modules cannot collide however alike their sources are.</para>
    /// </summary>
    public void Absorb(DocumentationTable other)
    {
        if (ReferenceEquals(this, other)) return;
        foreach (var (node, text) in other._docs) _docs[node] = text;
    }
}
