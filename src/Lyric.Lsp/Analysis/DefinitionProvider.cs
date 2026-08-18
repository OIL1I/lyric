using Lyric.AST;
using Lyric.Compiler;
using Lyric.Core;
using Lyric.Resolver;

namespace Lyric.Lsp.Analysis;

/// <summary>
/// Where a name was declared: the file it stands in, the span of the whole declaration, and the
/// span of the name inside it.
///
/// <para>Both spans, because the two answer different questions and the protocol asks for both:
/// what the jump reveals is the declaration, what it selects is the name.
/// <paramref name="NameSpan"/> lies within <paramref name="Span"/>.</para>
/// </summary>
public sealed record DefinitionTarget(FileId File, Span Span, Span NameSpan);

/// <summary>
/// The declaration a name refers to.
///
/// <para>Needs nothing the compiler did not already record. <see cref="Symbol.Declaration"/> holds
/// the AST node a symbol came from and every node carries its span, so the whole feature is a
/// lookup — which is why it follows hover rather than preceding it: hover built the search for the
/// node under a cursor, and this asks the same search a different question.</para>
///
/// <para>A target in ANOTHER file is the ordinary case, not an edge one: every call into the
/// standard library lands there. Those files are read from disk with their real paths, so the
/// span's file id names something an editor can open.</para>
/// </summary>
public static class DefinitionProvider
{
    /// <param name="root">The module AST of the file the cursor is in — see
    /// <see cref="HoverProvider.At"/> for why it is passed.</param>
    public static DefinitionTarget? At(SemanticModel model, Module root, FileId file, int offset)
    {
        var path = NodeFinder.PathAt(root, file, offset);

        // From the inside out, like hover: the innermost node is the most specific, and the nodes
        // above it are the fallback when it binds to nothing.
        for (var i = path.Count - 1; i >= 0; i--)
        {
            var symbol = model.Types.RefOf(path[i]) ?? model.Binding.Resolve(path[i]);
            if (symbol is null) continue;

            // Reached from inside rather than from its name: the cursor is on something this
            // declaration contains, not on the declaration. Answering with it would send the reader
            // somewhere they did not point at, which is worse than the null below.
            if (!NodeFinder.Answers(path[i], symbol, file, offset)) return null;

            if (Declaration(symbol) is { } target) return target;

            // A symbol was found and it has no declaration to jump to. Stopping here rather than
            // continuing outwards: the enclosing statement is not the definition of this name, and
            // offering it would send the reader somewhere they did not ask about.
            return null;
        }

        return null;
    }

    private static DefinitionTarget? Declaration(Symbol symbol) => symbol switch
    {
        // The name of an import stands for what it imports. Jumping to the import line would answer
        // "where did this name enter this file", which is a different question and one the reader
        // is already looking at.
        ImportBindingSymbol import => Declaration(import.Target),

        // Nothing to jump to, each for its own reason: a builtin has no declaration in any file, a
        // module outside the compilation was never parsed, and an unresolved name has no target by
        // definition — the diagnostic on the same span says so already.
        ExternalSymbol or ErrorSymbol => null,

        // A node that declares a name knows where the name stands. One that does not is its own
        // best answer: the span it covers is all there is to point at.
        { Declaration: INamedDecl named } when named.Span.File.IsValid =>
            new DefinitionTarget(named.Span.File, named.Span, named.NameSpan),

        { Declaration: { } node } when node.Span.File.IsValid =>
            new DefinitionTarget(node.Span.File, node.Span, node.Span),

        _ => null,
    };
}
