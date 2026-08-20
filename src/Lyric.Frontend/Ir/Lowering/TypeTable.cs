using Lyric.AST;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Ir.Lowering;

/// <summary>
/// Assigns a <see cref="TypeId"/> to every lowered <c>class</c> and builds its layout.
///
/// <para>INTERNED ON DEMAND, as in <see cref="ImportTable"/>: a declared but never used class does not
/// belong in the bytecode's type table. The order follows the lowering order and is therefore
/// deterministic.</para>
///
/// <para>THE ID IS ASSIGNED BEFORE THE LAYOUT. That is the whole trick making
/// <c>class Node { next: Node }</c> possible: on entry the place is reserved and the id recorded, and
/// only then are the field types lowered. A recursive reference then already finds the id and
/// terminates rather than interning itself again. The same two-phase shape as the function lowering,
/// and for the same reason.</para>
///
/// <para>FIELD ORDER COMES FROM THE AST, NOT FROM THE SYMBOL TABLE. The field index is the slot in the
/// object — it has to be declaration order, and only the AST list guarantees that. A symbol table is a
/// map, and relying on its enumeration order would hang a layout on an implementation detail.</para>
/// </summary>
internal sealed class TypeTable
{
    private readonly Dictionary<TypeSymbol, TypeId> _assigned = new(ReferenceEqualityComparer.Instance);
    private readonly List<IrTypeDef> _defs = new();
    private readonly Dictionary<TypeSymbol, UnsupportedConstructException> _failed =
        new(ReferenceEqualityComparer.Instance);
    /// <summary>The variant names per enum entry, for the lowering only; the bytecode holds the index.
    /// </summary>
    private readonly Dictionary<int, string[]> _variantNames = new();
    private readonly BindingResult _binding;

    /// <param name="binding">The resolver has already bound every <c>NamedType</c> to its symbol. Using
    /// this table instead of resolving names again is not convenience: a second resolution would be a
    /// second truth about visibility and shadowing.</param>
    public TypeTable(BindingResult binding) => _binding = binding;

    /// <summary>The compilation, when the lowerer passes it through. Needed for two questions that
    /// cannot be answered without it: which symbol a builtin type has, and which <c>extend</c> blocks are
    /// visible.</summary>
    /// <summary>The name binding, for the conformance questions the lowering asks.</summary>
    public BindingResult Binding => _binding;

    public Compilation? Compilation { get; init; }

    /// <summary>The worklist of used extension methods. It hangs here rather than being threaded through
    /// every lowerer: EVERY one has the TypeTable anyway, and the alternative would be an extra parameter
    /// on four tables (instances, lambdas, coroutines, extensions themselves) — four opportunities to
    /// forget it in one place.</summary>
    public ExtensionTable? Extensions { get; set; }

    /// <summary>The symbol behind a primitive type (<c>int</c>, <c>string</c>, …), the anchor an
    /// <c>extend int { … }</c> hangs on. Primitives have no symbol in <see cref="TypeFacts.SymbolOf"/>,
    /// and that is deliberate: on it hangs the boundary that a scalar does NOT fit into an interface
    /// slot, which would need boxing.</summary>
    public TypeSymbol? BuiltinSymbolOf(LyrType type) =>
        type is PrimitiveType prim && Compilation is { } comp
            ? comp.Builtins.LookupLocal(TypeFacts.Display(prim)) as TypeSymbol
            : null;

    /// <summary>The <c>extend</c> block this method symbol belongs to, with the target name and the
    /// declaring module, the two facts the mangling needs. <c>null</c> when the symbol is not an
    /// extension method.</summary>
    public (ModuleSymbol Module, string TargetName, TypeSymbol Target, TypeNode TargetNode)?
        ExtensionOwnerOf(FunctionSymbol symbol)
    {
        if (Compilation is not { } comp) return null;
        foreach (var block in comp.Extensions.Blocks)
        {
            if (block.Target is not { } target) continue;
            foreach (var method in block.Methods)
                if (ReferenceEquals(method, symbol))
                    return (block.Module, target.Name, target, block.Decl.Target);
        }
        return null;
    }

    /// <summary>An extension method of this name on this type, across all visible <c>extend</c> blocks.
    /// </summary>
    public FunctionSymbol? ExtensionMethod(TypeSymbol target, string member)
    {
        if (Compilation is not { } comp) return null;
        foreach (var block in comp.Extensions.Blocks)
        {
            if (!ReferenceEquals(block.Target, target)) continue;
            if (block.MethodScope.LookupLocal(member) is FunctionSymbol found) return found;
        }
        return null;
    }

    public List<IrTypeDef> Defs => _defs;

    /// <summary>The interface behind a constraint (<c>T :: [P]</c>). The lowerer needs it to find a
    /// default method the concrete type does not have itself; the resolution belongs here, because the
    /// binding lives here.</summary>
    public TypeSymbol? ConstraintInterface(TypeNode node) => Conformance.InterfaceOf(node, _binding);

