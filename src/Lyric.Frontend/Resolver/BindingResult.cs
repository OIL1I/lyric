using Lyric.AST;

namespace Lyric.Resolver;

/// <summary>
/// Seiten-Tabelle (Roslyn-`SemanticModel`-Stil): bindet AST-Referenz-Knoten an die
/// aufgelösten Symbole, ohne den immutablen AST anzufassen. In Slice 1 gefüllt für
/// Typ-Namen (<see cref="NamedType"/> → Symbol). Spätere Slices ergänzen Ausdrücke.
/// </summary>
public sealed class BindingResult
{
    private readonly Dictionary<Node, Symbol> _bindings = new(ReferenceEqualityComparer.Instance);

    public void Bind(Node node, Symbol symbol) => _bindings[node] = symbol;

    public Symbol? Resolve(Node node) => _bindings.TryGetValue(node, out var s) ? s : null;

    public int Count => _bindings.Count;
}
