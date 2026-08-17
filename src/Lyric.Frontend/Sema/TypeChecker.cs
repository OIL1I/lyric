using Lyric.AST;
using Lyric.Core;
using Lyric.Resolver;

namespace Lyric.Sema;

/// <summary>
/// Type checking of expressions. Walks function bodies, manages the local scopes (parameters, then
/// block locals), resolves expression names and assigns every expression a <see cref="LyrType"/>
/// in the <see cref="TypeResult"/>.
///
/// Arithmetic is strict: both operands have the same type, and only untyped literals adapt through
/// a range fit. `+` and `*` also serve string and T[] as concatenation and repetition. `as`
/// converts between numeric types only.
/// </summary>
public sealed class TypeChecker
{
    private readonly Compilation _comp;
    private readonly BindingResult _binding;
    private readonly DiagnosticEngine _de;
    private readonly TypeResult _result = new();
    private readonly Dictionary<GlobalSymbol, LyrType> _globals = new(ReferenceEqualityComparer.Instance);

    // The aliases currently being expanded, and the ones already reported as cyclic. An alias names a
    // type rather than being one, so expanding it is a recursion with no base case of its own.
    private readonly HashSet<TypeSymbol> _expanding = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<TypeSymbol> _cyclic = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Is a global initializer running? Then a global that has not been computed yet is an ERROR
    /// rather than merely an unknown type.
    ///
    /// <para>Without this flag the lookup silently yields <see cref="LyrType.Error"/>, the sema
    /// reports nothing, and the lowering trips over an <c>&lt;error&gt;</c> type later.
    /// <c>Error</c> means "already reported", so something has to be reported here.</para>
    /// </summary>
    private bool _inGlobalInitializer;
    private readonly TypeSymbol? _throwable; // the builtin Throwable interface
    private readonly FunctionSymbol? _panic; // the builtin panic, returning never
    private readonly TypeSymbol? _coroutine; // the builtin Coroutine<T>, mapped to CoroutineOf

    /// <summary>The <c>Iterator&lt;T&gt;</c> interface from <c>std.iter</c>, what <c>for-in</c> checks
    /// against. <c>null</c> when the stdlib is not loaded; the loop head then reports the ordinary
    /// "not iterable" diagnostic.</summary>
    private readonly TypeSymbol? _iterator;
    private readonly TypeSymbol? _arrayIterator;
    private readonly TypeSymbol? _rangeIterator;
    private readonly TypeSymbol? _stringIterator;
    private readonly TypeSymbol? _iterable;
    private readonly TypeSymbol? _indexable;

    private LyrType _currentReturn = LyrType.Void;
    private LyrType? _currentYield; // the yield type when the current function is a coroutine
    private LyrType? _currentThis;
    private ModuleSymbol? _currentModule; // for extension visibility
    private Dictionary<Symbol, LyrType> _narrowed = new(ReferenceEqualityComparer.Instance); // ?T narrowed to T inside a proven non-null region

    public TypeChecker(Compilation comp, BindingResult binding, DiagnosticEngine de)
    {
        _comp = comp;
        _binding = binding;
        _de = de;
        _throwable = comp.Builtins.LookupLocal("Throwable") as TypeSymbol;
        _panic = comp.Builtins.LookupLocal("panic") as FunctionSymbol;
        _coroutine = comp.Builtins.LookupLocal("Coroutine") as TypeSymbol;

        // 'Iterator<T>' lives in the stdlib rather than among the builtins: it is an ordinary
        // interface anyone can implement. The compiler only has to FIND it to check 'for-in'
        // against it.
        var iter = comp.FindModule(["std", "iter"])?.Members;
        _iterator = iter?.LookupLocal("Iterator") as TypeSymbol;
        _arrayIterator = iter?.LookupLocal("ArrayIterator") as TypeSymbol;
        _rangeIterator = iter?.LookupLocal("RangeIterator") as TypeSymbol;
        _stringIterator = iter?.LookupLocal("StringIterator") as TypeSymbol;
        _iterable = iter?.LookupLocal("Iterable") as TypeSymbol;

        // 'Indexable<T>' lives in std.collections and is to '[i]' what 'Iterator<T>' is to 'for-in':
        // the compiler knows ONE built-in form, the array, and binds everything else to an interface
        // from the stdlib.
        _indexable = comp.FindModule(["std", "collections"])?.Members
            .LookupLocal("Indexable") as TypeSymbol;

        // `panic` exists TWICE: as a builtin in the root scope, so it is callable without an import,
        // and as a native declaration in `std.core`, which gives it its signature and native binding.
        // Both mean the same function, but only the builtin carried the `never` type — whoever wrote
        // `import std.core { panic }` got a `void` back, and the flow analysis did not see the
        // divergence. The place where `never` originates has to know both.
        _stdPanic = comp.FindModule(["std", "core"])?.Members
            .LookupLocal("panic") as FunctionSymbol;

        // 'Equatable<T>' is what '==' desugars through on a user type, 'Ordered<T>' what the four
        // comparisons desugar through — the same pattern as 'Iterator' for 'for-in': the compiler
        // knows the built-in scalars, and everything else binds to an interface from the stdlib.
        var core = comp.FindModule(["std", "core"])?.Members;
        _equatable = core?.LookupLocal("Equatable") as TypeSymbol;
        _ordered = core?.LookupLocal("Ordered") as TypeSymbol;
    }

    /// <summary>What <c>==</c> on a user type resolves through. Null without a standard library,
    /// and user-type equality is then a diagnostic rather than a crash.</summary>
    private readonly TypeSymbol? _equatable;

    /// <summary>What <c>&lt;</c> and its three siblings resolve through, under the same rules.</summary>
    private readonly TypeSymbol? _ordered;

    /// <summary>The native declaration from <c>std.core</c>. See the constructor.</summary>
    private readonly FunctionSymbol? _stdPanic;

    /// <summary>Is <paramref name="f"/> the <c>panic</c> function, no matter which of the two names
    /// reached it?</summary>
    private bool IsPanic(FunctionSymbol f) =>
        (_panic is not null && ReferenceEquals(f, _panic))
        || (_stdPanic is not null && ReferenceEquals(f, _stdPanic));

    public TypeResult Check()
    {
        _result.IteratorInterface = _iterator;
        _result.ArrayIterator = _arrayIterator;
        _result.RangeIterator = _rangeIterator;
        _result.StringIterator = _stringIterator;
        _result.Indexable = _indexable;
        _result.Iterable = _iterable;

        foreach (var module in _comp.Modules) ComputeGlobals(module);
        foreach (var module in _comp.Modules)
        {
            _currentModule = module;
            foreach (var decl in _comp.AstOf(module).Declarations)
                CheckDecl(decl, module);
        }
        CheckExtensionBlocks(); // extend bodies, the orphan rule, conformance
        _currentModule = null;
        new FlowAnalyzer(_comp, _result, _de).Run(); // definite assignment
        return _result;
    }

    // --- declarations ---

    private void ComputeGlobals(ModuleSymbol module)
    {
        foreach (var decl in _comp.AstOf(module).Declarations)
        {
            if (decl is not GlobalBindingDecl g) continue;
            var declared = g.Binding.Type is not null ? ResolveType(g.Binding.Type, module.Members) : null;
            LyrType type;
            if (g.Binding.Initializer is not null)
            {
                _inGlobalInitializer = true;
                var initT = CheckExpr(g.Binding.Initializer, module.Members);
                _inGlobalInitializer = false;
                if (declared is not null) { CheckAssignable(g.Binding.Initializer, initT, declared, g.Span); type = declared; }
                else type = initT;
            }
            else type = declared ?? LyrType.Error;

            if (module.Members.LookupLocal(g.Binding.Name) is not GlobalSymbol gs) continue;
            _globals[gs] = type;
            _result.BindGlobal(gs, type);   // for the lowering
        }
    }

    private void CheckDecl(Decl decl, ModuleSymbol module)
    {
        switch (decl)
        {
            case FunctionDecl fn: RequireBody(fn, module); CheckFunction(fn, module.Members, thisType: null); break;
            case StructDecl s:
                CheckStructIsFinite(s, module);
                CheckMethods(s.Name, s.Members, module);
                CheckTypeConformance(s.Name, s.Interfaces, module);
                break;
            case ClassDecl c: CheckMethods(c.Name, c.Members, module); CheckTypeConformance(c.Name, c.Interfaces, module); break;
            case EnumDecl e: CheckEnumMethods(e, module); CheckTypeConformance(e.Name, e.Interfaces, module); break;
            case InterfaceDecl i: CheckMethods(i.Name, i.Members, module); break;
            // ExtendDecl goes to CheckExtensionBlocks after all types; GlobalBindingDecl to ComputeGlobals.
        }
    }

    /// <summary>
    /// A bodyless function is a NATIVE DECLARATION: the signature is written in Lyric, the
    /// implementation lives in the host and is bound by name at load time. Reserved for the stdlib —
    /// in user code nothing could supply the body.
    /// </summary>
    /// <remarks>Interfaces are exempt: there, bodyless means abstract, and conformance checks that.
    /// <see cref="CheckMethods"/> therefore calls this for struct, class and enum only.</remarks>
    private void RequireBody(FunctionDecl fn, ModuleSymbol module)
    {
        if (fn.Body is not null || _comp.IsNative(module)) return;

        _de.Report("LYR-SEM0051", Severity.Error, fn.Span,
            $"'{fn.Name}' has no body; only standard-library modules may declare native functions");
    }

    private void CheckMethods(string typeName, Decl[] members, ModuleSymbol module)
    {
        if (module.Members.LookupLocal(typeName) is not TypeSymbol ts) return;
        var thisType = SelfType(ts);
        var isInterface = ts.Kind == TypeSymbolKind.Interface;
        foreach (var m in members)
        {
            if (m is StaticBindingDecl sb)
            {
                CheckStaticBinding(sb, ts);
                continue;
            }

            // A field default is an EXPRESSION and has to be checked like any other. Without this
            // visit its type never reaches the side table, the lowering reads an ErrorType at the
            // construction site (LowerObjectInit) and fails with "ir: type not lowerable: <error>".
            //
            // It only shows when an initializer OMITS the field: 'K { v = 9 }' never evaluates the
            // default, 'K { }' does.
            if (m is FieldDecl { Default: not null } field)
            {
                CheckAssignable(field.Default, CheckExpr(field.Default, module.Members),
                    ResolveType(field.Type, module.Members), field.Default.Span);
                continue;
            }

            if (m is not FunctionDecl fn) continue;
            if (!isInterface) RequireBody(fn, module);
            CheckMemberModifiers(fn);

            // A static member has no receiver, so 'this' is not bound there; CheckExpr reports it as
            // LYR-SEM0008.
            CheckFunction(fn, ts.Members, fn.IsStatic ? null : thisType);
        }
    }

    /// <summary>
    /// <c>static mut fn</c> is an error: <c>mut</c> speaks about the receiver, and a static member
    /// has none.
    ///
    /// <para><c>mut</c> on a class method stays allowed. It enforces nothing there — a reference is
    /// mutable through every binding anyway — but it is a readability convention, and interfaces
    /// declare <c>mut fn</c> that implementing classes have to satisfy.</para>
    /// </summary>
    private void CheckMemberModifiers(FunctionDecl fn)
    {
        if (fn.IsStatic && fn.IsMut)
            _de.Report("LYR-SEM0054", Severity.Error, fn.Span,
                $"'{fn.Name}' is static and cannot be 'mut' — a static member has no receiver");
    }

    /// <summary>A <c>static let</c> constant. Its initializer is checked in the type scope but
    /// without <c>this</c>: there is no instance it could refer to.</summary>
    private void CheckStaticBinding(StaticBindingDecl sb, TypeSymbol ts)
    {
        var outerThis = _currentThis;
        _currentThis = null;

        var declared = sb.Binding.Type is { } t ? ResolveType(t, ts.Members) : null;

        // As with a module 'let': inside an initializer a global that has not been computed yet is an
        // error, not an unknown type.
        _inGlobalInitializer = true;
        var init = sb.Binding.Initializer is { } e ? CheckExpr(e, ts.Members, declared) : null;
        _inGlobalInitializer = false;

        if (declared is null && init is null)
            _de.Report("LYR-SEM0010", Severity.Error, sb.Span,
                $"'{sb.Binding.Name}' needs a type or an initializer");
        else if (declared is not null && init is not null)
            CheckAssignable(sb.Binding.Initializer!, init, declared, sb.Span);

        if (ts.Members.LookupLocal(sb.Binding.Name) is GlobalSymbol gs)
        {
            _globals[gs] = declared ?? init ?? LyrType.Error;
            _result.BindGlobal(gs, _globals[gs]);   // for the lowering
        }

        _currentThis = outerThis;
    }

    private void CheckEnumMethods(EnumDecl e, ModuleSymbol module)
    {
        if (module.Members.LookupLocal(e.Name) is not TypeSymbol ts) return;
        var thisType = SelfType(ts);
        foreach (var fn in e.Methods) CheckFunction(fn, ts.Members, thisType);
    }

    // `this` inside a method: for a generic type the self-instance Stack<T>, with the type parameters
    // as arguments; otherwise plainly the reference.
    private static LyrType SelfType(TypeSymbol ts) =>
        ts.Generics.Length == 0
            ? new NamedRef(ts)
            : new GenericInstance(ts, Array.ConvertAll(ts.Generics, g => (LyrType)new TypeParamType(g)));

    // extend blocks: check the bodies, the orphan rule and interface conformance. Runs after all
    // types, so member lookup goes through the complete registry.
    private void CheckExtensionBlocks()
    {
        foreach (var block in _comp.Extensions.Blocks)
        {
            _currentModule = block.Module;

            if (block.Target is null)
            {
                // An unresolvable target, where RES0002 was already reported, against a resolved but
                // non-extendable one — a generic instance, an array, a tuple, an alias. Only the
                // latter reports SEM0047.
                if (_binding.Resolve(block.Decl.Target) is not (null or ErrorSymbol))
                    _de.Report("LYR-SEM0047", Severity.Error, block.Decl.Target.Span,
                        "extend target must be a plain named type in v1 (no generic, array, tuple or function targets)");
                continue; // without a target there is no useful body check
            }

            // Body check: the outer scope is the block's method scope, which carries cross-calls and
            // the method generics. `this` is the target type — the primitive for builtins, the
            // reference otherwise.
            var thisType = block.Target.Kind == TypeSymbolKind.Builtin
                ? TypeFacts.FromBuiltinName(block.Target.Name)
                : new NamedRef(block.Target);
            foreach (var fn in block.Decl.Methods) CheckFunction(fn, block.MethodScope, thisType);

            CheckOrphanRule(block);
            CheckTypeConformance(block.Target, block.Decl.Interfaces, block.Module, block.Target.Name);
        }
    }

    // Orphan rule: `extend T :: [I]` only when T or one of the I is declared in the module itself.
    // Inherent extends, `extend T { }` without interfaces, are unrestricted.
    private void CheckOrphanRule(ExtensionBlock block)
    {
        if (block.Decl.Interfaces.Length == 0) return;
        if (DeclaredInModule(block.Target!, block.Module)) return;
        foreach (var iface in block.Decl.Interfaces)
            if (Conformance.InterfaceOf(iface, _binding) is { } it && DeclaredInModule(it, block.Module))
                return;
        _de.Report("LYR-SEM0041", Severity.Error, block.Decl.Span,
            $"orphan extension: neither '{block.Target!.Name}' nor any implemented interface is declared in this module");
    }

    private bool DeclaredInModule(TypeSymbol ts, ModuleSymbol module) =>
        ReferenceEquals(module.Members.LookupLocal(ts.Name), ts);

