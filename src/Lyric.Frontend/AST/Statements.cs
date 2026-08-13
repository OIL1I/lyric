using Lyric.Core;

namespace Lyric.AST;

// Statements. Every node carries its span. A block is required where the grammar demands one, so a
// branch is never a loose statement.

public abstract record Stmt(Span Span) : Node(Span);

public sealed record Block(Stmt[] Statements, Span Span) : Stmt(Span);

// let (immutable) / var (mutable); type and initializer each optional.
public sealed record BindingStmt(bool IsMutable, string Name, TypeNode? Type, Expr? Initializer, Span Span) : Stmt(Span);

/// <summary>
/// <c>let (a, b) = paar;</c> — bindet mehrere Namen aus einem Tupel (Sprache.md §4).
///
/// <para>Its own statement rather than a variant of <see cref="BindingStmt"/>: there one name
/// stands, here several, and the initializer is required. The difference in type makes the two
/// impossible to confuse.</para>
///
/// <para>There is no element access (<c>t.0</c>); this is the way to the elements.</para>
/// </summary>
public sealed record DestructuringStmt(bool IsMutable, TuplePattern Pattern, TypeNode? Type,
    Expr Initializer, Span Span) : Stmt(Span);

// Else is a block, an IfStmt (else-if) or null.
public sealed record IfStmt(Expr Condition, Block Then, Stmt? Else, Span Span) : Stmt(Span);

public sealed record WhileStmt(Expr Condition, Block Body, Span Span) : Stmt(Span);
public sealed record DoWhileStmt(Block Body, Expr Condition, Span Span) : Stmt(Span);
public sealed record ForInStmt(string Variable, Expr Iterable, Block Body, Span Span) : Stmt(Span);

public sealed record BreakStmt(Span Span) : Stmt(Span);
public sealed record ContinueStmt(Span Span) : Stmt(Span);
public sealed record ReturnStmt(Expr? Value, Span Span) : Stmt(Span);
public sealed record YieldStmt(Expr? Value, Span Span) : Stmt(Span);
// resume is an EXPRESSION (ResumeExpr in Expressions.cs); as a statement it runs
// 'resume co;' über ExprStmt. Send-Werte ('resume co, v') sind post-v1 (D7).

// The body is a block or an ExprStmt.
public sealed record DeferStmt(Stmt Body, Span Span) : Stmt(Span);

public sealed record ThrowStmt(Expr Value, Span Span) : Stmt(Span);

public sealed record MatchStmt(Expr Scrutinee, MatchArm[] Arms, Span Span) : Stmt(Span);

public sealed record TryStmt(Block Body, CatchClause[] Catches, Span Span) : Stmt(Span);
// BindingName == null  => '_' (catch-all ohne Binding)
// BindingType == null: catch-all with a binding (Throwable); otherwise a typed catch.
public sealed record CatchClause(string? BindingName, TypeNode? BindingType, Block Body, Span Span) : Node(Span);

// Nur Call/Assign sind semantisch gültig (Sprache.md §5) — vom Parser generisch
// parsed here, restricted by the sema.
public sealed record ExprStmt(Expr Expr, Span Span) : Stmt(Span);

public sealed record ErrorStmt(Span Span) : Stmt(Span); // Recovery-Platzhalter
