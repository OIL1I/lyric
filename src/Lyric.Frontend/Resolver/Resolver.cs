using Lyric.AST;
using Lyric.Core;

namespace Lyric.Resolver;

/// <summary>
/// Name resolution in three passes:
///   1. Declare: register all top-level symbols and type members, using the two-pass principle for
///      forward references, and report duplicates.
///   2. Resolve imports: find the target module in the compilation (otherwise external and
///      opaque), bring the names into the module scope, check visibility, detect cycles.
///   3. Bind type names in signatures and fields, recording the result in the
///      <see cref="BindingResult"/> side table.
/// </summary>
public sealed class Resolver
{
    private readonly Compilation _comp;
    private readonly DiagnosticEngine _de;
    private readonly BindingResult _binding = new();

    public Resolver(Compilation comp, SourceManager sm, DiagnosticEngine de)
    {
        _comp = comp;
        _de = de;
        _ = sm; // reserved for later source-related diagnostics
    }

    /// <summary>The "previous declaration" note for a duplicate, or none when the first claimant
    /// has no source (a builtin, a synthetic symbol).</summary>
    private static DiagnosticNote[] PreviousDeclaration(SymbolTable scope, string name) =>
        scope.LookupLocal(name)?.Declaration is { } previous
            ? [new DiagnosticNote(previous.Span, "previous declaration")]
            : [];

    public BindingResult Run()
    {
        foreach (var module in _comp.Modules) DeclareModule(module);
        foreach (var module in _comp.Modules) ResolveImports(module);
        DetectImportCycles();
        foreach (var module in _comp.Modules) BindTypeNames(module);
        ResolveExtensionTargets();
        _comp.Extensions.BuildIndex();
        return _binding;
    }

    // --- pass 1: declaring ---

    private void DeclareModule(ModuleSymbol module)
    {
        foreach (var decl in _comp.AstOf(module).Declarations)
        {
            switch (decl)
            {
                case StructDecl s: DeclareType(module, s.Name, TypeSymbolKind.Struct, Vis(s.IsPublic), s.Generics, s.Members, s); break;
                case ClassDecl c: DeclareType(module, c.Name, TypeSymbolKind.Class, Vis(c.IsPublic), c.Generics, c.Members, c); break;
                case EnumDecl e: DeclareEnum(module, e); break;
                case InterfaceDecl i: DeclareInterface(module, i); break;
                case TypeAliasDecl a:
                    DeclareTop(module, new TypeSymbol(a.Name, TypeSymbolKind.Alias, Vis(a.IsPublic), new SymbolTable(), a), a);
                    break;
                case FunctionDecl fn:
                    DeclareTop(module, Fn(fn), fn);
                    break;
                case GlobalBindingDecl g:
                    DeclareTop(module, new GlobalSymbol(g.Binding.Name, Vis(g.IsPublic), g), g);
                    break;
                case ExtendDecl ex: DeclareExtend(module, ex); break;
                // ImportDecl → Pass 2; ErrorDecl → skip
            }
        }
    }

    // An extend block: its methods get their own FunctionSymbol in a block scope
    // (parent = module scope), so `T` and free names resolve. The target type is bound in pass 3.
    // No new top-level symbol; the methods live only in the ExtensionRegistry.
    private void DeclareExtend(ModuleSymbol module, ExtendDecl ex)
    {
        var methodScope = new SymbolTable(module.Members);
        var methods = new List<FunctionSymbol>();
        foreach (var fn in ex.Methods)
        {
            var fsym = Fn(fn);
            if (methodScope.TryDeclare(fsym)) methods.Add(fsym);
            else _de.Report("LYR-RES0001", Severity.Error, fn.Span,
                $"'{fn.Name}' is already declared in this extend block",
                PreviousDeclaration(methodScope, fn.Name));
        }
        _comp.Extensions.Add(new ExtensionBlock(ex, module, methodScope, methods.ToArray()));
    }

    private void DeclareType(ModuleSymbol module, string name, TypeSymbolKind kind, Visibility vis, GenericParam[] generics, Decl[] members, Decl decl)
    {
        var scope = new SymbolTable(module.Members);
        var ts = new TypeSymbol(name, kind, vis, scope, decl) { Generics = MakeGenerics(generics) };
        DeclareGenerics(scope, ts.Generics);
        DeclareTop(module, ts, decl);
        foreach (var m in members)
        {
            switch (m)
            {
                case FieldDecl f: DeclareMember(scope, new FieldSymbol(f.Name, f), f); break;
                case FunctionDecl fn: DeclareMember(scope, Fn(fn), fn); break;

                // A 'static let' is a type-bound constant, held as a GlobalSymbol because that is
                // what it is: an immutable binding without an instance, scoped to the type rather
                // than to the module.
                case StaticBindingDecl sb:
                    DeclareMember(scope, new GlobalSymbol(sb.Binding.Name, Vis(sb.IsPublic), sb), sb);
                    break;
            }
        }
    }

