using Lyric.AST;
using Lyric.Core;

namespace Lyric.Parsing;

/// <summary>
/// A parsed file: its tree, and the documentation written in it.
///
/// <para>The two come out together because they are produced together and the second one cannot be
/// recovered from the first. <see cref="Parser"/> keeps doc blocks out of the token stream and in a
/// table of its own, so the table dies with the parser instance — every caller that wants the
/// documentation has to take it here, at the one moment it exists.</para>
/// </summary>
public sealed record ParsedModule(Module Ast, DocumentationTable Documentation)
{
    /// <summary>
    /// Parses a file and binds its doc blocks to the declarations they stand above.
    ///
    /// <para>The bind is a walk rather than a lookup per interested consumer:
    /// <see cref="Parser.DocOf"/> answers by source offset, and turning that into node identity has
    /// to happen while the parser is still in hand.</para>
    /// </summary>
    public static ParsedModule Parse(SourceManager sources, FileId file, DiagnosticEngine diagnostics)
    {
        var parser = new Parser(sources, file, diagnostics);
        var ast = parser.ParseModule();
        var docs = new DocumentationTable();

        // The module header, which documents the file as a whole rather than anything in it.
        if (ast.Header is { } header && parser.DocOf(header) is { } moduleDoc)
            docs.Record(header, moduleDoc);

        // Everything that declares a name can carry a block. The module node itself is left out on
        // purpose: its span starts at the first token of the file, which in a file without a header
        // is the first declaration — it would inherit that declaration's documentation.
        foreach (var node in Walk(ast))
        {
            if (node is not INamedDecl) continue;
            if (parser.DocOf(node) is { } text) docs.Record(node, text);
        }

        return new ParsedModule(ast, docs);
    }

    private static IEnumerable<Node> Walk(Node root)
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            foreach (var child in AstChildren.Of(node)) stack.Push(child);
        }
    }
}
