using Lyric.AST;
using Lyric.Core;
using Lyric.Resolver;

namespace Lyric.Sema;

/// <summary>
/// Typprüfung von Ausdrücken (M3-Slice 2a, Sprache.md §4/§6/§7). Läuft durch Funktions
/// -Bodies, verwaltet lokale Scopes (Params → Block-Locals), löst Ausdrucks-Namen auf
/// und weist jedem Ausdruck einen <see cref="LyrType"/> zu (in <see cref="TypeResult"/>).
///
/// Numerik strikt (①A): beide Operanden gleicher Typ; nur untyped Literale passen sich
/// per Range-Fit an (②a). `+`/`*` auch für string/T[] (concat/repeat). `as` nur
/// Numerik↔Numerik (④). Nullable-Narrowing und Mutabilitäts-/Regel-Checks → Slice 3;
/// Calls/Member/Struct-Init/if-Ausdruck/match/Lambda → Slice 2b (hier vorerst Error).
/// </summary>
public sealed class TypeChecker
{
    private readonly Compilation _comp;
    private readonly BindingResult _binding;
    private readonly DiagnosticEngine _de;
    private readonly TypeResult _result = new();
    private readonly Dictionary<GlobalSymbol, LyrType> _globals = new(ReferenceEqualityComparer.Instance);
    private readonly TypeSymbol? _throwable; // Builtin-Interface Throwable (§9)
    private readonly FunctionSymbol? _panic; // Builtin panic → never (§9)

    private LyrType _currentReturn = LyrType.Void;
    private LyrType? _currentThis;
    private Dictionary<Symbol, LyrType> _narrowed = new(ReferenceEqualityComparer.Instance); // ?T → T im bewiesen-non-null-Bereich

    public TypeChecker(Compilation comp, BindingResult binding, DiagnosticEngine de)
    {
        _comp = comp;
        _binding = binding;
        _de = de;
        _throwable = comp.Builtins.LookupLocal("Throwable") as TypeSymbol;
        _panic = comp.Builtins.LookupLocal("panic") as FunctionSymbol;
    }

    public TypeResult Check()
    {
        foreach (var module in _comp.Modules) ComputeGlobals(module);
        foreach (var module in _comp.Modules)
            foreach (var decl in _comp.AstOf(module).Declarations)
                CheckDecl(decl, module);
        new FlowAnalyzer(_comp, _result, _de).Run(); // DAA (definite assignment)
        return _result;
    }

    // --- Deklarationen ---

    private void ComputeGlobals(ModuleSymbol module)
    {
        foreach (var decl in _comp.AstOf(module).Declarations)
        {
            if (decl is not GlobalBindingDecl g) continue;
            var declared = g.Binding.Type is not null ? ResolveType(g.Binding.Type, module.Members) : null;
            LyrType type;
            if (g.Binding.Initializer is not null)
            {
                var initT = CheckExpr(g.Binding.Initializer, module.Members);
                if (declared is not null) { CheckAssignable(g.Binding.Initializer, initT, declared, g.Span); type = declared; }
                else type = initT;
            }
            else type = declared ?? LyrType.Error;

            if (module.Members.LookupLocal(g.Binding.Name) is GlobalSymbol gs) _globals[gs] = type;
        }
    }

    private void CheckDecl(Decl decl, ModuleSymbol module)
    {
        switch (decl)
        {
            case FunctionDecl fn: CheckFunction(fn, module.Members, thisType: null); break;
            case StructDecl s: CheckMethods(s.Name, s.Members, module); break;
            case ClassDecl c: CheckMethods(c.Name, c.Members, module); break;
            case EnumDecl e: CheckEnumMethods(e, module); break;
            case InterfaceDecl i: CheckMethods(i.Name, i.Members, module); break;
            case ExtendDecl ex: CheckExtend(ex, module); break;
            // GlobalBindingDecl: schon in ComputeGlobals geprüft.
        }
    }

    private void CheckMethods(string typeName, Decl[] members, ModuleSymbol module)
    {
        if (module.Members.LookupLocal(typeName) is not TypeSymbol ts) return;
        var thisType = SelfType(ts);
        foreach (var m in members)
            if (m is FunctionDecl fn)
                CheckFunction(fn, ts.Members, thisType);
    }

    private void CheckEnumMethods(EnumDecl e, ModuleSymbol module)
    {
        if (module.Members.LookupLocal(e.Name) is not TypeSymbol ts) return;
        var thisType = SelfType(ts);
        foreach (var fn in e.Methods) CheckFunction(fn, ts.Members, thisType);
    }

    // `this` innerhalb einer Methode: bei generischem Typ die Selbst-Instanz Stack<T>
    // (Typ-Params als Argumente), sonst schlicht die Referenz.
    private static LyrType SelfType(TypeSymbol ts) =>
        ts.Generics.Length == 0
            ? new NamedRef(ts)
            : new GenericInstance(ts, Array.ConvertAll(ts.Generics, g => (LyrType)new TypeParamType(g)));

    private void CheckExtend(ExtendDecl ex, ModuleSymbol module)
    {
        var target = ResolveType(ex.Target, module.Members);
        var (scope, thisType) = target is NamedRef nr
            ? (nr.Symbol.Members, (LyrType?)nr)
            : (module.Members, null);
        foreach (var fn in ex.Methods) CheckFunction(fn, scope, thisType);
    }

    private void CheckFunction(FunctionDecl fn, SymbolTable outerScope, LyrType? thisType)
    {
        var savedReturn = _currentReturn;
        var savedThis = _currentThis;
        var savedNarrowed = _narrowed;
        _currentThis = thisType;
        _narrowed = new(ReferenceEqualityComparer.Instance);

        var scope = new SymbolTable(outerScope);
        // Funktions-eigene Typ-Params (fn map<U>) in den Body-Scope; Signatur-Typen sind schon
        // vom Resolver gebunden, aber Body-Typen (let x: U) lösen nur über diesen Scope auf.
        if (outerScope.LookupLocal(fn.Name) is FunctionSymbol fsym)
            foreach (var g in fsym.Generics) scope.TryDeclare(g);
        foreach (var p in fn.Parameters)
        {
            var pt = ResolveType(p.Type, scope);
            var ps = new ParameterSymbol(p.Name, pt, p);
            scope.TryDeclare(ps);
            _result.BindRef(p, ps); // für DAA
            if (p.Default is not null)
                CheckAssignable(p.Default, CheckExpr(p.Default, scope), pt, p.Span);
        }
        _currentReturn = fn.ReturnType is not null ? ResolveType(fn.ReturnType, scope) : LyrType.Void;
        CheckThrowsClause(fn, scope);

        if (fn.Body is not null)
        {
            CheckBlock(fn.Body, scope);
            if (!TypeFacts.IsVoid(_currentReturn) && !Flow.AlwaysReturns(fn.Body, _result))
                _de.Report("LYR-SEM0017", Severity.Error, fn.Span, $"not all code paths of '{fn.Name}' return a value");
        }

        _currentReturn = savedReturn;
        _currentThis = savedThis;
        _narrowed = savedNarrowed;
    }

    // --- Statements ---

    private void CheckBlock(Block block, SymbolTable parent)
    {
        var scope = new SymbolTable(parent);
        var savedNarrowed = new Dictionary<Symbol, LyrType>(_narrowed, ReferenceEqualityComparer.Instance);
        foreach (var stmt in block.Statements) CheckStmt(stmt, scope);
        _narrowed = savedNarrowed; // im Block etablierte Narrowings (Early-Exit) enden hier
    }

    private void CheckStmt(Stmt stmt, SymbolTable scope)
    {
        switch (stmt)
        {
            case Block b: CheckBlock(b, scope); break;
            case BindingStmt bnd: CheckBinding(bnd, scope); break;
            case IfStmt f: CheckIf(f, scope); break;
            case WhileStmt w: CheckCondition(w.Condition, scope); CheckBlock(w.Body, scope); break;
            case DoWhileStmt d: CheckBlock(d.Body, scope); CheckCondition(d.Condition, scope); break;
            case ForInStmt fo: CheckForIn(fo, scope); break;
            case ReturnStmt r:
                if (r.Value is not null) CheckAssignable(r.Value, CheckExpr(r.Value, scope, _currentReturn), _currentReturn, r.Span);
                else if (!TypeFacts.IsVoid(_currentReturn))
                    _de.Report("LYR-SEM0001", Severity.Error, r.Span, "return without a value in a non-void function");
                break;
            case ExprStmt es: CheckExpr(es.Expr, scope); break;
            case ThrowStmt t:
                var thrown = CheckExpr(t.Value, scope);
                if (!Conformance.IsThrowable(thrown, _throwable, _binding))
                    _de.Report("LYR-SEM0030", Severity.Error, t.Span,
                        $"cannot throw '{TypeFacts.Display(thrown)}' — only types implementing 'Throwable' can be thrown");
                break;
            case YieldStmt y: if (y.Value is not null) CheckExpr(y.Value, scope); break;
            case ResumeStmt re:
                CheckExpr(re.Coroutine, scope);
                if (re.Value is not null) CheckExpr(re.Value, scope);
                break;
            case DeferStmt de: CheckStmt(de.Body, scope); break;
            case TryStmt tr:
                CheckBlock(tr.Body, scope);
                foreach (var c in tr.Catches) CheckCatch(c, scope);
                break;
            case MatchStmt m: CheckMatch(m, m.Scrutinee, m.Arms, scope, asExpression: false); break;
            // Break/Continue/Error: nichts zu prüfen.
        }
    }

