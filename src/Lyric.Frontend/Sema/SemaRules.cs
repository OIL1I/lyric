using Lyric.AST;
using Lyric.Core;
using Lyric.Resolver;

namespace Lyric.Sema;

/// <summary>
/// Strukturelle Sema-Regeln (M3-Slice 3b), read-only auf <see cref="TypeResult"/>/
/// <see cref="BindingResult"/>: Lvalue-/Mutabilitäts-Check (§6.4), `ExprStmt` nur Call/
/// Assign (§5), Interface-Konformität (`::`), Signatur-Regeln (§3.1), `main`-Contract (§11).
/// </summary>
public sealed class SemaRules
{
    private readonly Compilation _comp;
    private readonly BindingResult _binding;
    private readonly TypeResult _types;
    private readonly DiagnosticEngine _de;
    private bool _thisMut; // in einer 'mut fn'-Methode?

    public SemaRules(Compilation comp, BindingResult binding, TypeResult types, DiagnosticEngine de)
    {
        _comp = comp;
        _binding = binding;
        _types = types;
        _de = de;
    }

    public void Run()
    {
        foreach (var module in _comp.Modules)
            foreach (var decl in _comp.AstOf(module).Declarations)
                CheckDecl(decl);
        CheckMain();
    }

    private void CheckDecl(Decl decl)
    {
        switch (decl)
        {
            case FunctionDecl fn:
                CheckSignature(fn, isMethod: false);
                RunBody(fn);
                break;
            case StructDecl s: CheckTypeDecl(s.Members.OfType<FunctionDecl>()); break;
            case ClassDecl c: CheckTypeDecl(c.Members.OfType<FunctionDecl>()); break;
            case EnumDecl e: CheckTypeDecl(e.Methods); break;
            case InterfaceDecl i: foreach (var m in i.Members) { CheckSignature(m, true); RunBody(m); } break;
            case ExtendDecl x: foreach (var m in x.Methods) { CheckSignature(m, true); RunBody(m); } break;
        }
    }

    // Konformanz (Signatur-Match) macht der TypeChecker; hier nur Signatur-Regeln + Bodies.
    private void CheckTypeDecl(IEnumerable<FunctionDecl> methods)
    {
        foreach (var fn in methods) { CheckSignature(fn, isMethod: true); RunBody(fn); }
    }

    // --- Signatur-Regeln (§3.1) ---

    private void CheckSignature(FunctionDecl fn, bool isMethod)
    {
        if (fn.IsMut && !isMethod)
            _de.Report("LYR-SEM0023", Severity.Error, fn.Span, $"'mut' is only allowed on methods, not on free function '{fn.Name}'");

        var ps = fn.Parameters;
        for (var i = 0; i < ps.Length; i++)
            if (ps[i].IsParams)
            {
                if (i != ps.Length - 1)
                    _de.Report("LYR-SEM0024", Severity.Error, ps[i].Span, "'params' must be the last parameter");
                if (ps[i].Type is not ArrayType)
                    _de.Report("LYR-SEM0024", Severity.Error, ps[i].Span, "'params' requires an array type");
            }

        var seenDefault = false;
        foreach (var p in ps)
        {
            if (p.Default is not null) seenDefault = true;
            else if (seenDefault && !p.IsParams)
                _de.Report("LYR-SEM0025", Severity.Error, p.Span, $"required parameter '{p.Name}' follows a default parameter");
        }
    }

    // --- main-Contract (§11) ---

    private void CheckMain()
    {
        var mains = _comp.Modules
            .SelectMany(m => _comp.AstOf(m).Declarations.OfType<FunctionDecl>())
            .Where(f => f.Name == "main")
            .ToList();

        foreach (var main in mains)
            if (!ValidMain(main))
                _de.Report("LYR-SEM0021", Severity.Error, main.Span,
                    "'main' must be 'fn main(): int' or 'fn main(args: string[]): int'");

        for (var i = 1; i < mains.Count; i++)
            _de.Report("LYR-SEM0021", Severity.Error, mains[i].Span, "duplicate 'main' function");
    }

    private static bool ValidMain(FunctionDecl fn)
    {
        if (!IsNamed(fn.ReturnType, "int")) return false;
        return fn.Parameters.Length switch
        {
            0 => true,
            1 => fn.Parameters[0].Type is ArrayType a && IsNamed(a.Element, "string"),
            _ => false
        };
    }