    // --- interface conformance with a signature match ---

    private void CheckTypeConformance(string typeName, TypeNode[] interfaces, ModuleSymbol module)
    {
        if (module.Members.LookupLocal(typeName) is TypeSymbol ts)
            CheckTypeConformance(ts, interfaces, module, typeName);
    }

    // One matching implementation per abstract interface method, either an own member or a visible
    // extension; default methods may be missing. The signature has to match exactly.
    private void CheckTypeConformance(TypeSymbol implementer, TypeNode[] interfaces, ModuleSymbol module, string name)
    {
        if (interfaces.Length == 0) return;
        var candidates = CandidateMethods(implementer, module);

        foreach (var node in interfaces)
        {
            if (InterfaceWithSubst(node) is not { } r) continue;
            var (iface, subst) = r;
            if (iface.Declaration is not InterfaceDecl idecl) continue;

            foreach (var im in idecl.Members)
            {
                var impl = candidates.TryGetValue(im.Name, out var c) ? c : null;
                if (impl is null)
                {
                    if (im.Body is null) // abstract and not implemented
                        _de.Report("LYR-SEM0020", Severity.Error, NodeSpan(node),
                            $"'{name}' does not implement abstract method '{im.Name}' of interface '{iface.Name}'");
                    continue; // a default method is inherited
                }
                var want = (FnType)Substitute(FnTypeOf(FnSym(iface, im.Name)!), subst);
                if (SignatureMismatch(want, im, impl) is { } reason)
                    _de.Report("LYR-SEM0042", Severity.Error, impl.Declaration?.Span ?? NodeSpan(node),
                        $"'{name}.{im.Name}' does not match interface '{iface.Name}': {reason}");
            }
        }
    }

    // Own methods plus visible extension methods, by name; own ones win.
    private Dictionary<string, FunctionSymbol> CandidateMethods(TypeSymbol ts, ModuleSymbol module)
    {
        var map = new Dictionary<string, FunctionSymbol>();
        foreach (var s in ts.Members.Symbols)
            if (s is FunctionSymbol fn) map[fn.Name] = fn;
        foreach (var ext in _comp.Extensions.MethodsFor(ts))
            if (_comp.Sees(module, ext.Module)) map.TryAdd(ext.Symbol.Name, ext.Symbol);
        return map;
    }

    private static FunctionSymbol? FnSym(TypeSymbol iface, string name) =>
        iface.Members.LookupLocal(name) as FunctionSymbol;

    // Signature comparison, invariant: arity, parameter types, return type, mut, and throws ⊆.
    private string? SignatureMismatch(FnType want, FunctionDecl ifaceMethod, FunctionSymbol impl)
    {
        var decl = (FunctionDecl)impl.Declaration!;
        var have = FnTypeOf(impl);
        if (want.Parameters.Length != have.Parameters.Length)
            return $"expected {want.Parameters.Length} parameter(s), found {have.Parameters.Length}";
        for (var i = 0; i < want.Parameters.Length; i++)
            if (!LyrType.Equal(want.Parameters[i], have.Parameters[i]))
                return $"parameter {i + 1} is '{TypeFacts.Display(have.Parameters[i])}', expected '{TypeFacts.Display(want.Parameters[i])}'";
        if (!LyrType.Equal(want.Return, have.Return))
            return $"returns '{TypeFacts.Display(have.Return)}', expected '{TypeFacts.Display(want.Return)}'";
        if (ifaceMethod.IsMut != decl.IsMut)
            return decl.IsMut ? "must not be 'mut'" : "must be declared 'mut'";
        if (!ThrowsSubset(decl.Throws, ifaceMethod.Throws))
            return "throws more than the interface method allows";
        return null;
    }

    // May the implementation's throws clause sit under the interface's? nothing ⊆ typed ⊆ any.
    private bool ThrowsSubset(ThrowsClause? impl, ThrowsClause? iface)
    {
        if (impl is null) return true;            // throws nothing, so always allowed
        if (iface is null) return false;          // the interface forbids throwing, the implementation throws
        if (iface.Type is null) return true;      // the interface allows any throwable
        if (impl.Type is null) return false;      // the implementation is unrestricted, the interface narrow
        var it = ResolveType(iface.Type, _currentModule?.Members ?? _comp.Builtins);
        var pt = ResolveType(impl.Type, _currentModule?.Members ?? _comp.Builtins);
        if (LyrType.Equal(it, pt)) return true;
        // Both sides may be instances ('Box<int> :: [Src<int>]'); TypeFacts.SymbolOf answers the
        // question for both forms.
        return TypeFacts.SymbolOf(pt) is { } implementation
               && TypeFacts.SymbolOf(it) is { } required
               && Conformance.Implements(implementation, required, _binding);
    }

    private static Span NodeSpan(TypeNode n) => n.Span;

    private void CheckFunction(FunctionDecl fn, SymbolTable outerScope, LyrType? thisType)
    {
        var savedReturn = _currentReturn;
        var savedYield = _currentYield;
        var savedThis = _currentThis;
        var savedNarrowed = _narrowed;
        _currentThis = thisType;
        _narrowed = new(ReferenceEqualityComparer.Instance);

        var scope = new SymbolTable(outerScope);
        // The function's own type parameters (fn map<U>) go into the body scope. Signature types are
        // already bound by the resolver, but body types (let x: U) resolve through this scope only.
        if (outerScope.LookupLocal(fn.Name) is FunctionSymbol fsym)
            foreach (var g in fsym.Generics) scope.TryDeclare(g);
        foreach (var p in fn.Parameters)
        {
            var pt = ResolveType(p.Type, scope);
            var ps = new ParameterSymbol(p.Name, pt, p);
            scope.TryDeclare(ps);
            _result.BindRef(p, ps); // for definite-assignment analysis
            if (p.Default is not null)
                CheckAssignable(p.Default, CheckExpr(p.Default, scope), pt, p.Span);
        }
        _currentReturn = fn.ReturnType is not null ? ResolveType(fn.ReturnType, scope) : LyrType.Void;
        // Coroutine: the body never produces the coroutine value, which the runtime builds at the
        // call. Return coverage does not apply; the yield context does instead.
        _currentYield = _currentReturn is CoroutineOf co ? co.Yield : null;
        CheckThrowsClause(fn, scope);

        if (fn.Body is not null)
        {
            CheckBlock(fn.Body, scope);
            if (!TypeFacts.IsVoid(_currentReturn) && _currentYield is null && !Flow.AlwaysReturns(fn.Body, _result))
                _de.Report("LYR-SEM0017", Severity.Error, fn.Span, $"not all code paths of '{fn.Name}' return a value");
        }

        _currentReturn = savedReturn;
        _currentYield = savedYield;
        _currentThis = savedThis;
        _narrowed = savedNarrowed;
    }

    // --- statements ---

    private void CheckBlock(Block block, SymbolTable parent)
    {
        var scope = new SymbolTable(parent);
        var savedNarrowed = new Dictionary<Symbol, LyrType>(_narrowed, ReferenceEqualityComparer.Instance);
        foreach (var stmt in block.Statements) CheckStmt(stmt, scope);
        _narrowed = savedNarrowed; // narrowings established inside the block by an early exit end here
    }

    private void CheckStmt(Stmt stmt, SymbolTable scope)
    {
        switch (stmt)
        {
            case Block b: CheckBlock(b, scope); break;
            case BindingStmt bnd: CheckBinding(bnd, scope); break;
            case DestructuringStmt d: CheckDestructuring(d, scope); break;
            case IfStmt f: CheckIf(f, scope); break;
            case WhileStmt w: CheckWhile(w, scope); break;
            case DoWhileStmt d: CheckBlock(d.Body, scope); CheckCondition(d.Condition, scope); break;
            case ForInStmt fo: CheckForIn(fo, scope); break;
            case ReturnStmt r:
                if (_currentYield is not null) // in a coroutine only a bare return is allowed, as an early end
                {
                    if (r.Value is not null)
                    {
                        CheckExpr(r.Value, scope);
                        _de.Report("LYR-SEM0039", Severity.Error, r.Span,
                            "a coroutine ends with a bare 'return;' — it cannot return a value");
                    }
                }
                else if (r.Value is not null) CheckAssignable(r.Value, CheckExpr(r.Value, scope, _currentReturn), _currentReturn, r.Span);
                else if (!TypeFacts.IsVoid(_currentReturn) && !_currentReturn.IsError)
                    _de.Report("LYR-SEM0001", Severity.Error, r.Span, "return without a value in a non-void function");
                break;
            case ExprStmt es: CheckExpr(es.Expr, scope); break;
            case ThrowStmt t:
                var thrown = CheckExpr(t.Value, scope);
                if (!Conformance.IsThrowable(thrown, _throwable, _binding))
                    _de.Report("LYR-SEM0030", Severity.Error, t.Span,
                        $"cannot throw '{TypeFacts.Display(thrown)}' — only types implementing 'Throwable' can be thrown");
                break;
            case YieldStmt y: // coroutines only; the value is checked against the yield type
                var yv = y.Value is not null ? CheckExpr(y.Value, scope, _currentYield) : null;
                if (_currentYield is null)
                    _de.Report("LYR-SEM0038", Severity.Error, y.Span,
                        "'yield' is only allowed in a coroutine — declare the return type as 'Coroutine<T>'");
                else if (yv is null)
                {
                    if (!TypeFacts.IsVoid(_currentYield) && !_currentYield.IsError)
                        _de.Report("LYR-SEM0038", Severity.Error, y.Span,
                            $"'yield' without a value requires 'Coroutine<void>', this coroutine yields '{TypeFacts.Display(_currentYield)}'");
                }
                else CheckAssignable(y.Value!, yv, _currentYield, y.Span);
                break;
            case DeferStmt de: CheckStmt(de.Body, scope); break;
            case TryStmt tr:
                CheckBlock(tr.Body, scope);
                foreach (var c in tr.Catches) CheckCatch(c, scope);
                break;
            case MatchStmt m: CheckMatch(m, m.Scrutinee, m.Arms, scope, asExpression: false); break;
            // break, continue and error need no check.
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
        _result.BindRef(bnd, local); // for definite-assignment analysis
    }

    private void CheckForIn(ForInStmt fo, SymbolTable scope)
    {
        var iterType = CheckExpr(fo.Iterable, scope);
        var elem = iterType switch
        {
            // The three built-in forms. They have no declaration a conformance could hang on, so the
            // compiler builds an adapter from std.iter for them. Semantically the same protocol; only
            // the way it is obtained differs.
            ArrayOf a => a.Element,
            RangeOf r => r.Element,
            PrimitiveType { Kind: PrimitiveKind.String } => LyrType.Char,

            ErrorType => LyrType.Error,

            // Everything else has to satisfy 'Iterator<T>'. What T is stands in the type's
            // conformance. 'Iterable<T>' comes first — a container SAYS how to walk it — then
            // 'Iterator<T>', for the case where the expression is already a cursor
            // ('for (x in myIterator)').
            //
            // The order matters: reversed, a type satisfying both would be used as its own cursor,
            // and two loops over it would advance each other.
            _ => TypeArgumentOfConformance(iterType, _iterable)
                 ?? YieldTypeOfIterator(iterType, fo.Iterable.Span)
                 ?? Report(fo.Iterable.Span, "LYR-SEM0007",
                     $"'{TypeFacts.Display(iterType)}' is not iterable — it must implement "
                     + "'Iterable<T>' or 'Iterator<T>' from std.iter")
        };
        var loopScope = new SymbolTable(scope);
        var loopVar = new LocalSymbol(fo.Variable, elem, false, fo);
        loopScope.TryDeclare(loopVar);
        _result.BindRef(fo, loopVar); // for definite-assignment analysis
        CheckBlock(fo.Body, loopScope);
    }

    /// <summary>
    /// What does this type yield when iterated? The yield type stands in its
    /// <c>Iterator&lt;T&gt;</c> conformance.
    ///
    /// <para><c>null</c> when the type is not an iterator; the caller reports then. A return value
    /// rather than a diagnostic here, so the message sits at ONE place and can name the expression it
    /// is about.</para>
    /// </summary>
    private LyrType? YieldTypeOfIterator(LyrType type, Span span) =>
        TypeArgumentOfConformance(type, _iterator);

    /// <summary>
    /// The type argument with which <paramref name="type"/> satisfies the interface
    /// <paramref name="iface"/>: <c>class Ones :: [Iterator&lt;int&gt;]</c> yields <c>int</c>.
    ///
    /// <para>One function for both existing cases: <c>Iterator&lt;T&gt;</c> behind <c>for-in</c> and
    /// <c>Indexable&lt;T&gt;</c> behind <c>[i]</c>.</para>
    ///
    /// <para><c>null</c> when the type does not satisfy the interface; the caller reports then. A
    /// return value rather than a diagnostic here, so the message sits at ONE place and can name the
    /// expression it is about.</para>
    /// </summary>
    private LyrType? TypeArgumentOfConformance(LyrType type, TypeSymbol? iface)
    {
        if (iface is null) return null;

        if (TypeFacts.SymbolOf(type) is not { } symbol) return null;

        // The type IS the interface ('Iterator<int>' as a parameter type) or implements it.
        if (ReferenceEquals(symbol, iface))
            return type is GenericInstance { Arguments.Length: 1 } direct ? direct.Arguments[0] : null;

        if (!Conformance.Implements(symbol, iface, _binding)) return null;

        // The type argument stands in the declaration's conformance list.
        var declared = symbol.Declaration switch
        {
            ClassDecl c => c.Interfaces,
            StructDecl v => v.Interfaces,
            _ => null,
        };

        if (declared is null) return null;

        foreach (var node in declared)
        {
            if (node is not NamedType { TypeArguments.Length: 1 } named) continue;

            // Bound through an import ('import std.iter { Iterator }'), the binding points at the
            // import symbol rather than at the type. Without this step the comparison never finds
            // anything, and 'for-in' rejects every user-written iterator.
            var bound = _binding.Resolve(named);
            if (bound is ImportBindingSymbol import) bound = import.Target;

            if (bound is TypeSymbol target && ReferenceEquals(target, iface))
            {
                var argument = ResolveType(named.TypeArguments[0], _comp.Builtins);

                // The conformance list holds the declaration's type PARAMETER:
                // 'class List<T> :: [Indexable<T>]' names T there. For an instance it has to be
                // substituted, or 'List<int>' yields 'T' as its element type instead of 'int'.
                //
                // For a non-generic conformance ('class Ones :: [Iterator<int>]') the substitution is
                // empty and the result unchanged, so there is no special case here.
                return type is GenericInstance instance
                    ? Substitute(argument, SubstMap(instance))
                    : argument;
            }
        }

        return null;
    }

    // throws clause: the declared type has to be throwable, and the resolved symbol is bound to the
    // clause for the ExceptionAnalyzer.
    private void CheckThrowsClause(FunctionDecl fn, SymbolTable scope)
    {
        if (fn.Throws?.Type is not { } tn) return;
        var t = ResolveType(tn, scope);
        if (!Conformance.IsThrowable(t, _throwable, _binding))
            _de.Report("LYR-SEM0030", Severity.Error, fn.Throws.Span,
                $"'{TypeFacts.Display(t)}' in 'throws' does not implement 'Throwable'");
        if (TypeFacts.SymbolOf(t) is { } thrown) _result.BindRef(fn.Throws, thrown);
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
            if (TypeFacts.SymbolOf(bt) is { } caught)
                _result.BindRef(clause.BindingType, caught); // for the ExceptionAnalyzer
        }
        else bt = _throwable is not null ? new NamedRef(_throwable) : LyrType.Error; // a catch-all binds Throwable

        if (clause.BindingName is not null)
        {
            var local = new LocalSymbol(clause.BindingName, bt, false, clause);
            catchScope.TryDeclare(local);
            _result.BindRef(clause, local); // for definite-assignment analysis: the catch assigns the binding
        }
        CheckBlock(clause.Body, catchScope);
    }

