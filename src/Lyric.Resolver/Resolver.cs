using Lyric.AST;
using Lyric.Core;

namespace Lyric.Resolver;

/// <summary>
/// Name-Auflösung (Sprache.md §2/§3). Drei Pässe:
///   1. Deklarieren: alle Top-Level-Symbole + Typ-Member registrieren (2-Pass-Prinzip
///      für Forward-Refs), Duplikate melden.
///   2. Imports auflösen: Ziel-Modul in der Compilation suchen (sonst extern/opak),
///      Namen in den Modul-Scope bringen, Sichtbarkeit prüfen, Zyklen erkennen.
///   3. Typ-Namen binden: jede <see cref="NamedType"/> an ihr Symbol (Builtin / lokal /
///      importiert / extern) — Ergebnis in der <see cref="BindingResult"/>-Seitentabelle.
/// Slice 1 bindet noch keine Ausdrücke und berechnet keine Typen (das ist Slice 2).
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
        _ = sm; // (für spätere quellbezogene Diagnostik reserviert)
    }

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

    // --- Pass 1: Deklarieren ---

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

    // Extend-Block (§3.6): Methoden bekommen ein eigenes FunctionSymbol in einer Block-Scope
    // (Parent = Modul-Scope), damit `T`/freie Namen auflösen. Der Ziel-Typ wird erst in Pass 3
    // gebunden. Kein neues Top-Level-Symbol; die Methoden leben nur in der ExtensionRegistry.
    private void DeclareExtend(ModuleSymbol module, ExtendDecl ex)
    {
        var methodScope = new SymbolTable(module.Members);
        var methods = new List<FunctionSymbol>();
        foreach (var fn in ex.Methods)
        {
            var fsym = Fn(fn);
            if (methodScope.TryDeclare(fsym)) methods.Add(fsym);
            else _de.Report("LYR-RES0001", Severity.Error, fn.Span, $"'{fn.Name}' is already declared in this extend block");
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
        new(fn.Name, Vis(fn.IsPublic), fn.IsMut, fn) { Generics = MakeGenerics(fn.Generics) };

    private static GenericParamSymbol[] MakeGenerics(GenericParam[] generics)
    {
        if (generics.Length == 0) return [];
        var result = new GenericParamSymbol[generics.Length];
        for (var i = 0; i < generics.Length; i++)
            result[i] = new GenericParamSymbol(generics[i].Name, generics[i].Constraints, generics[i]);
        return result;
    }

    // Typ-Params in den Member-/Signatur-Scope legen, damit `T` auflöst. Kollidiert ein
    // Param mit einem Member-Namen, meldet TryDeclare später ohnehin — hier still ignoriert.
    private static void DeclareGenerics(SymbolTable scope, GenericParamSymbol[] generics)
    {
        foreach (var g in generics) scope.TryDeclare(g);
    }

    private void DeclareTop(ModuleSymbol module, Symbol sym, Node decl)
    {
        if (!module.Members.TryDeclare(sym))
            _de.Report("LYR-RES0001", Severity.Error, decl.Span, $"'{sym.Name}' is already declared in this module");
    }

    private void DeclareMember(SymbolTable scope, Symbol sym, Node decl)
    {
        if (!scope.TryDeclare(sym))
            _de.Report("LYR-RES0001", Severity.Error, decl.Span, $"'{sym.Name}' is already declared in this type");
    }

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

        // Ein Modul, das nicht gefunden wird, ist ein Fehler — kein „extern/opak". Die alte
        // Regel stammt aus M3, als es den ModuleLoader noch nicht gab; seit M6-2 wird die Stdlib
        // wirklich geladen, und was dann fehlt, fehlt.
        //
        // Ohne die Meldung ist ein Tippfehler im Modulnamen unsichtbar UND schaltet die Prüfung
        // jeder Verwendung stumm ab: das ExternalSymbol tragt LyrType.Error, und Error heißt für
        // jeden Konsumenten „wurde schon gemeldet", also schweigt er.
        if (target is null)
            _de.Report("LYR-RES0003", Severity.Error, imp.Span,
                $"cannot find module '{string.Join('.', imp.Path)}'");

        switch (imp.Clause)
        {
            case null: // import a.b;  → 'b' bindet das Modul
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
        return new ImportBindingSymbol(name, found, imp); // Recovery: auch bei not-public binden
    }

    private void DeclareImport(ModuleSymbol module, Symbol sym, ImportDecl imp)
    {
        if (!module.Members.TryDeclare(sym))
            _de.Report("LYR-RES0001", Severity.Error, imp.Span, $"'{sym.Name}' is already declared in this module");
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
                if (t is null) continue; // externes Modul → keine Kante
                if (state[idx[t]] == 1)
                    _de.Report("LYR-RES0005", Severity.Error, imp.Span, $"import cycle involving module '{t.FullName}'");
                else if (state[idx[t]] == 0) Dfs(t);
            }
            state[idx[m]] = 2;
        }

        for (var i = 0; i < _comp.Modules.Count; i++)
            if (state[i] == 0) Dfs(_comp.Modules[i]);
    }

    // --- Pass 3: Typ-Namen binden ---

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
                foreach (var m in i.Members) BindFunctionTypes(m, isc);
                break;
            // ExtendDecl → ResolveExtensionTargets (braucht die Block-Method-Scope für Generics).
            case TypeAliasDecl a: BindType(a.Aliased, scope); break;
            case GlobalBindingDecl g:
                if (g.Binding.Type is not null) BindType(g.Binding.Type, scope);
                break;
        }
    }

    // Pass 3.5: Extend-Ziele + Signaturen binden. Läuft nach allen BindTypeNames, damit
    // jeder Ziel-Typ bekannt ist. Signaturen binden gegen die Block-Method-Scope (Parent =
    // Modul), plus die Methoden-Generics — so löst `T` in `fn map<T>(x: T)` auf.
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
            // Nur schlichte benannte Ziele (kein Box<int>, kein T[]) sind in v1 erweiterbar (D-cut);
            // alles andere lässt Target null → Sema meldet SEM0047.
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
        // Generische Funktion: Signatur gegen einen Scope binden, der die Typ-Params enthält
        // (dieselben Symbol-Instanzen, die auf dem FunctionSymbol liegen → identisch für die Sema).
        var fsym = scope.LookupLocal(fn.Name) as FunctionSymbol;
        var sig = fsym is { Generics.Length: > 0 } ? WithGenerics(scope, fsym.Generics) : scope;
        foreach (var p in fn.Parameters) BindType(p.Type, sig);
        if (fn.ReturnType is not null) BindType(fn.ReturnType, sig);
        if (fn.Throws?.Type is not null) BindType(fn.Throws.Type, sig);
        foreach (var g in fn.Generics)
            foreach (var c in g.Constraints)
                BindType(c, sig);
        // Body-Typen (lokale Bindings, Casts) → Sema (TypeChecker).
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

        // Multi-Segment: nur über (importierte) Module navigieren.
        for (var i = 1; i < path.Length; i++)
        {
            switch (head)
            {
                case ExternalSymbol: return head; // alles hinter einem externen Modul ist extern
                case ImportBindingSymbol { Target: ModuleSymbol mod }:
                    var next = mod.Members.LookupLocal(path[i]);
                    if (next is null) return null;
                    head = next;
                    break;
                default:
                    return null; // verschachtelte Typen o.ä. — in v1 nicht vorgesehen
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
        _ => true // Externals/Imports/Builtins gelten als zugreifbar
    };

    private static bool IsTypeLike(Symbol s) => s switch
    {
        TypeSymbol => true,
        GenericParamSymbol => true, // T ist ein (abstrakter) Typ
        ExternalSymbol => true,
        ErrorSymbol => true,
        ImportBindingSymbol ib => IsTypeLike(ib.Target),
        _ => false // Function/Global/Field/Variant/Module sind keine Typen
    };
}
