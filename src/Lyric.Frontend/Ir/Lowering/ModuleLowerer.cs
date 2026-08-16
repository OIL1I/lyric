using Lyric.AST;
using Lyric.Core;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Ir.Lowering;

/// <summary>
/// The entry point of the lowering: a type-checked compilation to an <see cref="IrModule"/>.
///
/// <para>TWO PASSES. Pass 1 assigns every function to be lowered its <see cref="FunctionId"/>, pass 2
/// lowers the bodies. Without the split every forward call and every (mutual) recursion fails, because
/// the target would have no id while the call is lowered. The same solution as the two-pass
/// declaration in the resolver.</para>
///
/// <para>THE VERIFIER RUNS AS ACCEPTANCE. A finding is a bug in this lowering rather than a user
/// diagnostic, which is why <see cref="IrVerifier.VerifyOrThrow"/> throws. Always on in tests and
/// debug builds; for release builds the caller can switch it off, as LLVM's verifier is on in assert
/// builds.</para>
///
/// <para>WHAT IS SKIPPED: bodyless declarations, which have nothing to lower, and generic functions.
/// The latter need the worklist monomorphization — one instance per concrete type argument tuple,
/// starting from the roots. A call to a skipped function finds no id and reports that as
/// <c>LYR-IR0001</c> rather than silently producing wrong code.</para>
/// </summary>
public static class ModuleLowerer
{
    /// <summary>How often the downstream tables are drained in turn before the compiler gives up. Every
    /// round has to produce something new, or the loop ends anyway; the bound only catches two tables
    /// feeding each other forever.</summary>
    private const int MaxLoweringRounds = 100;

    internal static readonly Dictionary<GenericParamSymbol, LyrType> NoSubstitution =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Does the verifier run when the caller says nothing else? Yes in debug builds, no in release, as
    /// LLVM's verifier is on in assert builds.
    ///
    /// <para>Measured over 400 functions and 18,400 instructions: lowering with verification takes
    /// 30 ms, without it 2.8 ms. The check is therefore 90% of the total time, most of it in the
    /// availability data flow, which allocates hash sets per block and iterates to a fixed point.</para>
    ///
    /// <para>The risk is bounded by what the bytecode reader validates at load time
    /// (<c>LYR-BC####</c>), and only by that. This paragraph used to claim the reader checks
    /// EVERYTHING, so a lowering bug in a release build could never reach a user as silently wrong
    /// code. It did: the reader checked indices but never the type tag of an arithmetic opcode, so a
    /// compound assignment that emitted <c>add string</c> passed every release tool and evaluated to
    /// the empty string. The reader now checks the tags too. The general rule stands nonetheless —
    /// what the reader does not check, a release build does not catch, so a new invariant belongs in
    /// BOTH places or in the reader alone.</para>
    ///
    /// <para>The condition itself lives in <see cref="Pipeline.VerifiesIr"/>: it also decides which
    /// phases <c>--verbose</c> lists, and the tooling tests ask that question too.</para>
    /// </summary>
    public static bool VerifyByDefault => Pipeline.VerifiesIr;