    private void CheckBinding(BindingStmt bnd, SymbolTable scope)
    {
        var declared = bnd.Type is not null ? ResolveType(bnd.Type, scope) : null;
        var initT = bnd.Initializer is not null ? CheckExpr(bnd.Initializer, scope, declared) : null;

        LyrType type;
        if (declared is not null && initT is not null) { CheckAssignable(bnd.Initializer!, initT, declared, bnd.Span); type = declared; }
        else if (declared is not null) type = declared;
        else if (initT is not null) type = initT;
        else { _de.Report("LYR-SEM0010", Severity.Error, bnd.Span, $"binding '{bnd.Name}' needs a type or an initializer"); type = LyrType.Error; }

        var local = new LocalSymbol(bnd.Name, type, bnd.IsMutable, bnd);
        scope.TryDeclare(local);
        _result.BindRef(bnd, local); // für DAA
    }

    private void CheckForIn(ForInStmt fo, SymbolTable scope)
    {
        var iterType = CheckExpr(fo.Iterable, scope);
        var elem = iterType switch
        {
            ArrayOf a => a.Element,
            RangeOf r => r.Element,
            PrimitiveType { Kind: PrimitiveKind.String } => LyrType.Char,
            ErrorType => LyrType.Error,
            _ => Report(fo.Iterable.Span, "LYR-SEM0007", $"'{TypeFacts.Display(iterType)}' is not iterable")
        };
        var loopScope = new SymbolTable(scope);
        var loopVar = new LocalSymbol(fo.Variable, elem, false, fo);
        loopScope.TryDeclare(loopVar);
        _result.BindRef(fo, loopVar); // für DAA
        CheckBlock(fo.Body, loopScope);
    }

    // throws-Klausel (§9): deklarierter Typ muss werfbar sein; das aufgelöste Symbol wird
    // für den ExceptionAnalyzer an die Klausel gebunden.
    private void CheckThrowsClause(FunctionDecl fn, SymbolTable scope)
    {
        if (fn.Throws?.Type is not { } tn) return;
        var t = ResolveType(tn, scope);
        if (!Conformance.IsThrowable(t, _throwable, _binding))
            _de.Report("LYR-SEM0030", Severity.Error, fn.Throws.Span,
                $"'{TypeFacts.Display(t)}' in 'throws' does not implement 'Throwable'");
        if (t is NamedRef nr) _result.BindRef(fn.Throws, nr.Symbol);
    }

    private void CheckCatch(CatchClause clause, SymbolTable scope)
    {
        var catchScope = new SymbolTable(scope);
        LyrType bt;
        if (clause.BindingType is not null)
        {
            bt = ResolveType(clause.BindingType, scope);
            if (!Conformance.IsThrowable(bt, _throwable, _binding))
                _de.Report("LYR-SEM0030", Severity.Error, clause.BindingType.Span,
                    $"cannot catch '{TypeFacts.Display(bt)}' — only types implementing 'Throwable' can be caught");
            if (bt is NamedRef nr) _result.BindRef(clause.BindingType, nr.Symbol); // für den ExceptionAnalyzer
        }
        else bt = _throwable is not null ? new NamedRef(_throwable) : LyrType.Error; // Catch-All bindet Throwable

        if (clause.BindingName is not null)
        {
            var local = new LocalSymbol(clause.BindingName, bt, false, clause);
            catchScope.TryDeclare(local);
            _result.BindRef(clause, local); // für DAA: der Catch weist die Bindung zu
        }
        CheckBlock(clause.Body, catchScope);
    }

    private void CheckCondition(Expr cond, SymbolTable scope)
    {
        var t = CheckExpr(cond, scope);
        if (!TypeFacts.IsBool(t) && !t.IsError)
            _de.Report("LYR-SEM0004", Severity.Error, cond.Span, $"condition must be 'bool', got '{TypeFacts.Display(t)}'");
    }

    // if mit Nullable-Narrowing (§7): 'if (x != null)' engt x im then-Zweig auf T ein;
    // 'if (x == null) { return; }' engt x danach ein (Early-Exit, D1b).
    private void CheckIf(IfStmt f, SymbolTable scope)
    {
        CheckCondition(f.Condition, scope);
        var (thenFacts, elseFacts) = NarrowingFacts(f.Condition);

        var snapshot = new Dictionary<Symbol, LyrType>(_narrowed, ReferenceEqualityComparer.Instance);
        Apply(thenFacts);
        CheckBlock(f.Then, scope);
        _narrowed = snapshot;

        if (f.Else is not null)
        {
            snapshot = new Dictionary<Symbol, LyrType>(_narrowed, ReferenceEqualityComparer.Instance);
            Apply(elseFacts);
            CheckStmt(f.Else, scope);
            _narrowed = snapshot;
        }

        if (Flow.AlwaysReturns(f.Then, _result)) Apply(elseFacts);
        else if (f.Else is not null && Flow.AlwaysReturns(f.Else, _result)) Apply(thenFacts);
    }

    private void Apply(Dictionary<Symbol, LyrType> facts)
    {
        foreach (var (sym, type) in facts) _narrowed[sym] = type;
    }

    private (Dictionary<Symbol, LyrType> then, Dictionary<Symbol, LyrType> els) NarrowingFacts(Expr cond)
    {
        var then = new Dictionary<Symbol, LyrType>(ReferenceEqualityComparer.Instance);
        var els = new Dictionary<Symbol, LyrType>(ReferenceEqualityComparer.Instance);
        if (cond is BinaryExpr { Operator: BinaryOp.Ne or BinaryOp.Eq } b
            && NullCompared(b) is { } id
            && _result.RefOf(id) is { } sym
            && DeclaredType(sym) is Optional opt)
        {
            (b.Operator == BinaryOp.Ne ? then : els)[sym] = opt.Inner;
        }
        return (then, els);
    }

    private static IdentifierExpr? NullCompared(BinaryExpr b) => b switch
    {
        { Left: IdentifierExpr l, Right: NullLiteralExpr } => l,
        { Left: NullLiteralExpr, Right: IdentifierExpr r } => r,
        _ => null
    };

    // --- Ausdrücke ---

    // 'expected' ist der Kontext-Typ der Position (Binding-Typ, Return-Typ, Feld-Typ, …).
    // Er wird nur von den Formen genutzt, die ihn brauchen (leeres Array-Literal,
    // kontextuelle Enum-Varianten-Konstruktion §3.4); alles andere ignoriert ihn.
    private LyrType CheckExpr(Expr expr, SymbolTable scope, LyrType? expected = null)
    {
        var type = Compute(expr, scope, expected);
        _result.SetType(expr, type);
        return type;
    }

    private LyrType Compute(Expr expr, SymbolTable scope, LyrType? expected)
    {
        switch (expr)
        {
            case IntLiteralExpr il: return il.Suffix is { } isx ? IntSuffixType(isx) : LyrType.Int;
            case FloatLiteralExpr fl: return fl.Suffix is { } fsx ? FloatSuffixType(fsx) : LyrType.Float;
            case StringLiteralExpr: return LyrType.String;
            case CharLiteralExpr: return LyrType.Char;
            case BoolLiteralExpr: return LyrType.Bool;
            case NullLiteralExpr: return LyrType.Null;
            case IdentifierExpr id: return CheckIdentifier(id, scope);
            case ThisExpr t:
                if (_currentThis is not null) return _currentThis;
                return Report(t.Span, "LYR-SEM0008", "'this' is only valid inside a method");
            case UnaryExpr u: return CheckUnary(u, scope);
            case PostfixExpr p: return CheckPostfix(p, scope);
            case BinaryExpr b: return CheckBinary(b, scope);
            case AssignExpr a: return CheckAssign(a, scope);
            case RangeExpr r: return CheckRange(r, scope);
            case CastExpr c: return CheckCast(c, scope);
            case IndexExpr ix: return CheckIndex(ix, scope);
            case ArrayLitExpr arr: return CheckArrayLit(arr, scope, expected);
            case TupleLitExpr tu: return new TupleOf(tu.Elements.Select(e => CheckExpr(e, scope)).ToArray());
            case InterpolatedStringExpr fs:
                foreach (var seg in fs.Segments) if (seg is InterpHole h) CheckExpr(h.Expr, scope);
                return LyrType.String;
            case ErrorExpr: return LyrType.Error;

            case CallExpr call: return CheckCall(call, scope);
            case MemberExpr mem: return CheckMember(mem, scope);
            case StructInitExpr si: return CheckStructInit(si, scope, expected);
            case IfExpr iff: return CheckIfExpr(iff, scope);
            case MatchExpr ma: return UnifyArms(CheckMatch(ma, ma.Scrutinee, ma.Arms, scope, asExpression: true), ma.Span);
            case LambdaExpr lam: return CheckLambda(lam, scope);
            case AtIdentifierExpr: return LyrType.Error; // Attribute haben in v1 keinen Ausdruckstyp

            default: return LyrType.Error;
        }
    }

    private LyrType CheckIdentifier(IdentifierExpr id, SymbolTable scope)
    {
        var sym = scope.Lookup(id.Name);
        if (sym is null) return Report(id.Span, "LYR-SEM0002", $"unknown identifier '{id.Name}'");
        _result.BindRef(id, sym);
        if (_narrowed.TryGetValue(sym, out var narrowed)) return narrowed; // ?T → T im narrowten Bereich
        return sym switch
        {
            ParameterSymbol p => p.Type,
            LocalSymbol l => l.Type,
            GlobalSymbol g => _globals.TryGetValue(g, out var t) ? t : LyrType.Error,
            FunctionSymbol f => FnTypeOf(f),
            _ => LyrType.Error // Type/Module/Import als Wert → Member-Zugriff in Slice 2b
        };
    }

    private FnType FnTypeOf(FunctionSymbol f)
    {
        var fn = (FunctionDecl)f.Declaration!;
        var ps = fn.Parameters.Select(p => ResolveType(p.Type, _comp.Builtins)).ToArray();
        var ret = ReferenceEquals(f, _panic) ? LyrType.Never // §9: panic hat den unbenennbaren Typ never
            : fn.ReturnType is not null ? ResolveType(fn.ReturnType, _comp.Builtins) : LyrType.Void;
        return new FnType(ps, ret);
    }

