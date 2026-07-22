using Lyric.Core;

namespace Lyric.AST;

// Statements aus Sprache.md §5. Jeder Knoten trägt seinen Span (Union von Keyword
// bis abschließendem Token). Kontroll-Statements halten ihren Rumpf als `Block`,
// nie als loses Statement — das entspricht der Grammatik ('if' '(' Expr ')' Block …).
//
// Bewusst NICHT hier: MatchStmt — braucht Patterns (§6.3) und kommt in Slice 4.

public abstract record Stmt(Span Span) : Node(Span);

public sealed record Block(Stmt[] Statements, Span Span) : Stmt(Span);

// let (immutable) / var (mutable); Type und Initializer je optional (§5, DAA-Regeln in Sema).
public sealed record BindingStmt(bool IsMutable, string Name, TypeNode? Type, Expr? Initializer, Span Span) : Stmt(Span);

// Else ist Block, IfStmt (else-if) oder null.
public sealed record IfStmt(Expr Condition, Block Then, Stmt? Else, Span Span) : Stmt(Span);

public sealed record WhileStmt(Expr Condition, Block Body, Span Span) : Stmt(Span);
public sealed record DoWhileStmt(Block Body, Expr Condition, Span Span) : Stmt(Span);
public sealed record ForInStmt(string Variable, Expr Iterable, Block Body, Span Span) : Stmt(Span);

public sealed record BreakStmt(Span Span) : Stmt(Span);
public sealed record ContinueStmt(Span Span) : Stmt(Span);
public sealed record ReturnStmt(Expr? Value, Span Span) : Stmt(Span);
public sealed record YieldStmt(Expr? Value, Span Span) : Stmt(Span);
public sealed record ResumeStmt(Expr Coroutine, Expr? Value, Span Span) : Stmt(Span);

// Body ist Block oder ExprStmt (Sprache.md §5: 'defer' ( Block | Expr ';' )).
public sealed record DeferStmt(Stmt Body, Span Span) : Stmt(Span);

public sealed record ThrowStmt(Expr Value, Span Span) : Stmt(Span);

public sealed record MatchStmt(Expr Scrutinee, MatchArm[] Arms, Span Span) : Stmt(Span);

public sealed record TryStmt(Block Body, CatchClause[] Catches, Span Span) : Stmt(Span);
// BindingName == null  => '_' (catch-all ohne Binding)
// BindingType == null  => catch-all mit Binding (Throwable); sonst typed catch.
public sealed record CatchClause(string? BindingName, TypeNode? BindingType, Block Body, Span Span) : Node(Span);

// Nur Call/Assign sind semantisch gültig (Sprache.md §5) — vom Parser generisch
// geparst, von der Sema eingeschränkt.
public sealed record ExprStmt(Expr Expr, Span Span) : Stmt(Span);

public sealed record ErrorStmt(Span Span) : Stmt(Span); // Recovery-Platzhalter