    /// <summary>Lowers the compilation. Returns <c>null</c> when scope boundaries were reported as
    /// <c>LYR-IR0001</c>; the cause then stands in <paramref name="de"/>.</summary>
    /// <param name="verify"><c>null</c> means <see cref="VerifyByDefault"/>. Tests set the value
    /// explicitly, so their result does not depend on the build configuration.</param>
    public static IrModule? Lower(Compilation compilation, BindingResult binding, TypeResult types,
        DiagnosticEngine de, bool? verify = null)
    {
        // Receiver == null means a free function or a 'static fn'. Otherwise the type whose instance is
        // passed as parameter 0.
        var pending = new List<(FunctionDecl Decl, string Name, TypeSymbol? Receiver, TypeNode? ExtendTarget)>();
        var ids = new Dictionary<FunctionSymbol, FunctionId>(ReferenceEqualityComparer.Instance);
        var imports = new ImportTable();
        var typeTable = new TypeTable(binding) { Compilation = compilation };
        var globals = new GlobalTable();
        FunctionId? entry = null;
        var failed = false;

        // Pass 1: the function table. The order is module order then declaration order and therefore
        // deterministic; FunctionIds land as indices in the bytecode.
        foreach (var module in compilation.Modules)
        {
            foreach (var decl in compilation.AstOf(module).Declarations)
            {
                if (decl is not FunctionDecl function) continue;
                if (function.Generics.Length > 0) continue;
                if (module.Members.LookupLocal(function.Name) is not FunctionSymbol symbol) continue;

                // Bodyless in a stdlib module means a native declaration. The signature is in Lyric, the
                // implementation lives in the host and is bound by name at load time. In user code the
                // sema already rejected this as LYR-SEM0051.
                if (function.Body is null)
                {
                    if (!compilation.IsNative(module)) continue;

                    // Caught rather than thrown: a native signature with a type the lowering does not
                    // know is a scope boundary like any other, and the user should see a diagnostic with
                    // a position rather than a compiler crash.
                    try
                    {
                        var host = HostTypeResolver(module, compilation);
                        imports.Declare(symbol, new IrImport(
                        NameMangling.ForFunction(module, function.Name),
                            function.Parameters
                                .Select(p => DeclaredTypes.Lower(p.Type, host)).ToArray(),
                            DeclaredTypes.Lower(function.ReturnType, host)));
                    }
                    catch (UnsupportedConstructException ex)
                    {
                        de.Report(LoweringDiagnostics.NotSupported, Severity.Error, ex.Span,
                            ex.Message);
                        failed = true;
                    }
                    continue;
                }

                var id = new FunctionId(pending.Count);
                ids[symbol] = id;
                pending.Add((function, NameMangling.ForFunction(module, function.Name), null, null));

                // The entry contract: exactly one 'main' per executable. The sema checked that it is
                // unique; here it is only recorded.
                if (function.Name != "main") continue;

                if (function.Parameters.Length == 0) { entry = id; continue; }

                // There are two forms: 'fn main(): int' and 'fn main(args: string[]): int'. The second
                // gets its array from the runtime, which reads the form from the entry's signature; the
                // function table carries it anyway, so the format needs no flag for it.
                if (function.Parameters is [{ Type: ArrayType { Element: NamedType arg, Size: null } }]
                    && arg.Path[^1] == "string")
                {
                    entry = id;
                    continue;
                }

                de.Report(LoweringDiagnostics.NotSupported, Severity.Error, function.Span,
                    "'main' takes either no parameters or exactly one 'string[]' (Sprache.md §11)");
                failed = true;
            }

            // Methods are ordinary functions with the receiver as parameter 0, the same convention as
            // CIL. The difference between an instance and a static method is therefore the parameter list
            // alone, and the vtable only has to decide WHICH function is called, not what it looks like.
            foreach (var decl in compilation.AstOf(module).Declarations)
            {
                // Classes and enums both carry methods; for the lowering they are the same case, with the
                // receiver as parameter 0, only the member list sits elsewhere in the AST.
                var (typeName, members) = decl switch
                {
                    ClassDecl c when c.Generics.Length == 0 => (c.Name, c.Members),
                    StructDecl v when v.Generics.Length == 0 => (v.Name, v.Members),
                    EnumDecl e when e.Generics.Length == 0 => (e.Name, e.Methods.Cast<Decl>().ToArray()),
                    // The default methods of an interface are ordinary functions with the receiver as
                    // parameter 0, except that its static type is the interface itself. A 'this.foo()'
                    // inside therefore becomes a callvirt, which is right: which implementation runs is
                    // settled only at runtime. Abstract methods without a body fall through the body check
                    // below.
                    InterfaceDecl i when i.Generics.Length == 0 => (i.Name, i.Members.Cast<Decl>().ToArray()),
                    _ => (null, null),
                };
                if (typeName is null || members is null) continue;
                if (module.Members.LookupLocal(typeName) is not TypeSymbol type) continue;

                foreach (var member in members)
                {
                    if (member is not FunctionDecl method) continue;
                    if (method.Generics.Length > 0) continue;
                    if (type.Members.LookupLocal(method.Name) is not FunctionSymbol symbol) continue;

                    // A bodyless method on a HOST type is a native with the receiver as parameter 0, the
                    // same convention as for every other method, except that the implementation lives at
                    // the host. Without this case it would be silently skipped here and the call in the
                    // script would find no id.
                    if (method.Body is null)
                    {
                        if (HostTypes.NameOf(type, compilation) is not { } owner) continue;

                        try
                        {
                            var host = HostTypeResolver(module, compilation);
                            var receiver = new IrHostType(owner);
                            imports.Declare(symbol, new IrImport(
                                NameMangling.ForMethod(module, typeName, method.Name),
                                [receiver, .. method.Parameters
                                    .Select(p => DeclaredTypes.Lower(p.Type, host))],
                                DeclaredTypes.Lower(method.ReturnType, host)));
                        }
                        catch (UnsupportedConstructException ex)
                        {
                            de.Report(LoweringDiagnostics.NotSupported, Severity.Error, ex.Span,
                                ex.Message);
                            failed = true;
                        }

                        continue;
                    }

                    ids[symbol] = new FunctionId(pending.Count);
                    pending.Add((method, NameMangling.ForMethod(module, typeName, method.Name),
                        method.IsStatic ? null : type, null));
                }
            }
        }

        // extend blocks get NO ids here. An extension method is requested at its first call
        // (ExtensionTable), the same worklist shape as for lambdas and monomorphized instances and for
        // the same reason: only what is used should stand in the bytecode.

        // Globals are collected BEFORE the bodies: a function may read a constant that stands further
        // down in the source. The same two-phase shape as for the FunctionIds.
        try
        {
            globals.Collect(compilation, types, typeTable);
        }
        catch (UnsupportedConstructException ex)
        {
            de.Report(LoweringDiagnostics.NotSupported, Severity.Error, ex.Span, ex.Message);
            return null;
        }

        // Coroutine bodies come after the written functions and the initializer, lifted lambdas behind
        // them. The position IS the FunctionId, so the order has to be settled before the first body is
        // lowered: a lambda in the initializer (`let f = () => 1;`) would otherwise shift its own id.
        // All three kinds of downstream function share ONE counter: they grow simultaneously and without
        // bound, so none can reserve a range of its own.
        var nextId = new FunctionIds(pending.Count + (globals.IsEmpty ? 0 : 1));
        var coroutines = new CoroutineTable(nextId);
        var instances = new InstanceTable(nextId);
        var lambdas = new LambdaTable(nextId);
        var extensions = new ExtensionTable(nextId);
        typeTable.Extensions = extensions;

        // Pass 2: the bodies. Scope boundaries are reported rather than thrown, so the user sees all the
        // missing constructs of their program in one run rather than one per call.
        var functions = new List<IrFunction>(pending.Count);
        var reported = new HashSet<(Span Span, string Message)>();
        foreach (var (decl, name, receiver, extendTarget) in pending)
        {
            try
            {
                // A coroutine becomes TWO functions: the factory carries the written name and yields a
                // state object, the body is registered and appended at the end.
                if (CoroutineYield(decl) is { } yieldNode)
                {
                    var state = typeTable.ReserveCoroutineState(name);
                    var yieldType = typeTable.Lower(yieldNode);
                    var parameterTypes = decl.Parameters
                        .Select(p => typeTable.Lower(p.Type)).ToArray();
                    var receiverType = receiver is null ? null : typeTable.RefTo(receiver);

                    var body = coroutines.Register(decl, name, state, yieldType, receiver);
                    functions.Add(CoroutineFactory.Build(decl, name, state, yieldType, body,
                        parameterTypes, receiver is not null, receiverType, decl.Span));
                    continue;
                }

                functions.Add(new FunctionLowerer(decl, name, types, ids, imports, typeTable,
                    NoSubstitution, globals, lambdas, instances, receiver,
                    receiverTypeNode: extendTarget).Run());
            }
            catch (UnsupportedConstructException ex)
            {
                // A scope boundary in the layout of a type hits every function using it, and it should be
                // reported once: the user should see all the MISSING CONSTRUCTS of their program, not
                // every place the same one is missing.
                if (reported.Add((ex.Span, ex.Message)))
                    de.Report(LoweringDiagnostics.NotSupported, Severity.Error, ex.Span, ex.Message);
                failed = true;
            }
        }

        // A skipped function would shift the FunctionIds of the following ones, so the module build is
        // beyond saving. No partial result is returned.
        if (failed) return null;

        FunctionId? globalInit = null;
        if (!globals.IsEmpty)
        {
            try
            {
                globalInit = new FunctionId(functions.Count);
                functions.Add(GlobalInitializer.Build(globals, types, ids, imports, typeTable, lambdas, instances));
            }
            catch (UnsupportedConstructException ex)
            {
                de.Report(LoweringDiagnostics.NotSupported, Severity.Error, ex.Span, ex.Message);
                return null;
            }
        }

        // The downstream functions: coroutine bodies, monomorphized instances and lifted lambdas. Each
        // kind can request the others while being lowered, so they are drained in turn until nothing
        // more arrives, and sorted by id at the end, because the position in the list IS the id.
        var deferred = new List<(FunctionId Id, IrFunction Function)>();
        try
        {
            for (var round = 0; round < MaxLoweringRounds; round++)
            {
                var before = deferred.Count;
                deferred.AddRange(coroutines.LowerAll(types, ids, imports, typeTable, globals,
                    lambdas, instances));
                deferred.AddRange(instances.LowerAll(types, ids, imports, typeTable, globals, lambdas));
                deferred.AddRange(lambdas.LowerAll(types, ids, imports, typeTable, globals, instances));
                deferred.AddRange(extensions.LowerAll(types, ids, imports, typeTable, globals,
                    lambdas, instances));
                if (deferred.Count == before) break;
            }
        }
        catch (UnsupportedConstructException ex)
        {
            de.Report(LoweringDiagnostics.NotSupported, Severity.Error, ex.Span, ex.Message);
            return null;
        }

        functions.AddRange(deferred.OrderBy(entry => entry.Id.Value).Select(entry => entry.Function));

        // The vtable rows FIRST, because they can request an extension nobody has called yet:
        // 'extend A :: [I]' is needed as soon as an A lands in an I slot, even when the method appears
        // nowhere directly in the source.
        var impls = BuildImpls(typeTable, binding, compilation, ids, extensions, instances,
            de, ref failed);
        if (failed) return null;

        var late = new List<(FunctionId Id, IrFunction Function)>();
        try
        {
            for (var round = 0; round < MaxLoweringRounds; round++)
            {
                var before = late.Count;
                // All three, not only two: a vtable row for a generic instance requests its method
                // (ListIterator<int>.next), and that arises only through the monomorphization. With
                // 'instances' missing here, the row points at a FunctionId nobody filled, which the
                // verifier reports as "targets f7, which is out of range".
                late.AddRange(instances.LowerAll(types, ids, imports, typeTable, globals, lambdas));
                late.AddRange(extensions.LowerAll(types, ids, imports, typeTable, globals,
                    lambdas, instances));
                late.AddRange(lambdas.LowerAll(types, ids, imports, typeTable, globals, instances));
                if (late.Count == before) break;
            }
        }
        catch (UnsupportedConstructException ex)
        {
            de.Report(LoweringDiagnostics.NotSupported, Severity.Error, ex.Span, ex.Message);
            return null;
        }

        functions.AddRange(late.OrderBy(entry => entry.Id.Value).Select(entry => entry.Function));

        // Types are collected after the lowering rather than before: the table contains only what was
        // actually used — a declared but never instantiated class does not belong in the bytecode. The
        // same rule as for the imports.
        var result = new IrModule(functions)
        {
            EntryFunction = entry, Imports = imports.Used, Types = typeTable.Defs,
            Globals = globals.Defs, GlobalInit = globalInit,
            Capabilities = RequiredCapabilities(compilation),
            Impls = impls,
        };
        if (failed) return null;

        // BEFORE the verifier: what gets deleted does not need checking, and the verifier runs again at
        // load time anyway, so this is the one place where the saving counts twice.
        Reachability.Prune(result);

        if (verify ?? VerifyByDefault) IrVerifier.VerifyOrThrow(result);
        return result;
    }

