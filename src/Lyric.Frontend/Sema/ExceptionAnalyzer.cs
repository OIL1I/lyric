using Lyric.AST;
using Lyric.Core;
using Lyric.Resolver;

namespace Lyric.Sema;

/// <summary>
/// throws-Propagation (Sprache.md §9), read-only Post-Pass nach dem TypeChecker: jede
/// Throw-Site (throw-Statement oder Call einer throws-Funktion) muss entweder von einem
/// umgebenden try mit passendem catch abgedeckt sein oder von der throws-Klausel der
/// umgebenden Funktion (auto-propagation). Lambdas sind eigene Kontexte ohne
/// throws-Klausel; globale Initializer und Default-Werte haben keinen Handler.
/// Typ-Zuordnung: gleicher Typ, Interface-Implementierung oder Catch-All/typloses throws.
/// </summary>
internal sealed class ExceptionAnalyzer
{
    private readonly Compilation _comp;
    private readonly BindingResult _binding;
    private readonly TypeResult _types;
    private readonly DiagnosticEngine _de;
    private readonly TypeSymbol? _throwable;

    // Was die aktuelle Funktion werfen darf: (nichts | alles | genau ein Symbol).
    private enum Permit { None, Any, Typed }
    private Permit _permit = Permit.None;
    private TypeSymbol? _permitted;
    private readonly List<CatchClause[]> _tryStack = new(); // nur try-BODIES schützen

