using Lyric.Core;

namespace Lyric.AST;

// Expressions. Every node carries its own span, the union of its children's spans, so
// diagnostics and later stages point precisely at the source.

public enum IntSuffix
{
    I8, I16, I32, I64, U8, U16, U32, U64
}

public enum FloatSuffix
{
    F32, F64
}

public abstract record Expr(Span Span) : Node(Span);

// --- literals ---
public sealed record IntLiteralExpr(ulong Value, IntSuffix? Suffix, Span Span) : Expr(Span);
public sealed record FloatLiteralExpr(double Value, FloatSuffix? Suffix, Span Span) : Expr(Span);
public sealed record StringLiteralExpr(string Value, Span Span) : Expr(Span);
public sealed record CharLiteralExpr(int CodePoint, Span Span) : Expr(Span);
public sealed record BoolLiteralExpr(bool Value, Span Span) : Expr(Span);
public sealed record NullLiteralExpr(Span Span) : Expr(Span);

// --- names ---
public sealed record IdentifierExpr(string Name, Span Span) : Expr(Span);
public sealed record AtIdentifierExpr(string Name, Expr[]? Arguments, Span Span) : Expr(Span); // Name INCLUDING the leading '@' (for example "@test"); Arguments == null when none were written
public sealed record ThisExpr(Span Span) : Expr(Span);

// --- operators ---
public sealed record UnaryExpr(UnaryOp Operator, Expr Operand, Span Span) : Expr(Span);
// 'resume co': a prefix expression at the unary level, yielding the value of the coroutine's next
// yield. Send values do not exist.
public sealed record ResumeExpr(Expr Coroutine, Span Span) : Expr(Span);
public sealed record PostfixExpr(Expr Operand, PostfixOp Operator, Span Span) : Expr(Span);
public sealed record BinaryExpr(Expr Left, BinaryOp Operator, Expr Right, Span Span) : Expr(Span);
public sealed record AssignExpr(Expr Target, BinaryOp? Operator, Expr Value, Span Span) : Expr(Span); // Operator == null means '='; otherwise a compound assignment
public sealed record RangeExpr(Expr Low, Expr High, bool IsInclusive, Span Span) : Expr(Span);
public sealed record CastExpr(Expr Operand, TypeNode Type, Span Span) : Expr(Span);

// --- nodes produced by postfix ---
/// <param name="TypeArguments">Explicitly written type arguments: <c>f&lt;int&gt;()</c>. Empty
/// when none were written; the sema then infers them from the arguments. They are needed where
/// the arguments give nothing: a factory <c>empty&lt;T&gt;(): List&lt;T&gt;</c> has none.</param>
public sealed record CallExpr(Expr Callee, Expr[] Arguments, Span Span,
    TypeNode[]? TypeArguments = null) : Expr(Span);
public sealed record IndexExpr(Expr Target, Expr Index, Span Span) : Expr(Span);
public sealed record MemberExpr(Expr Target, string Member, bool IsOptional, Span Span) : Expr(Span); // IsOptional means '?.' rather than '.'

// --- composite literals ---
public sealed record ArrayLitExpr(Expr[] Elements, Span Span) : Expr(Span);
public sealed record TupleLitExpr(Expr[] Elements, Span Span) : Expr(Span);

// --- f-strings ---
public sealed record InterpolatedStringExpr(InterpSegment[] Segments, Span Span) : Expr(Span);
public abstract record InterpSegment(Span Span) : Node(Span);
public sealed record InterpText(string Text, Span Span) : InterpSegment(Span);                     // raw text, escapes NOT resolved
public sealed record InterpHole(Expr Expr, string? FormatSpec, Span Span) : InterpSegment(Span);   // {expr} and {expr:spec}

// --- lambdas ---
public sealed record LambdaExpr(LambdaParam[] Parameters, TypeNode? ReturnType, Node Body, Span Span) : Expr(Span); // Body is an Expr or a Block
public sealed record LambdaParam(string Name, TypeNode? Type, Span Span) : Node(Span), INamedDecl
{
    public required Span NameSpan { get; init; }
}

// --- control flow as an expression ---
// IfExpr branches are EXPRESSIONS rather than blocks, so a value is guaranteed. For statement blocks
// there is IfStmt. The else is mandatory; 'else if' is a nested IfExpr.
public sealed record IfExpr(Expr Condition, Expr Then, Expr Else, Span Span) : Expr(Span);
public sealed record MatchExpr(Expr Scrutinee, MatchArm[] Arms, Span Span) : Expr(Span);

// --- struct initializers: TypePath '{' field = expr, … '}' ---
// Recognised in value position only, not at the start of an ExprStmt, where it would be ambiguous
// with a block. The field separator is '='; ':' is reserved for types.
public sealed record StructInitExpr(string[] Path, TypeNode[] TypeArguments, StructInitField[] Fields, Span Span) : Expr(Span);

// --- a type path in value position: 'Pair<int>.of(3)' ---
//
// The non-generic case does not need this node: 'P.neu()' is an IdentifierExpr whose symbol is a
// type, and CheckMember works through the symbol anyway. Only type arguments carry something an
// identifier cannot express.
//
// It always stands as the target of a MemberExpr; alone it is a type rather than a value, and
// CheckExpr reports it there as LYR-SEM0052, like any other type name.
public sealed record TypePathExpr(string[] Path, TypeNode[] TypeArguments, Span Span) : Expr(Span);
public sealed record StructInitField(string Name, Expr Value, Span Span) : Node(Span);

// --- recovery ---
public sealed record ErrorExpr(Span Span) : Expr(Span);
