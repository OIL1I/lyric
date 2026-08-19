using Lyric.AST;
using Lyric.Core;
using Lyric.Resolver;

namespace Lyric.Sema;

/// <summary>
/// The warnings pass: unused locals, unused imports, unreachable statements. Warnings never make
/// a program invalid — everything here compiles and runs; the pass says what deserves fixing.
///
/// <para>Read-only over the tables the checker already built. Deliberately no lint framework:
/// the analyses consume <see cref="TypeResult.AllReferences"/>, <see cref="BindingResult.All"/>
/// and <see cref="Flow"/>, and an abstraction over three consumers of existing tables would be
/// machinery without a second customer.</para>
///
/// <para>Native modules are skipped: they are a host's SDK surface, and a warning the user
/// cannot fix is noise with a good conscience. The standard library is NOT skipped — it is
/// ordinary Lyric, this repository owns it, and a corpus test holds it warning-free.</para>
/// </summary>
internal sealed class WarningAnalyzer
{
    private readonly Compilation _comp;
    private readonly BindingResult _binding;
    private readonly TypeResult _types;
    private readonly DiagnosticEngine _de;

    private readonly HashSet<FileId> _nativeFiles = [];
    private readonly HashSet<Symbol> _used = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<FileId, HashSet<Symbol>> _usedByFile = [];
    private readonly HashSet<Symbol> _mutated = new(ReferenceEqualityComparer.Instance);

    private readonly Dictionary<Node, (string Message, Span AttributeSpan)> _deprecatedDecls =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ModuleSymbol, (string Message, Span AttributeSpan)>
        _deprecatedModules = new(ReferenceEqualityComparer.Instance);
    private readonly List<(FileId File, Span Extent)> _deprecatedExtents = [];

    public WarningAnalyzer(Compilation comp, BindingResult binding, TypeResult types,
        DiagnosticEngine de)
    {
        _comp = comp;
        _binding = binding;
        _types = types;
        _de = de;
    }

    public void Run()
    {
        foreach (var module in _comp.Modules)
            if (_comp.IsNative(module))
                _nativeFiles.Add(_comp.AstOf(module).Span.File);

        // Both tables: the checker's references carry expression uses, the resolver's carry the
        // type names of annotations and conformance lists. An import used only in an annotation
        // is used.
        CollectUses(_types.AllReferences);
        CollectUses(_binding.All);

        // The walk first: it reports unreachable statements and collects the reassignments the
        // never-reassigned hint reads.
        foreach (var module in _comp.Modules)
        {
            if (_comp.IsNative(module)) continue;
            foreach (var decl in _comp.AstOf(module).Declarations)
                WalkDecl(decl);
        }

        WarnUnusedLocals();
        HintNeverReassigned();
        WarnUnusedImports();
        WarnBuiltinShadowingImports();

        CollectDeprecated();
        WarnDeprecatedUses();
    }

    /// <summary>
    /// An import that binds a name a BUILTIN TYPE carries: <c>import std.string;</c> binds
    /// <c>string</c>, and from then on the type annotation resolves to the module. Legal by the
    /// scoping rules — module members shadow the builtin root scope like any parent — and almost
    /// never meant, so it warns at the import with the way out.
    /// </summary>
    private void WarnBuiltinShadowingImports()
    {
        foreach (var module in _comp.Modules)
        {
            if (_comp.IsNative(module)) continue;
            foreach (var decl in _comp.AstOf(module).Declarations)
            {
                if (decl is not ImportDecl import) continue;
                switch (import.Clause)
                {
                    case null when import.Path.Length > 0:
                        WarnIfShadowsBuiltin(import.Path[^1], import.Span);
                        break;
                    case ImportAlias alias:
                        WarnIfShadowsBuiltin(alias.Alias, alias.Span);
                        break;
                    case ImportSelective selective:
                        for (var i = 0; i < selective.Names.Length; i++)
                            WarnIfShadowsBuiltin(selective.Names[i], selective.NameSpans[i]);
                        break;
                }
            }
        }
    }

    private void WarnIfShadowsBuiltin(string name, Span span)
    {
        if (_comp.Builtins.LookupLocal(name) is not TypeSymbol) return;
        _de.Report("LYR-SEM0077", Severity.Warning, span,
            $"this import binds '{name}', shadowing the builtin type — the annotation "
            + $"'{name}' then names the import; import selectively, or rename it with 'as'");
    }

    // ─── deprecated uses ───────────────────────────────────────────────────

