using Lyric.AST;
using Lyric.Core;

namespace Lyric.Lsp.Analysis;

/// <summary>
/// The node under a cursor, and everything above it.
///
/// <para>A PATH rather than a single node, because the innermost node is often not the one that
/// knows the answer. The member name in <c>a.b</c> is no node of its own — it is a string on the
/// <see cref="MemberExpr"/> — and a name inside a type is a <see cref="NamedType"/> whose symbol
/// hangs on itself. Handing out only the innermost node would force every caller to search
/// downwards again for the parent it actually needs.</para>
///
/// <para>The search descends into the FIRST child that covers the offset, which is why
/// <see cref="AstChildren"/> yields in source order. Spans nest, so at most one child can qualify —
/// except where a parser recovery produced overlapping spans, and there the first is as good an
/// answer as any.</para>
/// </summary>
public static class NodeFinder
{
    /// <summary>
    /// The chain from the module down to the innermost node covering <paramref name="offset"/>,
    /// outermost first. Empty when the offset lies outside the module.
    /// </summary>
    public static IReadOnlyList<Node> PathAt(Node root, FileId file, int offset)
    {
        if (!Covers(root, file, offset)) return [];

        var path = new List<Node> { root };
        var current = root;

        while (true)
        {
            Node? next = null;
            foreach (var child in AstChildren.Of(current))
            {
                if (!Covers(child, file, offset)) continue;
                next = child;
                break;
            }

            if (next is null) return path;
            path.Add(next);
            current = next;
        }
    }

    /// <summary>The innermost node covering the offset, or <c>null</c>.</summary>
    public static Node? At(Node root, FileId file, int offset)
    {
        var path = PathAt(root, file, offset);
        return path.Count == 0 ? null : path[^1];
    }

    /// <summary>
    /// Does this node's span contain the offset?
    ///
    /// <para>The END is included, unlike <see cref="Span.Contains"/>. A cursor sits BETWEEN
    /// characters, and the position just after an identifier is the one an editor reports when the
    /// caret is at its right edge — hovering the last character of a name would otherwise find the
    /// enclosing statement.</para>
    /// </summary>
    private static bool Covers(Node node, FileId file, int offset) =>
        node.Span.File == file && offset >= node.Span.Start && offset <= node.Span.End;
}
