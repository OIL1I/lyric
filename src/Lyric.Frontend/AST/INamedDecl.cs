using Lyric.Core;

namespace Lyric.AST;

/// <summary>
/// A node that introduces a name, together with the place the name is written.
///
/// <para>A declaration's own span covers everything it declares — a function includes its body, a
/// loop its statements — so it answers "how far does this reach" and not "where does this name
/// stand". The two are the same only for a declaration that fits its name exactly, which is none of
/// them. <see cref="NameSpan"/> is the second answer, and it is recorded where the name is read
/// rather than searched for afterwards: the parser holds the identifier token at that moment, and
/// looking for the text again later would find the wrong occurrence in
/// <c>fn area(area: int)</c>.</para>
///
/// <para><see cref="NameSpan"/> always lies within <see cref="Span"/>. Consumers rely on the
/// containment — the language server reports the pair as a range and the selection inside it, and
/// the protocol requires the inner one to be enclosed by the outer.</para>
///
/// <para>Implemented by every node whose name is a plain string field. A name that is a node of its
/// own — a binding or field pattern — carries its span already and needs nothing from here.</para>
/// </summary>
public interface INamedDecl
{
    /// <summary>The declared name.</summary>
    string Name { get; }

    /// <summary>Where <see cref="Name"/> is written. Empty and carrying an invalid
    /// <see cref="FileId"/> for a synthesised declaration that stands in no file.</summary>
    Span NameSpan { get; }

    /// <summary>Everything the declaration covers, its name included.</summary>
    Span Span { get; }
}
