using Lyric.AST;
using Lyric.Compiler;
using Lyric.Core;
using Lyric.Lsp.Protocol;
using Lyric.Sema;

namespace Lyric.Lsp.Analysis;

/// <summary>
/// The type the inference gave a binding that names none: <c>let total = …</c> reads
/// <c>total: int</c> without anyone writing it.
///
/// <para>Only bindings. A hint on every call argument is a different feature with a different
/// noise budget, and it is deliberately not in this one — recorded in STATUS rather than
/// half-built here.</para>
///
/// <para>Off the last good analysis, like hover: a hint one edit behind is invisible, a hint
/// column that empties while the file does not parse flickers on every keystroke.</para>
/// </summary>
public static class InlayHintProvider
{
    public static IReadOnlyList<InlayHint> Of(
        Compiler.SemanticModel model, Module root, FileId file, SourceManager sources,
        Protocol.Range range)
    {
        var hints = new List<InlayHint>();

        Walk(root);
        return hints;

        void Walk(Node node)
        {
            switch (node)
            {
                // A written annotation already says it; a binding without an initializer has
                // nothing inferred to show.
                case BindingStmt { Type: null, Initializer: not null } binding:
                    Add(binding, binding.NameSpan);
                    break;

                // The loop variable never takes an annotation; the element type is always
                // inferred, which is exactly when a hint earns its place.
                case ForInStmt loop:
                    Add(loop, loop.NameSpan);
                    break;
            }

            foreach (var child in AstChildren.Of(node)) Walk(child);
        }

        void Add(Node declaration, Span nameSpan)
        {
            if (nameSpan.File != file) return;

            // The definite-assignment analysis bound the declaration to its own symbol; the
            // symbol carries the inferred type.
            if ((model.Types.RefOf(declaration)
                ?? model.Binding.Resolve(declaration)) is not LocalSymbol local) return;

            // ErrorType means "already reported here"; a hint would repeat the failure as though
            // it were an answer. The internal types are not nameable in the language.
            if (local.Type is Sema.ErrorType or NullType or NeverType) return;

            var position = SpanMapper.ToRange(sources, nameSpan).End;
            if (position.Line < range.Start.Line || position.Line > range.End.Line) return;

            hints.Add(new InlayHint
            {
                Position = position,
                Label = $": {TypeFacts.Display(local.Type)}",
                Kind = InlayHintKind.Type,
            });
        }
    }
}
