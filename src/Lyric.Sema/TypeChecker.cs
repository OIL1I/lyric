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

    private LyrType _currentReturn = LyrType.Void;
    private LyrType? _currentThis;
    private Dictionary<Symbol, LyrType> _narrowed = new(ReferenceEqualityComparer.Instance); // ?T → T im bewiesen-non-null-Bereich

    public TypeChecker(Compilation comp, BindingResult binding, DiagnosticEngine de)
    {
        _comp = comp;
        _binding = binding;
        _de = de;
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
        var thisType = new NamedRef(ts);
        foreach (var m in members)
            if (m is FunctionDecl fn)
                CheckFunction(fn, ts.Members, thisType);
    }

    private void CheckEnumMethods(EnumDecl e, ModuleSymbol module)
    {
        if (module.Members.LookupLocal(e.Name) is not TypeSymbol ts) return;
        var thisType = new NamedRef(ts);
        foreach (var fn in e.Methods) CheckFunction(fn, ts.Members, thisType);
    }

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

        if (fn.Body is not null)
        {
            CheckBlock(fn.Body, scope);
            if (!TypeFacts.IsVoid(_currentReturn) && !Flow.AlwaysReturns(fn.Body))
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
                if (r.Value is not null) CheckAssignable(r.Value, CheckExpr(r.Value, scope), _currentReturn, r.Span);
                else if (!TypeFacts.IsVoid(_currentReturn))
                    _de.Report("LYR-SEM0001", Severity.Error, r.Span, "return without a value in a non-void function");
                break;
            case ExprStmt es: CheckExpr(es.Expr, scope); break;
            case ThrowStmt t: CheckExpr(t.Value, scope); break;
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
            case MatchStmt m: CheckMatch(m.Scrutinee, m.Arms, scope); break;
            // Break/Continue/Error: nichts zu prüfen.
        }
    }

    private void CheckBinding(BindingStmt bnd, SymbolTable scope)
    {
        var declared = bnd.Type is not null ? ResolveType(bnd.Type, scope) : null;
        var initT = bnd.Initializer is not null ? CheckExpr(bnd.Initializer, scope) : null;

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

    private void CheckCatch(CatchClause clause, SymbolTable scope)
    {
        var catchScope = new SymbolTable(scope);
        if (clause.BindingName is not null)
        {
            var bt = clause.BindingType is not null ? ResolveType(clause.BindingType, scope) : LyrType.Error; // untyped catch: Throwable → opak
            catchScope.TryDeclare(new LocalSymbol(clause.BindingName, bt, false, clause));
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

        if (Flow.AlwaysReturns(f.Then)) Apply(elseFacts);
        else if (f.Else is not null && Flow.AlwaysReturns(f.Else)) Apply(thenFacts);
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

    private LyrType CheckExpr(Expr expr, SymbolTable scope)
    {
        var type = Compute(expr, scope);
        _result.SetType(expr, type);
        return type;
    }

    private LyrType Compute(Expr expr, SymbolTable scope)
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
            case ArrayLitExpr arr: return CheckArrayLit(arr, scope);
            case TupleLitExpr tu: return new TupleOf(tu.Elements.Select(e => CheckExpr(e, scope)).ToArray());
            case InterpolatedStringExpr fs:
                foreach (var seg in fs.Segments) if (seg is InterpHole h) CheckExpr(h.Expr, scope);
                return LyrType.String;
            case ErrorExpr: return LyrType.Error;

            case CallExpr call: return CheckCall(call, scope);
            case MemberExpr mem: return CheckMember(mem, scope);
            case StructInitExpr si: return CheckStructInit(si, scope);
            case IfExpr iff: return CheckIfExpr(iff, scope);
            case MatchExpr ma: return UnifyArms(CheckMatch(ma.Scrutinee, ma.Arms, scope), ma.Span);
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
        var ret = fn.ReturnType is not null ? ResolveType(fn.ReturnType, _comp.Builtins) : LyrType.Void;
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
        var value = CheckExpr(a.Value, scope);
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

    private LyrType CheckArrayLit(ArrayLitExpr arr, SymbolTable scope)
    {
        if (arr.Elements.Length == 0) return new ArrayOf(LyrType.Error, null); // leeres Array braucht Kontext → 2b
        var first = CheckExpr(arr.Elements[0], scope);
        for (var i = 1; i < arr.Elements.Length; i++)
        {
            var t = CheckExpr(arr.Elements[i], scope);
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

        var decl = (_result.RefOf(call.Callee) as FunctionSymbol)?.Declaration as FunctionDecl;
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
        if (baseType is NamedRef nr)
        {
            var mt = BindMember(mem, InstanceMember(nr.Symbol, mem.Member, mem.Span));
            return mem.IsOptional ? new Optional(mt) : mt;
        }
        if (targetType.IsError) return LyrType.Error;
        return Report(mem.Span, "LYR-SEM0012", $"'{TypeFacts.Display(targetType)}' has no member '{mem.Member}'");
    }

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
            EnumVariantSymbol ev => (VariantConstructorType(ev, ts), ev),
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

    private LyrType VariantConstructorType(EnumVariantSymbol ev, TypeSymbol enumTs)
    {
        var v = (EnumVariant)ev.Declaration!;
        return v.TupleFields is not null
            ? new FnType(v.TupleFields.Select(t => ResolveType(t, enumTs.Members)).ToArray(), new NamedRef(enumTs))
            : new NamedRef(enumTs); // Unit; Struct-Varianten-Konstruktion → M4
    }

    private LyrType FieldType(FieldSymbol fs) => ResolveType(((FieldDecl)fs.Declaration!).Type, _comp.Builtins);

    private LyrType CheckStructInit(StructInitExpr si, SymbolTable scope)
    {
        var sym = ResolveTypePath(si.Path, scope);
        if (sym is ImportBindingSymbol ib) sym = ib.Target;
        if (sym is not TypeSymbol ts)
        {
            if (sym is null) _de.Report("LYR-SEM0011", Severity.Error, si.Span, $"unknown type '{string.Join('.', si.Path)}'");
            foreach (var f in si.Fields) CheckExpr(f.Value, scope);
            return LyrType.Error;
        }
        foreach (var field in si.Fields)
        {
            var vt = CheckExpr(field.Value, scope);
            if (ts.Members.LookupLocal(field.Name) is FieldSymbol fs)
                CheckAssignable(field.Value, vt, FieldType(fs), field.Span);
            else
                _de.Report("LYR-SEM0015", Severity.Error, field.Span, $"'{ts.Name}' has no field '{field.Name}'");
        }
        return new NamedRef(ts);
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

    private List<LyrType> CheckMatch(Expr scrutinee, MatchArm[] arms, SymbolTable scope)
    {
        var st = CheckExpr(scrutinee, scope);
        var bodies = new List<LyrType>();
        foreach (var arm in arms)
        {
            var armScope = new SymbolTable(scope);
            BindPattern(arm.Pattern, st, armScope);
            if (arm.Guard is not null) CheckCondition(arm.Guard, armScope);
            bodies.Add(arm.Body switch
            {
                Block b => CheckBlockAsValue(b, armScope),
                Expr e => CheckExpr(e, armScope),
                _ => LyrType.Error
            });
        }
        return bodies;
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
        return LyrType.Error; // Block-Arm-/Block-Lambda-Wert → Block-Wert-Frage (geparkt)
    }

    // Pattern-Bindungen: einfache Bindings + Tuple-Destructuring werden echt getypt;
    // Enum-Payload-Destructuring ist M4 → dort gebundene Variablen als Error (Poison).
    private void BindPattern(Pattern pattern, LyrType scrutinee, SymbolTable scope)
    {
        switch (pattern)
        {
            case BindingPattern b:
                if (scrutinee is NamedRef { Symbol.Kind: TypeSymbolKind.Enum } nr
                    && nr.Symbol.Members.LookupLocal(b.Name) is EnumVariantSymbol)
                    return; // Unit-Varianten-Match, keine Bindung
                var local = new LocalSymbol(b.Name, scrutinee, false, b);
                scope.TryDeclare(local);
                _result.BindRef(b, local); // für DAA
                return;
            case TuplePattern t when scrutinee is TupleOf tup && tup.Elements.Length == t.Elements.Length:
                for (var i = 0; i < t.Elements.Length; i++) BindPattern(t.Elements[i], tup.Elements[i], scope);
                return;
            case VariantPattern or TuplePattern or OrPattern:
                BindPoison(pattern, scope);
                return;
            // Wildcard/Literal/Range: keine Bindung
        }
    }

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
                if (sym is null)
                    return Report(n.Span, "LYR-SEM0011", $"unresolved type '{string.Join('.', n.Path)}'");
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
        TypeSymbol t => new NamedRef(t),
        ImportBindingSymbol ib => SymbolToType(ib.Target, scope),
        _ => LyrType.Error // Extern/Error/Nicht-Typ → opak
    };

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