    /// <summary>
    /// The declarations carrying <c>@Deprecated</c>, by the CANONICAL type: the struct
    /// <c>std.core</c> declares, resolved by identity — a struct someone else names
    /// <c>Deprecated</c> deprecates nothing.
    /// </summary>
    private void CollectDeprecated()
    {
        if (_comp.FindModule(["std", "core"])?.Members.LookupLocal("Deprecated")
            is not TypeSymbol canonical) return;

        foreach (var module in _comp.Modules)
        {
            var ast = _comp.AstOf(module);

            if (FindDeprecated(ast.Attributes, canonical) is { } onModule)
            {
                _deprecatedModules[module] = onModule;
                _deprecatedExtents.Add((ast.Span.File, ast.Span));
            }

            foreach (var decl in ast.Declarations)
            {
                AttributeNode[] attributes = decl switch
                {
                    FunctionDecl f => f.Attributes,
                    StructDecl s => s.Attributes,
                    ClassDecl c => c.Attributes,
                    EnumDecl e => e.Attributes,
                    _ => [],
                };
                if (FindDeprecated(attributes, canonical) is not { } info) continue;
                _deprecatedDecls[decl] = info;
                _deprecatedExtents.Add((decl.Span.File, decl.Span));
            }
        }
    }

    private (string Message, Span AttributeSpan)? FindDeprecated(
        AttributeNode[] attributes, TypeSymbol canonical)
    {
        foreach (var attribute in attributes)
        {
            if (!ReferenceEquals(_types.RefOf(attribute), canonical)) continue;

            // One string field; its declared default is "". Reading the default from the struct
            // declaration would be the general form — one field does not need it.
            var written = attribute.Fields.FirstOrDefault(f => f.Name == "message");
            var message = written?.Value is StringLiteralExpr s ? s.Value : "";
            return (message, attribute.Span);
        }
        return null;
    }

    /// <summary>
    /// Every use of a deprecated declaration warns at the use site, with the note pointing at
    /// the attribute. Uses INSIDE anything itself deprecated are exempt — a deprecated function
    /// may call itself and its deprecated siblings without the compiler nagging the one place
    /// that is allowed not to care. A deprecated module warns at the imports that pull it in.
    /// </summary>
    private void WarnDeprecatedUses()
    {
        if (_deprecatedDecls.Count > 0)
        {
            var reported = new HashSet<Node>(ReferenceEqualityComparer.Instance);
            foreach (var table in new[]
                     {
                         (IEnumerable<KeyValuePair<Node, Symbol>>)_types.AllReferences,
                         _binding.All,
                     })
                foreach (var (node, symbol) in table)
                {
                    var target = symbol is ImportBindingSymbol shell ? shell.Target : symbol;
                    if (target.Declaration is not { } declaration) continue;
                    if (!_deprecatedDecls.TryGetValue(declaration, out var info)) continue;
                    if (ReferenceEquals(symbol.Declaration, node)) continue; // the declaration itself
                    if (_nativeFiles.Contains(node.Span.File)) continue;
                    if (InsideDeprecated(node.Span)) continue;
                    if (!reported.Add(node)) continue;

                    _de.Report("LYR-SEM0076", Severity.Warning, node.Span,
                        DeprecationMessage($"'{target.Name}'", info.Message),
                        new DiagnosticNote(info.AttributeSpan, "declared deprecated here"));
                }
        }

        if (_deprecatedModules.Count == 0) return;
        foreach (var module in _comp.Modules)
        {
            if (_comp.IsNative(module)) continue;
            foreach (var decl in _comp.AstOf(module).Declarations)
            {
                if (decl is not ImportDecl import) continue;
                if (_comp.FindModule(import.Path) is not { } imported) continue;
                if (!_deprecatedModules.TryGetValue(imported, out var info)) continue;
                if (InsideDeprecated(import.Span)) continue;

                _de.Report("LYR-SEM0076", Severity.Warning, import.Span,
                    DeprecationMessage($"module '{imported.FullName}'", info.Message),
                    new DiagnosticNote(info.AttributeSpan, "declared deprecated here"));
            }
        }
    }

    private static string DeprecationMessage(string what, string message) =>
        message.Length == 0 ? $"{what} is deprecated" : $"{what} is deprecated: {message}";

    private bool InsideDeprecated(Span use) =>
        _deprecatedExtents.Any(d =>
            d.File == use.File && use.Start >= d.Extent.Start && use.End <= d.Extent.End);