    private void DeclareEnum(ModuleSymbol module, EnumDecl e)
    {
        var scope = new SymbolTable(module.Members);
        var ts = new TypeSymbol(e.Name, TypeSymbolKind.Enum, Vis(e.IsPublic), scope, e) { Generics = MakeGenerics(e.Generics) };
        DeclareGenerics(scope, ts.Generics);
        DeclareTop(module, ts, e);
        foreach (var v in e.Variants) DeclareMember(scope, new EnumVariantSymbol(v.Name, v), v);
        foreach (var fn in e.Methods) DeclareMember(scope, Fn(fn), fn);
    }

    private void DeclareInterface(ModuleSymbol module, InterfaceDecl i)
    {
        var scope = new SymbolTable(module.Members);
        var ts = new TypeSymbol(i.Name, TypeSymbolKind.Interface, Vis(i.IsPublic), scope, i) { Generics = MakeGenerics(i.Generics) };
        DeclareGenerics(scope, ts.Generics);
        DeclareTop(module, ts, i);
        foreach (var fn in i.Members) DeclareMember(scope, Fn(fn), fn);
    }

    private static FunctionSymbol Fn(FunctionDecl fn) =>
        new(fn.Name, Vis(fn.IsPublic), fn.IsMut, fn, fn.IsStatic) { Generics = MakeGenerics(fn.Generics) };

    private static GenericParamSymbol[] MakeGenerics(GenericParam[] generics)
    {
        if (generics.Length == 0) return [];
        var result = new GenericParamSymbol[generics.Length];
        for (var i = 0; i < generics.Length; i++)
            result[i] = new GenericParamSymbol(generics[i].Name, generics[i].Constraints, generics[i]);
        return result;
    }

    // Type parameters go into the member and signature scope so `T` resolves. A collision with a
    // member name is reported by TryDeclare later.
    private static void DeclareGenerics(SymbolTable scope, GenericParamSymbol[] generics)
    {
        foreach (var g in generics) scope.TryDeclare(g);
    }

    private void DeclareTop(ModuleSymbol module, Symbol sym, Node decl)
    {
        if (!module.Members.TryDeclare(sym))
            _de.Report("LYR-RES0001", Severity.Error, decl.Span,
                $"'{sym.Name}' is already declared in this module{OverloadHint(module.Members, sym)}",
                PreviousDeclaration(module.Members, sym.Name));
    }

    private void DeclareMember(SymbolTable scope, Symbol sym, Node decl)
    {
        if (!scope.TryDeclare(sym))
            _de.Report("LYR-RES0001", Severity.Error, decl.Span,
                $"'{sym.Name}' is already declared in this type{OverloadHint(scope, sym)}",
                PreviousDeclaration(scope, sym.Name));
    }

    /// <summary>
    /// Two functions of the same name are an attempt to overload rather than a slip, and that is a
    /// deliberate rule rather than a plain name collision. Without the addition the message reads
    /// as if the author had made a mistake.
    /// </summary>
    private static string OverloadHint(SymbolTable scope, Symbol sym) =>
        sym is FunctionSymbol && scope.LookupLocal(sym.Name) is FunctionSymbol
            ? " — Lyric has no overloading; give the functions distinct names"
            : "";

    // --- Pass 2: Imports ---

    private void ResolveImports(ModuleSymbol module)
    {
        foreach (var decl in _comp.AstOf(module).Declarations)
            if (decl is ImportDecl imp)
                ResolveImport(module, imp);
    }

    private void ResolveImport(ModuleSymbol module, ImportDecl imp)
    {
        var target = _comp.FindModule(imp.Path);

        // A module that cannot be found is an error rather than external and opaque.
        //
        // Without the diagnostic a typo in a module name is invisible AND silently disables the
        // check of every use: the ExternalSymbol carries LyrType.Error, and Error means
        // "already reported" to every consumer, so it stays silent.
        if (target is null)
            _de.Report("LYR-RES0003", Severity.Error, imp.Span,
                $"cannot find module '{string.Join('.', imp.Path)}'");

        switch (imp.Clause)
        {
            case null: // import a.b; binds 'b' to the module
            {
                var name = imp.Path[^1];
                DeclareImport(module, target is not null
                    ? new ImportBindingSymbol(name, target, imp)
                    : new ExternalSymbol(name, imp.Path, imp), imp);
                break;
            }
            case ImportSelective sel: // import a.b { x, y };
                foreach (var name in sel.Names)
                    DeclareImport(module, ResolveSelective(name, target, imp), imp);
                break;
            case ImportAlias alias: // import a.b as C;
                DeclareImport(module, target is not null
                    ? new ImportBindingSymbol(alias.Alias, target, imp)
                    : new ExternalSymbol(alias.Alias, imp.Path, imp), imp);
                break;
        }
    }

