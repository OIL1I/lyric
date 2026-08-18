using Lyric.AST;
using Lyric.Core;
using Lyric.Lsp.Protocol;

namespace Lyric.Lsp.Analysis;

/// <summary>
/// What an editor may collapse: every multi-line declaration, block and match.
///
/// <para>Syntax alone, off the last good tree — folding is read while the file is broken, and
/// ranges that empty out on a type error would snap every region open exactly when someone is
/// mid-edit. The same argument as the outline's, and the same walk kind.</para>
///
/// <para>The last line stays visible: a fold that swallows the closing brace leaves the reader
/// with no cue that anything is folded at all.</para>
/// </summary>
public static class FoldingProvider
{
    public static IReadOnlyList<FoldingRange> Of(Module root, FileId file, SourceManager sources)
    {
        var ranges = new List<FoldingRange>();
        var startLines = new HashSet<int>();

        Walk(root);
        return ranges;

        void Walk(Node node)
        {
            if (Foldable(node) && node.Span.File == file)
            {
                var range = SpanMapper.ToRange(sources, node.Span);

                // Minus one keeps the closing line visible. A one-line node has nothing to hide.
                var end = range.End.Line - 1;

                // One range per start line: 'fn f() {' is the declaration AND its body block, and
                // two folds on one line render as a doubled control. The outer node walks first
                // and wins.
                if (end > range.Start.Line && startLines.Add(range.Start.Line))
                    ranges.Add(new FoldingRange { StartLine = range.Start.Line, EndLine = end });
            }

            foreach (var child in AstChildren.Of(node)) Walk(child);
        }
    }

    /// <summary>The forms worth collapsing. An expression is not one: folding a subexpression
    /// leaves text that reads as a different program.</summary>
    private static bool Foldable(Node node) => node is
        StructDecl or ClassDecl or EnumDecl or InterfaceDecl or ExtendDecl or FunctionDecl
        or Block or MatchStmt or MatchExpr;
}