    /// <summary>The interface <paramref name="ts"/> inherits a member named <paramref name="member"/>
    /// from, or <c>null</c> when none has it.
    ///
    /// <para>Needed for default methods: such a method belongs to the INTERFACE, its <c>this</c> is the
    /// interface type, and no direct call leads there. The receiver has to be lifted first. This is
    /// called only when the concrete type does NOT have the member itself, since an own member beats a
    /// default.</para></summary>
    public TypeSymbol? InterfaceProviding(TypeSymbol ts, string member)
    {
        foreach (var iface in Conformance.DeclaredInterfaces(ts, _binding))
            if (iface.Members.LookupLocal(member) is FunctionSymbol)
                return iface;
        return null;
    }

    /// <summary>One cell per element type: <c>&lt;cell:int&gt;</c> exists exactly once, however many
    /// variables live in it.</summary>
    private readonly List<(IrType Element, TypeId Id)> _cells = new();

    /// <summary>
    /// Instances of generic types, under their full name (<c>Box&lt;int&gt;</c>).
    ///
    /// <para>Separate from <see cref="_assigned"/>, where the SYMBOL is the key: <c>Box&lt;int&gt;</c>
    /// and <c>Box&lt;string&gt;</c> share one and are still two types with different layouts.</para>
    /// </summary>
    private readonly Dictionary<string, TypeId> _instances = new(StringComparer.Ordinal);

    /// <summary>The symbol and id of every instance, for the impl table, which has to know which class
    /// satisfies which interface even when both are instances.</summary>
    /// <summary>Every interned generic instance with its type arguments.
    ///
    /// <para>The arguments stand there because the impl table needs them: a vtable row for
    /// <c>ListIterator&lt;int&gt;</c> has to record the method of the INSTANCE, and that arises only on
    /// request during the monomorphization. Without them, which instance was meant could not be computed
    /// back from the TypeId.</para></summary>
    private readonly List<(TypeSymbol Symbol, TypeId Id, LyrType[] Arguments)> _instanceSymbols = new();

    /// <summary>
    /// The type arguments currently being substituted while the layout of an instance is built.
    ///
    /// <para>A stack, because layouts nest: <c>Box&lt;Pair&lt;int&gt;&gt;</c> lowers the field
    /// <c>v: T</c> to <c>Pair&lt;int&gt;</c>, and its fields then need ITS substitution rather than that
    /// of <c>Box</c>.</para>
    /// </summary>
    private readonly Stack<IReadOnlyDictionary<string, LyrType>> _substitutions = new();

    /// <summary>One tuple layout per element sequence: <c>(int, int)</c> exists exactly once.</summary>
    private readonly List<(IrType[] Elements, TypeId Id)> _tuples = new();

    /// <summary>
    /// The type of a tuple: an object with one field per element.
    ///
    /// <para>No IR type and no opcode of its own, the same decision as for cells and closure
    /// environments. A tuple IS an object with N fields, so <c>newobj</c> and <c>ldfld</c> do it; the
    /// verifier checks it like any other object, and the bytecode format stays unchanged.</para>
    ///
    /// <para>REFERENCE RATHER THAN VALUE SEMANTICS, and that is not observable: a tuple is immutable.
    /// There is no element access and therefore no assignment to an element — the only way in is
    /// destructuring, and that reads. "Copying" is thus indistinguishable from "sharing", and the copy
    /// would only be more expensive.</para>
    ///
    /// <para>Interned, because two tuples of the same shape have the same layout. The field names are
    /// the positions and appear only in disassembly and diagnostics.</para>
    /// </summary>
    public IrRefType TupleOf(IrType[] elements)
    {
        foreach (var (existing, id) in _tuples)
            if (existing.Length == elements.Length
                && existing.Zip(elements).All(pair => IrType.Equal(pair.First, pair.Second)))
                return new IrRefType(id);

        var fresh = new TypeId(_defs.Count);
        var names = new string[elements.Length];
        for (var i = 0; i < names.Length; i++)
            names[i] = i.ToString(System.Globalization.CultureInfo.InvariantCulture);

        _defs.Add(new IrTypeDef("<tuple>", elements, names));
        _tuples.Add((elements, fresh));
        return new IrRefType(fresh);
    }

    /// <summary>Is this type a cell? Asked when reading a capture: a captured cell transports a variable,
    /// and what the program wants to see is its content.</summary>
    public bool IsCell(TypeId id) => _cells.Any(c => c.Id == id);

    /// <summary>
    /// The type a captured <c>var</c> lives in: an object with ONE field.
    ///
    /// <para>Deliberately no IR type and no opcode of its own. A cell is an object, so <c>newobj</c>,
    /// <c>ldfld 0</c> and <c>stfld 0</c> do it — the verifier checks it like any other object, the
    /// disassembler shows it without a special case, and the bytecode format stays unchanged. A
    /// <c>newcell</c>/<c>ldcell</c>/<c>stcell</c> trio would be a second mechanism for "field of an
    /// object".</para>
    ///
    /// <para>Interned, because two cells of the same element type are indistinguishable. That keeps the
    /// type table small when a function has several <c>var</c> captures.</para>
    /// </summary>
    public IrRefType CellOf(IrType element)
    {
        foreach (var (existing, id) in _cells)
            if (IrType.Equal(existing, element)) return new IrRefType(id);

        var fresh = new TypeId(_defs.Count);
        _defs.Add(new IrTypeDef($"<cell>", [element], ["value"]));
        _cells.Add((element, fresh));
        return new IrRefType(fresh);
    }