    private Symbol ResolveSelective(string name, ModuleSymbol? target, ImportDecl imp)
    {
        if (target is null) return new ExternalSymbol(name, imp.Path, imp); // extern/opak

        var found = target.Members.LookupLocal(name);
        if (found is null)
        {
            _de.Report("LYR-RES0004", Severity.Error, imp.Span, $"module '{target.FullName}' has no exported '{name}'");
            return new ErrorSymbol(name);
        }
        if (!IsPublic(found))
            _de.Report("LYR-RES0004", Severity.Error, imp.Span, $"'{name}' is not public in '{target.FullName}'");
        return new ImportBindingSymbol(name, found, imp); // recovery: bind even when not public
    }

    private void DeclareImport(ModuleSymbol module, Symbol sym, ImportDecl imp)
    {
        if (!module.Members.TryDeclare(sym))
            _de.Report("LYR-RES0001", Severity.Error, imp.Span,
                $"'{sym.Name}' is already declared in this module",
                PreviousDeclaration(module.Members, sym.Name));
    }

    private void DetectImportCycles()
    {
        var idx = new Dictionary<ModuleSymbol, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < _comp.Modules.Count; i++) idx[_comp.Modules[i]] = i;
        var state = new int[_comp.Modules.Count]; // 0 = neu, 1 = im Stack, 2 = fertig

        void Dfs(ModuleSymbol m)
        {
            state[idx[m]] = 1;
            foreach (var decl in _comp.AstOf(m).Declarations)
            {
                if (decl is not ImportDecl imp) continue;
                var t = _comp.FindModule(imp.Path);
                if (t is null) continue; // an external module means no edge
                if (state[idx[t]] == 1)
                    _de.Report("LYR-RES0005", Severity.Error, imp.Span, $"import cycle involving module '{t.FullName}'");
                else if (state[idx[t]] == 0) Dfs(t);
            }
            state[idx[m]] = 2;
        }

