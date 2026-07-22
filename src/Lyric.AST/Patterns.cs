using Lyric.Core;

namespace Lyric.AST;

// Patterns (Sprache.md §6.3) und Match-Arme.
//
// Bind-vs-Unit-Variante: Ein einzelner nackter Identifier (`x`, `Empty`) wird als
// BindingPattern geparst. Ob er eine Bindung oder eine Unit-Variante ist, kann der
// Parser nicht wissen (kein Namensraum) — das entscheidet die Sema (wie in Rust).
// Ein qualifizierter Pfad (`Shape.Circle`) ODER ein Pfad mit `(…)`/`{…}` ist immer
// eine VariantPattern.

public abstract record Pattern(Span Span) : Node(Span);

public sealed record WildcardPattern(Span Span) : Pattern(Span);                        // _
public sealed record LiteralPattern(Expr Literal, Span Span) : Pattern(Span);           // 42, "x", true, null, 'c', -1
public sealed record BindingPattern(string Name, Span Span) : Pattern(Span);            // x  (Bindung ODER Unit-Variante → Sema)
public sealed record VariantPattern(string[] Path, Pattern[]? TupleElements, FieldPattern[]? StructFields, Span Span) : Pattern(Span);
public sealed record TuplePattern(Pattern[] Elements, Span Span) : Pattern(Span);       // (a, b)
public sealed record RangePattern(Expr Low, Expr High, bool IsInclusive, Span Span) : Pattern(Span); // 0..=9
public sealed record OrPattern(Pattern[] Alternatives, Span Span) : Pattern(Span);      // a | b | c
public sealed record FieldPattern(string Name, Pattern? Pattern, Span Span) : Node(Span); // x  ODER  x = Pattern
public sealed record ErrorPattern(Span Span) : Pattern(Span);

// MatchArm = Pattern [ 'if' Guard ] '=>' ( Expr | Block ). Body ist Expr oder Block.
public sealed record MatchArm(Pattern Pattern, Expr? Guard, Node Body, Span Span) : Node(Span);