    public ExceptionAnalyzer(Compilation comp, BindingResult binding, TypeResult types, DiagnosticEngine de)
    {
        _comp = comp;
        _binding = binding;
        _types = types;
        _de = de;
        _throwable = comp.Builtins.LookupLocal("Throwable") as TypeSymbol;
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
            case StructDecl s: AnalyzeMembers(s.Members); break;
            case ClassDecl c: AnalyzeMembers(c.Members); break;
            case EnumDecl e: foreach (var f in e.Methods) AnalyzeFunction(f); break;
            case InterfaceDecl i: foreach (var f in i.Members) AnalyzeFunction(f); break; // Default-Bodies
            case ExtendDecl x: foreach (var f in x.Methods) AnalyzeFunction(f); break;
            case GlobalBindingDecl g: // Top-Level: kein try möglich, kein throws deklarierbar
                if (g.Binding.Initializer is not null) AnalyzeExpr(g.Binding.Initializer);
                break;
        }
    }

    private void AnalyzeMembers(Decl[] members)
    {
        foreach (var m in members)
            switch (m)
            {
                case FunctionDecl f: AnalyzeFunction(f); break;
                case FieldDecl { Default: not null } fd: AnalyzeExpr(fd.Default); break; // kein Handler-Kontext
            }
    }

    private void AnalyzeFunction(FunctionDecl fn)
    {
        foreach (var p in fn.Parameters)
            if (p.Default is not null) AnalyzeExpr(p.Default); // Default-Werte: kein Handler

        if (fn.Body is null) return;
        var (savedPermit, savedType) = (_permit, _permitted);
        (_permit, _permitted) = PermitOf(fn);
        var savedStack = _tryStack.Count;
        AnalyzeStmt(fn.Body);
        _tryStack.RemoveRange(savedStack, _tryStack.Count - savedStack);
        (_permit, _permitted) = (savedPermit, savedType);
    }

    private (Permit, TypeSymbol?) PermitOf(FunctionDecl fn)
    {
        if (fn.Throws is null) return (Permit.None, null);
        if (fn.Throws.Type is null) return (Permit.Any, null);
        // Vom TypeChecker an die Klausel gebundenes Symbol; 'throws Throwable' ≡ typlos.
        var sym = _types.RefOf(fn.Throws) as TypeSymbol;
        if (sym is null) return (Permit.Any, null); // unauflösbar/extern → lenient
        return ReferenceEquals(sym, _throwable) ? (Permit.Any, null) : (Permit.Typed, sym);
    }

    // --- Statement-Walk mit try-Stack ---

    private void AnalyzeStmt(Stmt stmt)
    {
        switch (stmt)
        {
            case Block b: foreach (var s in b.Statements) AnalyzeStmt(s); break;
            case BindingStmt bd: if (bd.Initializer is not null) AnalyzeExpr(bd.Initializer); break;
            case ExprStmt es: AnalyzeExpr(es.Expr); break;
            case IfStmt f:
                AnalyzeExpr(f.Condition);
                AnalyzeStmt(f.Then);
                if (f.Else is not null) AnalyzeStmt(f.Else);
                break;
            case WhileStmt w: AnalyzeExpr(w.Condition); AnalyzeStmt(w.Body); break;
            case DoWhileStmt d: AnalyzeStmt(d.Body); AnalyzeExpr(d.Condition); break;
            case ForInStmt fo: AnalyzeExpr(fo.Iterable); AnalyzeStmt(fo.Body); break;
            case ReturnStmt r: if (r.Value is not null) AnalyzeExpr(r.Value); break;
            case YieldStmt y: if (y.Value is not null) AnalyzeExpr(y.Value); break;
            case DeferStmt de: AnalyzeStmt(de.Body); break; // v1: wie Code am Deklarationsort
            case ThrowStmt t:
                AnalyzeExpr(t.Value);
                var thrownType = _types.TypeOf(t.Value);
                // Nicht-werfbare Typen hat der TypeChecker schon gemeldet (SEM0030).
                if (Conformance.IsThrowable(thrownType, _throwable, _binding))
                    CheckSite(ThrownOf(thrownType), t.Span, "'throw'");
                break;
            case TryStmt tr:
                _tryStack.Add(tr.Catches);
                AnalyzeStmt(tr.Body);
                _tryStack.RemoveAt(_tryStack.Count - 1);
                foreach (var c in tr.Catches) AnalyzeStmt(c.Body); // catch fängt sich nicht selbst
                break;
            case MatchStmt m:
                AnalyzeExpr(m.Scrutinee);
                foreach (var arm in m.Arms) AnalyzeArm(arm);
                break;
        }
    }

    private void AnalyzeArm(MatchArm arm)
    {
        if (arm.Guard is not null) AnalyzeExpr(arm.Guard);
        if (arm.Body is Block b) AnalyzeStmt(b);
        else if (arm.Body is Expr e) AnalyzeExpr(e);
    }

    // --- Expression-Walk: Calls sind Throw-Sites, Fn-Referenzen außerhalb der
    // --- Call-Position verlieren die throws-Info (SEM0037), Lambdas sind eigene Kontexte.

    private void AnalyzeExpr(Expr expr)
    {
        switch (expr)
        {
            case CallExpr call:
                AnalyzeCallee(call.Callee);
                foreach (var a in call.Arguments) AnalyzeExpr(a);
                if (ThrowsOf(call.Callee) is { } thrown)
                    CheckSite(thrown, call.Span, $"call to '{CalleeName(call.Callee)}'");
                break;
            case IdentifierExpr or MemberExpr:
                CheckFnValue(expr);
                if (expr is MemberExpr m) AnalyzeExpr(m.Target);
                break;
            case LambdaExpr lam: AnalyzeLambda(lam); break;
            case UnaryExpr u: AnalyzeExpr(u.Operand); break;
            case ResumeExpr re: AnalyzeExpr(re.Coroutine); break;
            case PostfixExpr p: AnalyzeExpr(p.Operand); break;
            case BinaryExpr b: AnalyzeExpr(b.Left); AnalyzeExpr(b.Right); break;
            case AssignExpr a: AnalyzeExpr(a.Target); AnalyzeExpr(a.Value); break;
            case RangeExpr r: AnalyzeExpr(r.Low); AnalyzeExpr(r.High); break;
            case CastExpr c: AnalyzeExpr(c.Operand); break;
            case IndexExpr ix: AnalyzeExpr(ix.Target); AnalyzeExpr(ix.Index); break;
            case ArrayLitExpr arr: foreach (var e in arr.Elements) AnalyzeExpr(e); break;
            case TupleLitExpr tu: foreach (var e in tu.Elements) AnalyzeExpr(e); break;
            case StructInitExpr si: foreach (var f in si.Fields) AnalyzeExpr(f.Value); break;
            case InterpolatedStringExpr fs:
                foreach (var seg in fs.Segments) if (seg is InterpHole h) AnalyzeExpr(h.Expr);
                break;
            case IfExpr iff:
                AnalyzeExpr(iff.Condition); AnalyzeExpr(iff.Then); AnalyzeExpr(iff.Else);
                break;
            case MatchExpr ma:
                AnalyzeExpr(ma.Scrutinee);
                foreach (var arm in ma.Arms) AnalyzeArm(arm);
                break;
        }
    }

    // Callee-Position: die Fn-Referenz selbst ist legitim, nur ihre Sub-Ausdrücke laufen.
    private void AnalyzeCallee(Expr callee)
    {
        if (callee is MemberExpr m) AnalyzeExpr(m.Target);
        else if (callee is not IdentifierExpr) AnalyzeExpr(callee);
    }

    // Lambda: eigener Funktions-Kontext ohne throws-Klausel (Grammatik §6.2) und ohne
    // Schutz durch trys am Definitionsort (der Body läuft später).
    private void AnalyzeLambda(LambdaExpr lam)
    {
        var (savedPermit, savedType) = (_permit, _permitted);
        var savedStack = new List<CatchClause[]>(_tryStack);
        (_permit, _permitted) = (Permit.None, null);
        _tryStack.Clear();
        if (lam.Body is Block b) AnalyzeStmt(b);
        else if (lam.Body is Expr e) AnalyzeExpr(e);
        _tryStack.Clear();
        _tryStack.AddRange(savedStack);
        (_permit, _permitted) = (savedPermit, savedType);
    }

    // --- Throw-Sites prüfen ---

    // Geworfener Typ einer Site: (any, null) = statisch unbekannt (typloses throws,
    // Throwable-Wert, Typ-Param); (false, sym) = konkretes Symbol; null = Poison/keine Site.
    private (bool any, TypeSymbol? sym)? ThrownOf(LyrType t) => t switch
    {
        ErrorType => null,
        NamedRef nr when ReferenceEquals(nr.Symbol, _throwable) => (true, null),
        NamedRef nr => (false, nr.Symbol),
        GenericInstance gi => (false, gi.Definition),
        _ => (true, null) // Typ-Param mit Throwable-Constraint u.ä.
    };

    private (bool any, TypeSymbol? sym)? ThrowsOf(Expr callee)
    {
        if (_types.RefOf(callee) is not FunctionSymbol { Declaration: FunctionDecl decl } fn) return null;
        if (decl.Throws is null) return null;
        if (decl.Throws.Type is null) return (true, null);
        var sym = _types.RefOf(decl.Throws) as TypeSymbol;
        if (sym is null || ReferenceEquals(sym, _throwable)) return (true, null);
        return (false, sym);
    }

    private void CheckSite((bool any, TypeSymbol? sym)? thrown, Span span, string what)
    {
        if (thrown is not { } th) return;
        if (HandledByTry(th)) return;
        if (PermittedByDeclaration(th)) return;
        var name = th.sym?.Name ?? "Throwable";
        _de.Report("LYR-SEM0034", Severity.Error, span,
            $"{what} may throw '{name}', which nothing handles — declare 'throws' on the enclosing function or wrap it in try/catch");
    }

    private bool HandledByTry((bool any, TypeSymbol? sym) th)
    {
        for (var i = _tryStack.Count - 1; i >= 0; i--)
            foreach (var c in _tryStack[i])
                if (CatchHandles(c, th))
                    return true;
        return false;
    }

    private bool CatchHandles(CatchClause c, (bool any, TypeSymbol? sym) th)
    {
        if (c.BindingType is null) return true; // Catch-All
        if (_types.RefOf(c.BindingType) is not TypeSymbol ct) return true; // unauflösbar → lenient
        if (ReferenceEquals(ct, _throwable)) return true; // catch (e: Throwable) ≡ Catch-All
        if (th.any || th.sym is null) return false; // statisch unbekannt: nur Catch-All hilft
        return ReferenceEquals(th.sym, ct) || Conformance.Implements(th.sym, ct, _binding);
    }

    private bool PermittedByDeclaration((bool any, TypeSymbol? sym) th) => _permit switch
    {
        Permit.Any => true,
        Permit.Typed => !th.any && th.sym is not null && _permitted is not null
            && (ReferenceEquals(th.sym, _permitted) || Conformance.Implements(th.sym, _permitted, _binding)),
        _ => false
    };

    // --- throws-Funktion als Wert (SEM0037): FnType trägt keine throws-Info (§4) ---

    private void CheckFnValue(Expr expr)
    {
        if (_types.RefOf(expr) is FunctionSymbol { Declaration: FunctionDecl { Throws: not null } } fn)
            _de.Report("LYR-SEM0037", Severity.Error, expr.Span,
                $"'{fn.Name}' declares 'throws' and cannot be used as a value — function types carry no throws information; call it directly");
    }

    private static string CalleeName(Expr callee) => callee switch
    {
        IdentifierExpr id => id.Name,
        MemberExpr m => m.Member,
        _ => "function"
    };
}