    private LyrType CheckUnary(UnaryExpr u, SymbolTable scope)
    {
        var t = CheckExpr(u.Operand, scope);
        if (t.IsError) return LyrType.Error;
        switch (u.Operator)
        {
            case UnaryOp.Not:
                if (!TypeFacts.IsBool(t)) BadOp(u.Span, "!", t);
                return LyrType.Bool;
            case UnaryOp.Neg:
                if (!TypeFacts.IsNumeric(t)) BadOp(u.Span, "-", t);
                return t;
            case UnaryOp.BitNot:
                if (!TypeFacts.IsInteger(t)) BadOp(u.Span, "~", t);
                return t;
            default: // PreInc/PreDec
                if (!TypeFacts.IsNumeric(t)) BadOp(u.Span, "++/--", t);
                return t;
        }
    }

    private LyrType CheckPostfix(PostfixExpr p, SymbolTable scope)
    {
        var t = CheckExpr(p.Operand, scope);
        if (t.IsError) return LyrType.Error;
        switch (p.Operator)
        {
            case PostfixOp.ForceUnwrap:
                if (t is Optional o) return o.Inner;
                _de.Report("LYR-SEM0005", Severity.Error, p.Span, $"cannot force-unwrap non-nullable '{TypeFacts.Display(t)}'");
                return t;
            default: // Inc/Dec
                if (!TypeFacts.IsNumeric(t)) BadOp(p.Span, "++/--", t);
                return t;
        }
    }

    private LyrType CheckBinary(BinaryExpr b, SymbolTable scope)
    {
        var l = CheckExpr(b.Left, scope);
        var r = CheckExpr(b.Right, scope);
        if (l.IsError || r.IsError) return LyrType.Error;

        switch (b.Operator)
        {
            case BinaryOp.Add: return CheckAdd(b, l, r);
            case BinaryOp.Mul: return CheckMul(b, l, r);
            case BinaryOp.Sub or BinaryOp.Div or BinaryOp.Rem:
                return UnifyNumeric(b.Left, l, b.Right, r) ?? BadBinary(b, l, r);
            case BinaryOp.Shl or BinaryOp.Shr or BinaryOp.BitAnd or BinaryOp.BitXor or BinaryOp.BitOr:
                if (TypeFacts.IsInteger(l) && TypeFacts.IsInteger(r))
                    return UnifyNumeric(b.Left, l, b.Right, r) ?? BadBinary(b, l, r);
                return BadBinary(b, l, r);
            case BinaryOp.Lt or BinaryOp.Le or BinaryOp.Gt or BinaryOp.Ge:
                if (UnifyNumeric(b.Left, l, b.Right, r) is not null
                    || (TypeFacts.IsString(l) && TypeFacts.IsString(r))
                    || (LyrType.Equal(l, r) && l is PrimitiveType { Kind: PrimitiveKind.Char }))
                    return LyrType.Bool;
                BadBinary(b, l, r);
                return LyrType.Bool;
            case BinaryOp.Eq or BinaryOp.Ne:
                if (!LyrType.Equal(l, r) && UnifyNumeric(b.Left, l, b.Right, r) is null
                    && l is not NullType && r is not NullType)
                    BadBinary(b, l, r);
                return LyrType.Bool;
            case BinaryOp.LogicalAnd or BinaryOp.LogicalOr:
                if (!TypeFacts.IsBool(l) || !TypeFacts.IsBool(r)) BadBinary(b, l, r);
                return LyrType.Bool;
            default: // Coalesce
                return CheckCoalesce(b, l, r);
        }
    }

    private LyrType CheckAdd(BinaryExpr b, LyrType l, LyrType r)
    {
        if (UnifyNumeric(b.Left, l, b.Right, r) is { } n) return n;
        if (TypeFacts.IsString(l) && TypeFacts.IsString(r)) return LyrType.String;      // "a" + "b"
        if (l is ArrayOf la && r is ArrayOf ra && LyrType.Equal(la.Element, ra.Element)) // [..] + [..]
            return new ArrayOf(la.Element, null);
        return BadBinary(b, l, r);
    }

    private LyrType CheckMul(BinaryExpr b, LyrType l, LyrType r)
    {
        if (UnifyNumeric(b.Left, l, b.Right, r) is { } n) return n;
        if (TypeFacts.IsString(l) && TypeFacts.IsInteger(r)) return LyrType.String;   // "x" * 3
        if (TypeFacts.IsString(r) && TypeFacts.IsInteger(l)) return LyrType.String;   // 3 * "x"
        if (l is ArrayOf la && TypeFacts.IsInteger(r)) return new ArrayOf(la.Element, null); // [0] * 5
        if (r is ArrayOf ra && TypeFacts.IsInteger(l)) return new ArrayOf(ra.Element, null);
        return BadBinary(b, l, r);
    }

    private LyrType CheckCoalesce(BinaryExpr b, LyrType l, LyrType r)
    {
        if (l is Optional o)
        {
            if (IsAssignable(b.Right, r, o.Inner)) return o.Inner; // ?T ?? T → T
            if (IsAssignable(b.Right, r, l)) return l;              // ?T ?? ?T → ?T
            BadBinary(b, l, r);
            return o.Inner;
        }
        if (l is NullType) return r; // null ?? b → typeof b
        return l; // linke Seite nicht nullable — ?? ist wirkungslos, aber kein Typfehler
    }

    private LyrType CheckAssign(AssignExpr a, SymbolTable scope)
    {
        CheckExpr(a.Target, scope); // bindet RefOf
        var targetSym = a.Target is IdentifierExpr ? _result.RefOf(a.Target) : null;
        // Bei Identifier-Zielen den DEKLARIERTEN Typ nehmen (nicht den narrowten) — sonst
        // wäre 'x = null' auf einem narrowten ?T fälschlich ein Fehler.
        var targetType = targetSym is not null ? DeclaredType(targetSym) ?? _result.TypeOf(a.Target) : _result.TypeOf(a.Target);
        var value = CheckExpr(a.Value, scope, targetType);
        // Lvalue-/Mutabilitäts-Check → Slice 3b. Hier nur Typ-Kompatibilität.
        CheckAssignable(a.Value, value, targetType, a.Span);
        if (targetSym is not null) _narrowed.Remove(targetSym); // Neuzuweisung hebt Narrowing auf
        return targetType;
    }

    private static LyrType? DeclaredType(Symbol s) => s switch
    {
        LocalSymbol l => l.Type,
        ParameterSymbol p => p.Type,
        _ => null
    };

    private LyrType CheckRange(RangeExpr r, SymbolTable scope)
    {
        var lo = CheckExpr(r.Low, scope);
        var hi = CheckExpr(r.High, scope);
        var elem = UnifyNumeric(r.Low, lo, r.High, hi);
        if (elem is null && !lo.IsError && !hi.IsError)
            _de.Report("LYR-SEM0003", Severity.Error, r.Span, $"range bounds must be matching numerics, got '{TypeFacts.Display(lo)}' and '{TypeFacts.Display(hi)}'");
        return new RangeOf(elem ?? LyrType.Error);
    }

    private LyrType CheckCast(CastExpr c, SymbolTable scope)
    {
        var op = CheckExpr(c.Operand, scope);
        var target = ResolveType(c.Type, scope);
        if (op.IsError || target.IsError) return target;
        if (TypeFacts.IsNumeric(op) && TypeFacts.IsNumeric(target)) return target; // ④ nur Numerik↔Numerik
        _de.Report("LYR-SEM0006", Severity.Error, c.Span, $"cannot cast '{TypeFacts.Display(op)}' to '{TypeFacts.Display(target)}'");
        return target;
    }

    private LyrType CheckIndex(IndexExpr ix, SymbolTable scope)
    {
        var target = CheckExpr(ix.Target, scope);
        var index = CheckExpr(ix.Index, scope);
        if (!TypeFacts.IsInteger(index) && !index.IsError)
            _de.Report("LYR-SEM0007", Severity.Error, ix.Index.Span, $"index must be an integer, got '{TypeFacts.Display(index)}'");
        return target switch
        {
            ArrayOf a => a.Element,
            PrimitiveType { Kind: PrimitiveKind.String } => LyrType.Char,
            ErrorType => LyrType.Error,
            _ => Report(ix.Span, "LYR-SEM0007", $"'{TypeFacts.Display(target)}' is not indexable")
        };
    }

    private LyrType CheckArrayLit(ArrayLitExpr arr, SymbolTable scope, LyrType? expected)
    {
        var elemExpected = expected is ArrayOf ea ? ea.Element : null;
        if (arr.Elements.Length == 0)
            return new ArrayOf(elemExpected ?? LyrType.Error, null); // leer: Element-Typ nur aus dem Kontext
        var first = CheckExpr(arr.Elements[0], scope, elemExpected);
        for (var i = 1; i < arr.Elements.Length; i++)
        {
            var t = CheckExpr(arr.Elements[i], scope, elemExpected);
            if (!LyrType.Equal(t, first) && !t.IsError && !first.IsError)
                _de.Report("LYR-SEM0009", Severity.Error, arr.Elements[i].Span,
                    $"array elements must share a type: '{TypeFacts.Display(first)}' vs '{TypeFacts.Display(t)}'");
        }
        return new ArrayOf(first, null);
    }

    // --- Calls / Member / Struct-Init / composite (Slice 2b) ---

    private LyrType CheckCall(CallExpr call, SymbolTable scope)
    {
        var calleeType = CheckExpr(call.Callee, scope);
        var argTypes = call.Arguments.Select(a => CheckExpr(a, scope)).ToArray();
        if (calleeType.IsError) return LyrType.Error;
        if (calleeType is not FnType fn)
            return Report(call.Span, "LYR-SEM0013", $"'{TypeFacts.Display(calleeType)}' is not callable");

        var fsym = _result.RefOf(call.Callee) as FunctionSymbol;
        var decl = fsym?.Declaration as FunctionDecl;

        // Generische Funktion: Typ-Args aus den Argumenten inferieren, Signatur substituieren.
        if (fsym is { Generics.Length: > 0 })
        {
            var map = InferTypeArgs(fn, argTypes);
            CheckInferredConstraints(fsym.Generics, map, call.Span);
            fn = (FnType)Substitute(fn, map);
        }
        CheckCallArgs(call, fn, argTypes, decl);
        return fn.Return;
    }

