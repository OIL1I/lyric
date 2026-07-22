using Lyric.Core;

namespace Lyric.AST;

// Ausdrücke aus Sprache.md §6. Jeder Knoten trägt seinen eigenen Span (Union der
// Kind-Spans), damit Diagnostics und spätere Stufen präzise auf Quelltext zeigen.
//
// Bewusst NICHT hier (kommt in späteren M2-Slices mit den nötigen Bausteinen):
//   - IfExpr / MatchExpr : brauchen Block bzw. MatchArm (Slice 2 / Slice 4).
//   - StructInitExpr     : braucht ein StructInitField-Modell (Name '=' Expr) und
//                          die '{'-Block-vs-Struct-Init-Disambiguierung (Slice mit Decls).

public enum IntSuffix
{
    I8, I16, I32, I64, U8, U16, U32, U64
}

public enum FloatSuffix
{
    F32, F64
}

public abstract record Expr(Span Span) : Node(Span);

// --- Literale ---
public sealed record IntLiteralExpr(ulong Value, IntSuffix? Suffix, Span Span) : Expr(Span);
public sealed record FloatLiteralExpr(double Value, FloatSuffix? Suffix, Span Span) : Expr(Span);
public sealed record StringLiteralExpr(string Value, Span Span) : Expr(Span);
public sealed record CharLiteralExpr(int CodePoint, Span Span) : Expr(Span);
public sealed record BoolLiteralExpr(bool Value, Span Span) : Expr(Span);
public sealed record NullLiteralExpr(Span Span) : Expr(Span);

// --- Namen ---
public sealed record IdentifierExpr(string Name, Span Span) : Expr(Span);
public sealed record AtIdentifierExpr(string Name, Expr[]? Arguments, Span Span) : Expr(Span); // Name INKL. führendem '@' (z.B. "@test"); Arguments == null: '@name' ohne '()'
public sealed record ThisExpr(Span Span) : Expr(Span);

// --- Operatoren ---
public sealed record UnaryExpr(UnaryOp Operator, Expr Operand, Span Span) : Expr(Span);
public sealed record PostfixExpr(Expr Operand, PostfixOp Operator, Span Span) : Expr(Span);
public sealed record BinaryExpr(Expr Left, BinaryOp Operator, Expr Right, Span Span) : Expr(Span);
public sealed record AssignExpr(Expr Target, BinaryOp? Operator, Expr Value, Span Span) : Expr(Span); // Operator == null: '='; sonst compound (z.B. Add => '+=')
public sealed record RangeExpr(Expr Low, Expr High, bool IsInclusive, Span Span) : Expr(Span);
public sealed record CastExpr(Expr Operand, TypeNode Type, Span Span) : Expr(Span);

// --- Postfix-erzeugte Knoten ---
public sealed record CallExpr(Expr Callee, Expr[] Arguments, Span Span) : Expr(Span);
public sealed record IndexExpr(Expr Target, Expr Index, Span Span) : Expr(Span);
public sealed record MemberExpr(Expr Target, string Member, bool IsOptional, Span Span) : Expr(Span); // IsOptional: '?.' statt '.'

// --- Zusammengesetzte Literale ---
public sealed record ArrayLitExpr(Expr[] Elements, Span Span) : Expr(Span);
public sealed record TupleLitExpr(Expr[] Elements, Span Span) : Expr(Span);

// --- f-Strings ---
public sealed record InterpolatedStringExpr(InterpSegment[] Segments, Span Span) : Expr(Span);
public abstract record InterpSegment(Span Span) : Node(Span);
public sealed record InterpText(string Text, Span Span) : InterpSegment(Span);                     // Roh-Text, Escapes NICHT aufgelöst
public sealed record InterpHole(Expr Expr, string? FormatSpec, Span Span) : InterpSegment(Span);   // {expr} bzw. {expr:spec}

// --- Lambdas ---
public sealed record LambdaExpr(LambdaParam[] Parameters, TypeNode? ReturnType, Node Body, Span Span) : Expr(Span); // Body: Expr oder Block
public sealed record LambdaParam(string Name, TypeNode? Type, Span Span) : Node(Span);

// --- Control-flow als Ausdruck (§6.2) ---
// IfExpr-Branches sind AUSDRÜCKE (kein Block) → garantierter Wert. Für Statement-Blocks
// gibt es das IfStmt. Else ist Pflicht; 'else if' ist ein geschachteltes IfExpr.
public sealed record IfExpr(Expr Condition, Expr Then, Expr Else, Span Span) : Expr(Span);
public sealed record MatchExpr(Expr Scrutinee, MatchArm[] Arms, Span Span) : Expr(Span);

// --- Struct-Init (§6.2): TypePath '{' field = expr, … '}' ---
// Wird nur in Wert-Position erkannt, nicht am Anfang eines ExprStmt (sonst mehrdeutig
// mit einem Block). Feld-Trenner ist '=' (':' ist Typen vorbehalten).
public sealed record StructInitExpr(string[] Path, StructInitField[] Fields, Span Span) : Expr(Span);
public sealed record StructInitField(string Name, Expr Value, Span Span) : Node(Span);

// --- Recovery ---
public sealed record ErrorExpr(Span Span) : Expr(Span);
