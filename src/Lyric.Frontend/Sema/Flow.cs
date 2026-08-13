using Lyric.AST;

namespace Lyric.Sema;

/// <summary>
/// Structured control-flow facts over the AST; no CFG is needed, because Lyric has no unstructured
/// jumps. Shared by return coverage and by narrowing on an early exit.
/// </summary>
internal static class Flow
{
    /// <summary>Does this statement leave the function on EVERY path, by return, throw or
    /// divergence? With <paramref name="types"/> a match exhaustiveness proven by the sema counts
    /// too; without it, match falls back to the syntactic '_' arm.</summary>
    public static bool AlwaysReturns(Stmt s, TypeResult? types = null) => s switch
    {
        ReturnStmt => true,
        ThrowStmt => true,
        ExprStmt es => types?.TypeOf(es.Expr) is NeverType, // panic(...) diverges
        Block b => b.Statements.Any(st => AlwaysReturns(st, types)),
        IfStmt f => f.Else is not null && AlwaysReturns(f.Then, types) && AlwaysReturns(f.Else, types),
        DoWhileStmt d => AlwaysReturns(d.Body, types) || Diverges(d.Condition, d.Body),
        WhileStmt w => Diverges(w.Condition, w.Body),
        ForInStmt => false, // the loop may not run at all
        TryStmt t => AlwaysReturns(t.Body, types) && t.Catches.All(c => AlwaysReturns(c.Body, types)),
        MatchStmt m => (types?.IsMatchExhaustive(m) == true || m.Arms.Any(a => a.Pattern is WildcardPattern))
                       && m.Arms.All(a => ArmReturns(a, types)),
        _ => false
    };

    /// <summary>
    /// Does control flow leave this block IN ANY CASE, no matter where to?
    ///
    /// <para>A DIFFERENT question from <see cref="AlwaysReturns"/>. For "a return is missing at the
    /// end of the function" a <c>continue</c> must not count as a return; for "is the code after the
    /// if reached" it must. One function for both would answer one of them wrongly.</para>
    ///
    /// <para>Used by flow narrowing: after <c>if (x == null) { continue; }</c> x is narrowed for the
    /// rest of the loop, exactly as after a <c>return</c>.</para>
    /// </summary>
    public static bool AlwaysExits(Stmt s, TypeResult? types = null) => s switch
    {
        BreakStmt => true,
        ContinueStmt => true,
        Block b => b.Statements.Any(st => AlwaysExits(st, types)),
        IfStmt f => f.Else is not null && AlwaysExits(f.Then, types) && AlwaysExits(f.Else, types),
        TryStmt t => AlwaysExits(t.Body, types) && t.Catches.All(c => AlwaysExits(c.Body, types)),
        MatchStmt m => (types?.IsMatchExhaustive(m) == true || m.Arms.Any(a => a.Pattern is WildcardPattern))
                       && m.Arms.All(a => a.Body is Block bl && AlwaysExits(bl, types)),

        // Anything that already satisfies 'AlwaysReturns' leaves the block too.
        _ => AlwaysReturns(s, types),
    };

    private static bool Diverges(Expr cond, Block body) => cond is BoolLiteralExpr { Value: true } && !HasBreak(body);

    private static bool ArmReturns(MatchArm a, TypeResult? types) => a.Body is Block b && AlwaysReturns(b, types);

    // A break that leaves THIS loop: do not descend into nested loops, whose break targets them.
    private static bool HasBreak(Stmt s) => s switch
    {
        BreakStmt => true,
        Block b => b.Statements.Any(HasBreak),
        IfStmt f => HasBreak(f.Then) || (f.Else is not null && HasBreak(f.Else)),
        TryStmt t => HasBreak(t.Body) || t.Catches.Any(c => HasBreak(c.Body)),
        MatchStmt m => m.Arms.Any(a => a.Body is Block bl && HasBreak(bl)),
        DeferStmt d => HasBreak(d.Body),
        _ => false // WhileStmt, DoWhileStmt, ForInStmt: do not descend
    };
}