    private void CheckCallArgs(CallExpr call, FnType fn, LyrType[] argTypes, FunctionDecl? decl)
    {
        var ps = decl?.Parameters;
        var variadic = ps is { Length: > 0 } && ps[^1].IsParams;
        var fixedCount = variadic ? ps!.Length - 1 : ps?.Length ?? fn.Parameters.Length;
        var minRequired = ps is null ? fn.Parameters.Length : ps.Take(fixedCount).Count(p => p.Default is null);

        if (argTypes.Length < minRequired || (!variadic && argTypes.Length > fn.Parameters.Length))
            _de.Report("LYR-SEM0014", Severity.Error, call.Span,
                $"call expects {(variadic ? $"at least {minRequired}" : minRequired == fn.Parameters.Length ? minRequired.ToString() : $"{minRequired}–{fn.Parameters.Length}")} argument(s), got {argTypes.Length}");

        for (var i = 0; i < argTypes.Length && i < fixedCount && i < fn.Parameters.Length; i++)
            CheckAssignable(call.Arguments[i], argTypes[i], fn.Parameters[i], call.Arguments[i].Span);

        if (variadic && fn.Parameters[^1] is ArrayOf elem)
            for (var i = fixedCount; i < argTypes.Length; i++)
                CheckAssignable(call.Arguments[i], argTypes[i], elem.Element, call.Arguments[i].Span);
    }

    private LyrType CheckMember(MemberExpr mem, SymbolTable scope)
    {
        var targetType = CheckExpr(mem.Target, scope);
        switch (TargetSymbol(mem.Target))
        {
            case TypeSymbol ts: return BindMember(mem, MemberOfType(ts, mem.Member, mem.Span));
            case ModuleSymbol mod: return BindMember(mem, MemberOfModule(mod, mem.Member, mem.Span));
            case ExternalSymbol: return LyrType.Error;
        }

        var baseType = mem.IsOptional && targetType is Optional opt ? opt.Inner : targetType;
        if (InstanceMemberOf(baseType, mem, mem.Span) is { } mt)
            return mem.IsOptional ? new Optional(mt) : mt;
        if (targetType.IsError) return LyrType.Error;
        return Report(mem.Span, "LYR-SEM0012", $"'{TypeFacts.Display(targetType)}' has no member '{mem.Member}'");
    }

    // Instanz-Member über die drei „Objekt"-Typen: konkret (NamedRef), generische Instanz
    // (Member-Typ mit T→Argument substituiert), oder Typ-Param (Member aus den Constraints).
    private LyrType? InstanceMemberOf(LyrType baseType, MemberExpr mem, Span span)
    {
        switch (baseType)
        {
            case NamedRef nr:
                return BindMember(mem, InstanceMember(nr.Symbol, mem.Member, span));
            case GenericInstance gi:
                var (t, s) = InstanceMember(gi.Definition, mem.Member, span);
                if (s is not null) _result.BindRef(mem, s);
                return Substitute(t, SubstMap(gi));
            case TypeParamType tp:
                return BindMember(mem, MemberOfTypeParam(tp.Param, mem.Member, span));
            default:
                return null;
        }
    }

    // Member auf einem Typ-Param T: nur was seine Constraint-Interfaces bereitstellen (D2).
    private (LyrType, Symbol?) MemberOfTypeParam(GenericParamSymbol gp, string member, Span span)
    {
        foreach (var c in gp.Constraints)
        {
            if (c is not NamedType nt) continue;
            var csym = _binding.Resolve(nt);
            if (csym is ImportBindingSymbol ib) csym = ib.Target;
            if (csym is TypeSymbol { Kind: TypeSymbolKind.Interface } it
                && it.Members.LookupLocal(member) is FunctionSymbol fn)
                return (FnTypeOf(fn), fn);
        }
        return (Report(span, "LYR-SEM0027",
            $"type parameter '{gp.Name}' has no member '{member}' (no constraint provides it)"), null);
    }

    private static Dictionary<GenericParamSymbol, LyrType> SubstMap(GenericInstance gi)
    {
        var map = new Dictionary<GenericParamSymbol, LyrType>(ReferenceEqualityComparer.Instance);
        var n = Math.Min(gi.Definition.Generics.Length, gi.Arguments.Length);
        for (var i = 0; i < n; i++) map[gi.Definition.Generics[i]] = gi.Arguments[i];
        return map;
    }

    // T → Argument über einen Typ hinweg (Stack<T>.items: T[]  →  int[] bei Stack<int>).
    private static LyrType Substitute(LyrType type, Dictionary<GenericParamSymbol, LyrType> map)
    {
        if (map.Count == 0) return type;
        return type switch
        {
            TypeParamType tp => map.TryGetValue(tp.Param, out var m) ? m : tp,
            Optional o => new Optional(Substitute(o.Inner, map)),
            ArrayOf a => new ArrayOf(Substitute(a.Element, map), a.Size),
            TupleOf t => new TupleOf(t.Elements.Select(e => Substitute(e, map)).ToArray()),
            FnType f => new FnType(f.Parameters.Select(p => Substitute(p, map)).ToArray(), Substitute(f.Return, map)),
            GenericInstance gi => new GenericInstance(gi.Definition, gi.Arguments.Select(a => Substitute(a, map)).ToArray()),
            RangeOf r => new RangeOf(Substitute(r.Element, map)),
            _ => type // Primitive / NamedRef / Error / Null
        };
    }

    // --- Generische Call-Inferenz + Constraint-Erfüllung (Slice 1b) ---

    private Dictionary<GenericParamSymbol, LyrType> InferTypeArgs(FnType fn, LyrType[] args)
    {
        var map = new Dictionary<GenericParamSymbol, LyrType>(ReferenceEqualityComparer.Instance);
        var n = Math.Min(fn.Parameters.Length, args.Length);
        for (var i = 0; i < n; i++) UnifyInfer(fn.Parameters[i], args[i], map);
        return map;
    }

    // Löst Typ-Params aus (param trägt T, arg ist konkret). Erste Bindung gewinnt;
    // Widersprüche (pair(1, "x")) fallen später in CheckCallArgs als Typfehler auf.
    private static void UnifyInfer(LyrType param, LyrType arg, Dictionary<GenericParamSymbol, LyrType> map)
    {
        switch (param)
        {
            case TypeParamType tp:
                if (!arg.IsError) map.TryAdd(tp.Param, arg);
                break;
            case ArrayOf pa when arg is ArrayOf aa: UnifyInfer(pa.Element, aa.Element, map); break;
            case Optional po when arg is Optional ao: UnifyInfer(po.Inner, ao.Inner, map); break;
            case TupleOf pt when arg is TupleOf at && pt.Elements.Length == at.Elements.Length:
                for (var i = 0; i < pt.Elements.Length; i++) UnifyInfer(pt.Elements[i], at.Elements[i], map);
                break;
            case GenericInstance pg when arg is GenericInstance ag
                && ReferenceEquals(pg.Definition, ag.Definition) && pg.Arguments.Length == ag.Arguments.Length:
                for (var i = 0; i < pg.Arguments.Length; i++) UnifyInfer(pg.Arguments[i], ag.Arguments[i], map);
                break;
            case FnType pf when arg is FnType af && pf.Parameters.Length == af.Parameters.Length:
                for (var i = 0; i < pf.Parameters.Length; i++) UnifyInfer(pf.Parameters[i], af.Parameters[i], map);
                UnifyInfer(pf.Return, af.Return, map);
                break;
        }
    }

    private void CheckConstraints(GenericParamSymbol[] generics, LyrType[] args, Span span)
    {
        var n = Math.Min(generics.Length, args.Length);
        for (var i = 0; i < n; i++) CheckSatisfies(generics[i], args[i], span);
    }

    private void CheckInferredConstraints(GenericParamSymbol[] generics, Dictionary<GenericParamSymbol, LyrType> map, Span span)
    {
        foreach (var g in generics)
            if (map.TryGetValue(g, out var arg)) CheckSatisfies(g, arg, span);
    }

    private void CheckSatisfies(GenericParamSymbol param, LyrType arg, Span span)
    {
        foreach (var c in param.Constraints)
            if (ConstraintInterface(c) is { } iface && !Satisfies(arg, iface))
                _de.Report("LYR-SEM0028", Severity.Error, span,
                    $"type '{TypeFacts.Display(arg)}' does not satisfy constraint '{iface.Name}' on '{param.Name}'");
    }

    private TypeSymbol? ConstraintInterface(TypeNode c) => Conformance.InterfaceOf(c, _binding);

    // Erfüllt arg das Constraint iface? Nutzertypen über ihre deklarierte Interface-Liste;
    // Typ-Param über seine eigenen Constraints. Builtins/extern: lenient (Conformance erst M8).
    private bool Satisfies(LyrType arg, TypeSymbol iface) => arg switch
    {
        NamedRef nr => Conformance.Implements(nr.Symbol, iface, _binding),
        GenericInstance gi => Conformance.Implements(gi.Definition, iface, _binding),
        TypeParamType tp => tp.Param.Constraints.Any(c => ReferenceEquals(ConstraintInterface(c), iface)),
        _ => true // Primitive/extern/Error: opak durchlassen
    };

    private LyrType BindMember(MemberExpr mem, (LyrType type, Symbol? sym) r)
    {
        if (r.sym is not null) _result.BindRef(mem, r.sym);
        return r.type;
    }

    private Symbol? TargetSymbol(Expr target)
    {
        var sym = _result.RefOf(target);
        return sym is ImportBindingSymbol ib ? ib.Target : sym;
    }

