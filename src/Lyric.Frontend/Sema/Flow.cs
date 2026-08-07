using Lyric.AST;

namespace Lyric.Sema;

/// <summary>
/// Strukturierte Kontrollfluss-Fakten über den AST (kein CFG nötig, da Lyric keine
/// unstrukturierten Sprünge hat). Geteilt von Return-Coverage und Narrowing (Early-Exit).
/// </summary>
internal static class Flow
{
    /// <summary>Verlässt dieses Statement die Funktion auf JEDEM Pfad (return/throw/Divergenz)?
    /// Mit <paramref name="types"/> zählt auch Sema-bewiesene match-Exhaustivität (M4-2);
    /// ohne fällt match auf den syntaktischen '_'-Arm zurück.</summary>
    public static bool AlwaysReturns(Stmt s, TypeResult? types = null) => s switch
    {
        ReturnStmt => true,
        ThrowStmt => true,
        ExprStmt es => types?.TypeOf(es.Expr) is NeverType, // panic(...) divergiert (§9)
        Block b => b.Statements.Any(st => AlwaysReturns(st, types)),
        IfStmt f => f.Else is not null && AlwaysReturns(f.Then, types) && AlwaysReturns(f.Else, types),
        DoWhileStmt d => AlwaysReturns(d.Body, types) || Diverges(d.Condition, d.Body),
        WhileStmt w => Diverges(w.Condition, w.Body),
        ForInStmt => false, // Schleife läuft evtl. gar nicht
        TryStmt t => AlwaysReturns(t.Body, types) && t.Catches.All(c => AlwaysReturns(c.Body, types)),
        MatchStmt m => (types?.IsMatchExhaustive(m) == true || m.Arms.Any(a => a.Pattern is WildcardPattern))
                       && m.Arms.All(a => ArmReturns(a, types)),
        _ => false
    };

    /// <summary>
    /// Verlaesst der Kontrollfluss diesen Block <b>auf jeden Fall</b> — egal wohin?
    ///
    /// <para>Das ist eine ANDERE Frage als <see cref="AlwaysReturns"/>, und die Unterscheidung
    /// ist der Grund, warum es zwei Funktionen gibt. „Fehlt ein return am Ende der Funktion"
    /// darf <c>continue</c> nicht als Rueckgabe zaehlen; „wird der Code nach dem if erreicht"
    /// muss es. Beides in eine Funktion zu legen hiesse, eine der beiden Antworten still falsch
    /// zu geben.</para>
    ///
    /// <para>Gebraucht fuer das Flow-Narrowing (§7): nach <c>if (x == null) { continue; }</c> ist
    /// x im Rest der Schleife eingeengt — genauso wie nach einem <c>return</c>. Dass nur der
    /// eine Fall galt, ist beim Bau des M8-Gates aufgefallen.</para>
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

        // Alles, was schon 'AlwaysReturns' erfuellt, verlaesst den Block erst recht.
        _ => AlwaysReturns(s, types),
    };

    private static bool Diverges(Expr cond, Block body) => cond is BoolLiteralExpr { Value: true } && !HasBreak(body);

    private static bool ArmReturns(MatchArm a, TypeResult? types) => a.Body is Block b && AlwaysReturns(b, types);

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