    private void CheckCondition(Expr cond, SymbolTable scope)
    {
        var t = CheckExpr(cond, scope);
        if (!TypeFacts.IsBool(t) && !t.IsError)
            _de.Report("LYR-SEM0004", Severity.Error, cond.Span, $"condition must be 'bool', got '{TypeFacts.Display(t)}'");
    }

    // if with nullable narrowing: 'if (x != null)' narrows x to T in the then branch, and
    // 'if (x == null) { return; }' narrows x afterwards through the early exit.
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

        // 'AlwaysExits' rather than 'AlwaysReturns': for narrowing what counts is whether the code
        // after the 'if' is reached at all, and 'continue' leaves the block just as 'return' does.
        if (Flow.AlwaysExits(f.Then, _result)) Apply(elseFacts);
        else if (f.Else is not null && Flow.AlwaysExits(f.Else, _result)) Apply(thenFacts);
    }

    /// <summary>
    /// <c>while (x != null) { … }</c> — inside the body x is no longer a <c>?T</c>.
    ///
    /// <para>Sound although a loop may change its variable: the condition is re-checked before EVERY
    /// iteration and therefore holds at the start of the body, and an assignment in the body drops
    /// the narrowing from that point on (see <c>CheckAssign</c>).</para>
    ///
    /// <para><c>do-while</c> deliberately does NOT get this: there the body runs before the first
    /// check, so the condition says nothing at the start of the body.</para>
    /// </summary>
    private void CheckWhile(WhileStmt w, SymbolTable scope)
    {
        CheckCondition(w.Condition, scope);

        var (thenFacts, _) = NarrowingFacts(w.Condition);
        var snapshot = new Dictionary<Symbol, LyrType>(_narrowed, ReferenceEqualityComparer.Instance);

        Apply(thenFacts);
        CheckBlock(w.Body, scope);
        _narrowed = snapshot;
    }

    private void Apply(Dictionary<Symbol, LyrType> facts)
    {
        foreach (var (sym, type) in facts) _narrowed[sym] = type;
    }

    /// <summary>
    /// What a condition proves about nullable variables, separately for the true and the false
    /// outcome.
    ///
    /// <para>Composite conditions count: for <c>a &amp;&amp; b</c> the then branch gets what BOTH
    /// sides prove, because the branch runs only when both are true. For the else branch nothing
    /// holds: it is reached as soon as ONE side is false, and which one is unknown. For <c>||</c> it
    /// is exactly the other way round.</para>
    ///
    /// <para>That asymmetry is why the two directions are collected separately rather than as one
    /// fact with a sign.</para>
    /// </summary>
    private (Dictionary<Symbol, LyrType> then, Dictionary<Symbol, LyrType> els) NarrowingFacts(Expr cond)
    {
        var then = new Dictionary<Symbol, LyrType>(ReferenceEqualityComparer.Instance);
        var els = new Dictionary<Symbol, LyrType>(ReferenceEqualityComparer.Instance);

        if (cond is BinaryExpr { Operator: BinaryOp.LogicalAnd or BinaryOp.LogicalOr } logical)
        {
            var (leftThen, leftElse) = NarrowingFacts(logical.Left);
            var (rightThen, rightElse) = NarrowingFacts(logical.Right);

            if (logical.Operator == BinaryOp.LogicalAnd)
            {
                foreach (var (sym, type) in leftThen) then[sym] = type;
                foreach (var (sym, type) in rightThen) then[sym] = type;
            }
            else
            {
                foreach (var (sym, type) in leftElse) els[sym] = type;
                foreach (var (sym, type) in rightElse) els[sym] = type;
            }

            return (then, els);
        }

        if (cond is BinaryExpr { Operator: BinaryOp.Ne or BinaryOp.Eq } b
            && NullCompared(b) is { } id
            && _result.RefOf(id) is { } sym2
            && DeclaredType(sym2) is Optional opt)
        {
            (b.Operator == BinaryOp.Ne ? then : els)[sym2] = opt.Inner;
        }

        return (then, els);
    }

    private static IdentifierExpr? NullCompared(BinaryExpr b) => b switch
    {
        { Left: IdentifierExpr l, Right: NullLiteralExpr } => l,
        { Left: NullLiteralExpr, Right: IdentifierExpr r } => r,
        _ => null
    };

    // --- expressions ---

    // 'expected' is the context type of the position: the binding type, the return type, a field type.
    // Only the forms that need it use it — an empty array literal, a contextual enum variant
    // construction; everything else ignores it.
    /// <summary>
    /// An expression in VALUE POSITION. If it names a type or a module instead, that is an error here
    /// — exactly once, at the place where the value is needed. From there on <see cref="ErrorType"/>
    /// applies, and with it the ordinary "already reported" rule.
    /// </summary>
    private LyrType CheckExpr(Expr expr, SymbolTable scope, LyrType? expected = null)
    {
        var type = CheckTarget(expr, scope, expected);
        if (type is not NonValueType nv) return type;

        var hint = nv.Symbol is TypeSymbol ? $" — did you mean '{nv.Symbol.Name} {{ … }}'?" : "";
        _de.Report("LYR-SEM0052", Severity.Error, expr.Span,
            $"'{nv.Symbol.Name}' is a {nv.Kind}, not a value{hint}");

        _result.SetType(expr, LyrType.Error);
        return LyrType.Error;
    }

    /// <summary>
    /// Like <see cref="CheckExpr"/>, but leaves a <see cref="NonValueType"/> standing. Only for the
    /// TARGET OF A MEMBER ACCESS, where a type or module name is legitimate (<c>Point.new(…)</c>,
    /// <c>console.println(…)</c>) and <c>CheckMember</c> continues through the symbol anyway, not
    /// through the type.
    /// </summary>
    private LyrType CheckTarget(Expr expr, SymbolTable scope, LyrType? expected = null)
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

            case CallExpr call: return CheckCall(call, scope, expected);
            case MemberExpr mem: return CheckMember(mem, scope, expected);
            case StructInitExpr si: return CheckStructInit(si, scope, expected);
            case TypePathExpr tp: return CheckTypePath(tp, scope);
            case IfExpr iff: return CheckIfExpr(iff, scope);
            case MatchExpr ma: return UnifyArms(CheckMatch(ma, ma.Scrutinee, ma.Arms, scope, asExpression: true), ma.Span);
            case LambdaExpr lam: return CheckLambda(lam, scope, expected);
            case ResumeExpr re: return CheckResume(re, scope);
            // Attributes are post-v1 and have no expression type. Reporting that rather than silently
            // yielding Error is the difference between "does not work" and "does not work
            // unnoticed".
            case AtIdentifierExpr at:
                return Report(at.Span, "LYR-SEM0053",
                    $"'{at.Name}' is an attribute, and attributes are not part of v1");

            default: return LyrType.Error;
        }
    }

    /// <summary>
    /// The type of a global being read, and the place where "used before it is initialized" shows.
    ///
    /// <para>Globals are filled in DECLARATION ORDER, first by module and then by source order;
    /// reading a later one would see a null value nobody wrote. That is the only order without a
    /// dependency analysis — C# does the same for field initializers, while Go sorts topologically
    /// and rejects cycles.</para>
    ///
    /// <para>Only INSIDE an initializer is this an error. From a function body every global is
    /// readable wherever it stands, because the init phase is long over by then.</para>
    /// </summary>
    private LyrType TypeOfGlobalReference(GlobalSymbol symbol, Span span)
    {
        if (_globals.TryGetValue(symbol, out var type)) return type;
        if (!_inGlobalInitializer) return LyrType.Error;

        return Report(span, "LYR-SEM0057",
            $"'{symbol.Name}' is used before it is initialized; constants are initialized in " +
            "declaration order, so an initializer may only read constants declared before it");
    }

    private LyrType CheckIdentifier(IdentifierExpr id, SymbolTable scope)
    {
        var sym = scope.Lookup(id.Name);
        if (sym is null) return Report(id.Span, "LYR-SEM0002", $"unknown identifier '{id.Name}'");
        _result.BindRef(id, sym);
        if (_narrowed.TryGetValue(sym, out var narrowed)) return narrowed; // ?T is T inside the narrowed region
        // A selective import (import std.io.console { println }) binds to a shell; the target is what
        // gets typed. Without this every stdlib call falls into the error branch and is skipped
        // silently, leaving the signature from the .lyr file without effect.
        if (sym is ImportBindingSymbol binding) sym = binding.Target;
        return sym switch
        {
            ParameterSymbol p => p.Type,
            LocalSymbol l => l.Type,
            GlobalSymbol g => TypeOfGlobalReference(g, id.Span),
            FunctionSymbol f => FnTypeOf(f),

            // A type or module name is not a value. As a member TARGET it is legitimate all the same,
            // so no immediate error here; CheckExpr reports it where a value is needed.
            TypeSymbol ts => new NonValueType(ts, "type"),
            ModuleSymbol ms => new NonValueType(ms, "module"),

            // ExternalSymbol: the module could not be found and was reported as LYR-RES0003, so Error
            // is the correct poison here.
            _ => LyrType.Error
        };
    }

    private FnType FnTypeOf(FunctionSymbol f)
    {
        var fn = (FunctionDecl)f.Declaration!;
        var ps = fn.Parameters.Select(p => ResolveType(p.Type, _comp.Builtins)).ToArray();
        var ret = IsPanic(f) ? LyrType.Never // panic has the unnameable type never
            : fn.ReturnType is not null ? ResolveType(fn.ReturnType, _comp.Builtins) : LyrType.Void;
        return new FnType(ps, ret);
    }

    /// <summary>
    /// The callee of a call, checked with the expected RESULT type behind it.
    ///
    /// <para>Needed at exactly one place: an enum variant without written type arguments.
    /// <c>Opt.Some(7)</c> names the instance nowhere, <c>let o: Opt&lt;int&gt; = …</c> does — and the
    /// struct form <c>Ev.Hit { … }</c> has always read the context.</para>
    ///
    /// <para>The expected type belongs to the call, not to the callee; it is therefore used ONLY for
    /// resolving the instance and is not passed on as an expectation about the function type.</para>
    /// </summary>
    private LyrType CheckTargetOfCall(Expr callee, SymbolTable scope, LyrType? expected) =>
        callee is MemberExpr mem ? CheckExpr(mem, scope, expected) : CheckExpr(callee, scope);

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
            default: // PreInc and PreDec
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
            default: // Inc and Dec
                if (!TypeFacts.IsNumeric(t)) BadOp(p.Span, "++/--", t);
                return t;
        }
    }

    private LyrType CheckBinary(BinaryExpr b, SymbolTable scope)
    {
        var l = CheckExpr(b.Left, scope);

        // Short circuit: the right side runs only when the left is true, and for '||' only when it is
        // false. So while checking the right side, what the left proved holds: 'x != null && x > 0'
        // is well formed, because x is no longer a '?int' in the second part.
        LyrType r;
        if (b.Operator is BinaryOp.LogicalAnd or BinaryOp.LogicalOr)
        {
            var (thenFacts, elseFacts) = NarrowingFacts(b.Left);
            var snapshot = new Dictionary<Symbol, LyrType>(_narrowed, ReferenceEqualityComparer.Instance);
            Apply(b.Operator == BinaryOp.LogicalAnd ? thenFacts : elseFacts);
            r = CheckExpr(b.Right, scope);
            _narrowed = snapshot;
        }
        else
        {
            r = CheckExpr(b.Right, scope);
        }

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
                // Numerics keep their opcodes; a 'char' is numeric itself and needs no branch of
                // its own. Everything else orders through 'Ordered' — 'string < string' included,
                // because the stdlib conforms string to Ordered<string>.
                if (UnifyNumeric(b.Left, l, b.Right, r) is not null)
                    return LyrType.Bool;
                CheckOrdered(b, l, r, scope);
                return LyrType.Bool;
            case BinaryOp.Eq or BinaryOp.Ne:
                if (!LyrType.Equal(l, r) && UnifyNumeric(b.Left, l, b.Right, r) is null
                    && l is not NullType && r is not NullType)
                    BadBinary(b, l, r);
                else CheckEquatable(b, l, r, scope);
                return LyrType.Bool;
            case BinaryOp.LogicalAnd or BinaryOp.LogicalOr:
                if (!TypeFacts.IsBool(l) || !TypeFacts.IsBool(r)) BadBinary(b, l, r);
                return LyrType.Bool;
            default: // Coalesce
                return CheckCoalesce(b, l, r);
        }
    }

    /// <summary>
    /// What <c>==</c> and <c>!=</c> may compare: scalars, <c>null</c>, and every type that conforms
    /// to <c>Equatable</c>.
    /// </summary>
    /// <remarks>
    /// <para>On a conforming type the operator IS the interface method. The checker builds the call
    /// <c>a.equals(b)</c> from synthetic nodes, checks it through the ordinary member path — which
    /// settles extensions, generic instances and constraint dispatch exactly as a written call
    /// would — and records it for the lowering. No second dispatch mechanism: written as an
    /// operator, resolved as the method.</para>
    ///
    /// <para>CONFORMANCE is required, not the method alone. A type with an <c>equals</c> nobody
    /// declared as <c>Equatable</c> stays rejected — otherwise any method of that name would
    /// silently become an operator, and the diagnostic could not name the contract.</para>
    ///
    /// <para>For everything else the rule lives here as well as in the IR verifier. Without it the
    /// sema lets <c>a == b</c> through and the verifier rejects it afterwards, as a compiler crash
    /// rather than a diagnostic.</para>
    /// </remarks>
    private void CheckEquatable(BinaryExpr b, LyrType l, LyrType r, SymbolTable scope)
    {
        if (l is NullType || r is NullType) return;
        if (l is PrimitiveType or ErrorType) return;

        var op = b.Operator is BinaryOp.Eq ? "==" : "!=";

        // NO unwrapping of '?T': comparing two optionals is not implemented in the backend, where
        // the verifier reports "equality comparison on type ?i64". The common case '?T == null' is
        // already handled above, and flow narrowing covers it too.
        if (l is Optional)
        {
            _de.Report("LYR-SEM0059", Severity.Error, b.Span,
                $"'{op}' is not defined for '{TypeFacts.Display(l)}' — an optional compares "
                + "against 'null'; narrow it first, then compare the value");
            return;
        }

        if (CanConform(l))
        {
            if (_equatable is { } equatable
                && Satisfies(l, equatable, new GenericInstance(equatable, [l])))
            {
                // A failed check inside the call has reported already; no second message.
                DesugarToMethodCall(b, "equals", scope);
                return;
            }

            _de.Report("LYR-SEM0059", Severity.Error, b.Span,
                $"'{op}' is not defined for '{TypeFacts.Display(l)}' — equality comes from "
                + $"'Equatable': declare the type with ':: [Equatable<{TypeFacts.Display(l)}>]' "
                + $"and a 'fn equals(other: {TypeFacts.Display(l)}): bool'");
            return;
        }

        _de.Report("LYR-SEM0059", Severity.Error, b.Span,
            $"'{op}' is not defined for '{TypeFacts.Display(l)}'");
    }

    /// <summary>Can this type conform to an interface at all? Only what can carry a conformance
    /// list — directly or through an <c>extend</c> block.</summary>
    private static bool CanConform(LyrType t) => t switch
    {
        NamedRef { Symbol.Kind: TypeSymbolKind.Struct or TypeSymbolKind.Class or TypeSymbolKind.Enum }
            => true,
        GenericInstance { Definition.Kind: TypeSymbolKind.Struct or TypeSymbolKind.Class or TypeSymbolKind.Enum }
            => true,
        TypeParamType => true,
        _ => false,
    };

    /// <summary>
    /// Builds <c>left.method(right)</c>, checks it, and records it as what the operator means.
    ///
    /// <para>The synthetic nodes carry the operator expression's span, so anything reported or
    /// mapped from them lands on what the user wrote. They reuse the REAL operand nodes, which is
    /// what makes the lowering evaluate each operand exactly once. The operands were clean — poison
    /// returned before the operator was examined — so their second pass through the checker
    /// reproduces the same table entries.</para>
    /// </summary>
    private void DesugarToMethodCall(BinaryExpr b, string method, SymbolTable scope)
    {
        var member = new MemberExpr(b.Left, method, IsOptional: false, b.Span);
        var call = new CallExpr(member, [b.Right], b.Span);

        if (CheckExpr(call, scope).IsError) return;

        _result.DesugarOperator(b, call);
    }

    /// <summary>
    /// What <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c> and <c>&gt;=</c> may compare beyond numerics:
    /// every type that conforms to <c>Ordered</c>.
    /// </summary>
    /// <remarks>
    /// <para>The desugar is <c>a.compare(b)</c>; which of the four operators stood there decides
    /// how the lowering reads the sign of the answer. One method, four operators — the reason
    /// <c>Ordered</c> has a single <c>compare</c> rather than four members.</para>
    ///
    /// <para><c>string</c> arrives here as a primitive and conforms through the stdlib's own
    /// <c>extend string :: [Ordered&lt;string&gt;]</c> — the same route any type takes, which is
    /// what finally admits <c>string &lt; string</c> without a rule of its own.</para>
    /// </remarks>
    private void CheckOrdered(BinaryExpr b, LyrType l, LyrType r, SymbolTable scope)
    {
        if (!LyrType.Equal(l, r))
        {
            BadBinary(b, l, r);
            return;
        }

        if ((CanConform(l) || l is PrimitiveType)
            && _ordered is { } ordered
            && Satisfies(l, ordered, new GenericInstance(ordered, [l])))
        {
            DesugarToMethodCall(b, "compare", scope);
            return;
        }

        if (CanConform(l))
        {
            var op = b.Operator switch
            {
                BinaryOp.Lt => "<",
                BinaryOp.Le => "<=",
                BinaryOp.Gt => ">",
                _ => ">=",
            };
            _de.Report("LYR-SEM0003", Severity.Error, b.Span,
                $"'{op}' is not defined for '{TypeFacts.Display(l)}' — ordering comes from "
                + $"'Ordered': declare the type with ':: [Ordered<{TypeFacts.Display(l)}>]' and a "
                + $"'fn compare(other: {TypeFacts.Display(l)}): int'");
            return;
        }

        BadBinary(b, l, r);
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

    /// <summary>
    /// Gives an EMPTY array literal its element type from the context.
    /// </summary>
    /// <remarks>
    /// <para><c>[]</c> has no element type of its own; it comes from outside. Where the expected type
    /// is already at hand while checking (<c>let xs: int[] = []</c>), the ordinary context passing
    /// does it; where it is not, it has to be supplied afterwards.</para>
    /// <para>There are two such places: arguments, where the two-phase inference types non-lambda
    /// arguments without context, and <c>??</c>, where only <see cref="IsAssignable"/> is asked
    /// rather than <see cref="CheckAssignable"/>.</para>
    /// <para>The call has to come BEFORE any poison check: the type of the empty literal contains an
    /// <c>ErrorType</c>, but it is not a reported error — it is an open slot the context is about to
    /// close.</para>
    /// </remarks>
    private bool AdaptEmptyArray(Expr expr, LyrType to)
    {
        while (to is Optional optional) to = optional.Inner;

        if (expr is not ArrayLitExpr { Elements.Length: 0 } || to is not ArrayOf) return false;

        _result.SetType(expr, to);
        return true;
    }

    private LyrType CheckCoalesce(BinaryExpr b, LyrType l, LyrType r)
    {
        if (l is Optional o)
        {
            // Adapt first, then ask: 'source() ?? []' would otherwise fail on an '<error>[]' on the
            // right, although the target type stands next to it.
            if (AdaptEmptyArray(b.Right, o.Inner)) return o.Inner;

            if (IsAssignable(b.Right, r, o.Inner)) return o.Inner; // ?T ?? T yields T
            if (IsAssignable(b.Right, r, l)) return l;              // ?T ?? ?T yields ?T
            BadBinary(b, l, r);
            return o.Inner;
        }
        if (l is NullType) return r; // null ?? b yields the type of b
        return l; // the left side is not nullable, so ?? has no effect, but it is no type error
    }

    private LyrType CheckAssign(AssignExpr a, SymbolTable scope)
    {
        CheckExpr(a.Target, scope); // binds RefOf
        var targetSym = a.Target is IdentifierExpr ? _result.RefOf(a.Target) : null;
        // For identifier targets take the DECLARED type rather than the narrowed one, or 'x = null' on
        // a narrowed ?T would wrongly be an error.
        var targetType = targetSym is not null ? DeclaredType(targetSym) ?? _result.TypeOf(a.Target) : _result.TypeOf(a.Target);
        var value = CheckExpr(a.Value, scope, targetType);
        // The lvalue and mutability check happens in SemaRules; only type compatibility here.
        CheckAssignable(a.Value, value, targetType, a.Span);
        if (targetSym is not null) _narrowed.Remove(targetSym); // a reassignment drops the narrowing
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
        if (TypeFacts.IsNumeric(op) && TypeFacts.IsNumeric(target)) return target; // numeric to numeric only
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

            // A string has NO index operator. A code point position costs O(n) — a 'char' is a code
            // point and the length counts the same units — so the obvious indexing loop would be
            // quadratic without looking like it.
            //
            // The message names both ways out: 'charAt' is what the user is looking for right now,
            // 'for-in' is what they usually want.
            PrimitiveType { Kind: PrimitiveKind.String } =>
                Report(ix.Span, "LYR-SEM0007",
                    "a string cannot be indexed — a codepoint position costs O(n), so an index "
                    + "loop would be quadratic. Use 'std.string.charAt(s, i)' if you really need "
                    + "one position, or 'for (c in s)' to walk all of them"),

            ErrorType => LyrType.Error,

            // Everything else has to satisfy 'Indexable<T>' from std.collections — the same rule and
            // mechanism 'for-in' uses for 'Iterator<T>'. The compiler knows exactly ONE built-in
            // indexable form, the array; every other container goes through the interface.
            _ => TypeArgumentOfConformance(target, _indexable)
                 ?? Report(ix.Span, "LYR-SEM0007",
                     $"'{TypeFacts.Display(target)}' is not indexable — it must implement "
                     + "'Indexable<T>' from std.collections")
        };
    }

    private LyrType CheckArrayLit(ArrayLitExpr arr, SymbolTable scope, LyrType? expected)
    {
        var elemExpected = expected is ArrayOf ea ? ea.Element : null;
        if (arr.Elements.Length == 0)
            return new ArrayOf(elemExpected ?? LyrType.Error, null); // empty: the element type comes from the context alone
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

    // --- calls, members, struct initializers, composites ---

    // Two phases: type the non-lambda arguments eagerly, infer the type arguments from them, then
    // check the lambdas with the substituted parameter type as context — their actual type binds the
    // remaining type arguments, such as U from the lambda return.
    /// <param name="expected">The expected type of the CALL. Needed for an enum variant alone:
    /// 'let o: Opt&lt;int&gt; = Opt.Some(7);' names the instance only on the left.</param>
    private LyrType CheckCall(CallExpr call, SymbolTable scope, LyrType? expected = null)
    {
        var calleeType = CheckTargetOfCall(call.Callee, scope, expected);

        // 'b?.get()' — optional chaining with a CALL. 'b?.get' is a '?fn() -> int', and without this
        // case the sema reports '"?fn() -> int" is not callable': a statement about an intermediate
        // type nobody wrote down.
        //
        // The call is checked against the unwrapped signature and the RESULT becomes optional: when
        // the receiver is empty no call happens and there is nothing to return.
        //
        // For a method ONLY. If the member holds a function VALUE ('f: fn() -> int') there are two
        // questions and one '?': whether the receiver is there, and whether the field is set.
        // Unwrapping here would answer the second one with yes, and for 'f: ?fn() -> int' that would
        // be a call on null. The language has no '?()', so the form does not exist.
        var optionalCall = false;
        if (call.Callee is MemberExpr { IsOptional: true } chainCallee && calleeType is Optional o)
        {
            if (_result.RefOf(chainCallee) is FunctionSymbol && o.Inner is FnType method)
            {
                optionalCall = true;
                calleeType = method;
            }
            else if (o.Inner is FnType)
            {
                foreach (var a in call.Arguments) CheckExpr(a, scope);
                return Report(call.Span, "LYR-SEM0062",
                    $"'?.' with a call works on a method; '{chainCallee.Member}' holds a function "
                    + "value — read it into a variable first, then call that");
            }
        }

        if (calleeType is not FnType fn)
        {
            foreach (var a in call.Arguments) CheckExpr(a, scope); // type them anyway, to avoid follow-up errors
            if (calleeType.IsError) return LyrType.Error;
            return Report(call.Span, "LYR-SEM0013", $"'{TypeFacts.Display(calleeType)}' is not callable");
        }

        var fsym = TargetSymbol(call.Callee) as FunctionSymbol;
        var decl = fsym?.Declaration as FunctionDecl;
        var args = call.Arguments;
        var argTypes = new LyrType[args.Length];

        // Phase A: non-lambdas, eagerly — with the declared parameter type as the context, the same
        // one phase C gives a lambda. It is what lets 'f(Opt.Some(5))' name its instance: without
        // it an argument position was the one value position with no expected type, while a
        // binding, a return and a field all had one.
        //
        // Only when the parameter is CONCRETE. In 'fn f<T>(o: Opt<T>)' the parameter still holds the
        // type parameter, and offering 'Opt<T>' as the expected type would fix the instance to
        // something the inference is supposed to determine from this very argument.
        for (var i = 0; i < args.Length; i++)
            if (args[i] is not LambdaExpr)
                argTypes[i] = CheckExpr(args[i], scope, ConcreteExpectation(fn, decl, i));

        // Phase B: type arguments from the eagerly typed arguments.
        Dictionary<GenericParamSymbol, LyrType>? map = null;
        var substituted = fn;
        if (fsym is { Generics.Length: > 0 })
        {
            map = new Dictionary<GenericParamSymbol, LyrType>(ReferenceEqualityComparer.Instance);

            // Explicitly written type arguments ('f<int>()') bind FIRST. The inference then runs
            // unchanged and only fills what is still open: 'UnifyInfer' uses 'TryAdd' and therefore
            // overwrites nothing. What is written always wins, and 'id<int>("x")' becomes a type
            // error instead of silently turning into 'id<string>'.
            //
            // They are needed where the arguments give nothing: a factory 'empty<T>(): List<T>' has
            // none and would not be callable without this route.
            if (call.TypeArguments is { Length: > 0 } written)
            {
                if (written.Length != fsym.Generics.Length)
                    _de.Report("LYR-SEM0026", Severity.Error, call.Span,
                        $"generic function '{fsym.Name}' expects {fsym.Generics.Length} type "
                        + $"argument(s), got {written.Length}");

                var explicitArgs = new LyrType[Math.Min(written.Length, fsym.Generics.Length)];
                for (var i = 0; i < explicitArgs.Length; i++)
                {
                    explicitArgs[i] = ResolveType(written[i], scope);
                    map[fsym.Generics[i]] = explicitArgs[i];
                }

                // Constraints apply to written arguments too, or the explicit form would be a way
                // around them.
                CheckConstraints(fsym.Generics, explicitArgs, call.Span);
            }

            var n = Math.Min(fn.Parameters.Length, args.Length);
            for (var i = 0; i < n; i++)
                if (args[i] is not LambdaExpr) UnifyInfer(fn.Parameters[i], argTypes[i], map);
            substituted = (FnType)Substitute(fn, map);
        }

        // Phase C: lambdas with context; their actual type binds the type arguments still open.
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is not LambdaExpr) continue;
            argTypes[i] = CheckExpr(args[i], scope, ExpectedParamAt(substituted, decl, i));
            if (map is not null && i < fn.Parameters.Length)
                UnifyInfer(Substitute(fn.Parameters[i], map), argTypes[i], map);
        }
        if (map is not null)
        {
            CheckInferredConstraints(fsym!.Generics, map, call.Span);
            substituted = (FnType)Substitute(fn, map);

            // A type parameter the inference could not bind is reported HERE. Otherwise it silently
            // becomes 'LyrType.Error' and only the lowering trips over it, with a compiler-internal
            // message at a place where the user merely omitted a type argument.
            //
            // Not reported when an argument was already faulty: the cause is reported then, and a
            // second line about a type argument would be follow-up noise.
            if (!argTypes.Any(ContainsError))
                foreach (var generic in fsym.Generics)
                    if (!map.ContainsKey(generic))
                        _de.Report("LYR-SEM0060", Severity.Error, call.Span,
                            $"cannot infer type argument '{generic.Name}' for '{fsym.Name}' — " +
                            "no argument determines it; write it explicitly");

            // For monomorphization: which instance is meant is settled here and nowhere else. In
            // declaration order, because that is the identity of the instance.
            _result.SetTypeArguments(call, fsym.Generics
                .Select(g => map.TryGetValue(g, out var bound) ? bound : LyrType.Error)
                .ToArray());
        }

        CheckCallArgs(call, substituted, argTypes, decl);

        // If the receiver was optional the result is too, collapsed, because optionals do not nest.
        return optionalCall ? Optionalized(substituted.Return) : substituted.Return;
    }

    /// <summary>
    /// The declared type of parameter <paramref name="i"/>, but only when it names no type parameter
    /// that is still open.
    ///
    /// <para>An expectation is a statement about which instance is meant. A type still holding a
    /// <c>T</c> makes no such statement — it is the question the inference answers from the argument
    /// — and passing it down would answer that question with itself.</para>
    /// </summary>
    private static LyrType? ConcreteExpectation(FnType fn, FunctionDecl? decl, int i) =>
        ExpectedParamAt(fn, decl, i) is { } t && !MentionsTypeParam(t) ? t : null;

    /// <summary>Does this type still carry a type parameter anywhere inside it?</summary>
    private static bool MentionsTypeParam(LyrType type) => type switch
    {
        TypeParamType => true,
        Optional o => MentionsTypeParam(o.Inner),
        ArrayOf a => MentionsTypeParam(a.Element),
        CoroutineOf c => MentionsTypeParam(c.Yield),
        TupleOf t => t.Elements.Any(MentionsTypeParam),
        GenericInstance g => g.Arguments.Any(MentionsTypeParam),
        FnType f => f.Parameters.Any(MentionsTypeParam) || MentionsTypeParam(f.Return),
        _ => false,
    };

    private static LyrType? ExpectedParamAt(FnType fn, FunctionDecl? decl, int i)
    {
        var ps = decl?.Parameters;
        var variadic = ps is { Length: > 0 } && ps[^1].IsParams;
        var fixedCount = variadic ? ps!.Length - 1 : fn.Parameters.Length;
        if (i < fixedCount && i < fn.Parameters.Length) return fn.Parameters[i];
        if (variadic && fn.Parameters[^1] is ArrayOf elem) return elem.Element;
        return null;
    }

    /// <summary>
    /// Is the <c>params</c> array passed as a whole rather than piece by piece?
    ///
    /// <para>Exactly when ONE argument is left and its type is that of the array — <c>sum(xs)</c>
    /// with <c>xs: int[]</c>. That is unambiguous in Lyric for two reasons C# lacks: there is no
    /// implicit conversion between <c>T</c> and <c>T[]</c>, and there is no overloading. With
    /// <c>params xs: int[][]</c> an element is <c>int[]</c> and the array <c>int[][]</c> — different
    /// types, no conflict.</para>
    ///
    /// <para>Allowed at all because without this route no variadic function could delegate to
    /// another: <c>fn logged(params xs: int[]) { return sum(xs); }</c> would be impossible. To pass
    /// an array deliberately as ONE element, write <c>f([a])</c>.</para>
    /// </summary>
    private static bool PassesArrayDirectly(LyrType[] argTypes, int fixedCount, LyrType arrayType) =>
        argTypes.Length == fixedCount + 1 && LyrType.Equal(argTypes[fixedCount], arrayType);

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
        {
            // A ready-made array passes through as a whole, or one variadic function could not
            // delegate to another. See PassesArrayDirectly.
            if (PassesArrayDirectly(argTypes, fixedCount, fn.Parameters[^1])) return;

            for (var i = fixedCount; i < argTypes.Length; i++)
                CheckAssignable(call.Arguments[i], argTypes[i], elem.Element, call.Arguments[i].Span);
        }
    }

    /// <param name="expected">Needed for an enum variant without written type arguments only;
    /// without effect otherwise.</param>
    private LyrType CheckMember(MemberExpr mem, SymbolTable scope, LyrType? expected = null)
    {
        // CheckTarget rather than CheckExpr: a type or module name is allowed here.
        var targetType = CheckTarget(mem.Target, scope);

        // Whether the receiver NAMES something or produces a value is what CheckTarget has just
        // decided: a NonValueType is a name, everything else is a value. Asking the reference table
        // instead is a second answer to that question and the weaker one — the table knows that
        // 'Point { … }' mentions Point, not that it BUILDS one, and would dispatch the member as if
        // the type itself stood there.
        //
        // An unresolvable import gives an error type and falls through: InstanceMemberOf has no case
        // for it and the IsError check below returns without a second diagnostic.
        switch (targetType)
        {
            case NonValueType { Symbol: TypeSymbol ts } named:
                return BindMember(mem, MemberOfType(ts, mem.Member, mem.Span,
                    named.Instance ?? ExpectedInstance(expected, ts)));

            case NonValueType { Symbol: ModuleSymbol mod }:
                return BindMember(mem, MemberOfModule(mod, mem.Member, mem.Span));
        }

        var baseType = mem.IsOptional && targetType is Optional opt ? opt.Inner : targetType;

        // 'length' on T[] is built in rather than a library method: T[] is a real array, and its
        // length is a property of the value. Growing containers bring their members along through
        // Indexable and Iterator.
        if (baseType is ArrayOf && mem.Member == "length") return LyrType.Int;

        if (InstanceMemberOf(baseType, mem, mem.Span) is { } mt)
            return mem.IsOptional ? Optionalized(mt) : mt;
        if (targetType.IsError) return LyrType.Error;
        return Report(mem.Span, "LYR-SEM0012", $"'{TypeFacts.Display(targetType)}' has no member '{mem.Member}'");
    }

    /// <summary>
    /// <c>T</c> becomes <c>?T</c>, while <c>?T</c> stays <c>?T</c>.
    ///
    /// <para>Optionals do not nest; the lowering reports <c>??T</c> as <c>LYR-IR0001</c>. Without the
    /// collapse, <c>b?.v</c> on a field of type <c>?int</c> would be a <c>??int</c>, and the error
    /// would arrive as "cannot assign '?int' to 'int'" one level too late.</para>
    /// </summary>
    private static LyrType Optionalized(LyrType t) => t is Optional ? t : new Optional(t);

    // Instance members over the three "object" types: a concrete one (NamedRef), a generic instance,
    // where the member type has T substituted by the argument, or a type parameter, whose members
    // come from its constraints.
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
            case PrimitiveType p when BuiltinSymbol(p) is { } bs: // extensions on builtins, such as string.shout()
                return BindMember(mem, InstanceMember(bs, mem.Member, span));
            default:
                return null;
        }
    }

    // The builtin TypeSymbol for a primitive type, used for extension lookup on string, int and so on.
    private TypeSymbol? BuiltinSymbol(PrimitiveType p) =>
        _comp.Builtins.LookupLocal(TypeFacts.Display(p)) as TypeSymbol;

    // Members on a type parameter T: only what its constraint interfaces provide.
    private (LyrType, Symbol?) MemberOfTypeParam(GenericParamSymbol gp, string member, Span span)
    {
        foreach (var c in gp.Constraints)
        {
            if (c is not NamedType nt) continue;
            if (InterfaceWithSubst(nt) is not { } resolved) continue;

            var (it, subst) = resolved;
            if (it.Members.LookupLocal(member) is not FunctionSymbol fn) continue;

            // Substitute the type arguments OF THE CONSTRAINT. 'T :: [Eq<T>]' means the 'T' in
            // 'Eq<T>.eq(other: T)' is the 'T' of the calling function — two different symbols with
            // the same name.
            //
            // Without the substitution the raw interface type comes back and 'a.eq(b)' fails with
            // "cannot assign 'T' to 'T'".
            return (Substitute(FnTypeOf(fn), subst), fn);
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

    // T to its argument across a type: Stack<T>.items of type T[] becomes int[] for Stack<int>.
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
            CoroutineOf co => new CoroutineOf(Substitute(co.Yield, map)),
            _ => type // primitive, NamedRef, error, null
        };
    }

    // --- generic call inference and constraint satisfaction ---

    // Resolves type parameters: the parameter carries T, the argument is concrete. The first binding
    // wins; contradictions such as pair(1, "x") surface later in CheckCallArgs as type errors.
    /// <summary>
    /// Binds type parameters by running the parameter type and the argument type against each other.
    /// </summary>
    /// <remarks>
    /// <para>The unification is STRUCTURAL: it compares shapes. That covers everything that nests
    /// like a tree — <c>T[]</c> against <c>int[]</c>, <c>fn(T) -&gt; bool</c> against
    /// <c>fn(int) -&gt; bool</c>, <c>Box&lt;T&gt;</c> against <c>Box&lt;int&gt;</c>.</para>
    /// <para>CONFORMANCE is not structural: between <c>RangeIterator</c> and
    /// <c>Iterator&lt;int&gt;</c> there is no similarity of shape but a declaration
    /// (<c>class RangeIterator :: [Iterator&lt;int&gt;]</c>). The last case below looks it up.</para>
    /// </remarks>
    private void UnifyInfer(LyrType param, LyrType arg, Dictionary<GenericParamSymbol, LyrType> map)
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
            case CoroutineOf pc when arg is CoroutineOf ac: UnifyInfer(pc.Yield, ac.Yield, map); break;

            // 'Iterator<T>' against 'RangeIterator': the parameter is an instance of an INTERFACE and
            // the argument a type satisfying it. Structurally the two have nothing in common; the
            // connection stands in the argument's declaration.
            //
            // Without this case 'T' stays unbound, and silently: the sema reports nothing and only
            // the lowering finds '<error>' as a type argument. Affected is every generic function
            // whose type parameter occurs in the interface parameter ONLY.
            //
            // The mapping is unambiguous, because a type cannot satisfy the same generic interface
            // twice with different arguments: the method would have two signatures, and LYR-SEM0042
            // rejects that.
            case GenericInstance { Definition.Kind: TypeSymbolKind.Interface } pi:
                UnifyThroughConformance(pi, arg, map);
                break;
        }
    }

    /// <summary>Looks up the conformance to <paramref name="wanted"/> in the argument type and
    /// unifies its type arguments against the written ones.</summary>
    private void UnifyThroughConformance(GenericInstance wanted, LyrType arg,
        Dictionary<GenericParamSymbol, LyrType> map)
    {
        if (TypeFacts.SymbolOf(arg) is not { } symbol) return;

        // If the argument is itself an instance ('ArrayIterator<string>'), its substitution has to go
        // on top: the DEFINITION's conformance list says 'Iterator<T>' with the class's type
        // parameter, not 'Iterator<string>'. Without this step a type parameter comes out instead of
        // a concrete type.
        var ofInstance = arg is GenericInstance gi ? SubstMap(gi) : EmptySubst;

        foreach (var (iface, subst) in InterfacesOf(symbol))
        {
            if (!ReferenceEquals(iface, wanted.Definition)) continue;

            // 'subst' maps the INTERFACE's generics onto what stood in the '::'. Walking both in the
            // same order, 'Iterator<T>' against '{T_iface -> int}' binds the calling function's T to
            // int.
            var n = Math.Min(iface.Generics.Length, wanted.Arguments.Length);
            for (var i = 0; i < n; i++)
                if (subst.TryGetValue(iface.Generics[i], out var bound))
                    UnifyInfer(wanted.Arguments[i], Substitute(bound, ofInstance), map);

            return;
        }
    }

    /// <summary>
    /// Is there an <see cref="ErrorType"/> anywhere inside this type?
    /// </summary>
    /// <remarks>
    /// <para><c>IsError</c> alone is not enough: a block lambda without a return type annotation has
    /// the type <c>fn(int) -&gt; &lt;error&gt;</c> — intact outside, broken inside. The cause is
    /// already reported as <c>LYR-SEM0046</c>, and a second line about a type argument that cannot be
    /// inferred would bury it.</para>
    /// </remarks>
    private static bool ContainsError(LyrType type) => type switch
    {
        ErrorType => true,
        Optional o => ContainsError(o.Inner),
        ArrayOf a => ContainsError(a.Element),
        TupleOf t => t.Elements.Any(ContainsError),
        FnType f => f.Parameters.Any(ContainsError) || ContainsError(f.Return),
        GenericInstance g => g.Arguments.Any(ContainsError),
        RangeOf r => ContainsError(r.Element),
        CoroutineOf c => ContainsError(c.Yield),
        _ => false,
    };

    private void CheckConstraints(GenericParamSymbol[] generics, LyrType[] args, Span span)
    {
        var n = Math.Min(generics.Length, args.Length);

        // The FULL mapping rather than one parameter at a time: a constraint may name the other type
        // parameters (`<K, V :: [Map<K, V>]>`), and without the other bindings `Map<K, V>` could not
        // resolve to `Map<string, int>`.
        var map = new Dictionary<GenericParamSymbol, LyrType>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < n; i++) map[generics[i]] = args[i];

        for (var i = 0; i < n; i++) CheckSatisfies(generics[i], args[i], map, span);
    }

    private void CheckInferredConstraints(GenericParamSymbol[] generics, Dictionary<GenericParamSymbol, LyrType> map, Span span)
    {
        foreach (var g in generics)
            if (map.TryGetValue(g, out var arg)) CheckSatisfies(g, arg, map, span);
    }

    /// <summary>
    /// Does <paramref name="arg"/> satisfy the constraints of <paramref name="param"/>?
    /// </summary>
    /// <param name="substitution">The bindings of all type parameters at this call site. A constraint
    /// carries its own type argument (`&lt;T :: [Eq&lt;T&gt;]&gt;`), and `Eq&lt;T&gt;` only becomes the
    /// question actually asked once `T := int`.</param>
    private void CheckSatisfies(GenericParamSymbol param, LyrType arg,
        Dictionary<GenericParamSymbol, LyrType> substitution, Span span)
    {
        foreach (var c in param.Constraints)
        {
            if (ConstraintInterface(c) is not { } iface) continue;

            // The constraint as a TYPE rather than only as a symbol: 'Src<string>' and 'Src<int>' are
            // the same symbol and different requirements.
            var wanted = Substitute(
                ResolveType(c, _currentModule?.Members ?? _comp.Builtins), substitution);

            if (Satisfies(arg, iface, wanted)) continue;

            _de.Report("LYR-SEM0028", Severity.Error, span,
                $"type '{TypeFacts.Display(arg)}' does not satisfy constraint "
                + $"'{TypeFacts.Display(wanted)}' on '{param.Name}'");
        }
    }

    private TypeSymbol? ConstraintInterface(TypeNode c) => Conformance.InterfaceOf(c, _binding);

    // Does arg satisfy the constraint iface? User types through their declared interface list, a type
    // parameter through its own constraints, and a BUILTIN through a visible 'extend int :: [I]' —
    // the same question as for every other type, answered by the same function.
    /// <param name="wanted">The interface WITH ITS TYPE ARGUMENTS, such as <c>Src&lt;string&gt;</c>.
    /// <c>iface</c> alone would be the symbol <c>Src</c>, under which <c>Ones :: [Src&lt;int&gt;]</c>
    /// would satisfy a <c>Src&lt;string&gt;</c> too.</param>
    private bool Satisfies(LyrType arg, TypeSymbol iface, LyrType wanted) => arg switch
    {
        NamedRef nr => ImplementsWithExtensions(nr.Symbol, iface, wanted, EmptySubst),

        // For an instance its own arguments count: 'class Box<T> :: [Src<T>]' satisfies exactly
        // 'Src<int>' for 'Box<int>'.
        GenericInstance gi => ImplementsWithExtensions(gi.Definition, iface, wanted, SubstMap(gi)),

        TypeParamType tp => tp.Param.Constraints.Any(c =>
            ReferenceEquals(ConstraintInterface(c), iface)
            && Matches(ResolveType(c, _currentModule?.Members ?? _comp.Builtins), EmptySubst, wanted)),

        PrimitiveType prim when BuiltinSymbol(prim) is { } builtin =>
            ImplementsWithExtensions(builtin, iface, wanted, EmptySubst),
        _ => true // external or error: pass through opaquely
    };

    // Conformance through the declared interfaces OR a visible `extend T :: [I]` block.
    private bool ImplementsWithExtensions(TypeSymbol ts, TypeSymbol iface, LyrType wanted,
        Dictionary<GenericParamSymbol, LyrType> ofInstance)
    {
        foreach (var node in DeclaredInterfaceNodes(ts))
            if (ReferenceEquals(Conformance.InterfaceOf(node, _binding), iface)
                && Matches(ResolveType(node, DeclarationScope(ts)), ofInstance, wanted))
                return true;

        foreach (var block in _comp.Extensions.Blocks)
        {
            if (!ReferenceEquals(block.Target, ts)) continue;
            if (_currentModule is not null && !_comp.Sees(_currentModule, block.Module)) continue;
            foreach (var node in block.Decl.Interfaces)
                if (ReferenceEquals(Conformance.InterfaceOf(node, _binding), iface)
                    && Matches(ResolveType(node, DeclarationScope(ts)), ofInstance, wanted))
                    return true;
        }
        return false;
    }

    /// <summary>
    /// Does a declared conformance match what the constraint demands?
    ///
    /// <para>An interface WITHOUT type arguments compares through the symbol — there is nothing to
    /// distinguish there, and <c>ResolveType</c> yields a <c>NamedRef</c>. Only for a generic instance
    /// do the arguments count.</para>
    /// </summary>
    private static bool Matches(LyrType declared, Dictionary<GenericParamSymbol, LyrType> subst,
        LyrType wanted)
    {
        var resolved = Substitute(declared, subst);
        if (resolved is not GenericInstance && wanted is not GenericInstance) return true;
        return LyrType.Equal(resolved, wanted);
    }

    /// <summary>The scope in which a type's conformance list stands; its own type parameters are
    /// visible there (<c>class Box&lt;T&gt; :: [Src&lt;T&gt;]</c>).</summary>
    private SymbolTable DeclarationScope(TypeSymbol ts) => ts.Members;

    private LyrType BindMember(MemberExpr mem, (LyrType type, Symbol? sym) r)
    {
        if (r.sym is not null) _result.BindRef(mem, r.sym);
        return r.type;
    }

    /// <summary>
    /// The symbol a callee expression was bound to, through an import binding to what it imports.
    ///
    /// <para>A question about the table and nothing else. It does NOT answer what kind of thing the
    /// expression is — <c>CheckMember</c> reads that off the type, because the table cannot tell a
    /// type NAME from an expression that builds a value of that type.</para>
    /// </summary>
    private Symbol? TargetSymbol(Expr target)
    {
        var sym = _result.RefOf(target);
        return sym is ImportBindingSymbol ib ? ib.Target : sym;
    }

    // Member resolution: own members, then visible extensions, then interface default methods.
    private (LyrType, Symbol?) InstanceMember(TypeSymbol ts, string member, Span span)
    {
        if (ts.Members.LookupLocal(member) is { } own)
            return own switch
            {
                FieldSymbol fs => (FieldType(fs), fs),

                // A static member belongs to the type, not to the instance.
                FunctionSymbol { IsStatic: true } fn => (Report(span, "LYR-SEM0055",
                    $"'{fn.Name}' is static — call it on the type: '{ts.Name}.{member}(…)'"), fn),

                FunctionSymbol fn => (FnTypeOf(fn), fn),

                GlobalSymbol g => (Report(span, "LYR-SEM0055",
                    $"'{member}' is a static constant — read it from the type: '{ts.Name}.{member}'"), g),

                _ => (Report(span, "LYR-SEM0012", $"'{ts.Name}' has no member '{member}'"), null)
            };
        if (ExtensionMember(ts, member, span) is { } ext) return (FnTypeOf(ext), ext);
        if (DefaultMember(ts, member, span) is { } def) return def;
        return (Report(span, "LYR-SEM0012", $"'{ts.Name}' has no member '{member}'"), null);
    }

    // A visible extension method of this name, meaning the declaring module is the current one or is
    // imported. Several visible candidates give SEM0044 for ambiguity, and the first one wins.
    private FunctionSymbol? ExtensionMember(TypeSymbol ts, string member, Span span)
    {
        FunctionSymbol? found = null;
        var ambiguous = false;
        foreach (var ext in _comp.Extensions.MethodsFor(ts))
        {
            if (ext.Symbol.Name != member) continue;
            if (_currentModule is not null && !_comp.Sees(_currentModule, ext.Module)) continue;
            if (found is null) found = ext.Symbol;
            else if (!ReferenceEquals(found, ext.Symbol)) ambiguous = true;
        }
        if (ambiguous)
            _de.Report("LYR-SEM0044", Severity.Error, span,
                $"ambiguous member '{member}' on '{ts.Name}': provided by more than one visible extension");
        return found;
    }

    // An interface default method, one with a body, through the type's interfaces, declared or via
    // extend. Two defaults from different interfaces give SEM0043, which asks for an explicit
    // override.
    private (LyrType, Symbol?)? DefaultMember(TypeSymbol ts, string member, Span span)
    {
        (LyrType type, Symbol sym)? found = null;
        var ambiguous = false;
        foreach (var (iface, subst) in InterfacesOf(ts))
        {
            if (iface.Members.LookupLocal(member) is not FunctionSymbol fn) continue;
            if (fn.Declaration is not FunctionDecl { Body: not null }) continue; // defaults only, not abstract ones
            var t = Substitute(FnTypeOf(fn), subst);
            if (found is null) found = (t, fn);
            else if (!ReferenceEquals(found.Value.sym, fn)) ambiguous = true;
        }
        if (ambiguous)
            _de.Report("LYR-SEM0043", Severity.Error, span,
                $"ambiguous default method '{member}' on '{ts.Name}': multiple interfaces provide it — override it explicitly");
        return found is { } f ? (f.type, f.sym) : null;
    }

    // All interfaces of a type with their substitution, mapping the interface generics to the type
    // arguments from the '::'. Declared interfaces plus those from visible `extend T :: [I]` blocks.
    private IEnumerable<(TypeSymbol iface, Dictionary<GenericParamSymbol, LyrType> subst)> InterfacesOf(TypeSymbol ts)
    {
        foreach (var node in DeclaredInterfaceNodes(ts))
            if (InterfaceWithSubst(node) is { } r) yield return r;
        foreach (var block in _comp.Extensions.Blocks)
        {
            if (!ReferenceEquals(block.Target, ts)) continue;
            if (_currentModule is not null && !_comp.Sees(_currentModule, block.Module)) continue;
            foreach (var node in block.Decl.Interfaces)
                if (InterfaceWithSubst(node) is { } r) yield return r;
        }
    }

    private (TypeSymbol, Dictionary<GenericParamSymbol, LyrType>)? InterfaceWithSubst(TypeNode node)
    {
        if (Conformance.InterfaceOf(node, _binding) is not { } iface) return null;
        var resolved = ResolveType(node, _currentModule?.Members ?? _comp.Builtins);
        var subst = resolved is GenericInstance gi ? SubstMap(gi) : EmptySubst;
        return (iface, subst);
    }

    private static TypeNode[] DeclaredInterfaceNodes(TypeSymbol ts) => ts.Declaration switch
    {
        StructDecl s => s.Interfaces,
        ClassDecl c => c.Interfaces,
        EnumDecl e => e.Interfaces,
        _ => []
    };

    /// <param name="instance">The instance from a type path with arguments
    /// (<c>Pair&lt;int&gt;.of(3)</c>). When present, the member's type is substituted through it;
    /// otherwise <c>T</c> stands in the result and the error arrives as "cannot assign 'int' to 'T'"
    /// one level too late.</param>
    private (LyrType, Symbol?) MemberOfType(TypeSymbol ts, string member, Span span,
        GenericInstance? instance = null)
    {
        // A generic type WITHOUT arguments in value position: a type path requires them explicitly,
        // as there is no field inference. Saying so here rather than letting the substitution
        // silently not happen is the difference between a message about the cause and one about its
        // consequence.
        if (instance is null && ts.Generics.Length > 0
            && ts.Members.LookupLocal(member) is FunctionSymbol or EnumVariantSymbol or GlobalSymbol)
            return (Report(span, "LYR-SEM0063",
                $"'{ts.Name}' is generic — write its type arguments: "
                + $"'{ts.Name}<{string.Join(", ", ts.Generics.Select(g => g.Name))}>.{member}'"),
                null);

        var subst = instance is null ? EmptySubst : SubstMap(instance);
        LyrType Of(LyrType t) => instance is null ? t : Substitute(t, subst);

        return ts.Members.LookupLocal(member) switch
        {
            // Without 'static' the method needs a receiver; otherwise the lowering would produce a
            // field access without an object.
            FunctionSymbol { IsStatic: false } fn => (Report(span, "LYR-SEM0055",
                $"'{fn.Name}' is an instance method and needs a receiver — " +
                $"call it on a value, or declare it 'static fn {member}(…)'"), fn),

            FunctionSymbol fn => (Of(FnTypeOf(fn)), fn),             // a static fn, such as the factory Point.new
            GlobalSymbol g => (Of(TypeOfGlobalReference(g, span)), g), // static let
            EnumVariantSymbol ev => (Of(VariantConstructorType(ev, ts, span, instance)), ev),

            FieldSymbol => (Report(span, "LYR-SEM0055",
                $"'{member}' is a field of '{ts.Name}' and belongs to an instance, not to the type"), null),

            // The same fallback the instance path has: an extension block may add a static member,
            // and the lowering already emits one without a receiver.
            _ => ExtensionMember(ts, member, span) switch
            {
                { IsStatic: true } ext => (Of(FnTypeOf(ext)), ext),

                { } ext => (Report(span, "LYR-SEM0055",
                    $"'{ext.Name}' is an instance method and needs a receiver — " +
                    $"call it on a value, or declare it 'static fn {member}(…)'"), ext),

                null => (Report(span, "LYR-SEM0012",
                    $"'{ts.Name}' has no static member '{member}'"), null),
            }
        };
    }

    private (LyrType, Symbol?) MemberOfModule(ModuleSymbol mod, string member, Span span) =>
        mod.Members.LookupLocal(member) switch
        {
            FunctionSymbol fn => (FnTypeOf(fn), fn),
            GlobalSymbol g => (TypeOfGlobalReference(g, span), g),
            ExternalSymbol ex => (LyrType.Error, ex),
            TypeSymbol tsym => (LyrType.Error, tsym), // a type as a value has no expression type
            _ => (Report(span, "LYR-SEM0012", $"module '{mod.FullName}' has no member '{member}'"), null)
        };

    /// <param name="instance">The instance in <c>Opt&lt;int&gt;.Some(5)</c>. It is the RESULT TYPE of
    /// the construction, which the substitution cannot supply, because what stands here is the bare
    /// enum symbol rather than a type parameter.</param>
    private LyrType VariantConstructorType(EnumVariantSymbol ev, TypeSymbol enumTs, Span span,
        GenericInstance? instance = null)
    {
        var v = (EnumVariant)ev.Declaration!;
        LyrType result = instance is not null ? instance : new NamedRef(enumTs);

        if (v.TupleFields is not null)
            return new FnType(v.TupleFields.Select(t => ResolveType(t, enumTs.Members)).ToArray(), result);
        if (v.StructFields is not null)
            return Report(span, "LYR-SEM0031", $"struct variant '{ev.Name}' must be constructed with '{ev.Name} {{ … }}'");
        return result; // a unit variant as a value
    }

    private LyrType FieldType(FieldSymbol fs) => ResolveType(((FieldDecl)fs.Declaration!).Type, _comp.Builtins);

    private LyrType CheckStructInit(StructInitExpr si, SymbolTable scope, LyrType? expected)
    {
        var (sym, owner) = ResolveInitPath(si.Path, scope);

        // An enum struct variant: qualified (Shape.Triangle { … }) or contextual (Triangle { … } in a
        // position with an expected enum type).
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

        // The initializer names its type, and until now that name was resolved and dropped — the
        // enum variant case records its symbol, this one did not, so nothing knew that
        // 'Point { … }' refers to Point. Safe to record only now that no consumer reads this table
        // to decide what KIND of receiver an expression is.
        _result.BindRef(si, ts);

        // A HOST type cannot be constructed. It has no layout this module knows: the host creates it
        // and the script passes it on. Without this diagnostic it is a compiler crash — the lowering
        // allocates an object after the empty class declaration while the variable carries the host
        // type, and the verifier reports 'cannot compare IrHostType with IrRefType'.
        if (Ir.Lowering.HostTypes.NameOf(ts, _comp) is { } hostName)
        {
            _de.Report("LYR-SEM0061", Severity.Error, si.Span,
                $"'{hostName}' is a host type — only the host can create one; a script receives "
                + "it and passes it on");
            foreach (var f in si.Fields) CheckExpr(f.Value, scope);
            return LyrType.Error;
        }

        // A generic type takes its type arguments from what is WRITTEN, else from the context. There
        // is still no inference from the field values: 'P { v = 1 }' with no context anywhere is an
        // error, not a guess from the '1'.
        LyrType result;
        Dictionary<GenericParamSymbol, LyrType> subst;
        if (ts.Generics.Length > 0 || si.TypeArguments.Length > 0)
        {
            // Written arguments beat the context, as they do for an enum variant: 'Box<int> { … }'
            // says itself which instance is meant, and that holds where there is no context at all.
            LyrType[] args;
            if (si.TypeArguments.Length > 0)
                args = si.TypeArguments.Select(a => ResolveType(a, scope)).ToArray();
            else if (InstanceFromExpected(expected, ts) is { } fromContext)
                args = fromContext.Arguments;
            else
                args = [];

            if (args.Length != ts.Generics.Length)
                _de.Report("LYR-SEM0026", Severity.Error, si.Span,
                    $"generic type '{ts.Name}' expects {ts.Generics.Length} type argument(s), got "
                    + $"{args.Length} — write them ('{ts.Name}<…> {{ … }}') or use it where the type "
                    + "is known");
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

    // Path resolution for a struct initializer: runs through modules AND type members, meaning enum
    // variants. Yields the final symbol plus, for an enum variant, the surrounding enum type.
    /// <summary>
    /// <c>Pair&lt;int&gt;</c> as the target of a member access.
    ///
    /// <para>The result is a <see cref="NonValueType"/>, as for an ordinary type name: a type is not
    /// a value, and writing it alone gives <c>LYR-SEM0052</c>. It carries the resolved instance
    /// along.</para>
    /// </summary>
    private LyrType CheckTypePath(TypePathExpr tp, SymbolTable scope)
    {
        var (sym, _) = ResolveInitPath(tp.Path, scope);
        if (sym is not TypeSymbol ts)
        {
            foreach (var a in tp.TypeArguments) ResolveType(a, scope);
            return Report(tp.Span, "LYR-SEM0011", $"unknown type '{string.Join('.', tp.Path)}'");
        }

        _result.BindRef(tp, ts);

        var args = tp.TypeArguments.Select(a => ResolveType(a, scope)).ToArray();
        if (args.Length != ts.Generics.Length)
        {
            _de.Report("LYR-SEM0026", Severity.Error, tp.Span,
                $"generic type '{ts.Name}' expects {ts.Generics.Length} type argument(s), "
                + $"got {args.Length}");
            return new NonValueType(ts, "type");
        }

        CheckConstraints(ts.Generics, args, tp.Span);
        return new NonValueType(ts, "type", new GenericInstance(ts, args));
    }

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

    // The expected type (Shape, ?Shape, Opt<int>) yields the enum definition plus its instance.
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

    /// <summary>
    /// The instance the context asks for, when it is an instance of <paramref name="owner"/>.
    ///
    /// <para>The counterpart of <see cref="ExpectedInstance"/> for a struct or class initializer.
    /// The definition must MATCH: an expectation of <c>Box&lt;int&gt;</c> says nothing about a
    /// <c>Pair { … }</c>, and taking its arguments anyway would silently build the wrong instance.
    /// </para>
    /// </summary>
    private static GenericInstance? InstanceFromExpected(LyrType? expected, TypeSymbol owner)
    {
        var t = expected is Optional o ? o.Inner : expected;
        return t is GenericInstance gi && ReferenceEquals(gi.Definition, owner) ? gi : null;
    }

    private LyrType CheckVariantInit(StructInitExpr si, EnumVariantSymbol ev, TypeSymbol enumTs,
        GenericInstance? instance, SymbolTable scope)
    {
        _result.BindRef(si, ev);
        var v = (EnumVariant)ev.Declaration!;

        // WRITTEN arguments beat the context: 'Ev<int>.Hit { … }' says itself which instance is
        // meant, and that holds where there is no context at all ('let e = …').
        if (si.TypeArguments.Length > 0 && enumTs.Generics.Length > 0)
        {
            var written = si.TypeArguments.Select(a => ResolveType(a, scope)).ToArray();
            if (written.Length != enumTs.Generics.Length)
                _de.Report("LYR-SEM0026", Severity.Error, si.Span,
                    $"generic enum '{enumTs.Name}' expects {enumTs.Generics.Length} type "
                    + $"argument(s), got {written.Length}");
            else
            {
                CheckConstraints(enumTs.Generics, written, si.Span);
                instance = new GenericInstance(enumTs, written);
            }
        }

        var subst = instance is not null ? SubstMap(instance) : EmptySubst;

        LyrType result;
        if (instance is not null) result = instance;
        else if (enumTs.Generics.Length > 0 || si.TypeArguments.Length > 0)
        {
            // A generic enum without a context instance, or with type arguments on the variant.
            _de.Report("LYR-SEM0026", Severity.Error, si.Span,
                $"generic enum '{enumTs.Name}' expects {enumTs.Generics.Length} type argument(s) "
                + "— write them ('Enum<int>.Variant { … }') or use it where the type is known");
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

    /// <summary>
    /// An <c>if</c> as an expression, with flow narrowing in both branches, like the statement form.
    /// </summary>
    /// <remarks>
    /// <para>Without the narrowing, <c>if (a == null) 0 else a</c> would be a type error
    /// (<c>LYR-SEM0001</c>) although the statement form <c>if (a == null) { return 0; } return a;</c>
    /// works next to it, for the same proof about the same value.</para>
    /// </remarks>
    private LyrType CheckIfExpr(IfExpr iff, SymbolTable scope)
    {
        CheckCondition(iff.Condition, scope);

        var (thenFacts, elseFacts) = NarrowingFacts(iff.Condition);
        var snapshot = new Dictionary<Symbol, LyrType>(_narrowed, ReferenceEqualityComparer.Instance);

        Apply(thenFacts);
        var thenT = CheckExpr(iff.Then, scope);

        // Back to the state BEFORE the then branch: what held there is precisely what does not hold
        // in the else branch.
        _narrowed = new Dictionary<Symbol, LyrType>(snapshot, ReferenceEqualityComparer.Instance);
        Apply(elseFacts);
        var elseT = CheckExpr(iff.Else, scope);

        _narrowed = snapshot;
        return Unify(iff.Then, thenT, iff.Else, elseT, iff.Span);
    }

    /// <summary>
    /// A <c>null</c> branch makes the other one optional: <c>if (c) 5 else null</c> is <c>?int</c>.
    /// Yields <c>null</c> when neither side is the <c>null</c> literal.
    ///
    /// <para>The case does not go through <c>IsAssignable</c> in EITHER direction: <c>null</c> is no
    /// <c>int</c>, and <c>int</c> is no <c>null</c>. The widening rule <c>T</c> to <c>?T</c> applies
    /// only where a target type already stands — an assignment, a parameter, a return. In an arm
    /// unification there is none; the result type arises here.</para>
    ///
    /// <para>One function rather than two, because the <c>if</c> expression and the <c>match</c>
    /// expression have separate unifications.</para>
    /// </summary>
    private static LyrType? WidenAgainstNull(LyrType a, LyrType b)
    {
        if (a is NullType && b is not NullType) return b is Optional ? b : new Optional(b);
        if (b is NullType && a is not NullType) return a is Optional ? a : new Optional(a);
        return null;   // the null/null case too: there the `Equal` above already decided
    }

    private LyrType Unify(Expr ae, LyrType a, Expr be, LyrType b, Span span)
    {
        if (LyrType.Equal(a, b)) return a;
        if (a.IsError) return b;
        if (b.IsError) return a;

        // A `null` branch makes the other one optional: `if (c) 5 else null` is `?int`.
        //
        // The case does not go through `IsAssignable` in either direction: `null` is not `int`, and
        // `int` is not `null`. The widening rule `T` -> `?T` applies only where a target type already
        // stands — an assignment, a parameter, a return. In an arm unification there is none.
        //
        // `?T` against `T` needs no line of its own: `IsAssignable` carries the widening there,
        // because one of the two sides is the finished target type.
        if (WidenAgainstNull(a, b) is { } widened) return widened;

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
                    // Blocks have no value: in a match EXPRESSION a block arm has to leave the
                    // function on every path, and it contributes nothing to the unification.
                    if (asExpression && !Flow.AlwaysReturns(b, _result))
                        _de.Report("LYR-SEM0033", Severity.Error, arm.Span,
                            "a block arm of a match expression must return or throw on every path (blocks have no value)");
                    break;
                case Expr e:
                    bodies.Add(CheckExpr(e, armScope));
                    break;
            }
        }
        // Faulty patterns would only produce follow-up noise, so exhaustiveness is skipped then.
        if (patternsClean && !st.IsError)
            CheckExhaustiveness(match, st, arms);
        return bodies;
    }

    // --- exhaustiveness: enum variants, bool and ?T are enumerated, while open types (int, string,
    // --- …) need a '_' or binding arm. Guards do not count. ---

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
                return ["_"]; // an open type is coverable only by a default
        }
    }

    // Which variant does this pattern cover completely, with an irrefutable payload?
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
                if (fp.Pattern is null) continue; // the short form only binds, so it is irrefutable
                var fd = Array.Find(variant.StructFields, f => f.Name == fp.Name);
                if (fd is null || !IsIrrefutable(fp.Pattern, Substitute(ResolveType(fd.Type, enumTs.Members), subst)))
                    return false;
            }
            return true;
        }
        return variant.TupleFields is null && variant.StructFields is null; // a qualified unit variant
    }

    // Does this pattern match EVERY value of the type?
    private bool IsIrrefutable(Pattern p, LyrType type)
    {
        switch (p)
        {
            case WildcardPattern:
                return true;
            case OrPattern o:
                return o.Alternatives.Any(a => IsIrrefutable(a, type));
            case BindingPattern b:
                if (type is Optional) return false; // binds the inner part and does not cover null
                return EnumDefOf(type) is not { } e || VariantOf(e, b.Name) is null; // a variant name is a test
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
                return false; // a literal or a range
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
            if (WidenAgainstNull(result, bodies[i]) is { } widened) { result = widened; continue; }
            _de.Report("LYR-SEM0016", Severity.Error, span, $"match arms have incompatible types: '{TypeFacts.Display(result)}' vs '{TypeFacts.Display(bodies[i])}'");
            break;
        }
        return result;
    }

    // Pattern bindings: every form types its bindings against the scrutinee type. Non-null patterns
    // on a ?T match against T ('null => …, u => u.name'), and errors poison their sub-bindings, so arm
    // bodies do not cascade.
    /// <summary>
    /// <c>let (a, b) = pair;</c>
    ///
    /// <para>The initializer has to yield a tuple of matching arity; the names are bound by the same
    /// routine a <c>match</c> arm uses. Two ways to bind names from a pattern would be two
    /// opportunities for different answers.</para>
    /// </summary>
    private void CheckDestructuring(DestructuringStmt stmt, SymbolTable scope)
    {
        var declared = stmt.Type is null ? null : ResolveType(stmt.Type, scope);
        var actual = CheckExpr(stmt.Initializer, scope, declared);

        if (declared is not null) CheckAssignable(stmt.Initializer, actual, declared, stmt.Span);
        var source = declared ?? actual;

        if (source.IsError) return; // already reported

        if (source is not TupleOf tuple)
        {
            Report(stmt.Initializer.Span, "LYR-SEM0058",
                $"cannot destructure '{TypeFacts.Display(source)}' — only tuples can be taken apart");
            return;
        }

        if (tuple.Elements.Length != stmt.Pattern.Elements.Length)
        {
            Report(stmt.Pattern.Span, "LYR-SEM0058",
                $"the pattern binds {stmt.Pattern.Elements.Length} name(s), but "
                + $"'{TypeFacts.Display(tuple)}' has {tuple.Elements.Length} element(s)");
            return;
        }

        BindPattern(stmt.Pattern, tuple, scope, stmt.IsMutable);
    }

    private void BindPattern(Pattern pattern, LyrType scrutinee, SymbolTable scope,
        bool mutable = false)
    {
        if (scrutinee.IsError) { BindPoison(pattern, scope); return; }
        if (scrutinee is Optional opt && pattern is not (WildcardPattern or OrPattern) && !IsNullPattern(pattern))
        {
            BindPattern(pattern, opt.Inner, scope, mutable);
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
                    _result.BindRef(b, bev); // a unit variant test, not a binding
                    return;
                }
                var local = new LocalSymbol(b.Name, scrutinee, mutable, b);
                scope.TryDeclare(local);
                _result.BindRef(b, local); // for definite-assignment analysis
                return;

            case TuplePattern t:
                if (scrutinee is TupleOf tup && tup.Elements.Length == t.Elements.Length)
                {
                    for (var i = 0; i < t.Elements.Length; i++)
                        BindPattern(t.Elements[i], tup.Elements[i], scope, mutable);
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
            // wildcard or error: no binding
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
            // A qualified path (Shape.Circle) has to point at EXACTLY this enum.
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

        // struct and class destructuring: Point { x, y }
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

        // A qualified unit variant (Shape.Empty) must have no payload.
        if (variant.TupleFields is not null || variant.StructFields is not null)
            Report(v.Span, "LYR-SEM0031", $"variant '{ev.Name}' carries a payload — destructure it");
    }

    private void BindFieldPattern(FieldPattern fp, LyrType type, SymbolTable scope)
    {
        if (fp.Pattern is not null) { BindPattern(fp.Pattern, type, scope); return; }
        var local = new LocalSymbol(fp.Name, type, false, fp); // short form: the field name binds
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

    // Or-pattern: every alternative binds into a table of its own, and all of them have to bind the
    // same names with the same types. The bindings of the FIRST alternative become the arm scope,
    // consistently with the definite-assignment analysis, which uses alternative 0.
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

    // --- pattern helpers ---

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

    // Resolves the first count segments of a pattern path through the scope: modules and imports.
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

    // resume co: yields the value of the next yield.
    private LyrType CheckResume(ResumeExpr re, SymbolTable scope)
    {
        var t = CheckExpr(re.Coroutine, scope);
        return t switch
        {
            CoroutineOf co => co.Yield,
            ErrorType => LyrType.Error,
            _ => Report(re.Span, "LYR-SEM0040", $"'resume' needs a Coroutine<T>, got '{TypeFacts.Display(t)}'")
        };
    }

    // A lambda with bidirectional inference: unannotated parameters take the context FnType, and the
    // return context — an annotation before the context — types the body. Block lambdas yield values
    // through 'return' only, so the return type has to come from an annotation or the context, and a
    // non-void one requires return coverage.
    private LyrType CheckLambda(LambdaExpr lam, SymbolTable scope, LyrType? expected = null)
    {
        var expFn = expected is FnType ef && ef.Parameters.Length == lam.Parameters.Length ? ef : null;

        var savedYield = _currentYield;
        var savedReturn = _currentReturn;
        _currentYield = null; // a lambda is no coroutine, so a yield inside it is an error

        var lambdaScope = new SymbolTable(scope);
        var pTypes = new LyrType[lam.Parameters.Length];
        for (var i = 0; i < lam.Parameters.Length; i++)
        {
            var p = lam.Parameters[i];
            LyrType pt;
            if (p.Type is not null) pt = ResolveType(p.Type, scope);
            else if (expFn is not null) pt = expFn.Parameters[i];
            else pt = Report(p.Span, "LYR-SEM0045",
                $"lambda parameter '{p.Name}' needs a type annotation (no context type available)");
            var ps = new ParameterSymbol(p.Name, pt, p);
            lambdaScope.TryDeclare(ps);
            _result.BindRef(p, ps); // for definite-assignment analysis: lambda parameters are assigned
            pTypes[i] = pt;
        }

        var contextRet = lam.ReturnType is not null ? ResolveType(lam.ReturnType, scope) : expFn?.Return;
        // A context return with type parameters still UNBOUND (map(xs, (x) => …), where U is open) is
        // not checked against: the body's actual type binds U in phase C.
        var openGeneric = lam.ReturnType is null && contextRet is not null && ContainsTypeParam(contextRet);
        LyrType ret;
        switch (lam.Body)
        {
            case Expr e:
                var bt = CheckExpr(e, lambdaScope, openGeneric ? null : contextRet);
                if (contextRet is null || openGeneric) ret = bt;
                else
                {
                    ret = contextRet;
                    if (!TypeFacts.IsVoid(contextRet)) // a void context discards the value: () => doStuff()
                        CheckAssignable(e, bt, contextRet, e.Span);
                }
                break;
            case Block b:
                if (contextRet is null && !openGeneric && !HasValueReturn(b))
                {
                    // A block lambda without context that returns no value becomes void, so
                    // side-effect closures (`() => { doStuff(); }`) need no `: void`.
                    ret = LyrType.Void;
                    _currentReturn = LyrType.Void;
                    CheckBlock(b, lambdaScope);
                }
                else if (contextRet is null || openGeneric)
                {
                    ret = Report(lam.Span, "LYR-SEM0046", openGeneric
                        ? "cannot infer a generic return type for a block lambda — add a return type annotation"
                        : "a block lambda that returns a value needs a return type annotation or a context type");
                    _currentReturn = LyrType.Error; // do not let returns in the body cascade
                    CheckBlock(b, lambdaScope);
                }
                else
                {
                    ret = contextRet;
                    _currentReturn = contextRet; // 'return' belongs to the LAMBDA, not to the enclosing function
                    CheckBlock(b, lambdaScope);
                    if (!TypeFacts.IsVoid(contextRet) && !contextRet.IsError && !Flow.AlwaysReturns(b, _result))
                        _de.Report("LYR-SEM0046", Severity.Error, lam.Span,
                            "a non-void block lambda must return or throw on every path");
                }
                break;
            default:
                ret = LyrType.Error;
                break;
        }

        _currentReturn = savedReturn;
        _currentYield = savedYield;

        RecordCaptures(lam);
        return new FnType(pTypes, ret);
    }

    // Does the block return a VALUE on any path (`return expr;`)? Descends through the statement
    // structure but NOT into nested lambdas, whose returns belong to them. For the void default:
    // only valueless block lambdas without context may become void.
    private static bool HasValueReturn(Stmt s) => s switch
    {
        ReturnStmt r => r.Value is not null,
        Block b => b.Statements.Any(HasValueReturn),
        IfStmt f => HasValueReturn(f.Then) || (f.Else is not null && HasValueReturn(f.Else)),
        WhileStmt w => HasValueReturn(w.Body),
        DoWhileStmt d => HasValueReturn(d.Body),
        ForInStmt fo => HasValueReturn(fo.Body),
        DeferStmt de => HasValueReturn(de.Body),
        TryStmt t => HasValueReturn(t.Body) || t.Catches.Any(c => HasValueReturn(c.Body)),
        MatchStmt m => m.Arms.Any(a => a.Body is Block ab && HasValueReturn(ab)),
        _ => false
    };

    private static bool ContainsTypeParam(LyrType t) => t switch
    {
        TypeParamType => true,
        Optional o => ContainsTypeParam(o.Inner),
        ArrayOf a => ContainsTypeParam(a.Element),
        TupleOf tu => tu.Elements.Any(ContainsTypeParam),
        FnType f => ContainsTypeParam(f.Return) || f.Parameters.Any(ContainsTypeParam),
        GenericInstance gi => gi.Arguments.Any(ContainsTypeParam),
        RangeOf r => ContainsTypeParam(r.Element),
        CoroutineOf c => ContainsTypeParam(c.Yield),
        _ => false
    };

    // Implicit captures: every referenced local and parameter whose declaration lies OUTSIDE the
    // lambda, plus the use of 'this'. A side table for closure lifting.
    private void RecordCaptures(LambdaExpr lam)
    {
        var captured = new List<Symbol>();
        var seen = new HashSet<Symbol>(ReferenceEqualityComparer.Instance);
        var capturesThis = false;

        void WalkNode(Node? node)
        {
            switch (node)
            {
                case null: return;
                case ThisExpr: capturesThis = true; return;
                case IdentifierExpr id:
                    if (_result.RefOf(id) is { } sym && sym is LocalSymbol or ParameterSymbol
                        && !DeclaredInside(sym) && seen.Add(sym))
                        captured.Add(sym);
                    return;
                case LambdaExpr inner: // nested: the span test sorts out inner declarations
                    WalkNode(inner.Body);
                    return;
                case Block b: foreach (var s in b.Statements) WalkNode(s); return;
                case BindingStmt bd: WalkNode(bd.Initializer); return;
                case IfStmt f: WalkNode(f.Condition); WalkNode(f.Then); WalkNode(f.Else); return;
                case WhileStmt w: WalkNode(w.Condition); WalkNode(w.Body); return;
                case DoWhileStmt d: WalkNode(d.Body); WalkNode(d.Condition); return;
                case ForInStmt fo: WalkNode(fo.Iterable); WalkNode(fo.Body); return;
                case ReturnStmt r: WalkNode(r.Value); return;
                case YieldStmt y: WalkNode(y.Value); return;
                case ThrowStmt t: WalkNode(t.Value); return;
                case DeferStmt de: WalkNode(de.Body); return;
                case ExprStmt es: WalkNode(es.Expr); return;
                case TryStmt tr:
                    WalkNode(tr.Body);
                    foreach (var c in tr.Catches) WalkNode(c.Body);
                    return;
                case MatchStmt m:
                    WalkNode(m.Scrutinee);
                    foreach (var arm in m.Arms) { WalkNode(arm.Guard); WalkNode(arm.Body); }
                    return;
                case MatchExpr ma:
                    WalkNode(ma.Scrutinee);
                    foreach (var arm in ma.Arms) { WalkNode(arm.Guard); WalkNode(arm.Body); }
                    return;
                case UnaryExpr u: WalkNode(u.Operand); return;
                case PostfixExpr p: WalkNode(p.Operand); return;
                case BinaryExpr b2: WalkNode(b2.Left); WalkNode(b2.Right); return;
                case AssignExpr a: WalkNode(a.Target); WalkNode(a.Value); return;
                case RangeExpr r2: WalkNode(r2.Low); WalkNode(r2.High); return;
                case CastExpr c2: WalkNode(c2.Operand); return;
                case CallExpr call: WalkNode(call.Callee); foreach (var a in call.Arguments) WalkNode(a); return;
                case IndexExpr ix: WalkNode(ix.Target); WalkNode(ix.Index); return;
                case MemberExpr mem: WalkNode(mem.Target); return;
                case ResumeExpr re: WalkNode(re.Coroutine); return;
                case ArrayLitExpr arr: foreach (var e in arr.Elements) WalkNode(e); return;
                case TupleLitExpr tu: foreach (var e in tu.Elements) WalkNode(e); return;
                case StructInitExpr si: foreach (var f in si.Fields) WalkNode(f.Value); return;
                case InterpolatedStringExpr fs:
                    foreach (var seg in fs.Segments) if (seg is InterpHole h) WalkNode(h.Expr);
                    return;
                case IfExpr iff: WalkNode(iff.Condition); WalkNode(iff.Then); WalkNode(iff.Else); return;
            }
        }

        bool DeclaredInside(Symbol sym) =>
            sym.Declaration is { } d && d.Span.File == lam.Span.File
            && d.Span.Start >= lam.Span.Start && d.Span.End <= lam.Span.End;

        WalkNode(lam.Body);
        if (captured.Count > 0 || capturesThis)
            _result.SetCaptures(lam, captured, capturesThis);

        // Captured 'var' bindings are shared rather than copied, so they need a cell. Conservatively:
        // what counts is not WHETHER the closure writes, but that it could.
        foreach (var symbol in captured)
            if (symbol is LocalSymbol { IsMutable: true }) _result.MarkBoxed(symbol);
    }

    // --- arithmetic, assignability, literal fit ---

    /// <summary>
    /// Brings two numeric operands to one type; an untyped literal adapts to the other.
    /// </summary>
    /// <remarks>
    /// <para>The adaptation is RECORDED here, not only checked. With <c>LiteralAdaptsTo</c> alone the
    /// sema accepts <c>a + 1</c> with <c>a: int8</c> but still notes <c>int</c> for the <c>1</c>, and
    /// the lowering puts a <c>const i64</c> next to an i8 operand — a compiler crash in the IR
    /// verifier with "operand types differ".</para>
    /// <para><see cref="AdaptLiteralType"/> describes the same error for the assignment path.</para>
    /// </remarks>
    private LyrType? UnifyNumeric(Expr le, LyrType l, Expr re, LyrType r)
    {
        if (LyrType.Equal(l, r) && TypeFacts.IsNumeric(l)) return l;

        if (TypeFacts.IsNumeric(l) && l is PrimitiveType pl && LiteralAdaptsTo(re, pl))
        {
            AdaptLiteralType(re, pl);   // r adapts to l
            return l;
        }

        if (TypeFacts.IsNumeric(r) && r is PrimitiveType pr && LiteralAdaptsTo(le, pr))
        {
            AdaptLiteralType(le, pr);   // l adapts to r
            return r;
        }

        return null;
    }

    private void CheckAssignable(Expr expr, LyrType from, LyrType to, Span span)
    {
        if (AdaptEmptyArray(expr, to)) return;

        // An error INSIDE one of the types means the cause is already reported, by the poison rule.
        // Testing only whether the type ITSELF is an ErrorType lets a 'fn(int) -> <error>' through and
        // produces "cannot assign 'fn(int) -> <error>' to 'fn(int) -> U'", which says nothing to the
        // reader and buries the actual message next to it.
        if (ContainsError(from) || ContainsError(to)) return;

        if (!IsAssignable(expr, from, to))
        {
            _de.Report("LYR-SEM0001", Severity.Error, span, $"cannot assign '{TypeFacts.Display(from)}' to '{TypeFacts.Display(to)}'");
            return;
        }

        AdaptLiteralType(expr, to);
    }

    /// <summary>
    /// An untyped literal IS of the target type; it is not converted to it. The side table therefore
    /// has to say so as well, or every later stage takes `let x: int8 = 5` for an `int`.
    /// </summary>
    /// <remarks>Without this step the sema checks the literal fit correctly but keeps noting the
    /// default type; the lowering then produces a `const i64` and pushes it into an i8 slot.</remarks>
    private void AdaptLiteralType(Expr expr, LyrType to)
    {
        while (to is Optional optional) to = optional.Inner; // T to ?T: the literal takes T

        if (to is not PrimitiveType target || !LiteralAdaptsTo(expr, target)) return;

        _result.SetType(expr, target);
        // '-5' is UnaryExpr(Neg, IntLiteral 5); both nodes carry the adapted type.
        if (expr is UnaryExpr { Operator: UnaryOp.Neg } negated)
            _result.SetType(negated.Operand, target);
    }


    /// <summary>
    /// A <c>struct</c> must not contain itself as a field, not even indirectly.
    ///
    /// <para>For a reference type <c>class Node { next: Node }</c> is fine: a field holds a reference,
    /// and that is one machine word. A value type contains its fields BY SIZE, so a struct containing
    /// itself would be infinitely large. Rust reports "recursive type has infinite size", C# reports
    /// CS0523.</para>
    ///
    /// <para>Without this check <see cref="Lyric.Ir.Lowering.TypeTable"/> would loop forever here:
    /// for classes it terminates through the pre-assigned id, but a value type needs its layout
    /// complete before it is finished.</para>
    ///
    /// <para>The way out is the same as in Rust and C#: an indirection, meaning <c>class</c>. A
    /// <c>?T</c> does not suffice, because <c>?Struct</c> still holds the value by size.</para>
    /// </summary>
    private void CheckStructIsFinite(StructDecl decl, ModuleSymbol module)
    {
        if (module.Members.LookupLocal(decl.Name) is not TypeSymbol self) return;

        // The path is carried along so the message can name the cycle rather than only its existence:
        // for 'A contains B contains A' that is the whole difference.
        var path = new List<string> { decl.Name };
        if (FindStructCycle(self, self, new HashSet<TypeSymbol>(ReferenceEqualityComparer.Instance), path))
            _de.Report("LYR-SEM0056", Severity.Error, decl.Span,
                $"struct '{decl.Name}' contains itself ({string.Join(" -> ", path)}) and would have "
                + "infinite size; use a 'class' for the recursive part");
    }

    private bool FindStructCycle(TypeSymbol root, TypeSymbol current,
        HashSet<TypeSymbol> visited, List<string> path)
    {
        if (!visited.Add(current)) return false;
        if (current.Declaration is not StructDecl decl) return false;

        foreach (var field in decl.Members.OfType<FieldDecl>())
        {
            // Only directly held structs count. An array is a reference and so is a class; both break
            // the chain.
            if (field.Type is not NamedType named) continue;

            var bound = _binding.Resolve(named);
            if (bound is ImportBindingSymbol import) bound = import.Target;
            if (bound is not TypeSymbol { Kind: TypeSymbolKind.Struct } nested) continue;

            path.Add(nested.Name);
            if (ReferenceEquals(nested, root) || FindStructCycle(root, nested, visited, path))
                return true;
            path.RemoveAt(path.Count - 1);
        }

        return false;
    }

    private bool IsAssignable(Expr expr, LyrType from, LyrType to)
    {
        if (from.IsError || to.IsError) return true;      // poison: no follow-up errors
        if (from is NeverType) return true;               // the bottom type: panic(...) fits anywhere
        if (LyrType.Equal(from, to)) return true;
        if (to is Optional inner)                          // T to ?T, widening
            return from is NullType || IsAssignable(expr, from, inner.Inner);
        if (from is NullType) return false;
        if (to is PrimitiveType pt && LiteralAdaptsTo(expr, pt)) return true; // literal fit
        if (ImplementsInterface(from, to)) return true;   // T to I when T :: [I]
        return false;
    }

    /// <summary>
    /// Nominal subtyping: a value may stand wherever one of its declared interfaces is expected.
    ///
    /// <para>In THIS direction only. The way back — an interface to a class — would be a downcast, and
    /// the language has none: <c>as</c> converts between numeric types only. An interface value
    /// therefore needs no runtime type check.</para>
    ///
    /// <para>The question itself is answered by <see cref="Conformance"/>, the same place the
    /// conformance check and the IR lowering ask.</para>
    /// </summary>
    private bool ImplementsInterface(LyrType from, LyrType to)
    {
        // The target may be a generic interface ('Src<int>'), in which case it is a GenericInstance
        // rather than a NamedRef. Without this case every assignment to a generic interface is a type
        // error and 'Iterator<T>' is unusable.
        if (TypeFacts.KindOf(to) != TypeSymbolKind.Interface) return false;
        var target = TypeFacts.SymbolOf(to)!;

        // Conformance may be DECLARED ('class P :: [I]') or come from a visible 'extend P :: [I]'.
        // Both are the same question, so one function answers it.
        //
        // 'to' is passed on as a whole rather than only its symbol: 'Src<int>' and 'Src<string>' are
        // the same symbol and different types. Without that, an assignment to a generic interface is
        // unsound.
        if (TypeFacts.SymbolOf(from) is { } source)
            return ImplementsWithExtensions(source, target, to,
                from is GenericInstance instance ? SubstMap(instance) : EmptySubst);

        // A type parameter satisfies what its constraints demand. It has no symbol of its own, so it
        // stands here beside rather than inside SymbolOf.
        return from is TypeParamType parameter
               && parameter.Param.Constraints.Any(c =>
                   Conformance.InterfaceOf(c, _binding) is { } it && ReferenceEquals(it, target)
                   && Matches(ResolveType(c, _currentModule?.Members ?? _comp.Builtins),
                       EmptySubst, to));
    }

    private static bool LiteralAdaptsTo(Expr expr, PrimitiveType target)
    {
        if (TryUntypedIntLiteral(expr, out var negative, out var magnitude))
        {
            if (TypeFacts.IsInteger(target)) return TypeFacts.IntLiteralFits(negative, magnitude, target.Kind);
            if (TypeFacts.IsFloat(target)) return true; // an integer literal adapts to a float
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

    // --- type resolution: TypeNode to LyrType ---

    private LyrType ResolveType(TypeNode node, SymbolTable scope)
    {
        switch (node)
        {
            case NamedType n:
                var sym = _binding.Resolve(n) ?? ResolveTypePath(n.Path, scope);

                // Recorded in the SAME table the resolver writes into. The resolver binds the type
                // names it walks — those in declarations — while the ones inside a function body
                // are reached only from here, and they were resolved and then dropped. One question
                // ("what does this type name refer to") answered by one table, whoever asked it.
                if (sym is not null && _binding.Resolve(n) is null) _binding.Bind(n, sym);

                if (sym is ImportBindingSymbol ibt) sym = ibt.Target;
                if (sym is null)
                    return Report(n.Span, "LYR-SEM0011", $"unresolved type '{string.Join('.', n.Path)}'");
                if (sym is GenericParamSymbol gp) return new TypeParamType(gp);
                if (ReferenceEquals(sym, _coroutine)) // Coroutine<T> becomes the internal CoroutineOf
                {
                    if (n.TypeArguments.Length != 1)
                        return Report(n.Span, "LYR-SEM0026",
                            $"'Coroutine' expects exactly 1 type argument, got {n.TypeArguments.Length}");
                    return new CoroutineOf(ResolveType(n.TypeArguments[0], scope));
                }
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

    /// <summary>
    /// The type an alias names. An alias is a NAME for a type, not a type of its own, so it is
    /// replaced by what it names wherever it stands.
    ///
    /// <para>Guarded against a cycle. <c>type A = B; type B = A;</c> expands forever, and the result
    /// was not a diagnostic but a STACK OVERFLOW — which .NET cannot catch, so the compiler process
    /// died instead of the compilation failing. Reported once per alias: the cycle is a property of
    /// the declaration, and one message per use site would be the same fault repeated.</para>
    /// </summary>
    private LyrType ExpandAlias(TypeSymbol alias, SymbolTable scope)
    {
        if (alias.Declaration is not TypeAliasDecl decl) return LyrType.Error;

        if (!_expanding.Add(alias))
        {
            if (_cyclic.Add(alias))
                Report(decl.Span, "LYR-SEM0064",
                    $"the type alias '{alias.Name}' expands to itself; an alias names a type and " +
                    "cannot be defined through itself");
            return LyrType.Error;
        }

        try { return ResolveType(decl.Aliased, scope); }
        finally { _expanding.Remove(alias); }
    }

    private LyrType SymbolToType(Symbol sym, SymbolTable scope) => sym switch
    {
        TypeSymbol { Kind: TypeSymbolKind.Builtin } t => TypeFacts.FromBuiltinName(t.Name) ?? LyrType.Error,
        TypeSymbol { Kind: TypeSymbolKind.Alias } t => ExpandAlias(t, scope),
        GenericParamSymbol g => new TypeParamType(g),
        TypeSymbol t => new NamedRef(t),
        ImportBindingSymbol ib => SymbolToType(ib.Target, scope),
        _ => LyrType.Error // external, error or non-type: opaque
    };

    // Stack<int> becomes a GenericInstance. The arity is checked here; constraint satisfaction runs
    // through the conformance model.
    private LyrType MakeGenericInstance(TypeSymbol ts, NamedType n, SymbolTable scope)
    {
        var args = n.TypeArguments.Select(a => ResolveType(a, scope)).ToArray();
        if (ts.Generics.Length != args.Length)
            _de.Report("LYR-SEM0026", Severity.Error, n.Span,
                $"generic type '{ts.Name}' expects {ts.Generics.Length} type argument(s), got {args.Length}");
        return new GenericInstance(ts, args);
    }

    // Compact path resolution for body types, which the resolver has not bound.
    private Symbol? ResolveTypePath(string[] path, SymbolTable scope)
    {
        var head = scope.Lookup(path[0]);
        if (head is null || path.Length == 1) return head;
        for (var i = 1; i < path.Length && head is ImportBindingSymbol { Target: ModuleSymbol mod }; i++)
            head = mod.Members.LookupLocal(path[i]);
        return head;
    }

    // --- helpers ---

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
