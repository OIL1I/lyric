using Lyric.Core;

namespace Lyric.AST;

// Statements. Every node carries its span. A block is required where the grammar demands one, so a
// branch is never a loose statement.

public abstract record Stmt(Span Span) : Node(Span);

public sealed record Block(Stmt[] Statements, Span Span) : Stmt(Span);

// let (immutable) / var (mutable); type and initializer each optional.
public sealed record BindingStmt(bool IsMutable, string Name, TypeNode? Type, Expr? Initializer, Span Span) : Stmt(Span), INamedDecl
{
    public required Span NameSpan { get; init; }
}

/// <summary>
/// <c>let (a, b) = pair;</c> — binds several names from a tuple.
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
/// <remarks>The loop variable is a declaration of its own; <see cref="INamedDecl.Name"/> is
/// implemented explicitly so the node keeps calling it what it is.</remarks>
public sealed record ForInStmt(string Variable, Expr Iterable, Block Body, Span Span) : Stmt(Span), INamedDecl
{
    public required Span NameSpan { get; init; }

    string INamedDecl.Name => Variable;
}

public sealed record BreakStmt(Span Span) : Stmt(Span);
public sealed record ContinueStmt(Span Span) : Stmt(Span);
public sealed record ReturnStmt(Expr? Value, Span Span) : Stmt(Span);
public sealed record YieldStmt(Expr? Value, Span Span) : Stmt(Span);
// resume is an EXPRESSION (ResumeExpr in Expressions.cs); as a statement 'resume co;' runs through
// ExprStmt. Send values ('resume co, v') are post-v1.

// The body is a block or an ExprStmt.
public sealed record DeferStmt(Stmt Body, Span Span) : Stmt(Span);

public sealed record ThrowStmt(Expr Value, Span Span) : Stmt(Span);

public sealed record MatchStmt(Expr Scrutinee, MatchArm[] Arms, Span Span) : Stmt(Span);

public sealed record TryStmt(Block Body, CatchClause[] Catches, Span Span) : Stmt(Span);

// BindingName == null means '_', a catch-all without a binding
// BindingType == null: catch-all with a binding (Throwable); otherwise a typed catch.
/// <remarks>
/// The grammar gives a catch binding exactly one token, <c>_</c> included, so
/// <see cref="INamedDecl.NameSpan"/> covers it in either form and <see cref="INamedDecl.Name"/>
/// reports the text that stands there.
///
/// <para>A <c>_</c> binds nothing and the sema creates no symbol for it, so no symbol's declaration
/// ever points at a clause in that form.</para>
/// </remarks>
public sealed record CatchClause(string? BindingName, TypeNode? BindingType, Block Body, Span Span) : Node(Span), INamedDecl
{
    public required Span NameSpan { get; init; }

    string INamedDecl.Name => BindingName ?? "_";
}

// Only calls and assignments are semantically valid; the parser accepts expressions generically here
// and the sema restricts them.
public sealed record ExprStmt(Expr Expr, Span Span) : Stmt(Span);

public sealed record ErrorStmt(Span Span) : Stmt(Span); // recovery placeholder