    private void CollectUses(IEnumerable<KeyValuePair<Node, Symbol>> table)
    {
        foreach (var (node, symbol) in table)
        {
            // A declaration is bound to the symbol it itself declares; only the other entries
            // are uses.
            if (symbol.Declaration is not null && ReferenceEquals(symbol.Declaration, node))
                continue;

            _used.Add(symbol);
            if (!_usedByFile.TryGetValue(node.Span.File, out var inFile))
                _usedByFile[node.Span.File] = inFile = new HashSet<Symbol>(
                    ReferenceEqualityComparer.Instance);
            inFile.Add(symbol);
        }
    }

    // ─── unused locals ─────────────────────────────────────────────────────

    /// <summary>
    /// A local that is never referenced after its declaration: bindings, loop variables, catch
    /// bindings and pattern bindings alike. Parameters are exempt — a signature is often not the
    /// author's to change — and so is every binder named <c>_</c>, which is the language's way of
    /// saying "deliberately unused".
    /// </summary>
    private void WarnUnusedLocals()
    {
        foreach (var (node, symbol) in _types.AllReferences)
        {
            if (symbol is not LocalSymbol local) continue;
            if (!ReferenceEquals(symbol.Declaration, node)) continue;
            if (local.Name == "_") continue;
            if (_nativeFiles.Contains(node.Span.File)) continue;
            if (_used.Contains(symbol)) continue;

            // A shorthand field pattern — 'Rect { w, h }' — binds the FIELD'S name, not one the
            // author chose, and the grammar has no ellipsis to leave a field out. Warning here
            // would demand 'w = _' boilerplate for the most idiomatic way to match a variant, so
            // the shorthand is exempt. An explicitly chosen name ('w = width') still warns.
            if (node is FieldPattern) continue;

            var (span, message) = node switch
            {
                BindingStmt b => (b.NameSpan, $"'{local.Name}' is never used"),
                ForInStmt f => (f.NameSpan, $"loop variable '{local.Name}' is never used"),
                CatchClause c => (c.NameSpan, $"catch binding '{local.Name}' is never used"),
                _ => (node.Span, $"'{local.Name}' is never used"),
            };
            _de.Report("LYR-SEM0071", Severity.Warning, span, message,
                new DiagnosticNote("name it '_' when the value is deliberately unused"));
        }
    }

    /// <summary>
    /// A <c>var</c> with an initializer through which nothing is ever changed: no reassignment,
    /// no field or element write, no <c>mut fn</c> call on it — <c>let</c> says what it actually
    /// is. Deliberately conservative: the language happens to allow field writes and mut calls
    /// through a <c>let</c> binding, so a stricter hint would be LITERALLY right — but it would
    /// advise hiding mutation behind <c>let</c>, and a hint must not teach that. A <c>var</c>
    /// that documents mutation keeps its <c>var</c>.
    ///
    /// <para>A hint rather than a warning — the program is fine, a clearer form exists. A
    /// <c>var</c> declared WITHOUT an initializer is exempt, because its later assignment is what
    /// completes the declaration, not a change of mind.</para>
    /// </summary>
    private void HintNeverReassigned()
    {
        foreach (var (node, symbol) in _types.AllReferences)
        {
            if (symbol is not LocalSymbol { IsMutable: true } local) continue;
            if (!ReferenceEquals(symbol.Declaration, node)) continue;
            if (node is not BindingStmt { Initializer: not null } binding) continue;
            if (local.Name == "_") continue;
            if (_nativeFiles.Contains(node.Span.File)) continue;
            if (!_used.Contains(symbol)) continue; // the unused warning already spoke
            if (_mutated.Contains(symbol)) continue;

            _de.Report("LYR-SEM0075", Severity.Hint, binding.NameSpan,
                $"'{local.Name}' is never reassigned — 'let' would do");
        }
    }

    // ─── unused imports ────────────────────────────────────────────────────

    /// <summary>
    /// An imported name nobody in the importing file refers to. Selective imports warn per name,
    /// an alias warns as a whole; a bare <c>import a.b;</c> is left alone — what it binds is the
    /// module for qualified access, and that question belongs to the resolver, not guessed here.
    /// </summary>
    private void WarnUnusedImports()
    {
        foreach (var module in _comp.Modules)
        {
            if (_comp.IsNative(module)) continue;

            var ast = _comp.AstOf(module);
            var inFile = _usedByFile.GetValueOrDefault(ast.Span.File);

            foreach (var decl in ast.Declarations)
            {
                if (decl is not ImportDecl { Clause: { } clause }) continue;
                switch (clause)
                {
                    case ImportSelective selective:
                        for (var i = 0; i < selective.Names.Length; i++)
                            WarnIfUnusedImport(module, selective.Names[i],
                                selective.NameSpans[i], inFile);
                        break;
                    case ImportAlias alias:
                        WarnIfUnusedImport(module, alias.Alias, alias.Span, inFile);
                        break;
                }
            }
        }
    }

