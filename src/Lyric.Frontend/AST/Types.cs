using Lyric.Core;

namespace Lyric.AST;

// Typ-Ausdrücke aus Sprache.md §4. Builtin-Typen (int, float, string, ...) haben
// KEINEN eigenen Knoten: sie werden vom Lexer als Identifier getokenized und hier
// als NamedType mit einelementigem Path repräsentiert. Ob ein Name ein Builtin oder
// ein benutzerdefinierter Typ ist, entscheidet erst die Sema.
public abstract record TypeNode(Span Span) : Node(Span);

public sealed record NullableType(TypeNode Inner, Span Span) : TypeNode(Span);                         // ?T
public sealed record NamedType(string[] Path, TypeNode[] TypeArguments, Span Span) : TypeNode(Span);   // a.b.C<...>
public sealed record ArrayType(TypeNode Element, IntLiteralExpr? Size, Span Span) : TypeNode(Span);    // T[] / T[N]; Parser erzwingt IntLit als Size
public sealed record TupleType(TypeNode[] Elements, Span Span) : TypeNode(Span);                       // (A, B[, C])
public sealed record FunctionType(TypeNode[] Parameters, TypeNode ReturnType, Span Span) : TypeNode(Span); // fn(A, B) -> R

// Recovery-Platzhalter: wird gesetzt, wenn ParseType nicht weiterkommt, damit
// nachgelagerte Stufen nicht auf null laufen (Symmetrie zu ErrorExpr).
public sealed record ErrorType(Span Span) : TypeNode(Span);