        for (var i = 0; i < _comp.Modules.Count; i++)
            if (state[i] == 0) Dfs(_comp.Modules[i]);
    }

    // --- pass 3: binding type names ---

    private void BindTypeNames(ModuleSymbol module)
    {
        var scope = module.Members;
        foreach (var decl in _comp.AstOf(module).Declarations) BindDeclTypes(decl, scope);
    }

    private void BindDeclTypes(Decl decl, SymbolTable scope)
    {
        switch (decl)
        {
            case FunctionDecl fn: BindFunctionTypes(fn, scope); break;
            case StructDecl s: { var ms = MemberScope(scope, s.Name); BindGenerics(s.Generics, ms); BindEach(s.Interfaces, ms); BindMembers(s.Members, ms); break; }
            case ClassDecl c: { var ms = MemberScope(scope, c.Name); BindGenerics(c.Generics, ms); BindEach(c.Interfaces, ms); BindMembers(c.Members, ms); break; }
            case EnumDecl e:
                var es = MemberScope(scope, e.Name);
                BindGenerics(e.Generics, es);
                BindEach(e.Interfaces, es);
                foreach (var v in e.Variants)
                {
                    foreach (var t in v.TupleFields ?? []) BindType(t, es);
                    foreach (var f in v.StructFields ?? []) BindType(f.Type, es);
                }
                foreach (var m in e.Methods) BindFunctionTypes(m, es);
                break;
            case InterfaceDecl i:
                var isc = MemberScope(scope, i.Name);
                BindGenerics(i.Generics, isc);
                BindEach(i.Interfaces, isc);
                foreach (var m in i.Members) BindFunctionTypes(m, isc);
                break;
            // ExtendDecl goes to ResolveExtensionTargets, which needs the block method scope for
            // generics.
            case TypeAliasDecl a: BindType(a.Aliased, scope); break;
            case GlobalBindingDecl g:
                if (g.Binding.Type is not null) BindType(g.Binding.Type, scope);
                break;
        }
    }

    // Pass 3.5: bind extend targets and signatures. Runs after every BindTypeNames, so each target
    // type is known. Signatures bind against the block method scope (parent = module) plus the
    // method generics, so `T` in `fn map<T>(x: T)` resolves.
    private void ResolveExtensionTargets()
    {
        foreach (var block in _comp.Extensions.Blocks)
        {
            var scope = block.MethodScope;
            BindType(block.Decl.Target, scope);
            BindEach(block.Decl.Interfaces, scope);
            foreach (var m in block.Decl.Methods) BindFunctionTypes(m, scope);

            var sym = _binding.Resolve(block.Decl.Target);
            if (sym is ImportBindingSymbol ib) sym = ib.Target;
            // Only plain named targets are extendable, no Box<int> and no T[]; everything else leaves
            // Target null and the sema reports SEM0047.
            block.Target = block.Decl.Target is NamedType { TypeArguments.Length: 0 } ? sym as TypeSymbol : null;
        }
    }

    private void BindMembers(Decl[] members, SymbolTable scope)
    {
        foreach (var m in members)
        {
            if (m is FieldDecl f) BindType(f.Type, scope);
            else if (m is FunctionDecl fn) BindFunctionTypes(fn, scope);
        }
    }

    private void BindFunctionTypes(FunctionDecl fn, SymbolTable scope)
    {
        // A generic function binds its signature against a scope holding the type parameters — the
        // same symbol instances that sit on the FunctionSymbol, so the sema sees them as identical.
        var fsym = scope.LookupLocal(fn.Name) as FunctionSymbol;
        var sig = fsym is { Generics.Length: > 0 } ? WithGenerics(scope, fsym.Generics) : scope;
        foreach (var p in fn.Parameters) BindType(p.Type, sig);
        if (fn.ReturnType is not null) BindType(fn.ReturnType, sig);
        if (fn.Throws?.Type is not null) BindType(fn.Throws.Type, sig);
        foreach (var g in fn.Generics)
            foreach (var c in g.Constraints)
                BindType(c, sig);
        // Body types, meaning local bindings and casts, are handled by the sema.
    }

    private static SymbolTable MemberScope(SymbolTable enclosing, string typeName) =>
        (enclosing.LookupLocal(typeName) as TypeSymbol)?.Members ?? enclosing;

    private static SymbolTable WithGenerics(SymbolTable parent, GenericParamSymbol[] generics)
    {
        var s = new SymbolTable(parent);
        DeclareGenerics(s, generics);
        return s;
    }

    private void BindEach(TypeNode[] types, SymbolTable scope)
    {
        foreach (var t in types) BindType(t, scope);
    }

    private void BindGenerics(GenericParam[] generics, SymbolTable scope)
    {
        foreach (var g in generics)
            foreach (var c in g.Constraints)
                BindType(c, scope);
    }

    private void BindType(TypeNode type, SymbolTable scope)
    {
        switch (type)
        {
            case NamedType n:
                var sym = ResolveTypePath(n.Path, scope);
                if (sym is null)
                {
                    _de.Report("LYR-RES0002", Severity.Error, n.Span, $"unresolved type '{string.Join('.', n.Path)}'");
                    _binding.Bind(n, new ErrorSymbol(n.Path[^1]));
                }
                else _binding.Bind(n, sym);
                foreach (var a in n.TypeArguments) BindType(a, scope);
                break;
            case NullableType nn: BindType(nn.Inner, scope); break;
            case ArrayType a: BindType(a.Element, scope); break;
            case TupleType t: foreach (var e in t.Elements) BindType(e, scope); break;
            case FunctionType f:
                foreach (var p in f.Parameters) BindType(p, scope);
                BindType(f.ReturnType, scope);
                break;
            case ErrorType: break;
        }
    }

    private Symbol? ResolveTypePath(string[] path, SymbolTable scope)
    {
        var head = scope.Lookup(path[0]);
        if (head is null) return null;
        if (path.Length == 1) return IsTypeLike(head) ? head : null;

        // Multi-segment paths navigate through imported modules only.
        for (var i = 1; i < path.Length; i++)
        {
            switch (head)
            {
                case ExternalSymbol: return head; // everything behind an external module is external
                case ImportBindingSymbol { Target: ModuleSymbol mod }:
                    var next = mod.Members.LookupLocal(path[i]);
                    if (next is null) return null;
                    head = next;
                    break;
                default:
                    return null; // nested types and the like are not provided for
            }
        }
        return IsTypeLike(head) ? head : null;
    }

    // --- Helpers ---

    private static Visibility Vis(bool isPublic) => isPublic ? Visibility.Public : Visibility.Module;

    private static bool IsPublic(Symbol s) => s switch
    {
        TypeSymbol t => t.Visibility == Visibility.Public,
        FunctionSymbol f => f.Visibility == Visibility.Public,
        GlobalSymbol g => g.Visibility == Visibility.Public,
        _ => true // externals, imports and builtins count as accessible
    };

    private static bool IsTypeLike(Symbol s) => s switch
    {
        TypeSymbol => true,
        GenericParamSymbol => true, // T is an (abstract) type
        ExternalSymbol => true,
        ErrorSymbol => true,
        ImportBindingSymbol ib => IsTypeLike(ib.Target),
        _ => false // functions, globals, fields, variants and modules are not types
    };
}
