using Lyric.AST;

namespace Lyric.Resolver;

/// <summary>
/// A side table binding AST reference nodes to their resolved symbols without touching the
/// immutable AST.
/// type names (<see cref="NamedType"/> to symbol).
/// </summary>
public sealed class BindingResult
{
    private readonly Dictionary<Node, Symbol> _bindings = new(ReferenceEqualityComparer.Instance);

    public void Bind(Node node, Symbol symbol) => _bindings[node] = symbol;

    public Symbol? Resolve(Node node) => _bindings.TryGetValue(node, out var s) ? s : null;

    /// <summary>
    /// Every bound node with its symbol.
    ///
    /// <para>The table answers "what does this node mean". Turning it round — "which nodes mean this
    /// symbol" — has no answer without walking it, and a second table built alongside would be a
    /// second truth about one relation. A consumer that needs the reverse direction builds it from
    /// here.</para>
    /// </summary>
    public IEnumerable<KeyValuePair<Node, Symbol>> All => _bindings;

    public int Count => _bindings.Count;
}