    private static bool IsNamed(TypeNode? t, string name) => t is NamedType n && n.Path is [var only] && only == name;

    // --- Lvalue / Mutabilität (§6.4) + ExprStmt-Regel (§5) ---

    private void RunBody(FunctionDecl fn)
    {
        if (fn.Body is null) return;
        var saved = _thisMut;
        _thisMut = fn.IsMut;
        WalkStmt(fn.Body);
        _thisMut = saved;
    }

    private void WalkStmt(Stmt stmt)
    {
        switch (stmt)
        {
            case Block b: foreach (var s in b.Statements) WalkStmt(s); break;
            case ExprStmt es: CheckExprStmt(es); WalkExpr(es.Expr); break;
            case BindingStmt bd: if (bd.Initializer is not null) WalkExpr(bd.Initializer); break;
            case DestructuringStmt d: WalkExpr(d.Initializer); break;
            case IfStmt f: WalkExpr(f.Condition); WalkStmt(f.Then); if (f.Else is not null) WalkStmt(f.Else); break;
            case WhileStmt w: WalkExpr(w.Condition); WalkStmt(w.Body); break;
            case DoWhileStmt d: WalkStmt(d.Body); WalkExpr(d.Condition); break;
            case ForInStmt fo: WalkExpr(fo.Iterable); WalkStmt(fo.Body); break;
            case ReturnStmt r: if (r.Value is not null) WalkExpr(r.Value); break;
            case ThrowStmt t: WalkExpr(t.Value); break;
            case YieldStmt y: if (y.Value is not null) WalkExpr(y.Value); break;
            case DeferStmt de: WalkStmt(de.Body); break;
            case TryStmt tr: CheckTry(tr); WalkStmt(tr.Body); foreach (var c in tr.Catches) WalkStmt(c.Body); break;
            case MatchStmt m:
                WalkExpr(m.Scrutinee);
                foreach (var arm in m.Arms)
                {
                    if (arm.Guard is not null) WalkExpr(arm.Guard);
                    if (arm.Body is Block ab) WalkStmt(ab); else if (arm.Body is Expr ae) WalkExpr(ae);
                }
                break;
        }
    }

    // try/catch-Struktur (§5/§9): mindestens ein catch; Catch-All (ohne Typ) nur als letzte Klausel.
    private void CheckTry(TryStmt tr)
    {
        if (tr.Catches.Length == 0)
            _de.Report("LYR-SEM0036", Severity.Error, tr.Span,
                "'try' needs at least one 'catch' ('finally' does not exist — use 'defer')");
        for (var i = 0; i < tr.Catches.Length - 1; i++)
            if (tr.Catches[i].BindingType is null)
                _de.Report("LYR-SEM0035", Severity.Error, tr.Catches[i].Span,
                    "catch-all must be the last catch clause");
    }

    private void CheckExprStmt(ExprStmt es)
    {
        var ok = es.Expr is CallExpr or AssignExpr or ResumeExpr
            or PostfixExpr { Operator: PostfixOp.Inc or PostfixOp.Dec } or ErrorExpr;
        if (!ok)
            _de.Report("LYR-SEM0022", Severity.Error, es.Span, "expression statement has no effect (only calls, assignments and resume are allowed)");
    }

    private void WalkExpr(Expr expr)
    {
        if (expr is AssignExpr a)
        {
            if (!_types.TypeOf(a.Target).IsError && !IsMutableLvalue(a.Target))
                _de.Report("LYR-SEM0019", Severity.Error, a.Target.Span, "cannot assign to this target (not a mutable lvalue)");
            WalkExpr(a.Value);
            WalkExpr(a.Target);
            return;
        }
        foreach (var child in Children(expr)) WalkExpr(child);
    }

    private bool IsMutableLvalue(Expr expr) => expr switch
    {
        IdentifierExpr id => _types.RefOf(id) is LocalSymbol { IsMutable: true },
        MemberExpr m => IsFieldMutable(m),
        // Ein Element ist schreibbar, sobald der Container eine REFERENZ ist — genau wie ein
        // class-Feld (ADR-020). 'let' haelt den Namen fest, nicht das Objekt dahinter.
        //
        // Vorher erbte der Index die Mutabilitaet des Containers, und das war wirkungslos:
        // 'let ps = [P { … }]; ps[0].hp = 9;' ging immer durch, verboten war nur die direkte
        // Element-Zuweisung. Eine Regel, die nichts schuetzt und dafuer 'T[]' anders behandelt
        // als eine Klasse, kostet nur Erklaerungsaufwand — seit ADR-016 ist 'T[]' beides.
        IndexExpr ix => IsIndexableTarget(_types.TypeOf(ix.Target)),
        _ => false
    };