    /// <summary>
    /// Reserves the state type of a coroutine, WITHOUT a layout.
    ///
    /// <para>The same two-phase shape as for a recursive class, and for the same reason: the id has to
    /// stand before the layout is known. Which locals survive a <c>yield</c> is known only once the body
    /// is lowered, and the body needs the id at its first field access.</para>
    ///
    /// <para>Slot 0 is the RE-ENTRY POINT: 0 means "not started yet", n the block behind the nth
    /// <c>yield</c>, -1 "ran through". Parameters and locals follow.</para>
    /// </summary>
    public TypeId ReserveCoroutineState(string name)
    {
        var id = new TypeId(_defs.Count);
        _defs.Add(new IrTypeDef($"<coro:{name}>", [], []));
        return id;
    }

    /// <summary>Supplies the layout once the body is lowered.</summary>
    public void CompleteCoroutineState(TypeId id, IrType[] fieldTypes, string[] fieldNames) =>
        _defs[id.Value] = _defs[id.Value] with { FieldTypes = fieldTypes, FieldNames = fieldNames };

    /// <summary>
    /// The type of a closure's environment: an object whose fields are the captured values.
    ///
    /// <para>NOT interned, unlike a cell: two lambdas with identically shaped captures still capture
    /// different variables, and sharing their environments would have no use — there are never two
    /// instances of the same environment type that could be saved.</para>
    ///
    /// <para>The name appears in disassembly and diagnostics and therefore carries the name of the
    /// function the lambda belongs to.</para>
    /// </summary>
    public IrRefType EnvironmentFor(string lambdaName, IrType[] fieldTypes, string[] fieldNames)
    {
        var id = new TypeId(_defs.Count);
        _defs.Add(new IrTypeDef($"<env:{lambdaName}>", fieldTypes, fieldNames));
        return new IrRefType(id);
    }

    /// <summary>The type of a value of this class: a reference, not an embedded value.</summary>
    public IrRefType RefTo(TypeSymbol symbol) => new(Intern(symbol));

    /// <summary>The type of an enum value: a reference to the enum entry rather than to a variant. Which
    /// variant is present stands in its slot 0 at runtime.</summary>
    public IrEnumType EnumOf(TypeSymbol symbol) => new(Intern(symbol));

    /// <summary>The type of a value addressed through an interface.</summary>
    public IrInterfaceType InterfaceOf(TypeSymbol symbol) => new(Intern(symbol));

    /// <summary>The type of a <c>struct</c> value: the same layout as a class, but value
    /// semantics.</summary>
    public IrStructType StructOf(TypeSymbol symbol) => new(Intern(symbol));

    /// <summary>Is this table entry a value type? The lowering asks to decide whether a
    /// <c>structcopy</c> is needed at a binding point.</summary>
    public bool IsStruct(TypeId id) => _defs[id.Value].IsStruct;

    /// <summary>
    /// The method slot of an interface. The INDEX is the contract, not the name: it is fixed at compile
    /// time, because Lyric is statically typed and has no monkey patching, exactly like the field index
    /// of a class.
    /// </summary>
    public int SlotOf(TypeSymbol interfaceSymbol, string method, Core.Span span)
    {
        var def = _defs[Intern(interfaceSymbol).Value];
        var index = Array.IndexOf(def.MethodSlots, method);
        if (index >= 0) return index;

        throw new UnsupportedConstructException(
            $"interface '{interfaceSymbol.Name}' has no method '{method}'", span);
    }

    /// <summary>The slot names of an interface, in declaration order. The <see cref="ModuleLowerer"/>
    /// needs them to fill the vtable rows.</summary>
    public string[] MethodSlotsOf(TypeId id) => _defs[id.Value].MethodSlots;

    /// <summary>All types interned so far with their symbol, the basis of the impl table. Only what was
    /// interned stands in the bytecode, and only that needs vtable rows.</summary>
    public IEnumerable<(TypeSymbol Symbol, TypeId Id)> Interned =>
        _assigned.Select(pair => (pair.Key, pair.Value))
            .Concat(_instanceSymbols.Select(entry => (entry.Symbol, entry.Id)));

    /// <summary>The generic instance behind a TypeId, or <c>null</c> when the type is not generic. The
    /// impl table needs it to record the method of the instance rather than of the definition.</summary>
    public GenericInstance? InstanceOf(TypeId id)
    {
        foreach (var entry in _instanceSymbols)
            if (entry.Id.Value == id.Value && entry.Arguments.Length > 0)
                return new GenericInstance(entry.Symbol, entry.Arguments);
        return null;
    }

    public bool IsInterface(TypeId id) => _defs[id.Value].IsInterface;

    /// <summary>
    /// The Types index of a variant. A variant is a layout entry of its own, and its slot 0 is the tag.
    ///
    /// <para>THE ENTRY COMES IN, IT IS NOT DETERMINED HERE. For a generic enum the variant hangs on the
    /// INSTANCE — <c>Opt&lt;int&gt;.Some</c> and <c>Opt&lt;string&gt;.Some</c> are different layouts. If
    /// this method determined the entry from the symbol itself, it would decide a second time which
    /// instance was meant, and without knowing the type arguments.</para>
    /// </summary>
    public TypeId VariantOf(TypeId enumId, string variantName, Core.Span span)
    {
        var index = Array.IndexOf(_variantNames[enumId.Value], variantName);
        if (index >= 0) return _defs[enumId.Value].Variants[index];

        throw new UnsupportedConstructException(
            $"'{_defs[enumId.Value].Name}' has no variant '{variantName}'", span);
    }

