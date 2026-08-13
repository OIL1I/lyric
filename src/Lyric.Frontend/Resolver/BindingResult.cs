using Lyric.AST;

namespace Lyric.Resolver;

/// <summary>
/// A side table binding AST reference nodes to their resolved symbols without touching the
/// immutable AST.
/// Typ-Namen (<see cref="NamedType"/> → Symbol). Spätere Slices ergänzen Ausdrücke.
/// </summary>
public sealed class BindingResult
{
    private readonly Dictionary<Node, Symbol> _bindings = new(ReferenceEqualityComparer.Instance);

    public void Bind(Node node, Symbol symbol) => _bindings[node] = symbol;

    public Symbol? Resolve(Node node) => _bindings.TryGetValue(node, out var s) ? s : null;

    public int Count => _bindings.Count;
}