    /// <summary>Recognises a host type in the signature of a native declaration; the rule itself lives
    /// in <see cref="HostTypes"/>, because the same question is asked at the call site.</summary>
    private static Func<TypeNode, string?> HostTypeResolver(ModuleSymbol module,
        Compilation compilation) => node =>
        node is NamedType { Path.Length: 1, TypeArguments.Length: 0 } named
            ? HostTypes.NameOf(module.Members.LookupLocal(named.Path[0]) as TypeSymbol, compilation)
            : null;

    /// <summary>
    /// What capabilities this program requires: the union over all loaded modules.
    ///
    /// <para>What counts is LOADED, not IMPORTED: a module importing <c>std.os</c> pulls it into the
    /// compilation, and its requirement belongs to the program, even when the main file never names it.
    /// Counting only the import lines of the root would leave a gap exactly one indirection deep.</para>
    /// </summary>
    private static Capability RequiredCapabilities(Compilation compilation)
    {
        var needed = Capability.None;
        foreach (var module in compilation.Modules)
            needed |= CapabilityTable.RequiredForImport(module.FullName);
        return needed;
    }

    /// <summary>
    /// The vtable rows: for every interned class and every interned interface it implements, the target
    /// function slot by slot.
    ///
    /// <para>AFTER the lowering, because only then is it settled which types reach the bytecode at all —
    /// the same rule as for types and imports. Interfaces are already interned by then, because every
    /// <c>mkiface</c> and <c>callvirt</c> needed their id while lowering.</para>
    ///
    /// <para>THE RESOLUTION ORDER IS DECIDED HERE, NOT AT RUNTIME: own member before interface default.
    /// The dispatch therefore finds a finished function index and has to search for nothing.</para>
    ///
    /// <para>Sorted deterministically: the rows land as a section in the bytecode, and the same input
    /// has to give byte-identical output. The enumeration order of a dictionary does not do that.</para>
    /// </summary>
    private static List<IrImpl> BuildImpls(TypeTable typeTable, BindingResult binding,
        Compilation compilation, Dictionary<FunctionSymbol, FunctionId> ids,
        ExtensionTable extensions, InstanceTable instances, DiagnosticEngine de, ref bool failed)
    {
        var impls = new List<IrImpl>();
        var interned = typeTable.Interned.ToList();
        var interfaces = interned.Where(t => t.Symbol.Kind == TypeSymbolKind.Interface).ToList();
        if (interfaces.Count == 0) return impls;

        foreach (var (type, typeId) in interned
                     .Where(t => t.Symbol.Kind is TypeSymbolKind.Class or TypeSymbolKind.Struct
                                 or TypeSymbolKind.Enum)
                     .OrderBy(t => t.Id.Value))
        {
            foreach (var (iface, ifaceId) in interfaces.OrderBy(t => t.Id.Value))
            {
                // Conformance may be declared OR come from an 'extend T :: [I]'. The vtable row is the
                // same; which of the two established it is no longer distinguishable at runtime.
                var viaExtension = ExtendBlocksFor(compilation, type, iface, binding);
                if (!Conformance.Implements(type, iface, binding) && viaExtension.Count == 0)
                    continue;

                var slots = typeTable.MethodSlotsOf(ifaceId);
                var methods = new FunctionId[slots.Length];
                var complete = true;

                for (var i = 0; i < slots.Length; i++)
                {
                    // The order is: own member, then extension, then the interface's default. An
                    // extension method does NOT stand in 'type.Members' — it belongs to the extend block,
                    // not to the target type.
                    //
                    // For a generic instance the method belongs to the INSTANCE rather than to the
                    // definition: 'ListIterator<int>.next' arises only through the monomorphization, and
                    // the definition has no lowerable version.
                    var target = ResolveInInstance(typeTable, typeId, slots[i], instances)
                                 ?? Resolve(type, slots[i], ids)
                                 ?? ResolveInExtensions(viaExtension, slots[i], extensions)
                                 ?? Resolve(iface, slots[i], ids);
                    if (target is { } id) { methods[i] = id; continue; }

                    // The sema already checked conformance. If something is missing here all the same, it
                    // is a lowering gap — a generic or bodyless implementation pass 1 skipped.
                    de.Report(LoweringDiagnostics.NotSupported, Severity.Error,
                        type.Declaration?.Span ?? default,
                        $"'{type.Name}' implements '{iface.Name}', but its '{slots[i]}' is not "
                        + "lowerable by this compiler version yet (generic or bodiless)");
                    complete = false;
                    break;
                }

                if (complete) impls.Add(new IrImpl(typeId, ifaceId, methods));
                else failed = true;
            }
        }

        return impls;
    }

