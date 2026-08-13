using Lyric.Core;

namespace Lyric.AST;

// Patterns and match arms.
//
// Bind versus unit variant: a bare identifier (`x`, `Empty`) parses as a BindingPattern. Whether
// it is a binding or a unit variant is something the parser cannot know without a namespace; the
// sema decides. A qualified path (`Shape.Circle`) or a path with `(…)`/`{…}` is always a
// VariantPattern.

public abstract record Pattern(Span Span) : Node(Span);

public sealed record WildcardPattern(Span Span) : Pattern(Span);                        // _
public sealed record LiteralPattern(Expr Literal, Span Span) : Pattern(Span);           // 42, "x", true, null, 'c', -1
public sealed record BindingPattern(string Name, Span Span) : Pattern(Span);            // x: a binding OR a unit variant, decided by the sema
public sealed record VariantPattern(string[] Path, Pattern[]? TupleElements, FieldPattern[]? StructFields, Span Span) : Pattern(Span);
public sealed record TuplePattern(Pattern[] Elements, Span Span) : Pattern(Span);       // (a, b)
public sealed record RangePattern(Expr Low, Expr High, bool IsInclusive, Span Span) : Pattern(Span); // 0..=9
public sealed record OrPattern(Pattern[] Alternatives, Span Span) : Pattern(Span);      // a | b | c
public sealed record FieldPattern(string Name, Pattern? Pattern, Span Span) : Node(Span); // x, or x = Pattern
public sealed record ErrorPattern(Span Span) : Pattern(Span);

// MatchArm = Pattern [ 'if' Guard ] '=>' ( Expr | Block ).
public sealed record MatchArm(Pattern Pattern, Expr? Guard, Node Body, Span Span) : Node(Span);
