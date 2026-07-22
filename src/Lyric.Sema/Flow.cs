using Lyric.AST;

namespace Lyric.Sema;

/// <summary>
/// Strukturierte Kontrollfluss-Fakten über den AST (kein CFG nötig, da Lyric keine
/// unstrukturierten Sprünge hat). Geteilt von Return-Coverage und Narrowing (Early-Exit).
/// </summary>
internal static class Flow
{
    /// <summary>Verlässt dieses Statement die Funktion auf JEDEM Pfad (return/throw/Divergenz)?</summary>
    public static bool AlwaysReturns(Stmt s) => s switch
    {
        ReturnStmt => true,
        ThrowStmt => true,
        Block b => b.Statements.Any(AlwaysReturns),
        IfStmt f => f.Else is not null && AlwaysReturns(f.Then) && AlwaysReturns(f.Else),
        DoWhileStmt d => AlwaysReturns(d.Body) || Diverges(d.Condition, d.Body),
        WhileStmt w => Diverges(w.Condition, w.Body),
        ForInStmt => false, // Schleife läuft evtl. gar nicht
        TryStmt t => AlwaysReturns(t.Body) && t.Catches.All(c => AlwaysReturns(c.Body)),
        // D2: match zählt in M3 nur mit '_'-Arm und wenn alle Arme returnen (echte Exhaustivität = M4).
        MatchStmt m => m.Arms.Any(a => a.Pattern is WildcardPattern) && m.Arms.All(ArmReturns),
        _ => false
    };

    private static bool Diverges(Expr cond, Block body) => cond is BoolLiteralExpr { Value: true } && !HasBreak(body);

    private static bool ArmReturns(MatchArm a) => a.Body is Block b && AlwaysReturns(b);

    // break, das DIESE Schleife verlässt: nicht in verschachtelte Schleifen absteigen (deren break zielt dorthin).
    private static bool HasBreak(Stmt s) => s switch
    {
        BreakStmt => true,
        Block b => b.Statements.Any(HasBreak),
        IfStmt f => HasBreak(f.Then) || (f.Else is not null && HasBreak(f.Else)),
        TryStmt t => HasBreak(t.Body) || t.Catches.Any(c => HasBreak(c.Body)),
        MatchStmt m => m.Arms.Any(a => a.Body is Block bl && HasBreak(bl)),
        DeferStmt d => HasBreak(d.Body),
        _ => false // WhileStmt/DoWhileStmt/ForInStmt: nicht absteigen
    };
}