    private (LyrType, Symbol?) InstanceMember(TypeSymbol ts, string member, Span span) =>
        ts.Members.LookupLocal(member) switch
        {
            FieldSymbol fs => (FieldType(fs), fs),
            FunctionSymbol fn => (FnTypeOf(fn), fn),
            _ => (Report(span, "LYR-SEM0012", $"'{ts.Name}' has no member '{member}'"), null)
        };

    private (LyrType, Symbol?) MemberOfType(TypeSymbol ts, string member, Span span) =>
        ts.Members.LookupLocal(member) switch
        {
            FunctionSymbol fn => (FnTypeOf(fn), fn),                 // statische Methode / Factory (Point.new)
            EnumVariantSymbol ev => (VariantConstructorType(ev, ts, span), ev),
            _ => (Report(span, "LYR-SEM0012", $"'{ts.Name}' has no static member '{member}'"), null)
        };

    private (LyrType, Symbol?) MemberOfModule(ModuleSymbol mod, string member, Span span) =>
        mod.Members.LookupLocal(member) switch
        {
            FunctionSymbol fn => (FnTypeOf(fn), fn),
            GlobalSymbol g => (_globals.TryGetValue(g, out var t) ? t : LyrType.Error, g),
            ExternalSymbol ex => (LyrType.Error, ex),
            TypeSymbol tsym => (LyrType.Error, tsym), // Typ als Wert → kein Ausdruckstyp
            _ => (Report(span, "LYR-SEM0012", $"module '{mod.FullName}' has no member '{member}'"), null)
        };

    private LyrType VariantConstructorType(EnumVariantSymbol ev, TypeSymbol enumTs, Span span)
    {
        var v = (EnumVariant)ev.Declaration!;
        if (v.TupleFields is not null)
            return new FnType(v.TupleFields.Select(t => ResolveType(t, enumTs.Members)).ToArray(), new NamedRef(enumTs));
        if (v.StructFields is not null)
            return Report(span, "LYR-SEM0031", $"struct variant '{ev.Name}' must be constructed with '{ev.Name} {{ … }}'");
        return new NamedRef(enumTs); // Unit-Variante als Wert
    }

    private LyrType FieldType(FieldSymbol fs) => ResolveType(((FieldDecl)fs.Declaration!).Type, _comp.Builtins);

    private LyrType CheckStructInit(StructInitExpr si, SymbolTable scope, LyrType? expected)
    {
        var (sym, owner) = ResolveInitPath(si.Path, scope);

        // Enum-Struct-Variante (§3.4): qualifiziert (Shape.Triangle { … }) oder kontextuell
        // (Triangle { … } in einer Position mit erwartetem Enum-Typ).
        if (sym is EnumVariantSymbol ev && owner is not null)
            return CheckVariantInit(si, ev, owner, ExpectedInstance(expected, owner), scope);
        if (sym is null && si.Path.Length == 1 && EnumFromExpected(expected) is { } ex
            && ex.def.Members.LookupLocal(si.Path[0]) is EnumVariantSymbol cev)
            return CheckVariantInit(si, cev, ex.def, ex.instance, scope);

        if (sym is not TypeSymbol ts)
        {
            if (sym is null) _de.Report("LYR-SEM0011", Severity.Error, si.Span, $"unknown type '{string.Join('.', si.Path)}'");
            foreach (var f in si.Fields) CheckExpr(f.Value, scope);
            return LyrType.Error;
        }

        // Generischer Typ: explizite Typ-Argumente (Stack<int> { }); Feld-Inferenz ist nicht dabei.
        LyrType result;
        Dictionary<GenericParamSymbol, LyrType> subst;
        if (ts.Generics.Length > 0 || si.TypeArguments.Length > 0)
        {
            var args = si.TypeArguments.Select(a => ResolveType(a, scope)).ToArray();
            if (args.Length != ts.Generics.Length)
                _de.Report("LYR-SEM0026", Severity.Error, si.Span,
                    $"generic type '{ts.Name}' expects {ts.Generics.Length} type argument(s), got {args.Length}");
            var gi = new GenericInstance(ts, args);
            subst = SubstMap(gi);
            CheckConstraints(ts.Generics, args, si.Span);
            result = gi;
        }
        else
        {
            result = new NamedRef(ts);
            subst = EmptySubst;
        }

        foreach (var field in si.Fields)
        {
            if (ts.Members.LookupLocal(field.Name) is FieldSymbol fs)
            {
                var ft = Substitute(FieldType(fs), subst);
                CheckAssignable(field.Value, CheckExpr(field.Value, scope, ft), ft, field.Span);
            }
            else
            {
                _de.Report("LYR-SEM0015", Severity.Error, field.Span, $"'{ts.Name}' has no field '{field.Name}'");
                CheckExpr(field.Value, scope);
            }
        }
        return result;
    }

    // Pfad-Auflösung für Struct-Init: läuft durch Module UND Typ-Member (Enum-Varianten).
    // Liefert das Endsymbol plus — falls Enum-Variante — den umgebenden Enum-Typ.
    private (Symbol? sym, TypeSymbol? owner) ResolveInitPath(string[] path, SymbolTable scope)
    {
        var cur = scope.Lookup(path[0]);
        if (cur is ImportBindingSymbol ib0) cur = ib0.Target;
        TypeSymbol? owner = null;
        for (var i = 1; i < path.Length && cur is not null; i++)
        {
            owner = cur as TypeSymbol;
            cur = cur switch
            {
                ModuleSymbol mod => mod.Members.LookupLocal(path[i]),
                TypeSymbol t => t.Members.LookupLocal(path[i]),
                _ => null
            };
            if (cur is ImportBindingSymbol ib) cur = ib.Target;
        }
        return (cur, cur is EnumVariantSymbol ? owner : null);
    }

    // Erwarteter Typ (Shape / ?Shape / Opt<int>) → Enum-Definition plus ggf. Instanz.
    private static (TypeSymbol def, GenericInstance? instance)? EnumFromExpected(LyrType? expected)
    {
        var t = expected is Optional o ? o.Inner : expected;
        return t switch
        {
            NamedRef { Symbol: { Kind: TypeSymbolKind.Enum } d } => (d, null),
            GenericInstance { Definition.Kind: TypeSymbolKind.Enum } gi => (gi.Definition, gi),
            _ => null
        };
    }

    private static GenericInstance? ExpectedInstance(LyrType? expected, TypeSymbol owner) =>
        EnumFromExpected(expected) is { } x && ReferenceEquals(x.def, owner) ? x.instance : null;

    private LyrType CheckVariantInit(StructInitExpr si, EnumVariantSymbol ev, TypeSymbol enumTs,
        GenericInstance? instance, SymbolTable scope)
    {
        _result.BindRef(si, ev);
        var v = (EnumVariant)ev.Declaration!;
        var subst = instance is not null ? SubstMap(instance) : EmptySubst;

        LyrType result;
        if (instance is not null) result = instance;
        else if (enumTs.Generics.Length > 0 || si.TypeArguments.Length > 0)
        {
            // Generisches Enum ohne Kontext-Instanz (bzw. Typ-Args an der Variante statt am Enum).
            _de.Report("LYR-SEM0026", Severity.Error, si.Span,
                $"generic enum '{enumTs.Name}' expects {enumTs.Generics.Length} type argument(s) from context");
            result = LyrType.Error;
        }
        else result = new NamedRef(enumTs);

        if (v.StructFields is not { } decls)
        {
            _de.Report("LYR-SEM0031", Severity.Error, si.Span, v.TupleFields is not null
                ? $"variant '{ev.Name}' has a tuple payload — construct it as '{ev.Name}(…)'"
                : $"variant '{ev.Name}' has no payload");
            foreach (var f in si.Fields) CheckExpr(f.Value, scope);
            return result;
        }

        foreach (var field in si.Fields)
        {
            if (Array.Find(decls, d => d.Name == field.Name) is { } fd)
            {
                var ft = Substitute(ResolveType(fd.Type, enumTs.Members), subst);
                CheckAssignable(field.Value, CheckExpr(field.Value, scope, ft), ft, field.Span);
            }
            else
            {
                _de.Report("LYR-SEM0015", Severity.Error, field.Span, $"variant '{ev.Name}' has no field '{field.Name}'");
                CheckExpr(field.Value, scope);
            }
        }
        return result;
    }

    private LyrType CheckIfExpr(IfExpr iff, SymbolTable scope)
    {
        CheckCondition(iff.Condition, scope);
        var thenT = CheckExpr(iff.Then, scope);
        var elseT = CheckExpr(iff.Else, scope);
        return Unify(iff.Then, thenT, iff.Else, elseT, iff.Span);
    }

    private LyrType Unify(Expr ae, LyrType a, Expr be, LyrType b, Span span)
    {
        if (LyrType.Equal(a, b)) return a;
        if (a.IsError) return b;
        if (b.IsError) return a;
        if (IsAssignable(be, b, a)) return a;
        if (IsAssignable(ae, a, b)) return b;
        _de.Report("LYR-SEM0016", Severity.Error, span, $"incompatible branch types: '{TypeFacts.Display(a)}' vs '{TypeFacts.Display(b)}'");
        return a;
    }

    private List<LyrType> CheckMatch(Node match, Expr scrutinee, MatchArm[] arms, SymbolTable scope, bool asExpression)
    {
        var st = CheckExpr(scrutinee, scope);
        var bodies = new List<LyrType>();
        var patternsClean = true;
        foreach (var arm in arms)
        {
            var armScope = new SymbolTable(scope);
            var before = _de.Diagnostics.Count;
            BindPattern(arm.Pattern, st, armScope);
            if (_de.Diagnostics.Count > before) patternsClean = false;
            if (arm.Guard is not null) CheckCondition(arm.Guard, armScope);
            switch (arm.Body)
            {
                case Block b:
                    CheckBlock(b, armScope);
                    // Blöcke haben keinen Wert: im match-AUSDRUCK muss ein Block-Arm die
                    // Funktion auf jedem Pfad verlassen; er trägt nichts zur Unifikation bei.
                    if (asExpression && !Flow.AlwaysReturns(b, _result))
                        _de.Report("LYR-SEM0033", Severity.Error, arm.Span,
                            "a block arm of a match expression must return or throw on every path (blocks have no value)");
                    break;
                case Expr e:
                    bodies.Add(CheckExpr(e, armScope));
                    break;
            }
        }
        // Fehlerhafte Patterns würden nur Folge-Rauschen produzieren → Exhaustivität dann skippen.
        if (patternsClean && !st.IsError)
            CheckExhaustiveness(match, st, arms);
        return bodies;
    }

