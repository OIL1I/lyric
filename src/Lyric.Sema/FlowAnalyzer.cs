using Lyric.AST;
using Lyric.Core;
using Lyric.Resolver;

namespace Lyric.Sema;

/// <summary>
/// Definite-Assignment-Analyse (§5): eine Local/Parameter muss vor jedem Read zugewiesen
/// sein. Strukturierte Datenfluss-Analyse über den AST — der „assigned"-Set wird durch die
/// Statements gefädelt; an if-else der Schnitt beider Zweige, Schleifen konservativ.
/// Läuft nach der Typprüfung und nutzt deren Symbol-Bindings (<see cref="TypeResult"/>).
/// </summary>
internal sealed class FlowAnalyzer
{
    private readonly Compilation _comp;
    private readonly TypeResult _types;
    private readonly DiagnosticEngine _de;

    public FlowAnalyzer(Compilation comp, TypeResult types, DiagnosticEngine de)
    {
        _comp = comp;
        _types = types;
        _de = de;
    }

    public void Run()
    {
        foreach (var module in _comp.Modules)
            foreach (var decl in _comp.AstOf(module).Declarations)
                AnalyzeDecl(decl);
    }

    private void AnalyzeDecl(Decl decl)
    {
        switch (decl)
        {
            case FunctionDecl fn: AnalyzeFunction(fn); break;
            case StructDecl s: foreach (var m in s.Members) if (m is FunctionDecl f) AnalyzeFunction(f); break;
            case ClassDecl c: foreach (var m in c.Members) if (m is FunctionDecl f) AnalyzeFunction(f); break;
            case EnumDecl e: foreach (var f in e.Methods) AnalyzeFunction(f); break;
            case InterfaceDecl i: foreach (var f in i.Members) AnalyzeFunction(f); break;
            case ExtendDecl x: foreach (var f in x.Methods) AnalyzeFunction(f); break;
        }
    }

    private void AnalyzeFunction(FunctionDecl fn)
    {
        if (fn.Body is null) return;
        var assigned = NewSet();
        foreach (var p in fn.Parameters)
            if (_types.RefOf(p) is { } ps) assigned.Add(ps); // Params sind zugewiesen
        AnalyzeStatements(fn.Body.Statements, assigned);
    }

    // Liefert den assigned-Set NACH den Statements (sequentiell).
    private HashSet<Symbol> AnalyzeStatements(IEnumerable<Stmt> stmts, HashSet<Symbol> assigned)
    {
        foreach (var s in stmts) assigned = AnalyzeStmt(s, assigned);
        return assigned;
    }