    private void WarnIfUnusedImport(ModuleSymbol module, string name, Span span,
        HashSet<Symbol>? usedInFile)
    {
        // An unresolvable import already has the resolver's error; nothing to add.
        if (module.Members.LookupLocal(name) is not { } binding) return;

        // A use may be bound to the import shell or to what it targets — the checker does both,
        // depending on the path the resolution took. Either one makes the import used.
        var target = binding is ImportBindingSymbol shell ? shell.Target : binding;
        if (usedInFile is not null
            && (usedInFile.Contains(binding) || usedInFile.Contains(target))) return;

        // A MODULE import is used when one of its extension methods resolved in this file:
        // 'import std.string as strings;' exists exactly for 's.trim()' (v1.15), and no name of
        // the module has to appear in the source for that.
        if (target is ModuleSymbol targetModule && usedInFile is not null)
            foreach (var used in usedInFile)
                if (ExtensionOwners.TryGetValue(used, out var owner)
                    && ReferenceEquals(owner, targetModule))
                    return;

        _de.Report("LYR-SEM0072", Severity.Warning, span, $"import '{name}' is never used");
    }

    /// <summary>Which module each extension METHOD belongs to — the lookup behind the rule that
    /// a used extension marks its module's import as used.</summary>
    private Dictionary<Symbol, ModuleSymbol> ExtensionOwners => _extensionOwners ??= Build();

    private Dictionary<Symbol, ModuleSymbol>? _extensionOwners;

    private Dictionary<Symbol, ModuleSymbol> Build()
    {
        var owners = new Dictionary<Symbol, ModuleSymbol>(ReferenceEqualityComparer.Instance);
        foreach (var block in _comp.Extensions.Blocks)
            foreach (var method in block.Methods)
                owners[method] = block.Module;
        return owners;
    }

    // ─── unreachable statements ────────────────────────────────────────────

    // The walk mirrors FlowAnalyzer's shape: every statement, and the expressions that carry
    // blocks of their own — lambdas and match expressions.

    private void WalkDecl(Decl decl)
    {
        switch (decl)
        {
            case FunctionDecl fn: WalkFunction(fn); break;
            case StructDecl s: foreach (var m in s.Members) if (m is FunctionDecl f) WalkFunction(f); break;
            case ClassDecl c: foreach (var m in c.Members) if (m is FunctionDecl f) WalkFunction(f); break;
            case EnumDecl e: foreach (var f in e.Methods) WalkFunction(f); break;
            case InterfaceDecl i: foreach (var f in i.Members) WalkFunction(f); break;
            case ExtendDecl x: foreach (var f in x.Methods) WalkFunction(f); break;
        }
    }

    private void WalkFunction(FunctionDecl fn)
    {
        if (fn.Body is not null) WalkBlock(fn.Body);
    }

    /// <summary>
    /// The first statement after one that always leaves the block is unreachable. One report per
    /// block rather than one per statement — everything after the first finding is unreachable
    /// for the same reason, and a cascade would say it once per line.
    /// </summary>
    private void WalkBlock(Block block)
    {
        var reported = false;
        for (var i = 0; i < block.Statements.Length; i++)
        {
            var stmt = block.Statements[i];
            if (!reported && i + 1 < block.Statements.Length && Flow.AlwaysExits(stmt, _types))
            {
                _de.Report("LYR-SEM0073", Severity.Warning, block.Statements[i + 1].Span,
                    "unreachable statement",
                    new DiagnosticNote(stmt.Span, "control flow leaves the block here"));
                reported = true;
            }
            WalkStmt(stmt);
        }
    }

    private void WalkStmt(Stmt stmt)
    {
        switch (stmt)
        {
            case Block b: WalkBlock(b); break;
            case BindingStmt { Initializer: { } init }: WalkExpr(init); break;
            case DestructuringStmt d: WalkExpr(d.Initializer); break;
            case ExprStmt es: WalkExpr(es.Expr); break;
            case ReturnStmt { Value: { } v }: WalkExpr(v); break;
            case YieldStmt { Value: { } v }: WalkExpr(v); break;
            case ThrowStmt t: WalkExpr(t.Value); break;
            case DeferStmt de: WalkStmt(de.Body); break;
            case IfStmt f:
                WalkExpr(f.Condition);
                WalkBlock(f.Then);
                if (f.Else is { } e) WalkStmt(e);
                break;
            case WhileStmt w: WalkExpr(w.Condition); WalkBlock(w.Body); break;
            case DoWhileStmt d: WalkBlock(d.Body); WalkExpr(d.Condition); break;
            case ForInStmt fo: WalkExpr(fo.Iterable); WalkBlock(fo.Body); break;
            case TryStmt tr:
                WalkBlock(tr.Body);
                foreach (var c in tr.Catches) WalkBlock(c.Body);
                break;
            case MatchStmt m:
                WalkExpr(m.Scrutinee);
                foreach (var arm in m.Arms) WalkArm(arm);
                break;
        }
    }

