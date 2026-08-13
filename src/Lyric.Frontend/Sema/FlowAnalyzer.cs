using Lyric.AST;
using Lyric.Core;
using Lyric.Resolver;

namespace Lyric.Sema;

/// <summary>
/// Definite-assignment analysis: a local or parameter must be assigned before every read.
/// A structured data-flow analysis over the AST — the "assigned" set is threaded through the
/// statements; at an if-else it is the intersection of both branches, and loops are treated
/// conservatively. Runs after type checking and uses its symbol bindings
/// (<see cref="TypeResult"/>).
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
            if (_types.RefOf(p) is { } ps) assigned.Add(ps); // parameters are assigned
        AnalyzeStatements(fn.Body.Statements, assigned);
    }

    // Returns the assigned set AFTER the statements, sequentially.
    /// <summary>Every name binding in a pattern, nested tuples included. <c>_</c> binds nothing and
    /// therefore does not appear.</summary>
    private static IEnumerable<Pattern> BoundNames(Pattern pattern)
    {
        switch (pattern)
        {
            case BindingPattern: yield return pattern; break;
            case TuplePattern t:
                foreach (var element in t.Elements)
                    foreach (var inner in BoundNames(element)) yield return inner;
                break;
        }
    }

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
                return assigned; // without an initializer: declared, but unassigned

            // 'let (a, b) = …' — the initializer is required, so ALL bound names are assigned
            // afterwards. Without this case the analysis holds them empty and reports LYR-SEM0018
            // on the first use.
            case DestructuringStmt d:
                AnalyzeExpr(d.Initializer, assigned);
                foreach (var name in BoundNames(d.Pattern))
                    if (_types.RefOf(name) is { } bound) assigned.Add(bound);
                return assigned;
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
                AnalyzeStatements(w.Body.Statements, Clone(assigned)); // the body may not run
                return assigned;
            case DoWhileStmt d:
                var after = AnalyzeStatements(d.Body.Statements, Clone(assigned)); // the body runs at least once
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
                    if (_types.RefOf(c) is { } bind) catchSet.Add(bind); // the catch assigns the binding
                    AnalyzeStatements(c.Body.Statements, catchSet);
                }
                return assigned;
            case MatchStmt m:
            {
                AnalyzeExpr(m.Scrutinee, assigned);
                HashSet<Symbol>? merged = null; // intersection of the arm assignments, non-leaving arms only
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
                // An exhaustive match runs exactly one arm, so whatever ALL continuing arms assign
                // is definitely assigned afterwards. merged == null means every arm leaves.
                if (_types.IsMatchExhaustive(m) && m.Arms.Length > 0)
                    return merged ?? assigned;
                return assigned;
            }
            default: // break, continue, error
                return assigned;
        }
    }

    private HashSet<Symbol> AnalyzeIf(IfStmt f, HashSet<Symbol> assigned)
    {
        AnalyzeExpr(f.Condition, assigned);
        var thenSet = AnalyzeStatements(f.Then.Statements, Clone(assigned));
        var thenExits = Flow.AlwaysReturns(f.Then, _types);

        if (f.Else is null)
            return assigned; // without an else nothing is definitely new; the then branch may be skipped

        var elseSet = AnalyzeStmt(f.Else, Clone(assigned));
        var elseExits = Flow.AlwaysReturns(f.Else, _types);

        if (thenExits && elseExits) return assigned;          // unreachable afterwards
        if (thenExits) return elseSet;                        // the continuation follows else
        if (elseExits) return thenSet;                        // the continuation follows then
        return Intersect(thenSet, elseSet);                   // only what BOTH branches assign
    }

    // Checks reads, where unassigned is an error, and handles assignments.
    private void AnalyzeExpr(Expr expr, HashSet<Symbol> assigned)
    {
        switch (expr)
        {
            case IdentifierExpr id:
                if (_types.RefOf(id) is LocalSymbol or ParameterSymbol && _types.RefOf(id) is { } s && !assigned.Contains(s))
                {
                    _de.Report("LYR-SEM0018", Severity.Error, id.Span, $"use of possibly unassigned variable '{id.Name}'");
                    assigned.Add(s); // suppress follow-up errors
                }
                return;
            case AssignExpr a:
                AnalyzeExpr(a.Value, assigned);
                if (a.Target is IdentifierExpr tid && _types.RefOf(tid) is LocalSymbol or ParameterSymbol && _types.RefOf(tid) is { } ts)
                {
                    if (a.Operator is not null && !assigned.Contains(ts)) // a compound assignment reads first
                        _de.Report("LYR-SEM0018", Severity.Error, tid.Span, $"use of possibly unassigned variable '{tid.Name}'");
                    assigned.Add(ts);
                }
                else AnalyzeExpr(a.Target, assigned); // field or index target: the sub-expressions are reads
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
            case LambdaExpr lam:
            {
                // Captures have to be definitely assigned AT THE CREATION SITE: analyze the body
                // with a snapshot of the current set plus the lambda's own parameters.
                var lamSet = Clone(assigned);
                foreach (var p in lam.Parameters)
                    if (_types.RefOf(p) is { } ps) lamSet.Add(ps);
                if (lam.Body is Block lb) AnalyzeStatements(lb.Statements, lamSet);
                else if (lam.Body is Expr le) AnalyzeExpr(le, lamSet);
                return;
            }
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
            // Literals, this and @ident are no reads.
        }
    }

    // Pattern-bound variables count as assigned inside the arm, because the match assigns them.
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
