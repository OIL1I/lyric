using Lyric.Core;

namespace Lyric.AST;

// Module structure and declarations. Enum keeps variants and methods apart, because they are
// structurally different.

public sealed record Module(ModulePath? Header, Decl[] Declarations, Span Span) : Node(Span);
public sealed record ModulePath(string[] Segments, Span Span) : Node(Span);

public abstract record Decl(Span Span) : Node(Span);

// --- imports ---
public sealed record ImportDecl(string[] Path, ImportClause? Clause, Span Span) : Decl(Span);
public abstract record ImportClause(Span Span) : Node(Span);
public sealed record ImportSelective(string[] Names, Span Span) : ImportClause(Span); // import a.b { x, y }
public sealed record ImportAlias(string Alias, Span Span) : ImportClause(Span);       // import a.b as C

// --- generics ---
public sealed record GenericParam(string Name, TypeNode[] Constraints, Span Span) : Node(Span); // T, or T :: [I1, I2]

// --- functions and members ---
public sealed record Param(bool IsParams, string Name, TypeNode Type, Expr? Default, Span Span) : Node(Span);
public sealed record ThrowsClause(TypeNode? Type, Span Span) : Node(Span); // Type == null means 'throws' without a type: any Throwable

/// <param name="IsStatic">A member without a receiver: no <c>this</c>, reachable only through
/// the type. Always <c>false</c> at top level.</param>
public sealed record FunctionDecl(
    bool IsPublic, bool IsMut, bool IsStatic, string Name, GenericParam[] Generics, Param[] Parameters,
    TypeNode? ReturnType, ThrowsClause? Throws, Block? Body, Span Span) : Decl(Span); // Body == null means abstract or declared with ';'

/// <summary>A <c>static let</c> constant in the body of a struct or class, reachable as
/// <c>Type.NAME</c>; syntactically the same binding as a module <c>let</c>.</summary>
public sealed record StaticBindingDecl(bool IsPublic, BindingStmt Binding, Span Span) : Decl(Span);

public sealed record FieldDecl(string Name, TypeNode Type, Expr? Default, Span Span) : Decl(Span);

// --- type declarations ---
public sealed record StructDecl(bool IsPublic, string Name, GenericParam[] Generics, TypeNode[] Interfaces, Decl[] Members, Span Span) : Decl(Span);
public sealed record ClassDecl(bool IsPublic, string Name, GenericParam[] Generics, TypeNode[] Interfaces, Decl[] Members, Span Span) : Decl(Span);
public sealed record EnumDecl(bool IsPublic, string Name, GenericParam[] Generics, TypeNode[] Interfaces, EnumVariant[] Variants, FunctionDecl[] Methods, Span Span) : Decl(Span);
public sealed record EnumVariant(string Name, TypeNode[]? TupleFields, FieldDecl[]? StructFields, Span Span) : Node(Span); // both null means a unit variant
public sealed record InterfaceDecl(bool IsPublic, string Name, GenericParam[] Generics, FunctionDecl[] Members, Span Span) : Decl(Span);
public sealed record ExtendDecl(bool IsPublic, TypeNode Target, TypeNode[] Interfaces, FunctionDecl[] Methods, Span Span) : Decl(Span);

// --- global bindings and type aliases ---
public sealed record GlobalBindingDecl(bool IsPublic, BindingStmt Binding, Span Span) : Decl(Span); // 'let' only, per the grammar
public sealed record TypeAliasDecl(bool IsPublic, string Name, TypeNode Aliased, Span Span) : Decl(Span);

public sealed record ErrorDecl(Span Span) : Decl(Span); // recovery placeholder