    /// <summary>The binding a write or a <c>mut</c> call reaches THROUGH: the identifier at the
    /// root of the member, index, unwrap and cast chain.</summary>
    private void MarkMutated(Expr target)
    {
        var root = target;
        while (true)
        {
            switch (root)
            {
                case MemberExpr m: root = m.Target; continue;
                case IndexExpr ix: root = ix.Target; continue;
                case PostfixExpr { Operator: PostfixOp.ForceUnwrap } fu: root = fu.Operand; continue;
                case CastExpr cs: root = cs.Operand; continue;
            }
            break;
        }
        if (root is IdentifierExpr id && _types.RefOf(id) is { } symbol) _mutated.Add(symbol);
    }

    private void WalkArm(MatchArm arm)
    {
        if (arm.Guard is { } guard) WalkExpr(guard);
        if (arm.Body is Block b) WalkBlock(b);
        else if (arm.Body is Expr e) WalkExpr(e);
    }

    private void WalkExpr(Expr expr)
    {
        switch (expr)
        {
            case LambdaExpr lam:
                if (lam.Body is Block b) WalkBlock(b);
                else if (lam.Body is Expr e) WalkExpr(e);
                break;
            case MatchExpr ma:
                WalkExpr(ma.Scrutinee);
                foreach (var arm in ma.Arms) WalkArm(arm);
                break;
            case IfExpr iff: WalkExpr(iff.Condition); WalkExpr(iff.Then); WalkExpr(iff.Else); break;
            case BinaryExpr bi: WalkExpr(bi.Left); WalkExpr(bi.Right); break;
            case UnaryExpr u:
                if (u.Operator is UnaryOp.PreInc or UnaryOp.PreDec) MarkMutated(u.Operand);
                WalkExpr(u.Operand);
                break;
            case PostfixExpr p:
                if (p.Operator is PostfixOp.Inc or PostfixOp.Dec) MarkMutated(p.Operand);
                WalkExpr(p.Operand);
                break;
            case ResumeExpr re: WalkExpr(re.Coroutine); break;
            case AssignExpr a:
                MarkMutated(a.Target);
                WalkExpr(a.Target);
                WalkExpr(a.Value);
                break;
            case CallExpr c:
                if (c.Callee is MemberExpr callee
                    && _types.RefOf(callee) is FunctionSymbol { IsMut: true })
                    MarkMutated(callee.Target);
                foreach (var arg in c.Arguments)
                {
                    // A reference-typed argument may be written by the callee — an array's
                    // elements, a class's fields. The analysis cannot see across the call, so a
                    // var handed over by reference conservatively counts as touched; a value
                    // argument (scalar, struct) is a copy and cannot be.
                    if (arg is IdentifierExpr passed
                        && _types.RefOf(passed) is LocalSymbol { IsMutable: true } byRef
                        && (byRef.Type is ArrayOf
                            || TypeFacts.Is(byRef.Type, TypeSymbolKind.Class)))
                        _mutated.Add(byRef);
                }
                WalkExpr(c.Callee);
                foreach (var arg in c.Arguments) WalkExpr(arg);
                break;
            case MemberExpr m: WalkExpr(m.Target); break;
            case IndexExpr ix: WalkExpr(ix.Target); WalkExpr(ix.Index); break;
            case CastExpr cs: WalkExpr(cs.Operand); break;
            case RangeExpr r: WalkExpr(r.Low); WalkExpr(r.High); break;
            case ArrayLitExpr arr: foreach (var e in arr.Elements) WalkExpr(e); break;
            case TupleLitExpr tu: foreach (var e in tu.Elements) WalkExpr(e); break;
            case StructInitExpr si: foreach (var f in si.Fields) WalkExpr(f.Value); break;
            case InterpolatedStringExpr fs:
                foreach (var seg in fs.Segments) if (seg is InterpHole h) WalkExpr(h.Expr);
                break;
        }
    }
}