    // --- Exhaustivität (§5, D4 pragmatisch): Enum-Varianten, bool und ?T werden aufgezählt,
    // --- offene Typen (int, string, …) brauchen einen '_'-/Bindungs-Arm. Guards zählen nicht.

    private void CheckExhaustiveness(Node match, LyrType scrutinee, MatchArm[] arms)
    {
        var pats = new List<Pattern>();
        foreach (var arm in arms)
            if (arm.Guard is null) Flatten(arm.Pattern, pats);
        var missing = MissingCases(scrutinee, pats);
        if (missing.Count == 0)
        {
            _result.MarkMatchExhaustive(match);
            return;
        }
        var what = missing is ["_"]
            ? "add a '_' or binding arm to cover the remaining values"
            : $"missing case(s): {string.Join(", ", missing.Select(m => $"'{m}'"))}";
        _de.Report("LYR-SEM0050", Severity.Error, match.Span,
            $"match on '{TypeFacts.Display(scrutinee)}' is not exhaustive — {what}");
    }

    private static void Flatten(Pattern p, List<Pattern> into)
    {
        if (p is OrPattern o) foreach (var a in o.Alternatives) Flatten(a, into);
        else into.Add(p);
    }

    private List<string> MissingCases(LyrType type, List<Pattern> pats)
    {
        if (pats.Any(p => IsIrrefutable(p, type))) return [];
        switch (type)
        {
            case Optional o:
            {
                var missing = new List<string>();
                if (!pats.Any(IsNullPattern)) missing.Add("null");
                missing.AddRange(MissingCases(o.Inner, pats.Where(p => !IsNullPattern(p)).ToList()));
                return missing;
            }
            case PrimitiveType { Kind: PrimitiveKind.Bool }:
            {
                var missing = new List<string>();
                if (!pats.Any(p => p is LiteralPattern { Literal: BoolLiteralExpr { Value: true } })) missing.Add("true");
                if (!pats.Any(p => p is LiteralPattern { Literal: BoolLiteralExpr { Value: false } })) missing.Add("false");
                return missing;
            }
            default:
                if (EnumDefOf(type) is { Declaration: EnumDecl ed } enumTs)
                {
                    var covered = new HashSet<string>();
                    foreach (var p in pats)
                        if (CoveredVariant(p, type, enumTs) is { } name) covered.Add(name);
                    return ed.Variants.Where(v => !covered.Contains(v.Name)).Select(v => v.Name).ToList();
                }
                return ["_"]; // offener Typ: nur per Default abdeckbar
        }
    }

    // Welche Variante deckt dieses Pattern vollständig ab (Payload irrefutabel)?
    private string? CoveredVariant(Pattern p, LyrType scrutinee, TypeSymbol enumTs)
    {
        switch (p)
        {
            case BindingPattern b when VariantOf(enumTs, b.Name) is
                { Declaration: EnumVariant { TupleFields: null, StructFields: null } } ev:
                return ev.Name;
            case VariantPattern v when VariantOf(enumTs, v.Path[^1]) is { } ev
                && VariantCovered(v, ev, scrutinee, enumTs):
                return ev.Name;
            default:
                return null;
        }
    }

    private bool VariantCovered(VariantPattern v, EnumVariantSymbol ev, LyrType scrutinee, TypeSymbol enumTs)
    {
        var variant = (EnumVariant)ev.Declaration!;
        var subst = scrutinee is GenericInstance gi ? SubstMap(gi) : EmptySubst;
        if (v.TupleElements is { } elems)
        {
            if (variant.TupleFields is not { } fields || fields.Length != elems.Length) return false;
            for (var i = 0; i < elems.Length; i++)
                if (!IsIrrefutable(elems[i], Substitute(ResolveType(fields[i], enumTs.Members), subst)))
                    return false;
            return true;
        }
        if (v.StructFields is { } fps)
        {
            if (variant.StructFields is null) return false;
            foreach (var fp in fps)
            {
                if (fp.Pattern is null) continue; // Kurzform bindet nur — irrefutabel
                var fd = Array.Find(variant.StructFields, f => f.Name == fp.Name);
                if (fd is null || !IsIrrefutable(fp.Pattern, Substitute(ResolveType(fd.Type, enumTs.Members), subst)))
                    return false;
            }
            return true;
        }
        return variant.TupleFields is null && variant.StructFields is null; // qualifizierte Unit-Variante
    }

    // Matcht dieses Pattern JEDEN Wert des Typs?
    private bool IsIrrefutable(Pattern p, LyrType type)
    {
        switch (p)
        {
            case WildcardPattern:
                return true;
            case OrPattern o:
                return o.Alternatives.Any(a => IsIrrefutable(a, type));
            case BindingPattern b:
                if (type is Optional) return false; // bindet den Inner-Teil, deckt null nicht ab
                return EnumDefOf(type) is not { } e || VariantOf(e, b.Name) is null; // Varianten-Name = Test
            case TuplePattern t:
                if (type is not TupleOf tt || tt.Elements.Length != t.Elements.Length) return false;
                for (var i = 0; i < t.Elements.Length; i++)
                    if (!IsIrrefutable(t.Elements[i], tt.Elements[i])) return false;
                return true;
            case VariantPattern v:
                if (EnumDefOf(type) is { Declaration: EnumDecl ed } enumTs)
                    return ed.Variants.Length == 1 && VariantOf(enumTs, v.Path[^1]) is { } ev
                        && VariantCovered(v, ev, type, enumTs);
                return StructDestructureIrrefutable(v, type);
            default:
                return false; // Literal / Range
        }
    }

    private bool StructDestructureIrrefutable(VariantPattern v, LyrType type)
    {
        var (def, subst) = DefinitionOf(type);
        if (def is null || v.StructFields is null || v.Path[^1] != def.Name) return false;
        foreach (var fp in v.StructFields)
        {
            if (fp.Pattern is null) continue;
            if (def.Members.LookupLocal(fp.Name) is not FieldSymbol fs) return false;
            if (!IsIrrefutable(fp.Pattern, Substitute(FieldType(fs), subst))) return false;
        }
        return true;
    }

    private LyrType UnifyArms(List<LyrType> bodies, Span span)
    {
        if (bodies.Count == 0) return LyrType.Error;
        var result = bodies[0];
        for (var i = 1; i < bodies.Count; i++)
        {
            if (bodies[i].IsError) continue;
            if (result.IsError) { result = bodies[i]; continue; }
            if (LyrType.Equal(result, bodies[i])) continue;
            _de.Report("LYR-SEM0016", Severity.Error, span, $"match arms have incompatible types: '{TypeFacts.Display(result)}' vs '{TypeFacts.Display(bodies[i])}'");
            break;
        }
        return result;
    }

    private LyrType CheckBlockAsValue(Block block, SymbolTable scope)
    {
        CheckBlock(block, scope);
        return LyrType.Error; // Block-Lambda-Wert → Slice 4 (bidirektionale Lambda-Inferenz, D5)
    }

    // Pattern-Bindungen (§6.3): jede Form typt ihre Bindungen echt gegen den Scrutinee-Typ.
    // Nicht-null-Patterns auf ?T matchen gegen T (Doku §6: 'null => …, u => u.name');
    // Fehler poisonen ihre Sub-Bindungen, damit Arm-Bodies nicht kaskadieren.
    private void BindPattern(Pattern pattern, LyrType scrutinee, SymbolTable scope)
    {
        if (scrutinee.IsError) { BindPoison(pattern, scope); return; }
        if (scrutinee is Optional opt && pattern is not (WildcardPattern or OrPattern) && !IsNullPattern(pattern))
        {
            BindPattern(pattern, opt.Inner, scope);
            return;
        }

        switch (pattern)
        {
            case LiteralPattern lit:
                CheckLiteralPattern(lit, scrutinee, scope);
                return;

            case BindingPattern b:
                if (EnumDefOf(scrutinee) is { } be && VariantOf(be, b.Name) is { } bev)
                {
                    if ((EnumVariant)bev.Declaration! is not { TupleFields: null, StructFields: null })
                        _de.Report("LYR-SEM0031", Severity.Error, b.Span,
                            $"variant '{b.Name}' carries a payload — destructure it ('{b.Name}(…)' or '{b.Name} {{ … }}')");
                    _result.BindRef(b, bev); // Unit-Varianten-Test, keine Bindung
                    return;
                }
                var local = new LocalSymbol(b.Name, scrutinee, false, b);
                scope.TryDeclare(local);
                _result.BindRef(b, local); // für DAA
                return;

            case TuplePattern t:
                if (scrutinee is TupleOf tup && tup.Elements.Length == t.Elements.Length)
                {
                    for (var i = 0; i < t.Elements.Length; i++) BindPattern(t.Elements[i], tup.Elements[i], scope);
                    return;
                }
                Report(t.Span, "LYR-SEM0029", scrutinee is TupleOf other
                    ? $"tuple pattern has {t.Elements.Length} element(s), but '{TypeFacts.Display(scrutinee)}' has {other.Elements.Length}"
                    : $"tuple pattern cannot match '{TypeFacts.Display(scrutinee)}'");
                BindPoison(t, scope);
                return;

            case VariantPattern v:
                BindVariantPattern(v, scrutinee, scope);
                return;

            case RangePattern r:
                CheckRangePattern(r, scrutinee, scope);
                return;

            case OrPattern o:
                BindOrPattern(o, scrutinee, scope);
                return;
            // Wildcard/Error: keine Bindung
        }
    }