    private HashSet<Symbol> AnalyzeStmt(Stmt stmt, HashSet<Symbol> assigned)
    {
        switch (stmt)
        {
            case Block b:
                return AnalyzeStatements(b.Statements, assigned);
            case BindingStmt bd:
                if (bd.Initializer is not null)
                {
                    AnalyzeExpr(bd.Initializer, assigned);
                    if (_types.RefOf(bd) is { } sym) assigned.Add(sym);
                }
                return assigned; // ohne Initializer: deklariert, aber unassigned
            case ExprStmt es:
                AnalyzeExpr(es.Expr, assigned);
                return assigned;
            case ReturnStmt r:
                if (r.Value is not null) AnalyzeExpr(r.Value, assigned);
                return assigned;
            case ThrowStmt t:
                AnalyzeExpr(t.Value, assigned);
                return assigned;
            case YieldStmt y:
                if (y.Value is not null) AnalyzeExpr(y.Value, assigned);
                return assigned;
            case DeferStmt de:
                return AnalyzeStmt(de.Body, assigned);
            case IfStmt f:
                return AnalyzeIf(f, assigned);
            case WhileStmt w:
                AnalyzeExpr(w.Condition, assigned);
                AnalyzeStatements(w.Body.Statements, Clone(assigned)); // Body läuft evtl. nicht
                return assigned;
            case DoWhileStmt d:
                var after = AnalyzeStatements(d.Body.Statements, Clone(assigned)); // Body läuft ≥ 1×
                AnalyzeExpr(d.Condition, after);
                return after;
            case ForInStmt fo:
                AnalyzeExpr(fo.Iterable, assigned);
                var loopSet = Clone(assigned);
                if (_types.RefOf(fo) is { } lv) loopSet.Add(lv);
                AnalyzeStatements(fo.Body.Statements, loopSet);
                return assigned;
            case TryStmt tr:
                AnalyzeStatements(tr.Body.Statements, Clone(assigned));
                foreach (var c in tr.Catches)
                {
                    var catchSet = Clone(assigned);
                    if (_types.RefOf(c) is { } bind) catchSet.Add(bind); // der Catch weist die Bindung zu
                    AnalyzeStatements(c.Body.Statements, catchSet);
                }
                return assigned;
            case MatchStmt m:
            {
                AnalyzeExpr(m.Scrutinee, assigned);
                HashSet<Symbol>? merged = null; // Schnitt der Arm-Zuweisungen (nur nicht-verlassende Arme)
                foreach (var arm in m.Arms)
                {
                    var armSet = Clone(assigned);
                    AddPatternBindings(arm.Pattern, armSet);
                    if (arm.Guard is not null) AnalyzeExpr(arm.Guard, armSet);
                    var exits = false;
                    if (arm.Body is Block ab)
                    {
                        armSet = AnalyzeStatements(ab.Statements, armSet);
                        exits = Flow.AlwaysReturns(ab, _types);
                    }
                    else if (arm.Body is Expr ae) AnalyzeExpr(ae, armSet);
                    if (!exits) merged = merged is null ? armSet : Intersect(merged, armSet);
                }
                // Exhaustiver match: genau ein Arm läuft — was ALLE (fortsetzenden) Arme
                // zuweisen, ist danach definitiv zugewiesen. merged == null: alle Arme verlassen.
                if (_types.IsMatchExhaustive(m) && m.Arms.Length > 0)
                    return merged ?? assigned;
                return assigned;
            }
            default: // Break/Continue/Error
                return assigned;
        }
    }

    private HashSet<Symbol> AnalyzeIf(IfStmt f, HashSet<Symbol> assigned)
    {
        AnalyzeExpr(f.Condition, assigned);
        var thenSet = AnalyzeStatements(f.Then.Statements, Clone(assigned));
        var thenExits = Flow.AlwaysReturns(f.Then, _types);

        if (f.Else is null)
            return assigned; // ohne else nichts definitiv Neues (then-Zweig evtl. übersprungen)

        var elseSet = AnalyzeStmt(f.Else, Clone(assigned));
        var elseExits = Flow.AlwaysReturns(f.Else, _types);

        if (thenExits && elseExits) return assigned;          // danach unerreichbar
        if (thenExits) return elseSet;                        // Fortsetzung folgt else
        if (elseExits) return thenSet;                        // Fortsetzung folgt then
        return Intersect(thenSet, elseSet);                   // nur was BEIDE Zweige zuweisen
    }