    /// <summary>The tag number of a variant: its index in declaration order. It is the same for all
    /// instances, because it comes from the declaration; the ENTRY is not.</summary>
    public int TagOf(TypeId enumId, string variantName, Core.Span span)
    {
        var index = Array.IndexOf(_variantNames[enumId.Value], variantName);
        if (index >= 0) return index;

        throw new UnsupportedConstructException(
            $"'{_defs[enumId.Value].Name}' has no variant '{variantName}'", span);
    }

    public TypeId Intern(TypeSymbol symbol) => Intern(symbol, []);

    /// <summary>
    /// The <see cref="TypeId"/> of a type; for a generic one that of its INSTANCE for exactly these type
    /// arguments.
    ///
    /// <para><c>Box&lt;int&gt;</c> and <c>Box&lt;string&gt;</c> get different entries with their own
    /// layout. That is the same monomorphization as for functions and for the same reason: the VM knows
    /// no types at runtime, so a field layout has to be settled at compile time.</para>
    /// </summary>
    public TypeId Intern(TypeSymbol symbol, IReadOnlyList<LyrType> typeArguments)
    {
        if (symbol.Generics.Length > 0)
        {
            if (typeArguments.Count != symbol.Generics.Length)
                throw new UnsupportedConstructException(
                    $"generic type '{symbol.Name}' needs {symbol.Generics.Length} type "
                    + $"argument(s), got {typeArguments.Count}", SpanOf(symbol));

            var instanceName =
                $"{symbol.Name}<{string.Join(", ", typeArguments.Select(TypeFacts.Display))}>";
            if (_instances.TryGetValue(instanceName, out var known)) return known;

            var mapping = new Dictionary<string, LyrType>(StringComparer.Ordinal);
            for (var i = 0; i < symbol.Generics.Length; i++)
                mapping[symbol.Generics[i].Name] = typeArguments[i];

            _substitutions.Push(mapping);
            try
            {
                // Interfaces and enums have entry forms of their own — method slots and variants — and no
                // field layout. They therefore take their own paths; only the substitution is shared.
                if (symbol.Kind == TypeSymbolKind.Interface)
                {
                    var id = InternInterface(symbol, instanceName);
                    _instances[instanceName] = id;
                    _instanceSymbols.Add((symbol, id, typeArguments.ToArray()));
                    return id;
                }

                if (symbol.Kind == TypeSymbolKind.Enum)
                    return InternEnum(symbol, instanceName, _instances, typeArguments.ToArray());

                return InternLayout(symbol, instanceName, _instances,
                    typeArguments.ToArray());
            }
            finally
            {
                _substitutions.Pop();
            }
        }

        return InternNonGeneric(symbol);
    }

    private TypeId InternNonGeneric(TypeSymbol symbol)
    {
        // A type whose layout has failed once fails again, with the same message. Without this the
        // placeholder from the first attempt would remain, and the second caller would read a layout with
        // FieldNames == null: a NullReferenceException in the compiler instead of a diagnostic.
        if (_failed.TryGetValue(symbol, out var failure))
            throw new UnsupportedConstructException(failure.Message, failure.Span);

        if (_assigned.TryGetValue(symbol, out var existing)) return existing;

        if (symbol.Kind == TypeSymbolKind.Enum) return InternEnum(symbol);
        if (symbol.Kind == TypeSymbolKind.Interface) return InternInterface(symbol);

        if (symbol.Kind is not (TypeSymbolKind.Class or TypeSymbolKind.Struct))
            throw new UnsupportedConstructException(
                $"type '{symbol.Name}' ({Describe(symbol.Kind)}) is not supported by this compiler version yet",
                SpanOf(symbol));


        // Class and struct share the entire layout procedure; they differ solely in binding semantics,
        // and that lives in the lowering rather than here.
        var members = symbol.Declaration switch
        {
            ClassDecl c => c.Members,
            StructDecl v => v.Members,
            _ => null,
        };

        if (members is null)
            throw new UnsupportedConstructException(
                $"type '{symbol.Name}' has no declaration to read a layout from",
                SpanOf(symbol));

        return InternLayout(symbol, symbol.Name, null);
    }