    private void CheckLiteralPattern(LiteralPattern lit, LyrType scrutinee, SymbolTable scope)
    {
        if (lit.Literal is NullLiteralExpr)
        {
            if (scrutinee is not Optional)
                _de.Report("LYR-SEM0029", Severity.Error, lit.Span,
                    $"'null' pattern cannot match non-nullable '{TypeFacts.Display(scrutinee)}'");
            return;
        }
        var lt = CheckExpr(lit.Literal, scope);
        if (!lt.IsError && !IsAssignable(lit.Literal, lt, scrutinee))
            _de.Report("LYR-SEM0029", Severity.Error, lit.Span,
                $"literal pattern of type '{TypeFacts.Display(lt)}' cannot match '{TypeFacts.Display(scrutinee)}'");
    }

    private void CheckRangePattern(RangePattern r, LyrType scrutinee, SymbolTable scope)
    {
        var lo = CheckExpr(r.Low, scope);
        var hi = CheckExpr(r.High, scope);
        if (lo.IsError || hi.IsError) return;
        var comparable = TypeFacts.IsNumeric(scrutinee) || scrutinee is PrimitiveType { Kind: PrimitiveKind.Char };
        if (!comparable || !IsAssignable(r.Low, lo, scrutinee) || !IsAssignable(r.High, hi, scrutinee))
            _de.Report("LYR-SEM0029", Severity.Error, r.Span,
                $"range pattern of '{TypeFacts.Display(lo)}'..'{TypeFacts.Display(hi)}' cannot match '{TypeFacts.Display(scrutinee)}'");
    }

    private void BindVariantPattern(VariantPattern v, LyrType scrutinee, SymbolTable scope)
    {
        if (EnumDefOf(scrutinee) is { } enumTs)
        {
            // Qualifizierter Pfad (Shape.Circle) muss auf GENAU dieses Enum zeigen.
            if (v.Path.Length > 1
                && !ReferenceEquals(ResolveNamePath(v.Path, v.Path.Length - 1, scope), enumTs))
            {
                Report(v.Span, "LYR-SEM0029",
                    $"pattern path '{string.Join('.', v.Path[..^1])}' does not refer to matched enum '{enumTs.Name}'");
                BindPoison(v, scope);
                return;
            }
            if (VariantOf(enumTs, v.Path[^1]) is not { } ev)
            {
                Report(v.Span, "LYR-SEM0031", $"enum '{enumTs.Name}' has no variant '{v.Path[^1]}'");
                BindPoison(v, scope);
                return;
            }
            _result.BindRef(v, ev);
            BindVariantPayload(v, ev, scrutinee, enumTs, scope);
            return;
        }

        // Struct-/Class-Destructuring: Point { x, y }
        var (def, subst) = DefinitionOf(scrutinee);
        if (def is null)
        {
            Report(v.Span, "LYR-SEM0029", $"pattern cannot destructure '{TypeFacts.Display(scrutinee)}'");
            BindPoison(v, scope);
            return;
        }
        if (!ReferenceEquals(ResolveNamePath(v.Path, v.Path.Length, scope), def))
        {
            Report(v.Span, "LYR-SEM0029",
                $"pattern names '{string.Join('.', v.Path)}', but the matched value is '{TypeFacts.Display(scrutinee)}'");
            BindPoison(v, scope);
            return;
        }
        if (v.TupleElements is not null)
        {
            Report(v.Span, "LYR-SEM0031", $"'{def.Name}' is not an enum variant — destructure fields with '{def.Name} {{ … }}'");
            BindPoison(v, scope);
            return;
        }
        _result.BindRef(v, def);
        foreach (var fp in v.StructFields ?? [])
        {
            if (def.Members.LookupLocal(fp.Name) is FieldSymbol fs)
            {
                BindFieldPattern(fp, Substitute(FieldType(fs), subst), scope);
            }
            else
            {
                _de.Report("LYR-SEM0015", Severity.Error, fp.Span, $"'{def.Name}' has no field '{fp.Name}'");
                BindPoisonField(fp, scope);
            }
        }
    }

    private void BindVariantPayload(VariantPattern v, EnumVariantSymbol ev, LyrType scrutinee,
        TypeSymbol enumTs, SymbolTable scope)
    {
        var variant = (EnumVariant)ev.Declaration!;
        var subst = scrutinee is GenericInstance gi ? SubstMap(gi) : EmptySubst;

        if (v.TupleElements is { } elems)
        {
            if (variant.TupleFields is not { } fields)
            {
                Report(v.Span, "LYR-SEM0031", variant.StructFields is not null
                    ? $"variant '{ev.Name}' has named fields — destructure with '{ev.Name} {{ … }}'"
                    : $"variant '{ev.Name}' has no payload");
                BindPoison(v, scope);
                return;
            }
            if (fields.Length != elems.Length)
            {
                Report(v.Span, "LYR-SEM0031",
                    $"variant '{ev.Name}' has {fields.Length} payload element(s), pattern has {elems.Length}");
                BindPoison(v, scope);
                return;
            }
            for (var i = 0; i < elems.Length; i++)
                BindPattern(elems[i], Substitute(ResolveType(fields[i], enumTs.Members), subst), scope);
            return;
        }

        if (v.StructFields is { } fps)
        {
            if (variant.StructFields is not { } decls)
            {
                Report(v.Span, "LYR-SEM0031", variant.TupleFields is not null
                    ? $"variant '{ev.Name}' has a tuple payload — destructure with '{ev.Name}(…)'"
                    : $"variant '{ev.Name}' has no payload");
                BindPoison(v, scope);
                return;
            }
            foreach (var fp in fps)
            {
                if (Array.Find(decls, f => f.Name == fp.Name) is { } fd)
                {
                    BindFieldPattern(fp, Substitute(ResolveType(fd.Type, enumTs.Members), subst), scope);
                }
                else
                {
                    _de.Report("LYR-SEM0031", Severity.Error, fp.Span, $"variant '{ev.Name}' has no field '{fp.Name}'");
                    BindPoisonField(fp, scope);
                }
            }
            return;
        }

        // Qualifizierte Unit-Variante (Shape.Empty): darf keinen Payload haben.
        if (variant.TupleFields is not null || variant.StructFields is not null)
            Report(v.Span, "LYR-SEM0031", $"variant '{ev.Name}' carries a payload — destructure it");
    }

    private void BindFieldPattern(FieldPattern fp, LyrType type, SymbolTable scope)
    {
        if (fp.Pattern is not null) { BindPattern(fp.Pattern, type, scope); return; }
        var local = new LocalSymbol(fp.Name, type, false, fp); // Kurzform: Feldname bindet
        scope.TryDeclare(local);
        _result.BindRef(fp, local);
    }

    private void BindPoisonField(FieldPattern fp, SymbolTable scope)
    {
        if (fp.Pattern is not null) { BindPoison(fp.Pattern, scope); return; }
        var local = new LocalSymbol(fp.Name, LyrType.Error, false, fp);
        scope.TryDeclare(local);
        _result.BindRef(fp, local);
    }

    // Or-Pattern (§6.3): jede Alternative bindet in eine eigene Tabelle; alle müssen dieselben
    // Namen mit denselben Typen binden. Die Bindungen der ERSTEN Alternative werden zum
    // Arm-Scope (konsistent mit der DAA, die Alternative 0 nutzt).
    private void BindOrPattern(OrPattern o, LyrType scrutinee, SymbolTable scope)
    {
        if (o.Alternatives.Length == 0) return;
        var tables = new List<SymbolTable>(o.Alternatives.Length);
        foreach (var alt in o.Alternatives)
        {
            var t = new SymbolTable(scope);
            BindPattern(alt, scrutinee, t);
            tables.Add(t);
        }
        var reference = tables[0].Symbols.OfType<LocalSymbol>().ToList();
        for (var i = 1; i < tables.Count; i++)
        {
            var alt = tables[i].Symbols.OfType<LocalSymbol>().ToList();
            foreach (var name in reference.Select(s => s.Name).Union(alt.Select(s => s.Name)))
            {
                var a = reference.Find(s => s.Name == name);
                var b = alt.Find(s => s.Name == name);
                if (a is null || b is null)
                    _de.Report("LYR-SEM0032", Severity.Error, o.Alternatives[i].Span,
                        $"or-pattern alternatives bind different variables: '{name}' is not bound in every alternative");
                else if (!a.Type.IsError && !b.Type.IsError && !LyrType.Equal(a.Type, b.Type))
                    _de.Report("LYR-SEM0032", Severity.Error, o.Alternatives[i].Span,
                        $"'{name}' is bound as '{TypeFacts.Display(a.Type)}' and as '{TypeFacts.Display(b.Type)}' in different or-pattern alternatives");
            }
        }
        foreach (var s in reference) scope.TryDeclare(s);
    }

    // --- Pattern-Helfer ---

    private static bool IsNullPattern(Pattern p) => p is LiteralPattern { Literal: NullLiteralExpr };

    private static TypeSymbol? EnumDefOf(LyrType t) => t switch
    {
        NamedRef { Symbol.Kind: TypeSymbolKind.Enum } nr => nr.Symbol,
        GenericInstance { Definition.Kind: TypeSymbolKind.Enum } gi => gi.Definition,
        _ => null
    };

    private static EnumVariantSymbol? VariantOf(TypeSymbol enumTs, string name) =>
        enumTs.Members.LookupLocal(name) as EnumVariantSymbol;

    private static (TypeSymbol? def, Dictionary<GenericParamSymbol, LyrType> subst) DefinitionOf(LyrType t) => t switch
    {
        NamedRef nr => (nr.Symbol, EmptySubst),
        GenericInstance gi => (gi.Definition, SubstMap(gi)),
        _ => (null, EmptySubst)
    };

