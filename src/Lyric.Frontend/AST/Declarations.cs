using Lyric.Core;

namespace Lyric.AST;

// Module structure and declarations. Enum keeps variants and methods apart, because they are
// structurally different.

public sealed record Module(ModulePath? Header, Decl[] Declarations, Span Span) : Node(Span);
public sealed record ModulePath(string[] Segments, Span Span) : Node(Span);

public abstract record Decl(Span Span) : Node(Span);

// --- Imports (§2.2) ---
public sealed record ImportDecl(string[] Path, ImportClause? Clause, Span Span) : Decl(Span);
public abstract record ImportClause(Span Span) : Node(Span);
public sealed record ImportSelective(string[] Names, Span Span) : ImportClause(Span); // import a.b { x, y }
public sealed record ImportAlias(string Alias, Span Span) : ImportClause(Span);       // import a.b as C

// --- Generics (§3.1) ---
public sealed record GenericParam(string Name, TypeNode[] Constraints, Span Span) : Node(Span); // T oder T :: [I1, I2]

// --- Funktionen & Member (§3.1) ---
public sealed record Param(bool IsParams, string Name, TypeNode Type, Expr? Default, Span Span) : Node(Span);
public sealed record ThrowsClause(TypeNode? Type, Span Span) : Node(Span); // Type == null => 'throws' ohne Typ (any Throwable)

/// <param name="IsStatic">A member without a receiver: no <c>this</c>, reachable only through
/// the type. Always <c>false</c> at top level.
/// könnte.</param>
public sealed record FunctionDecl(
    bool IsPublic, bool IsMut, bool IsStatic, string Name, GenericParam[] Generics, Param[] Parameters,
    TypeNode? ReturnType, ThrowsClause? Throws, Block? Body, Span Span) : Decl(Span); // Body == null => abstrakt/deklariert (';')

/// <summary>Eine <c>static let</c>-Konstante im Rumpf eines struct/class (ADR-014). Erreichbar als
/// <c>Typ.NAME</c>; syntaktisch dasselbe Binding wie ein Modul-<c>let</c>.</summary>
public sealed record StaticBindingDecl(bool IsPublic, BindingStmt Binding, Span Span) : Decl(Span);

public sealed record FieldDecl(string Name, TypeNode Type, Expr? Default, Span Span) : Decl(Span);

// --- Typdeklarationen (§3.2–§3.6) ---
public sealed record StructDecl(bool IsPublic, string Name, GenericParam[] Generics, TypeNode[] Interfaces, Decl[] Members, Span Span) : Decl(Span);
public sealed record ClassDecl(bool IsPublic, string Name, GenericParam[] Generics, TypeNode[] Interfaces, Decl[] Members, Span Span) : Decl(Span);
public sealed record EnumDecl(bool IsPublic, string Name, GenericParam[] Generics, TypeNode[] Interfaces, EnumVariant[] Variants, FunctionDecl[] Methods, Span Span) : Decl(Span);
public sealed record EnumVariant(string Name, TypeNode[]? TupleFields, FieldDecl[]? StructFields, Span Span) : Node(Span); // beide null => Unit-Variante
public sealed record InterfaceDecl(bool IsPublic, string Name, GenericParam[] Generics, FunctionDecl[] Members, Span Span) : Decl(Span);
public sealed record ExtendDecl(bool IsPublic, TypeNode Target, TypeNode[] Interfaces, FunctionDecl[] Methods, Span Span) : Decl(Span);

// --- Global binding & type-alias (§2.3) ---
public sealed record GlobalBindingDecl(bool IsPublic, BindingStmt Binding, Span Span) : Decl(Span); // nur 'let' laut Grammatik
public sealed record TypeAliasDecl(bool IsPublic, string Name, TypeNode Aliased, Span Span) : Decl(Span);

public sealed record ErrorDecl(Span Span) : Decl(Span); // Recovery-Platzhalter