    /// <summary>
    /// Builds the layout and records it. For an instance <paramref name="registry"/> is the instance map
    /// and <paramref name="name"/> carries the type arguments; otherwise the symbol counts.
    /// </summary>
    /// <param name="instanceArguments">The type arguments when this is a generic instance. They are
    /// carried along, because the impl table has to compute back from a TypeId which instance was meant:
    /// the vtable row records the method of the INSTANCE.</param>
    private TypeId InternLayout(TypeSymbol symbol, string name, Dictionary<string, TypeId>? registry,
        LyrType[]? instanceArguments = null)
    {
        var members = symbol.Declaration switch
        {
            ClassDecl c => c.Members,
            StructDecl v => v.Members,
            _ => null,
        };

        if (members is null)
            throw new UnsupportedConstructException(
                $"type '{symbol.Name}' has no declaration to read a layout from", SpanOf(symbol));

        // Reserve the place AND record the id before the field types are lowered — see the class
        // documentation. The placeholder is overwritten below and never becomes visible, because
        // Lower(field) needs only the id, not the layout.
        var id = new TypeId(_defs.Count);
        if (registry is null) _assigned[symbol] = id;
        else { registry[name] = id; _instanceSymbols.Add((symbol, id, instanceArguments ?? [])); }
        _defs.Add(default);

        try
        {
            var fields = members.OfType<FieldDecl>().ToArray();
            var names = new string[fields.Length];
            var types = new IrType[fields.Length];

            for (var i = 0; i < fields.Length; i++)
            {
                // A field default does NOT belong in the layout: it is an expression rather than a type
                // and is evaluated at the construction site, where the explicitly given values arise too.
                // Nothing of it stands in the bytecode.
                names[i] = fields[i].Name;
                types[i] = Lower(fields[i].Type, fields[i].Span);
            }

            _defs[id.Value] = new IrTypeDef(name, types, names)
            {
                IsStruct = symbol.Kind == TypeSymbolKind.Struct,
            };
            return id;
        }
        catch (UnsupportedConstructException ex)
        {
            // The id is NOT given back: in the meantime a field type may have interned further types
            // whose ids would otherwise shift. The failure is remembered instead — the module is discarded
            // anyway, and the table only has to stay consistent until every function has reported.
            _failed[symbol] = ex;
            throw;
        }
    }

    /// <summary>
    /// An enum becomes ONE enum entry plus ONE LAYOUT ENTRY PER VARIANT. Slot 0 of every variant is its
    /// tag; the payload fields follow from slot 1.
    ///
    /// <para>As with a class the id is assigned BEFORE the variants — an enum may name itself through a
    /// variant (<c>enum Tree { Leaf, Node(Tree, Tree) }</c>), and without the id up front that would run
    /// into an infinite loop.</para>
    /// </summary>
    private TypeId InternEnum(TypeSymbol symbol) => InternEnum(symbol, symbol.Name, null, null);

    /// <param name="name">For an instance the full name (<c>Opt&lt;int&gt;</c>). It appears in
    /// disassembly and diagnostics, and the variants inherit it.</param>
    /// <param name="registry">For an instance the instance map, otherwise <c>null</c>. As in
    /// <see cref="InternLayout"/>: a generic enum must NOT record itself under its symbol, or
    /// <c>Opt&lt;string&gt;</c> would get the id of <c>Opt&lt;int&gt;</c> and with it its variant layouts,
    /// meaning an <c>i64</c> slot for a string.</param>
    private TypeId InternEnum(TypeSymbol symbol, string name, Dictionary<string, TypeId>? registry,
        LyrType[]? instanceArguments)
    {
        if (symbol.Declaration is not EnumDecl decl)
            throw new UnsupportedConstructException(
                $"enum '{symbol.Name}' has no declaration to read its variants from", SpanOf(symbol));

        // Record the id BEFORE the variants are interned — an enum may name itself through a variant
        // ('enum Tree<T> { Leaf, Node(Tree<T>, Tree<T>) }'), and without recording it up front that would
        // run into an infinite loop. For an instance it has to go into the REGISTRY, because the
        // recursion comes back through the instance name rather than through the symbol.
        var id = new TypeId(_defs.Count);
        if (registry is null) _assigned[symbol] = id;
        else { registry[name] = id; _instanceSymbols.Add((symbol, id, instanceArguments ?? [])); }
        _defs.Add(default);
        _variantNames[id.Value] = decl.Variants.Select(v => v.Name).ToArray();

        try
        {
            var variants = new TypeId[decl.Variants.Length];
            for (var i = 0; i < decl.Variants.Length; i++)
                variants[i] = InternVariant(name, decl.Variants[i]);

            _defs[id.Value] = new IrTypeDef(name, [], []) { Variants = variants };
            return id;
        }
        catch (UnsupportedConstructException ex)
        {
            _failed[symbol] = ex;
            throw;
        }
    }

    /// <summary>
    /// An interface becomes an entry WITHOUT FIELDS that only names its method slots. The order comes
    /// from the declaration rather than from the symbol table: the slot is a contract, the enumeration
    /// order of a map is an implementation detail — the same rule as for the fields of a class.
    ///
    /// <para>ALL declared methods are taken in, abstract and default alike. A default occupies a slot,
    /// because a class may override it; without a slot it would not be overridable.</para>
    /// </summary>
    private TypeId InternInterface(TypeSymbol symbol) => InternInterface(symbol, symbol.Name);

    /// <param name="name">For an instance the full name (<c>Iterator&lt;int&gt;</c>): it appears in
    /// disassembly and diagnostics, and two instances of the same interface should be distinguishable
    /// there.</param>
    private TypeId InternInterface(TypeSymbol symbol, string name)
    {
        if (symbol.Declaration is not InterfaceDecl decl)
            throw new UnsupportedConstructException(
                $"interface '{symbol.Name}' has no declaration to read its methods from",
                SpanOf(symbol));

        var slots = SlotNames(symbol, decl);
        if (slots.Length == 0)
            throw new UnsupportedConstructException(
                $"interface '{symbol.Name}' declares no methods; an empty interface has nothing "
                + "to dispatch on", SpanOf(symbol));

        var id = new TypeId(_defs.Count);

        // A generic interface has an entry per instance; only a non-generic one records itself under its
        // symbol. Otherwise 'Iterator<string>' would get the id of 'Iterator<int>'.
        if (symbol.Generics.Length == 0) _assigned[symbol] = id;

        _defs.Add(new IrTypeDef(name, [], []) { MethodSlots = slots });
        return id;
    }