    /// <summary>
    /// Is this a coroutine, and what does it yield? The type stands there syntactically:
    /// <c>Coroutine&lt;T&gt;</c> is a built-in type rather than a library class.
    /// </summary>
    internal static TypeNode? CoroutineYield(FunctionDecl decl) =>
        decl.ReturnType is NamedType { TypeArguments.Length: 1 } named
        && named.Path[^1] == "Coroutine"
            ? named.TypeArguments[0]
            : null;

    /// <summary>The visible <c>extend T :: [I]</c> blocks that establish exactly this conformance. Empty
    /// means that if it holds at all, it is declared.</summary>
    private static List<ExtensionBlock> ExtendBlocksFor(Compilation compilation, TypeSymbol type,
        TypeSymbol iface, BindingResult binding)
    {
        var found = new List<ExtensionBlock>();
        foreach (var block in compilation.Extensions.Blocks)
        {
            if (!ReferenceEquals(block.Target, type)) continue;
            foreach (var node in block.Decl.Interfaces)
                if (ReferenceEquals(Conformance.InterfaceOf(node, binding), iface))
                {
                    found.Add(block);
                    break;
                }
        }
        return found;
    }

    /// <summary>The method of a generic instance, requested through the monomorphization. <c>null</c>
    /// when the type is not generic or does not have the method.</summary>
    private static FunctionId? ResolveInInstance(TypeTable typeTable, TypeId typeId, string method,
        InstanceTable instances)
    {
        if (typeTable.InstanceOf(typeId) is not { } instance) return null;
        if (instance.Definition.Members.LookupLocal(method) is not FunctionSymbol symbol) return null;
        if (symbol.Declaration is not FunctionDecl declaration || declaration.Body is null) return null;

        return instances.RequestMethod(symbol, declaration, instance, default);
    }

    private static FunctionId? ResolveInExtensions(List<ExtensionBlock> blocks, string method,
        ExtensionTable extensions)
    {
        foreach (var block in blocks)
        {
            if (block.MethodScope.LookupLocal(method) is not FunctionSymbol symbol) continue;
            if (symbol.Declaration is not FunctionDecl decl || decl.Body is null) continue;
            if (block.Target is not { } target) continue;

            // requests it if that has not happened yet: a vtable row is a use
            return extensions.Request(symbol, decl, block.Module, target.Name,
                decl.IsStatic ? null : target, decl.IsStatic ? null : block.Decl.Target);
        }
        return null;
    }

    private static FunctionId? Resolve(TypeSymbol owner, string method,
        Dictionary<FunctionSymbol, FunctionId> ids) =>
        owner.Members.LookupLocal(method) is FunctionSymbol symbol
        && ids.TryGetValue(symbol, out var id)
            ? id
            : null;
}
