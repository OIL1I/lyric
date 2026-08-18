using Lyric.AST;
using Lyric.Core;

namespace Lyric.Lsp.Analysis;

/// <summary>
/// The span of the NAME inside a node that refers to something by name.
///
/// <para>A node's own span covers its whole form — <c>p.add</c>, <c>geo.Vec2&lt;int&gt;</c>,
/// <c>Point { x = 1 }</c> — and two features need less than that: references underline the name,
/// a rename edits exactly it. One switch, because two copies of "where does the name stand in
/// this form" would disagree the first time a form is added.</para>
///
/// <para><c>null</c> has two readings the callers keep apart. A <see cref="MemberExpr"/> or
/// <see cref="NamedType"/> with an INVALID recorded span is a node the compiler synthesized — the
/// name stands in no text, so there is nothing to point at and nothing to edit. Any other form is
/// one this switch does not know, and a consumer that would EDIT must refuse rather than guess.
/// </para>
/// </summary>
internal static class NameSpans
{
    public static Span? Of(Node node) => node switch
    {
        // Declarations record their name span themselves; the interface is the contract.
        INamedDecl named => named.NameSpan,

        // An identifier IS its name.
        IdentifierExpr id => id.Span,

        MemberExpr member => Valid(member.MemberSpan),
        NamedType type => Valid(type.NameSpan),
        TypePathExpr path => Valid(path.NameSpan),
        StructInitExpr init => Valid(init.NameSpan),
        StructInitField field => Valid(field.NameSpan),
        AttributeNode attribute => Valid(attribute.NameSpan),

        _ => null,
    };

    private static Span? Valid(Span span) => span.File.IsValid ? span : null;
}