    // Prüft Reads (unassigned → Fehler) und behandelt Assignments.
    private void AnalyzeExpr(Expr expr, HashSet<Symbol> assigned)
    {
        switch (expr)
        {
            case IdentifierExpr id:
                if (_types.RefOf(id) is LocalSymbol or ParameterSymbol && _types.RefOf(id) is { } s && !assigned.Contains(s))
                {
                    _de.Report("LYR-SEM0018", Severity.Error, id.Span, $"use of possibly unassigned variable '{id.Name}'");
                    assigned.Add(s); // Folgefehler unterdrücken
                }
                return;
            case AssignExpr a:
                AnalyzeExpr(a.Value, assigned);
                if (a.Target is IdentifierExpr tid && _types.RefOf(tid) is LocalSymbol or ParameterSymbol && _types.RefOf(tid) is { } ts)
                {
                    if (a.Operator is not null && !assigned.Contains(ts)) // Compound-Assign liest zuerst
                        _de.Report("LYR-SEM0018", Severity.Error, tid.Span, $"use of possibly unassigned variable '{tid.Name}'");
                    assigned.Add(ts);
                }
                else AnalyzeExpr(a.Target, assigned); // Feld-/Index-Ziel: Sub-Ausdrücke sind Reads
                return;
            case BinaryExpr b: AnalyzeExpr(b.Left, assigned); AnalyzeExpr(b.Right, assigned); return;
            case UnaryExpr u: AnalyzeExpr(u.Operand, assigned); return;
            case ResumeExpr re: AnalyzeExpr(re.Coroutine, assigned); return;
            case PostfixExpr p: AnalyzeExpr(p.Operand, assigned); return;
            case CallExpr c:
                AnalyzeExpr(c.Callee, assigned);
                foreach (var arg in c.Arguments) AnalyzeExpr(arg, assigned);
                return;
            case MemberExpr m: AnalyzeExpr(m.Target, assigned); return;
            case IndexExpr ix: AnalyzeExpr(ix.Target, assigned); AnalyzeExpr(ix.Index, assigned); return;
            case CastExpr cs: AnalyzeExpr(cs.Operand, assigned); return;
            case RangeExpr r: AnalyzeExpr(r.Low, assigned); AnalyzeExpr(r.High, assigned); return;
            case ArrayLitExpr arr: foreach (var e in arr.Elements) AnalyzeExpr(e, assigned); return;
            case TupleLitExpr tu: foreach (var e in tu.Elements) AnalyzeExpr(e, assigned); return;
            case StructInitExpr si: foreach (var fld in si.Fields) AnalyzeExpr(fld.Value, assigned); return;
            case InterpolatedStringExpr fs:
                foreach (var seg in fs.Segments) if (seg is InterpHole h) AnalyzeExpr(h.Expr, assigned);
                return;
            case IfExpr iff:
                AnalyzeExpr(iff.Condition, assigned); AnalyzeExpr(iff.Then, assigned); AnalyzeExpr(iff.Else, assigned);
                return;
            case MatchExpr ma:
                AnalyzeExpr(ma.Scrutinee, assigned);
                foreach (var arm in ma.Arms)
                {
                    var armSet = Clone(assigned);
                    AddPatternBindings(arm.Pattern, armSet);
                    if (arm.Guard is not null) AnalyzeExpr(arm.Guard, armSet);
                    if (arm.Body is Expr ae) AnalyzeExpr(ae, armSet);
                }
                return;
            // Lambda-Bodies (Closures) → separater Kontext, hier übersprungen; Literale/this/@ident: keine Reads.
        }
    }

    // Pattern-gebundene Variablen gelten im Arm als zugewiesen (der Match weist sie zu).
    private void AddPatternBindings(Pattern pattern, HashSet<Symbol> set)
    {
        switch (pattern)
        {
            case BindingPattern b: if (_types.RefOf(b) is { } s) set.Add(s); return;
            case VariantPattern v:
                foreach (var sub in v.TupleElements ?? []) AddPatternBindings(sub, set);
                foreach (var f in v.StructFields ?? [])
                {
                    if (f.Pattern is not null) AddPatternBindings(f.Pattern, set);
                    else if (_types.RefOf(f) is { } fs) set.Add(fs);
                }
                return;
            case TuplePattern t: foreach (var sub in t.Elements) AddPatternBindings(sub, set); return;
            case OrPattern o: if (o.Alternatives.Length > 0) AddPatternBindings(o.Alternatives[0], set); return;
        }
    }

    private static HashSet<Symbol> NewSet() => new(ReferenceEqualityComparer.Instance);
    private static HashSet<Symbol> Clone(HashSet<Symbol> s) => new(s, ReferenceEqualityComparer.Instance);
    private static HashSet<Symbol> Intersect(HashSet<Symbol> a, HashSet<Symbol> b)
    {
        var r = NewSet();
        foreach (var x in a) if (b.Contains(x)) r.Add(x);
        return r;
    }
}
