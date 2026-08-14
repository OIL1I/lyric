namespace Lyric.DocGen.Model;

/// <summary>What kind of thing an item is. Drives the grouping and the heading on a page.</summary>
public enum ItemKind
{
    Function,
    Struct,
    Class,
    Enum,
    Interface,
    Extend,
    Binding,
    Alias,
    Variant,
    Field,
    Method,
}

/// <summary>Where an item stands, for a link back into the source.</summary>
/// <param name="File">Repository-relative, with forward slashes on every platform.</param>
/// <param name="Line">1-based.</param>
public sealed record SourceRef(string File, int Line);

/// <summary>
/// One documented thing.
///
/// <para><see cref="Signature"/> is a rendered string rather than a structured type: it is built in
/// one place and displayed in several, and keeping it structured would mean writing the formatting
/// once per display.</para>
/// </summary>
/// <param name="Members">Fields, methods and variants of a type; empty for everything else.</param>
public sealed record DocItem(
    ItemKind Kind,
    string Name,
    string Signature,
    string? Doc,
    DocItem[] Members,
    SourceRef Source);

/// <param name="Path">The dotted module path, for example <c>std.io.file</c>.</param>
public sealed record DocModule(string Path, string? Doc, DocItem[] Items);

/// <summary>
/// The whole documented surface, independent of how it is displayed.
///
/// <para>Deliberately not the AST: the AST carries spans, statement bodies and defaults that no page
/// shows. This is also what the golden snapshots compare, so a change to the public surface appears
/// as a diff.</para>
/// </summary>
public sealed record DocModel(DocModule[] Modules);
