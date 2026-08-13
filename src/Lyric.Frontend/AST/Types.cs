using Lyric.Core;

namespace Lyric.AST;

// Type expressions.
// Built-in type names have no node of their own: the lexer tokenizes them as identifiers and they
// are represented as a NamedType with a single-element path. Whether a name is a built-in or a
// user type is decided by the sema.
public abstract record TypeNode(Span Span) : Node(Span);

public sealed record NullableType(TypeNode Inner, Span Span) : TypeNode(Span);                         // ?T
public sealed record NamedType(string[] Path, TypeNode[] TypeArguments, Span Span) : TypeNode(Span);   // a.b.C<...>
public sealed record ArrayType(TypeNode Element, IntLiteralExpr? Size, Span Span) : TypeNode(Span);    // T[] and T[N]; the parser requires an integer literal as the size
public sealed record TupleType(TypeNode[] Elements, Span Span) : TypeNode(Span);                       // (A, B[, C])
public sealed record FunctionType(TypeNode[] Parameters, TypeNode ReturnType, Span Span) : TypeNode(Span); // fn(A, B) -> R

// Recovery placeholder, set when ParseType cannot continue, so later stages do not meet a null.
// The counterpart of ErrorExpr.
public sealed record ErrorType(Span Span) : TypeNode(Span);
