using Lyric.AST;
using Lyric.Compiler;
using Lyric.Core;
using Lyric.Resolver;

namespace Lyric.Lsp.Analysis;

/// <summary>Where a name occurs: the file it stands in and the span of the occurrence.</summary>
public sealed record ReferenceSite(FileId File, Span Span);

/// <summary>
/// Every place a symbol is used.
///
/// <para>The compiler's tables answer "what does this node mean"; this asks the reverse, and the
/// reverse has no table. It is built by walking both forward tables once per request — measured at
/// roughly eight hundred and three thousand entries for a program that pulls in eight standard
/// library modules, against seven to sixteen milliseconds for the compile that produced them. A
/// cached index would be state to invalidate in exchange for microseconds.</para>
///
/// <para>BOTH tables, because they answer for different nodes: the resolver binds names in type
/// position, the sema binds expressions. <c>let p: Point</c> stands in one and
/// <c>Point { x = 1 }</c> in the other, and either alone gives a list that is half right.</para>
///
/// <para>The sema's table holds declarations as well as uses — the definite-assignment analysis
/// binds a <c>BindingStmt</c>, a <c>Param</c> and the pattern bindings to the symbol they themselves
/// declare. They are separated by asking the symbol where it was declared, which is the same line
/// the protocol draws with <c>includeDeclaration</c>.</para>
/// </summary>
public static class ReferenceProvider
{
    /// <param name="root">The module AST of the file the cursor is in — see
    /// <see cref="HoverProvider.At"/> for why it is passed. Only the SEARCH for the symbol under
    /// the cursor needs it; the sites come from the whole compilation either way.</param>
    public static IReadOnlyList<ReferenceSite>? At(
        SemanticModel model, Module root, FileId file, int offset, bool includeDeclaration)
    {
        if (SymbolAt(model, root, file, offset) is not { } symbol) return null;

        // Reference equality throughout: symbols are identity objects, and two distinct locals of
        // the same name in different scopes must not collapse into one answer.
        var found = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        var sites = new List<ReferenceSite>();

        foreach (var (node, bound) in model.Types.AllReferences.Concat(model.Binding.All))
        {
            // Through the import binding as well as to the symbol itself. An imported name has TWO
            // symbols — the binding this file introduced and what it stands for — and every use in
            // this file is bound to the FIRST. Comparing against the target alone finds nothing;
            // comparing against the binding alone misses the module the name comes from.
            if (!ReferenceEquals(Target(bound), symbol)) continue;

            // The node that DECLARES the symbol is not a use of it. It is added below, and only
            // when the client asked for it.
            if (ReferenceEquals(symbol.Declaration, node)) continue;

            // A node can stand in both tables; the answer is a set of places, not of lookups.
            if (!found.Add(node)) continue;
            if (node.Span.File.IsValid) sites.Add(new ReferenceSite(node.Span.File, node.Span));
        }

        if (includeDeclaration && symbol.Declaration is { } declaration
            && declaration.Span.File.IsValid)
        {
            // The NAME of the declaration, not its whole extent — the same answer the jump gives,
            // and for the same reason.
            var span = declaration is INamedDecl named ? named.NameSpan : declaration.Span;
            sites.Add(new ReferenceSite(span.File, span));
        }

        return sites;
    }

    /// <summary>
    /// The symbol the cursor is on, whether it stands on a use or on the declaration itself.
    ///
    /// <para>The forward tables answer for uses and for the declarations the sema binds, which
    /// leaves the ones it does not: a function, a type or a field is never bound to itself. Asking
    /// for the references of a function while standing on its name is the ordinary gesture, so the
    /// module's own symbol tables are the fallback.</para>
    /// </summary>
    private static Symbol? SymbolAt(SemanticModel model, Module root, FileId file, int offset)
    {
        var path = NodeFinder.PathAt(root, file, offset);

        for (var i = path.Count - 1; i >= 0; i--)
            if ((model.Types.RefOf(path[i]) ?? model.Binding.Resolve(path[i])) is { } symbol)
                return NodeFinder.Answers(path[i], symbol, file, offset) ? Target(symbol) : null;

        // Standing on a declaration the tables do not carry. Only the name counts: the cursor
        // anywhere inside a twenty-line struct would otherwise mean its name.
        for (var i = path.Count - 1; i >= 0; i--)
            if (path[i] is INamedDecl named && named.NameSpan.File == file
                && offset >= named.NameSpan.Start && offset <= named.NameSpan.End
                && NodeFinder.DeclaredSymbol(model, path[i]) is { } symbol)
                return symbol;

        return null;
    }

    /// <summary>An imported name stands for what it imports, so the references asked for are the
    /// target's. The same redirection the jump and the hover make.</summary>
    private static Symbol Target(Symbol symbol) =>
        symbol is ImportBindingSymbol import ? Target(import.Target) : symbol;

}