    /// <summary>Ein Array oder ein Typ, der <c>Indexable&lt;T&gt;</c> erfuellt. Beides sind
    /// Referenzen, also ist das Element schreibbar — ADR-020: <c>let</c> haelt den Namen fest,
    /// nicht das Objekt dahinter.
    ///
    /// <para>Der <c>set</c>-Setter des Interfaces ist <c>mut fn</c>, und das kostet seit ADR-020
    /// nichts: haette die alte Regel („Container muss mut sein") noch gegolten, muesste dieses
    /// Interface sie nachbilden — eine Regel, die ohnehin wirkungslos war. Genau deshalb wurde
    /// sie vor diesem Slice entschieden.</para></summary>
    private bool IsIndexableTarget(LyrType type)
    {
        if (type is ArrayOf or ErrorType) return true;
        if (_types.Indexable is not { } indexable) return false;

        return TypeFacts.SymbolOf(type) is { } symbol
               && Conformance.Implements(symbol, indexable, _binding);
    }

    private bool IsFieldMutable(MemberExpr m)
    {
        var baseType = _types.TypeOf(m.Target);
        if (baseType.IsError) return true; // Poison: kein Folgefehler

        // Eine Instanz eines generischen Typs verhaelt sich wie ihre Definition: 'Box<int>' ist
        // eine Klasse, wenn 'Box' eine ist.
        var kind = TypeFacts.KindOf(baseType);

        if (kind == TypeSymbolKind.Class) return true;               // class-Felder immer mutabel (§6.4)

        // Struct-Felder ebenso — ausser an 'this' in einer Nicht-'mut'-Methode (ADR-023).
        //
        // Vorher erbte das Feld die Mutabilitaet seines Basis-Lvalues, und das war aus demselben
        // Grund wirkungslos wie bei ADR-020: eine 'mut fn' umging es vollstaendig. Gemessen:
        // 'let p = P { x = 1 }; p.x = 9;' war LYR-SEM0019, 'p.shift(9)' mit 'mut fn shift' ging
        // durch UND aenderte p wirklich. An einem Parameter aendern beide Formen nur die Kopie,
        // sind also gleich folgenlos. Verboten war so oder so nur die Schreibweise, die sich
        // ersetzen laesst.
        //
        // '_thisMut' bleibt: dass eine Nicht-'mut'-Methode ihren eigenen Empfaenger nicht
        // anfasst, ist die Zusage von 'mut fn' und das Einzige, was hier je etwas geschuetzt hat.
        if (kind == TypeSymbolKind.Struct)
            return m.Target is not ThisExpr || _thisMut;

        return false;
    }

    // Direkte Kind-Ausdrücke (zum Aufspüren verschachtelter Assignments).
    private static IEnumerable<Expr> Children(Expr e) => e switch
    {
        UnaryExpr u => [u.Operand],
        PostfixExpr p => [p.Operand],
        ResumeExpr re => [re.Coroutine],
        BinaryExpr b => [b.Left, b.Right],
        RangeExpr r => [r.Low, r.High],
        CastExpr c => [c.Operand],
        CallExpr call => [call.Callee, .. call.Arguments],
        IndexExpr ix => [ix.Target, ix.Index],
        MemberExpr m => [m.Target],
        ArrayLitExpr arr => arr.Elements,
        TupleLitExpr tu => tu.Elements,
        StructInitExpr si => si.Fields.Select(f => f.Value),
        InterpolatedStringExpr fs => fs.Segments.OfType<InterpHole>().Select(h => h.Expr),
        IfExpr iff => [iff.Condition, iff.Then, iff.Else],
        MatchExpr ma => new[] { ma.Scrutinee }.Concat(ma.Arms.Where(a => a.Body is Expr).Select(a => (Expr)a.Body)),
        AssignExpr a => [a.Target, a.Value],
        _ => []
    };
}