    /// <summary>Chain-prefix layout: the parent's slots first, own members after. A parent's
    /// default method runs with a CHILD's method table behind <c>this</c>; the prefix keeps the
    /// parent's slot indexes valid there — the reason the parent list holds at most one entry.
    /// Names are unique along a chain (LYR-SEM0079), so the name-to-index lookup stays exact.</summary>
    private string[] SlotNames(TypeSymbol symbol, InterfaceDecl decl)
    {
        var own = decl.Members.Select(m => m.Name);
        if (Conformance.ParentsOf(symbol, _binding).FirstOrDefault() is
            { Declaration: InterfaceDecl parentDecl } parent)
            return [.. SlotNames(parent, parentDecl), .. own];
        return own.ToArray();
    }

    private TypeId InternVariant(string ownerName, EnumVariant variant)
    {
        // Slot 0 is the tag. It stands in the layout so the field access after an 'enumas' stays an
        // ordinary ldfld: the variant is then a perfectly normal class.
        var names = new List<string> { "$tag" };
        var types = new List<IrType> { new IrScalarType(IrScalar.I64) };

        if (variant.TupleFields is { } tuple)
            for (var i = 0; i < tuple.Length; i++)
            {
                names.Add(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                types.Add(Lower(tuple[i], tuple[i].Span));
            }
        else if (variant.StructFields is { } fields)
            foreach (var field in fields)
            {
                if (field.Default is not null)
                    throw new UnsupportedConstructException(
                        "a field default is not supported by this compiler version yet", field.Span);
                names.Add(field.Name);
                types.Add(Lower(field.Type, field.Span));
            }

        var id = new TypeId(_defs.Count);
        _defs.Add(new IrTypeDef($"{ownerName}.{variant.Name}", types.ToArray(), names.ToArray()));
        return id;
    }

    /// <summary>Finds the field index. The name exists here and in diagnostics only; the bytecode holds
    /// the position alone.</summary>
    public FieldId FieldOf(TypeSymbol symbol, string name, Core.Span span)
    {
        var def = _defs[Intern(symbol).Value];
        var index = Array.IndexOf(def.FieldNames, name);
        if (index >= 0) return new FieldId(index);

        throw new UnsupportedConstructException(
            $"member '{name}' of '{symbol.Name}' is not a field; only field access is supported " +
            "by this compiler version yet",
            span);
    }

    public IrType Lower(LyrType type, Core.Span span) => type switch
    {
        // Host type BEFORE the ordinary class: an empty class in a native module is a reference to a host
        // object rather than to a module layout. 'HostTypes' answers the question for both places where
        // it arises.
        NamedRef { Symbol.Kind: TypeSymbolKind.Class } h
            when HostTypes.NameOf(h.Symbol, Compilation) is { } hostName
            => new IrHostType(hostName),

        NamedRef { Symbol.Kind: TypeSymbolKind.Class } n => RefTo(n.Symbol),
        NamedRef { Symbol.Kind: TypeSymbolKind.Struct } n => StructOf(n.Symbol),
        NamedRef { Symbol.Kind: TypeSymbolKind.Enum } n => EnumOf(n.Symbol),
        NamedRef { Symbol.Kind: TypeSymbolKind.Interface } n => InterfaceOf(n.Symbol),

        // An opaque alias IS its underlying at runtime; identity is the sema's business alone.
        // This one line is why 'x as Entity' costs nothing and why the value crosses the native
        // boundary unchanged.
        Sema.OpaqueRef o => Lower(o.Underlying, span),

        // A function type carries its signature structurally and therefore needs no entry in this table,
        // unlike every named type. Lowered recursively, because parameters and the return may themselves
        // be classes, enums or functions again.
        // A tuple: an object with one field per element.
        Sema.TupleOf t => TupleOf(t.Elements.Select(e => Lower(e, span)).ToArray()),

        // An instance of a generic type: 'Box<int>' is a table entry of its own with its own layout.
        GenericInstance g => InstanceType(g, span),

        // Coroutine<T> IS a function value: 'resume co' continues it and yields the next value,
        // which is a call. A coroutine differs from an ordinary function only in WHERE it starts
        // the next time. That the sema keeps them apart is right and belongs there; the IR checks
        // consistency, not language rules. The one parameter is the lenient flag: false panics on
        // exhaustion ('resume'), true delivers the done state instead ('next').
        CoroutineOf c => CoroutineSignature(Lower(c.Yield, span)),

        FnType f => new IrFunctionType(
            f.Parameters.Select(p => Lower(p, span)).ToArray(), Lower(f.Return, span)),

        // T[] is a reference type with the element type inline; it needs no table entry, because it has
        // no named layout.
        ArrayOf a => new IrArrayType(Lower(a.Element, span)),

        // ?T is not nestable. The sema already collapses '??T'; a boundary stands here all the same,
        // rather than a silent assumption.
        Optional o => OptionalOf(Lower(o.Inner, span), span),

        // A type parameter reaches this place only when the caller did not substitute it. That is a
        // lowering error rather than a language boundary, hence a message of its own instead of the
        // generic "not lowerable".
        TypeParamType p => throw new UnsupportedConstructException(
            $"type parameter '{p.Param.Name}' reached lowering unsubstituted", span),

        _ => TypeLowering.Lower(type)
    };

    /// <summary>
    /// The wire signature of a coroutine value: one bool parameter, the LENIENT flag. A
    /// <c>resume</c> passes false and the exhausted exits panic; a <c>next()</c> passes true and
    /// they deliver instead, with the done state read back through
    /// <c>std.core.coroutineIsDone</c>. One definition, because factory, body, resume and next
    /// have to agree on it to the letter.
    /// </summary>
    public static IrFunctionType CoroutineSignature(IrType yield) =>
        new([new IrScalarType(IrScalar.Bool)], yield);

    /// <summary>'?T' with the nesting boundary in one place rather than at every call site.</summary>
    private static IrType OptionalOf(IrType inner, Core.Span span) =>
        inner is IrOptionalType
            ? throw new UnsupportedConstructException(
                "a nested optional '??T' (optionals do not nest) is not supported by this compiler " +
                "version yet", span)
            : new IrOptionalType(inner);

    /// <summary>
    /// A syntactically written type: a field, a parameter, a return type. A class type interns
    /// recursively, and that terminates, because <see cref="Intern"/> assigns the id before the layout.
    /// </summary>
    public IrType Lower(TypeNode node) => Lower(node, node.Span);

    private IrType Lower(TypeNode node, Core.Span span)
    {
        // T[]. There is no size in the type: the length is a property of the value.
        if (node is ArrayType array)
        {
            if (array.Size is not null)
                throw new UnsupportedConstructException(
                    "a length in the array type ('T[N]') does not exist; the length belongs to the " +
                    "value — use 'T[]' and build it with '[x] * n'", node.Span);

            return new IrArrayType(Lower(array.Element, array.Element.Span));
        }

        if (node is NullableType option)
            return new IrOptionalType(Lower(option.Inner, option.Inner.Span));

        // '(A, B)' — a tuple written as a field, parameter or return type.
        if (node is AST.TupleType written)
            return TupleOf(written.Elements.Select(e => Lower(e, e.Span)).ToArray());

        // 'fn(A, B) -> R' — written in parameter and return positions. It needs no table entry: the type
        // carries its signature itself.
        if (node is FunctionType signature)
            return new IrFunctionType(
                signature.Parameters.Select(p => Lower(p, p.Span)).ToArray(),
                Lower(signature.ReturnType, signature.ReturnType.Span));

        if (node is NamedType named)
        {
            // A type parameter in the layout of an instance: 'v: T' in 'Box<int>' is an int. The question
            // has to come BEFORE the symbol resolution — 'T' is not a type one could find.
            if (named.TypeArguments.Length == 0 && _substitutions.Count > 0
                && _substitutions.Peek().TryGetValue(named.Path[^1], out var substituted))
                return Lower(substituted, span);

            if (named.TypeArguments.Length == 0
                && TypeFacts.FromBuiltinName(named.Path[^1]) is { } primitive)
                return TypeLowering.Lower(primitive);

            var bound = _binding.Resolve(named);
            if (bound is ImportBindingSymbol import) bound = import.Target;

            // 'Coroutine<T>' is a builtin, not a declared generic: it has no layout to intern.
            // A coroutine value is a function value over its state, so the written form lowers
            // exactly like the sema's CoroutineOf — the case the LyrType path always had.
            if (bound is TypeSymbol { Kind: TypeSymbolKind.Builtin, Name: "Coroutine" }
                && named.TypeArguments.Length == 1)
                return CoroutineSignature(
                    Lower(named.TypeArguments[0], named.TypeArguments[0].Span));

            // Written type arguments ('Box<int>' as a field or parameter type) are lowered BEFORE the
            // instance is interned: an argument may itself be a type parameter of the surrounding
            // instance ('Box<T>' in 'Pair<T>').
            if (named.TypeArguments.Length > 0 && bound is TypeSymbol generic)
            {
                var arguments = named.TypeArguments.Select(a => Resolve(a, span)).ToArray();
                var id = Intern(generic, arguments);
                return generic.Kind switch
                {
                    TypeSymbolKind.Struct => new IrStructType(id),
                    TypeSymbolKind.Enum => new IrEnumType(id),
                    TypeSymbolKind.Interface => new IrInterfaceType(id),
                    _ => new IrRefType(id),
                };
            }

            // A type alias carries no layout and gets no table entry: it is a NAME for a type, and
            // what it names is lowered in its place. The sema does the same through SymbolToType,
            // which is why an alias reaches this far at all. A cycle cannot arrive here — the sema
            // reports it and the lowering does not run on a faulty AST.
            if (bound is TypeSymbol { Kind: TypeSymbolKind.Alias, Declaration: TypeAliasDecl aliased })
                return Lower(aliased.Aliased, span);

            if (bound is TypeSymbol { Kind: TypeSymbolKind.Enum } enumType) return EnumOf(enumType);
            if (bound is TypeSymbol { Kind: TypeSymbolKind.Interface } iface) return InterfaceOf(iface);
            if (bound is TypeSymbol { Kind: TypeSymbolKind.Struct } value) return StructOf(value);
            if (bound is TypeSymbol type) return RefTo(type);
        }

        throw new UnsupportedConstructException(
            "a non-primitive field type is not supported by this compiler version yet", node.Span);
    }

    /// <summary>
    /// A written type argument as a <see cref="LyrType"/>, which is what <see cref="Intern"/> needs as a
    /// key.
    ///
    /// <para>Not through <see cref="Lower(TypeNode)"/>: that yields an IR type, and the sema type cannot
    /// be recovered from it. The name of an instance has to be formed from sema types, or
    /// <c>Box&lt;int&gt;</c> and <c>Box&lt;int64&gt;</c> would be named differently although they are the
    /// same.</para>
    /// </summary>
    /// <summary>The IR type of an instance: a reference, a value, an enum or an interface, depending on
    /// what the definition is.</summary>
    public IrType InstanceType(GenericInstance instance, Core.Span span)
    {
        var id = Intern(instance.Definition, instance.Arguments);
        return instance.Definition.Kind switch
        {
            TypeSymbolKind.Struct => new IrStructType(id),
            TypeSymbolKind.Enum => new IrEnumType(id),
            TypeSymbolKind.Interface => new IrInterfaceType(id),
            _ => new IrRefType(id),
        };
    }

    /// <summary>
    /// Pushes a substitution onto the stack for the lifetime of the returned scope — the same one
    /// <see cref="Intern"/> uses while lowering the members of a generic instance.
    ///
    /// <para>Needed by the <see cref="FunctionLowerer"/>: a monomorphized FUNCTION, not method, knows its
    /// type arguments, but the type table otherwise learns nothing of them. Without this,
    /// <c>fn make&lt;T&gt;(x: T): Box&lt;T&gt;</c> is not lowerable — the return type gets resolved without
    /// a substitution and <c>T</c> finds nothing.</para>
    /// </summary>
    public IDisposable PushSubstitution(IReadOnlyDictionary<string, LyrType> mapping)
    {
        _substitutions.Push(mapping);
        return new SubstitutionScope(this);
    }

    private sealed class SubstitutionScope(TypeTable owner) : IDisposable
    {
        public void Dispose() => owner._substitutions.Pop();
    }

    private LyrType Resolve(TypeNode node, Core.Span span)
    {
        if (node is NamedType { TypeArguments.Length: 0 } named)
        {
            if (_substitutions.Count > 0
                && _substitutions.Peek().TryGetValue(named.Path[^1], out var substituted))
                return substituted;

            if (TypeFacts.FromBuiltinName(named.Path[^1]) is { } primitive) return primitive;

            var bound = _binding.Resolve(named);
            if (bound is ImportBindingSymbol import) bound = import.Target;

            // As in Lower: an alias stands for what it names, here as a type ARGUMENT — 'List<Id>'
            // has to key the same instance as 'List<int>', or the two would intern separately.
            if (bound is TypeSymbol { Kind: TypeSymbolKind.Alias, Declaration: TypeAliasDecl aliased })
                return Resolve(aliased.Aliased, span);

            if (bound is TypeSymbol symbol) return new NamedRef(symbol);
        }

        // A generic type as a type argument or return type: 'fn empty<T>(): List<T>'. The arguments run
        // through the same resolution, so the substitution reaches into the depth too — 'List<T>' becomes
        // 'List<int>' inside the instance.
        if (node is NamedType { TypeArguments.Length: > 0 } generic)
        {
            var definition = _binding.Resolve(generic);
            if (definition is ImportBindingSymbol imported) definition = imported.Target;

            // 'List<Coroutine<int>>': the builtin has no definition to intern an instance of, so it
            // becomes the sema's CoroutineOf here, the same normalization ResolveType applies.
            if (definition is TypeSymbol { Kind: TypeSymbolKind.Builtin, Name: "Coroutine" }
                && generic.TypeArguments.Length == 1)
                return new CoroutineOf(Resolve(generic.TypeArguments[0], span));

            if (definition is TypeSymbol generictype)
                return new GenericInstance(generictype,
                    generic.TypeArguments.Select(argument => Resolve(argument, span)).ToArray());
        }

        if (node is ArrayType { Size: null } array) return new ArrayOf(Resolve(array.Element, span), null);
        if (node is NullableType option) return new Optional(Resolve(option.Inner, span));

        // A tuple as a type argument: 'Iterator<(int, T)>'. That is the signature of 'enumerate' and
        // 'zip'.
        if (node is AST.TupleType tuple)
            return new TupleOf(tuple.Elements.Select(e => Resolve(e, span)).ToArray());

        // 'fn(A) -> B' as a type argument. No known case needs it today; it stands here so the list is
        // not a partial copy of the others.
        if (node is FunctionType fn)
            return new FnType(fn.Parameters.Select(p => Resolve(p, span)).ToArray(),
                Resolve(fn.ReturnType, span));

        throw new UnsupportedConstructException(
            "this type argument is not supported by this compiler version yet", span);
    }

    private static Core.Span SpanOf(TypeSymbol symbol) => symbol.Declaration?.Span ?? default;

    private static string Describe(TypeSymbolKind kind) => kind switch
    {
        TypeSymbolKind.Enum => "an enum",
        TypeSymbolKind.Alias => "a type alias",
        _ => "not a class"
    };
}