    // Löst die ersten count Segmente eines Pattern-Pfads über den Scope auf (Module/Imports).
    private static Symbol? ResolveNamePath(string[] path, int count, SymbolTable scope)
    {
        var cur = scope.Lookup(path[0]);
        if (cur is ImportBindingSymbol ib0) cur = ib0.Target;
        for (var i = 1; i < count && cur is not null; i++)
        {
            cur = cur is ModuleSymbol mod ? mod.Members.LookupLocal(path[i]) : null;
            if (cur is ImportBindingSymbol ib) cur = ib.Target;
        }
        return cur;
    }

    private static readonly Dictionary<GenericParamSymbol, LyrType> EmptySubst = new(ReferenceEqualityComparer.Instance);

    private void BindPoison(Pattern pattern, SymbolTable scope)
    {
        switch (pattern)
        {
            case BindingPattern b:
                var lb = new LocalSymbol(b.Name, LyrType.Error, false, b);
                scope.TryDeclare(lb);
                _result.BindRef(b, lb);
                return;
            case VariantPattern v:
                foreach (var sub in v.TupleElements ?? []) BindPoison(sub, scope);
                foreach (var f in v.StructFields ?? [])
                {
                    if (f.Pattern is not null) BindPoison(f.Pattern, scope);
                    else
                    {
                        var fl = new LocalSymbol(f.Name, LyrType.Error, false, f);
                        scope.TryDeclare(fl);
                        _result.BindRef(f, fl);
                    }
                }
                return;
            case TuplePattern t: foreach (var sub in t.Elements) BindPoison(sub, scope); return;
            case OrPattern o: if (o.Alternatives.Length > 0) BindPoison(o.Alternatives[0], scope); return;
        }
    }

    private LyrType CheckLambda(LambdaExpr lam, SymbolTable scope)
    {
        var lambdaScope = new SymbolTable(scope);
        var pTypes = lam.Parameters.Select(p =>
        {
            var pt = p.Type is not null ? ResolveType(p.Type, scope) : LyrType.Error; // unannotiert → Kontext-Inferenz ist M4
            lambdaScope.TryDeclare(new ParameterSymbol(p.Name, pt, p));
            return pt;
        }).ToArray();

        var bodyType = lam.Body switch
        {
            Block b => CheckBlockAsValue(b, lambdaScope),
            Expr e => CheckExpr(e, lambdaScope),
            _ => LyrType.Error
        };
        var ret = lam.ReturnType is not null ? ResolveType(lam.ReturnType, scope) : bodyType;
        return new FnType(pTypes, ret);
    }

    // --- Numerik / Zuweisbarkeit / Literal-Fit (①A / ②a) ---

    private LyrType? UnifyNumeric(Expr le, LyrType l, Expr re, LyrType r)
    {
        if (LyrType.Equal(l, r) && TypeFacts.IsNumeric(l)) return l;
        if (TypeFacts.IsNumeric(l) && l is PrimitiveType pl && LiteralAdaptsTo(re, pl)) return l; // r passt sich l an
        if (TypeFacts.IsNumeric(r) && r is PrimitiveType pr && LiteralAdaptsTo(le, pr)) return r; // l passt sich r an
        return null;
    }

    private void CheckAssignable(Expr expr, LyrType from, LyrType to, Span span)
    {
        if (!IsAssignable(expr, from, to))
            _de.Report("LYR-SEM0001", Severity.Error, span, $"cannot assign '{TypeFacts.Display(from)}' to '{TypeFacts.Display(to)}'");
    }

    private bool IsAssignable(Expr expr, LyrType from, LyrType to)
    {
        if (from.IsError || to.IsError) return true;      // Poison: keine Folgefehler
        if (from is NeverType) return true;               // Bottom-Typ: panic(...) passt überall
        if (LyrType.Equal(from, to)) return true;
        if (to is Optional inner)                          // T → ?T (Widening §4)
            return from is NullType || IsAssignable(expr, from, inner.Inner);
        if (from is NullType) return false;
        if (to is PrimitiveType pt && LiteralAdaptsTo(expr, pt)) return true; // ②a Literal-Fit
        return false;
    }

    private static bool LiteralAdaptsTo(Expr expr, PrimitiveType target)
    {
        if (TryUntypedIntLiteral(expr, out var negative, out var magnitude))
        {
            if (TypeFacts.IsInteger(target)) return TypeFacts.IntLiteralFits(negative, magnitude, target.Kind);
            if (TypeFacts.IsFloat(target)) return true; // Ganzzahl-Literal → float
        }
        if (IsUntypedFloatLiteral(expr) && TypeFacts.IsFloat(target)) return true;
        return false;
    }

    private static bool TryUntypedIntLiteral(Expr expr, out bool negative, out ulong magnitude)
    {
        switch (expr)
        {
            case IntLiteralExpr { Suffix: null } n: negative = false; magnitude = n.Value; return true;
            case UnaryExpr { Operator: UnaryOp.Neg, Operand: IntLiteralExpr { Suffix: null } n }:
                negative = true; magnitude = n.Value; return true;
            default: negative = false; magnitude = 0; return false;
        }
    }

    private static bool IsUntypedFloatLiteral(Expr expr) => expr switch
    {
        FloatLiteralExpr { Suffix: null } => true,
        UnaryExpr { Operator: UnaryOp.Neg, Operand: FloatLiteralExpr { Suffix: null } } => true,
        _ => false
    };

    // --- Typ-Auflösung (TypeNode → LyrType) ---

    private LyrType ResolveType(TypeNode node, SymbolTable scope)
    {
        switch (node)
        {
            case NamedType n:
                var sym = _binding.Resolve(n) ?? ResolveTypePath(n.Path, scope);
                if (sym is ImportBindingSymbol ibt) sym = ibt.Target;
                if (sym is null)
                    return Report(n.Span, "LYR-SEM0011", $"unresolved type '{string.Join('.', n.Path)}'");
                if (sym is GenericParamSymbol gp) return new TypeParamType(gp);
                if (sym is TypeSymbol { Kind: not (TypeSymbolKind.Builtin or TypeSymbolKind.Alias) } gts
                    && (gts.Generics.Length > 0 || n.TypeArguments.Length > 0))
                    return MakeGenericInstance(gts, n, scope);
                return SymbolToType(sym, scope);
            case NullableType nn: return new Optional(ResolveType(nn.Inner, scope));
            case ArrayType a: return new ArrayOf(ResolveType(a.Element, scope), a.Size is { } sz ? (int)sz.Value : null);
            case TupleType t: return new TupleOf(t.Elements.Select(e => ResolveType(e, scope)).ToArray());
            case FunctionType f: return new FnType(f.Parameters.Select(p => ResolveType(p, scope)).ToArray(), ResolveType(f.ReturnType, scope));
            default: return LyrType.Error; // ErrorType
        }
    }

    private LyrType SymbolToType(Symbol sym, SymbolTable scope) => sym switch
    {
        TypeSymbol { Kind: TypeSymbolKind.Builtin } t => TypeFacts.FromBuiltinName(t.Name) ?? LyrType.Error,
        TypeSymbol { Kind: TypeSymbolKind.Alias } t => ResolveType(((TypeAliasDecl)t.Declaration!).Aliased, scope),
        GenericParamSymbol g => new TypeParamType(g),
        TypeSymbol t => new NamedRef(t),
        ImportBindingSymbol ib => SymbolToType(ib.Target, scope),
        _ => LyrType.Error // Extern/Error/Nicht-Typ → opak
    };

    // Stack<int> → GenericInstance. Arity wird geprüft; Constraint-Erfüllung (int :: Comparable?)
    // ist Slice 1b (braucht Konformanz-Modell inkl. Builtins).
    private LyrType MakeGenericInstance(TypeSymbol ts, NamedType n, SymbolTable scope)
    {
        var args = n.TypeArguments.Select(a => ResolveType(a, scope)).ToArray();
        if (ts.Generics.Length != args.Length)
            _de.Report("LYR-SEM0026", Severity.Error, n.Span,
                $"generic type '{ts.Name}' expects {ts.Generics.Length} type argument(s), got {args.Length}");
        return new GenericInstance(ts, args);
    }

    // Kompakte Pfad-Auflösung für Body-Typen (die der Resolver nicht gebunden hat).
    private Symbol? ResolveTypePath(string[] path, SymbolTable scope)
    {
        var head = scope.Lookup(path[0]);
        if (head is null || path.Length == 1) return head;
        for (var i = 1; i < path.Length && head is ImportBindingSymbol { Target: ModuleSymbol mod }; i++)
            head = mod.Members.LookupLocal(path[i]);
        return head;
    }

    // --- Helpers ---

    private static LyrType IntSuffixType(IntSuffix s) => new PrimitiveType(s switch
    {
        IntSuffix.I8 => PrimitiveKind.Int8, IntSuffix.I16 => PrimitiveKind.Int16, IntSuffix.I32 => PrimitiveKind.Int32, IntSuffix.I64 => PrimitiveKind.Int64,
        IntSuffix.U8 => PrimitiveKind.Uint8, IntSuffix.U16 => PrimitiveKind.Uint16, IntSuffix.U32 => PrimitiveKind.Uint32, _ => PrimitiveKind.Uint64
    });

    private static LyrType FloatSuffixType(FloatSuffix s) =>
        new PrimitiveType(s == FloatSuffix.F32 ? PrimitiveKind.Float32 : PrimitiveKind.Float64);

    private LyrType Report(Span span, string code, string message)
    {
        _de.Report(code, Severity.Error, span, message);
        return LyrType.Error;
    }

    private void BadOp(Span span, string op, LyrType t) =>
        _de.Report("LYR-SEM0003", Severity.Error, span, $"operator '{op}' is not applicable to '{TypeFacts.Display(t)}'");

    private LyrType BadBinary(BinaryExpr b, LyrType l, LyrType r)
    {
        _de.Report("LYR-SEM0003", Severity.Error, b.Span, $"operator '{b.Operator}' is not applicable to '{TypeFacts.Display(l)}' and '{TypeFacts.Display(r)}'");
        return LyrType.Error;
    }
}
