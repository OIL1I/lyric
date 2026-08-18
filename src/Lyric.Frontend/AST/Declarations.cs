using Lyric.Core;

namespace Lyric.AST;

// Module structure and declarations. Enum keeps variants and methods apart, because they are
// structurally different.

public sealed record Module(ModulePath? Header, Decl[] Declarations, Span Span) : Node(Span)
{
    /// <summary>Attributes written before the module header. A file without a header cannot carry
    /// them: at the top of such a file an attribute belongs to the first declaration.</summary>
    public AttributeNode[] Attributes { get; init; } = [];
}
public sealed record ModulePath(string[] Segments, Span Span) : Node(Span);

/// <summary>
/// An attribute before a declaration or the module header: <c>@Name</c> or
/// <c>@Name { field = literal, … }</c>.
///
/// <para>The path names a struct type and the fields reuse the initializer shape, because the
/// checking is the same — an attribute IS a struct, and where it may sit is the marker interface
/// it declares (<c>OnModule</c>, <c>OnType</c>, <c>OnFunction</c>).</para>
/// </summary>
public sealed record AttributeNode(string[] Path, StructInitField[] Fields, Span Span) : Node(Span)
{
    /// <summary>The span of the <c>@Name</c> path alone, without the argument block.</summary>
    public required Span PathSpan { get; init; }

    /// <summary>The LAST path segment without the <c>@</c> — the name of the struct the attribute
    /// refers to, which is what an editor renaming that struct has to edit.</summary>
    public required Span NameSpan { get; init; }
}

public abstract record Decl(Span Span) : Node(Span);

// --- imports ---
public sealed record ImportDecl(string[] Path, ImportClause? Clause, Span Span) : Decl(Span);
public abstract record ImportClause(Span Span) : Node(Span);
/// <remarks><c>import a.b { x, y }</c>. <see cref="NameSpans"/> is parallel to
/// <see cref="Names"/>: the clause is the one place an imported name stands that no use-site table
/// records, and an editor renaming the target has to edit exactly it.</remarks>
public sealed record ImportSelective(string[] Names, Span Span) : ImportClause(Span)
{
    public required Span[] NameSpans { get; init; }
}
public sealed record ImportAlias(string Alias, Span Span) : ImportClause(Span);       // import a.b as C

// --- generics ---
public sealed record GenericParam(string Name, TypeNode[] Constraints, Span Span) : Node(Span), INamedDecl // T, or T :: [I1, I2]
{
    public required Span NameSpan { get; init; }
}

// --- functions and members ---
public sealed record Param(bool IsParams, string Name, TypeNode Type, Expr? Default, Span Span) : Node(Span), INamedDecl
{
    public required Span NameSpan { get; init; }
}
public sealed record ThrowsClause(TypeNode? Type, Span Span) : Node(Span); // Type == null means 'throws' without a type: any Throwable

/// <param name="IsStatic">A member without a receiver: no <c>this</c>, reachable only through
/// the type. Always <c>false</c> at top level.</param>
public sealed record FunctionDecl(
    bool IsPublic, bool IsMut, bool IsStatic, string Name, GenericParam[] Generics, Param[] Parameters,
    TypeNode? ReturnType, ThrowsClause? Throws, Block? Body, Span Span) : Decl(Span), INamedDecl // Body == null means abstract or declared with ';'
{
    public required Span NameSpan { get; init; }

    /// <summary>Only set on a TOP-LEVEL function: the parser rejects attributes on members.</summary>
    public AttributeNode[] Attributes { get; init; } = [];
}

/// <summary>A <c>static let</c> constant in the body of a struct or class, reachable as
/// <c>Type.NAME</c>; syntactically the same binding as a module <c>let</c>.</summary>
/// <remarks>The name is the wrapped binding's. A symbol declares from THIS node rather than from the
/// binding inside it, so the two spans have to be reachable from here as well.</remarks>
public sealed record StaticBindingDecl(bool IsPublic, BindingStmt Binding, Span Span) : Decl(Span), INamedDecl
{
    public string Name => Binding.Name;

    public Span NameSpan => Binding.NameSpan;
}

public sealed record FieldDecl(string Name, TypeNode Type, Expr? Default, Span Span) : Decl(Span), INamedDecl
{
    public required Span NameSpan { get; init; }
}

// --- type declarations ---
public sealed record StructDecl(bool IsPublic, string Name, GenericParam[] Generics, TypeNode[] Interfaces, Decl[] Members, Span Span) : Decl(Span), INamedDecl
{
    public required Span NameSpan { get; init; }

    public AttributeNode[] Attributes { get; init; } = [];
}

public sealed record ClassDecl(bool IsPublic, string Name, GenericParam[] Generics, TypeNode[] Interfaces, Decl[] Members, Span Span) : Decl(Span), INamedDecl
{
    public required Span NameSpan { get; init; }

    public AttributeNode[] Attributes { get; init; } = [];
}

public sealed record EnumDecl(bool IsPublic, string Name, GenericParam[] Generics, TypeNode[] Interfaces, EnumVariant[] Variants, FunctionDecl[] Methods, Span Span) : Decl(Span), INamedDecl
{
    public required Span NameSpan { get; init; }

    public AttributeNode[] Attributes { get; init; } = [];
}

public sealed record EnumVariant(string Name, TypeNode[]? TupleFields, FieldDecl[]? StructFields, Span Span) : Node(Span), INamedDecl // both null means a unit variant
{
    public required Span NameSpan { get; init; }
}

public sealed record InterfaceDecl(bool IsPublic, string Name, GenericParam[] Generics, FunctionDecl[] Members, Span Span) : Decl(Span), INamedDecl
{
    public required Span NameSpan { get; init; }
}

public sealed record ExtendDecl(bool IsPublic, TypeNode Target, TypeNode[] Interfaces, FunctionDecl[] Methods, Span Span) : Decl(Span);

// --- global bindings and type aliases ---
/// <inheritdoc cref="StaticBindingDecl"/>
public sealed record GlobalBindingDecl(bool IsPublic, BindingStmt Binding, Span Span) : Decl(Span), INamedDecl // 'let' only, per the grammar
{
    public string Name => Binding.Name;

    public Span NameSpan => Binding.NameSpan;
}

public sealed record TypeAliasDecl(bool IsPublic, string Name, TypeNode Aliased, Span Span) : Decl(Span), INamedDecl
{
    public required Span NameSpan { get; init; }
}

public sealed record ErrorDecl(Span Span) : Decl(Span); // recovery placeholder
