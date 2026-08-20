using Lyric.AST;
using Lyric.Core;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Ir.Lowering;

/// <summary>
/// Lowers ONE function from the type-checked AST into an <see cref="IrFunction"/>. One object per
/// function, like <c>IrVerifier.FunctionVerifier</c>, because slots, blocks and the loop stack are
/// function-local and die with the function.
///
/// <para>STATEMENTS RETURN A <c>bool</c>: "does control flow fall through?" That is the load-bearing
/// signature decision. Without it one cannot decide whether a merge block may be created, and a merge
/// block without predecessors is unreachable, which the verifier rejects — deliberately, as there is no
/// <c>SimplifyCfg</c> pass. For the same reason <see cref="LowerStatements"/> stops as soon as a
/// statement does not fall through: code after a <c>return</c> must not produce a block.</para>
///
/// <para>VALUES CROSSING BLOCK BOUNDARIES TRAVEL THROUGH LOCALS, NOT THROUGH TEMPS. A temp is defined
/// exactly once and therefore cannot carry "the result from two branches". An if expression and
/// <c>&amp;&amp;</c>/<c>||</c> therefore create a synthetic local, write into it in both branches and
/// read it in the merge block. That is why this IR needs no <c>Phi</c>: the target is a stack VM with
/// local slots, and a phi would have to become store/load again at emission.</para>
///
/// <para>A construct the IR cannot express is valid Lyric and therefore a DIAGNOSTIC
/// (<c>LYR-IR0001</c>) with file, line and column rather than a crash — see
/// <see cref="UnsupportedConstructException"/>. Internal inconsistencies stay separate and keep
/// throwing <see cref="InternalCompilationException"/>.</para>
/// </summary>
internal sealed class FunctionLowerer
{
    private static readonly IrType VoidType = new IrScalarType(IrScalar.Void);
    private static readonly IrType BoolType = new IrScalarType(IrScalar.Bool);

    private readonly FunctionDecl? _decl;
    private readonly string _name;
    private readonly TypeResult _types;
    private readonly IReadOnlyDictionary<FunctionSymbol, FunctionId> _functions;
    private readonly ImportTable _imports;
    private readonly TypeTable _typeTable;
    private readonly GlobalTable _globals;

    /// <summary>The receiver's slot, always 0, or <c>null</c> for a free or static function.</summary>
    private readonly LocalId? _thisSlot;
    private IrType? _thisType;

    /// <summary>The receiver type of this function; a lambda in its body inherits it when it captures
    /// <c>this</c>.</summary>
    private readonly TypeSymbol? _receiver;

    /// <summary>The type instance this method belongs to, set when <c>Box&lt;int&gt;.get</c> is being
    /// lowered here. <c>this</c> is then of the INSTANCE's type rather than the definition's:
    /// <c>Box</c> alone has no layout, only <c>Box&lt;int&gt;</c> has one.</summary>
    private readonly GenericInstance? _ownerInstance;

    /// <summary>The type arguments of the instance being lowered. The hook sits in
    /// <see cref="LowerType"/>, so the worklist monomorphization only has to fill the map rather than
    /// rebuild the whole expression path.</summary>
    private readonly IReadOnlyDictionary<GenericParamSymbol, LyrType> _substitution;

    /// <summary>
    /// Temps holding a FRESHLY BUILT value: the result of a <c>newobj</c> or of a call.
    ///
    /// <para>Relevant for structs only, and there the line between correct and wasteful: a value nobody
    /// else holds does not have to be copied when bound. Without this distinction every
    /// <c>let p = P { … };</c> would get a <c>structcopy</c> directly behind its <c>newobj</c> — correct,
    /// but obviously pointless and visible in every disassembly.</para>
    /// </summary>
    /// <summary>
    /// What a call in a <c>?.</c> chain sees differently, and nothing else.
    ///
    /// <para><c>_chainReceivers</c> maps the RECEIVER expression to the already unwrapped value:
    /// <c>LowerExprOrVoid</c> returns it instead of evaluating <c>b</c> a second time.
    /// <c>_chainResults</c> maps the CALL expression to the method's return type, because the sema gave
    /// the expression the chain type (<c>?int</c> instead of <c>int</c>).</para>
    ///
    /// <para>On the node rather than as a parameter, because otherwise both facts would have to be
    /// threaded through LowerVirtualCall, LowerGenericMethodCall, LowerConstraintCall and
    /// LowerImportCall — four signatures for an exception none of the four cares about. Nested chains
    /// (<c>a?.f(b?.g())</c>) carry different nodes and therefore do not get in each other's way; the
    /// entries are removed again after the call.</para>
    /// </summary>
    private readonly Dictionary<Expr, TempId> _chainReceivers =
        new(ReferenceEqualityComparer.Instance);

    private readonly Dictionary<Expr, IrType> _chainResults =
        new(ReferenceEqualityComparer.Instance);

    private readonly HashSet<TempId> _fresh = new();

    /// <summary>The lifted lambdas of the module. A lambda in the body registers here and gets its id
    /// immediately, long before its own body is lowered.</summary>
    private readonly LambdaTable _lambdas;

    /// <summary>The monomorphized instances of the module. A call to a generic function requests its own
    /// here and gets an id immediately.</summary>
    private readonly InstanceTable _instances;

    /// <summary>
    /// Slots holding a CELL rather than a value: per slot the type of the cell and the type of what lies
    /// inside it.
    ///
    /// <para>Such a slot behaves unremarkably for the whole rest of the lowering: only
    /// <see cref="LoadValue"/> and <see cref="StoreValue"/> know about it, and they are the only places
    /// writing <c>ldloc</c> and <c>stloc</c> on named variables. Without this bundling each of the
    /// roughly fifteen access sites would have to ask the question itself, and the one that forgets lets
    /// the closure and the function see different values.</para>
    /// </summary>
    private readonly Dictionary<LocalId, (TypeId Cell, IrType Value)> _cells = new();

    /// <summary>While lowering a lambda: which environment field holds which captured symbol. Empty
    /// outside a lambda.</summary>
    private readonly Dictionary<Symbol, int> _captureFields =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>The environment's slot, always 0 when there is one, and its type.</summary>
    private readonly LocalId? _envSlot;
    private readonly TypeId? _envType;

    /// <summary>The lambda currently being lowered, or <c>null</c> for a written function. It carries
    /// the body, because a lambda has no <see cref="FunctionDecl"/>.</summary>
    private readonly LambdaExpr? _lambda;

    private readonly SlotAllocator _slots = new();
    private readonly List<IrBlock> _blocks = new();
    private readonly BlockBuilder _b;
    private readonly Stack<LoopScope> _loops = new();
    private readonly IrType _returnType;

    public FunctionLowerer(FunctionDecl decl, string name, TypeResult types,
        IReadOnlyDictionary<FunctionSymbol, FunctionId> functions,
        ImportTable imports,
        TypeTable typeTable,
        IReadOnlyDictionary<GenericParamSymbol, LyrType> substitution,
        GlobalTable globals,
        LambdaTable lambdas,
        InstanceTable instances,
        TypeSymbol? receiver = null,
        GenericInstance? ownerInstance = null,
        TypeNode? receiverTypeNode = null)
    {
        _ownerInstance = ownerInstance;
        _instances = instances;
        _lambdas = lambdas;
        _receiver = receiver;
        _globals = globals;
        _decl = decl;
        _name = name;
        _types = types;
        _functions = functions;
        _imports = imports;
        _typeTable = typeTable;
        _substitution = substitution;
        _b = new BlockBuilder(_blocks);
        _returnType = LowerDeclaredReturnType();

        // The receiver is parameter 0 and is allocated BEFORE the declared parameters: the IR's parameter
        // convention is positional, and a later slot would be a wrong-slot read in the VM. CIL takes the
        // same route with 'this'.
        if (receiver is not null)
        {
            // An enum receiver is the enum type rather than one of its variants; which one is present is
            // decided by the 'match' in the body.
            // In an interface default method 'this' is the interface type itself — which implementation
            // is behind it is known only at runtime. A 'this.foo()' inside therefore becomes a callvirt.
            // An extension brings its receiver type along as a written TypeNode and lets it run through
            // the same lowering as any parameter type. The detour over 'receiver.Kind' below cannot do
            // that: 'extend int' and 'extend string' target builtins that have no layout entry, and every
            // case there would invent an object for them. A scalar as parameter 0 is nothing new — every
            // free function has that. This is why an inherent extension needs no boxing.
            _thisType = receiverTypeNode is { } written
                ? _typeTable.Lower(written)
                : _ownerInstance is { } owner
                ? _typeTable.InstanceType(owner, SpanOfDecl(decl))
                : receiver.Kind switch
            {
                TypeSymbolKind.Enum => _typeTable.EnumOf(receiver),
                TypeSymbolKind.Interface => _typeTable.InterfaceOf(receiver),
                // The receiver of a struct method is the value itself. That it is a copy was arranged by
                // the caller, so a 'mut fn' mutates only this copy.
                TypeSymbolKind.Struct => _typeTable.StructOf(receiver),
                _ => _typeTable.RefTo(receiver),
            };
            _thisSlot = _slots.Declare("this", _thisType);
        }

        // Parameter convention: the first ParamCount locals ARE the parameters, in order. Without it the
        // IR carries parameter types nowhere and a call would not be type-checkable.
        foreach (var p in decl.Parameters)
        {
            // 'params' and default values are purely CALL-SITE matters: the callee sees an ordinary T[]
            // and an ordinary parameter. Both are materialized where the call stands — see
            // MaterializeArguments.
            //
            // The alternative, letting the callee build its own defaults, would mean lowering a default
            // expression once per function rather than once per call; for 'params' the signature would
            // additionally become variadic, and this IR cannot do that. C# decides at the call site for
            // the same reason.

            if (_types.RefOf(p) is not ParameterSymbol ps)
                throw Bug($"parameter '{p.Name}' was not bound by the type checker");
            _slots.DeclareFor(ps, LowerType(ps.Type, p.Span));
        }
    }

    /// <summary>
    /// The lowerer for a LIFTED LAMBDA.
    ///
    /// <para>A factory of its own rather than a synthetic <see cref="FunctionDecl"/>: the
    /// GlobalInitializer may build one, because its statements are real AST nodes. That would not work
    /// here — the sema bound the <c>LambdaParam</c> nodes to their symbols, and rebuilt <c>Param</c>
    /// nodes would be different objects without a binding. The detour would have needed a second symbol
    /// resolution.</para>
    /// </summary>
    public static FunctionLowerer ForLambda(LambdaExpr lambda, string name,
        IReadOnlyList<Symbol> captures, bool capturesThis, IrType environmentType,
        TypeSymbol? receiver, TypeResult types,
        IReadOnlyDictionary<FunctionSymbol, FunctionId> functions, ImportTable imports,
        TypeTable typeTable, GlobalTable globals, LambdaTable lambdas, InstanceTable instances,
        IReadOnlyDictionary<GenericParamSymbol, LyrType>? substitution = null) =>
        new(lambda, name, captures, capturesThis, environmentType, receiver, types, functions,
            imports, typeTable, globals, lambdas, instances, substitution);

    private FunctionLowerer(LambdaExpr lambda, string name, IReadOnlyList<Symbol> captures,
        bool capturesThis, IrType environmentType, TypeSymbol? receiver, TypeResult types,
        IReadOnlyDictionary<FunctionSymbol, FunctionId> functions, ImportTable imports,
        TypeTable typeTable, GlobalTable globals, LambdaTable lambdas, InstanceTable instances,
        IReadOnlyDictionary<GenericParamSymbol, LyrType>? substitution = null)
    {
        _instances = instances;
        _lambda = lambda;
        _receiver = receiver;
        _name = name;
        _types = types;
        _functions = functions;
        _imports = imports;
        _typeTable = typeTable;
        _globals = globals;
        _lambdas = lambdas;

        // The substitution of the ENCLOSING function. With 'NoSubstitution' every lambda in a
        // monomorphized instance breaks: '(a: T, b: T) => …' in 'sortList<T>' makes the lowering abort
        // with "type parameter 'T' is not supported".
        //
        // A lambda is no generic context of its own; it inherits the one of its body. That it is lowered
        // as a separate function is an implementation decision and must not change the types.
        _substitution = substitution ?? ModuleLowerer.NoSubstitution;
        _b = new BlockBuilder(_blocks);

        _returnType = _types.TypeOf(lambda) is FnType fn
            ? LowerType(fn.Return, lambda.Span)
            : throw Bug("lambda has no function type");

        // The environment is parameter 0, the same position 'this' occupies on a method. A closure call
        // is therefore an ordinary call, and the VM needs no second frame setup for 'callind'.
        if (environmentType is IrRefType env)
        {
            _envType = env.Type;
            _envSlot = _slots.Declare("<env>", environmentType);

            for (var i = 0; i < captures.Count; i++) _captureFields[captures[i]] = i;

            // 'this' lies behind the named captures when it is captured: it is no symbol, having no
            // declaration, so it needs a place of its own rather than an entry in the same map.
            if (capturesThis)
            {
                _thisType = receiver is null ? null : _typeTable.RefTo(receiver);
                _capturedThisField = captures.Count;
            }
        }

        foreach (var p in lambda.Parameters)
        {
            if (_types.RefOf(p) is not ParameterSymbol ps)
                throw Bug($"lambda parameter '{p.Name}' was not bound by the type checker");
            _slots.DeclareFor(ps, LowerType(ps.Type, p.Span));
        }
    }

    /// <summary>
    /// The lowerer for the BODY OF A COROUTINE.
    ///
    /// <para>It looks like an ordinary function with one parameter — the state object — and the yield
    /// type as its return. That is exactly what it is: <c>resume</c> is an ordinary call. The coroutine
    /// lies solely in WHERE the variables live and in the first block being a jump table.</para>
    /// </summary>
    public static FunctionLowerer ForCoroutineBody(FunctionDecl decl, string name, TypeId state,
        IrType yieldType, TypeSymbol? receiver, TypeResult types,
        IReadOnlyDictionary<FunctionSymbol, FunctionId> functions, ImportTable imports,
        TypeTable typeTable, GlobalTable globals, LambdaTable lambdas, InstanceTable instances) =>
        new(decl, name, state, yieldType, receiver, types, functions, imports, typeTable, globals,
            lambdas, instances);

    private FunctionLowerer(FunctionDecl decl, string name, TypeId state, IrType yieldType,
        TypeSymbol? receiver, TypeResult types,
        IReadOnlyDictionary<FunctionSymbol, FunctionId> functions, ImportTable imports,
        TypeTable typeTable, GlobalTable globals, LambdaTable lambdas, InstanceTable instances)
    {
        _decl = decl;
        _name = name;
        _types = types;
        _functions = functions;
        _imports = imports;
        _typeTable = typeTable;
        _globals = globals;
        _lambdas = lambdas;
        _instances = instances;
        _receiver = receiver;
        _substitution = ModuleLowerer.NoSubstitution;
        _b = new BlockBuilder(_blocks);
        _coroutineState = state;
        _returnType = yieldType;

        // Slot 0 holds the state object. It is the only thing living in a frame slot; everything else has
        // to survive the next 'yield' and therefore lives inside it. Slot 1 is the lenient flag:
        // 'resume' passes false, 'next()' passes true, and the exhausted exits branch on it.
        _stateSlot = _slots.Declare("<state>", new IrRefType(state));
        _lenientSlot = _slots.Declare("<lenient>", BoolType);

        // Field 0 is the re-entry point. It belongs to no symbol, so it stands here rather than in
        // _stateFields.
        _stateTypes.Add(new IrScalarType(IrScalar.I32));
        _stateNames.Add("<resume>");

        // 'this' and the parameters survive the first 'yield' just like any local; the factory wrote them
        // in when creating the object.
        if (receiver is not null)
        {
            _thisType = _typeTable.RefTo(receiver);
            _capturedThisField = _stateTypes.Count;
            _stateTypes.Add(_thisType);
            _stateNames.Add("this");
        }

        foreach (var p in decl.Parameters)
        {
            if (_types.RefOf(p) is not ParameterSymbol ps)
                throw Bug($"parameter '{p.Name}' was not bound by the type checker");
            DeclareStateField(ps, p.Name, LowerType(ps.Type, p.Span));
        }
    }

    /// <summary>The field index of the captured <c>this</c> in the environment, when captured.</summary>
    private readonly int? _capturedThisField;

    // ------------------------------------------------------------------ coroutines

    /// <summary>
    /// The state type when a COROUTINE BODY is being lowered here, <c>null</c> otherwise.
    ///
    /// <para>In coroutine mode parameters and locals do not live in frame slots but in fields of this
    /// object: a frame ends at every <c>yield</c>, the object does not. Slot 0 is the re-entry
    /// point.</para>
    /// </summary>
    private readonly TypeId? _coroutineState;

    /// <summary>The slot holding the state object: parameter 0 in coroutine mode.</summary>
    private LocalId _stateSlot;

    /// <summary>The slot holding the lenient flag: parameter 1 in coroutine mode.</summary>
    private LocalId _lenientSlot;

    /// <summary>The state field a lenient done-exit reads its value from — never written, so it
    /// holds the zero value <c>newobj</c> gave it. Created on the first exit that needs it;
    /// a void coroutine never does.</summary>
    private int? _zeroField;

    /// <summary>Symbol to field index in the state object. Grows during the lowering; the layout is
    /// supplied afterwards (see <see cref="TypeTable.CompleteCoroutineState"/>).</summary>
    private readonly Dictionary<Symbol, int> _stateFields = new(ReferenceEqualityComparer.Instance);

    private readonly List<IrType> _stateTypes = new();
    private readonly List<string> _stateNames = new();

    /// <summary>The blocks a <c>resume</c> re-enters at; index n belongs to the nth <c>yield</c>. The
    /// jump table is built from them once all are known.</summary>
    private readonly List<BlockId> _resumePoints = new();

    private bool InCoroutine => _coroutineState is not null;

    /// <summary>Creates a field in the state object and returns its index.</summary>
    private int DeclareStateField(Symbol symbol, string name, IrType type)
    {
        var index = _stateTypes.Count;
        _stateTypes.Add(type);
        _stateNames.Add(name);
        _stateFields[symbol] = index;
        return index;
    }

    /// <summary>The state object itself. It lives in an ordinary slot, because it does not change during
    /// a run.</summary>
    private TempId LoadState(Core.Span span)
    {
        var type = _slots.TypeOfLocal(_stateSlot);
        var dest = _slots.NewTemp(type);
        _b.Emit(new LoadLocal(dest, _stateSlot, type, span));
        return dest;
    }

    private TempId LoadStateField(int field, Core.Span span)
    {
        var type = _stateTypes[field];
        var dest = _slots.NewTemp(type);
        _b.Emit(new LoadField(dest, LoadState(span), _coroutineState!.Value, new FieldId(field),
            type, span));
        return dest;
    }

    private void StoreStateField(int field, TempId value, Core.Span span) =>
        _b.Emit(new StoreField(LoadState(span), _coroutineState!.Value, new FieldId(field), value,
            span));

    /// <summary>
    /// The ONE exhausted exit of a coroutine, shared by all three ways there: the body running
    /// through, a bare <c>return;</c>, and a call on an already-finished coroutine. It seals the
    /// current block.
    ///
    /// <para>The lenient flag decides what exhaustion IS at this call: <c>resume</c> passed false
    /// and gets the panic the specification promises; <c>next()</c> passed true and gets a
    /// delivered value the caller discards after reading the done state back. The delivered value
    /// is a state field NOTHING ever writes — <c>newobj</c> zeroed it, which answers every yield
    /// type uniformly, interfaces and function values included, without manufacturing one.</para>
    /// </summary>
    /// <param name="mark">Whether to write the end marker first. The dispatch's already-finished
    /// path finds it written; the two body exits write it here.</param>
    private void EmitCoroutineDoneExit(bool mark, Core.Span span)
    {
        if (mark)
        {
            var i32 = new IrScalarType(IrScalar.I32);
            var done = _slots.NewTemp(i32);
            // -1 as two's complement in 32 bits: the verifier checks the declared width
            _b.Emit(new Const(done, i32, new IntConst(unchecked((ulong)(uint)-1)), span));
            StoreStateField(0, done, span);
        }

        var lenient = _slots.NewTemp(BoolType);
        _b.Emit(new LoadLocal(lenient, _lenientSlot, BoolType, span));

        var deliver = _b.NewBlock();
        var panic = _b.NewBlock();
        _b.Seal(new CondBranch(lenient, deliver, panic, span));

        _b.SwitchTo(deliver);
        if (IsVoid(_returnType)) _b.Seal(new Return(null, span));
        else
        {
            _zeroField ??= DeclareZeroField();
            _b.Seal(new Return(LoadStateField(_zeroField.Value, span), span));
        }

        _b.SwitchTo(panic);
        CallHelper("std.core.coroutineEnded", span);
        _b.Seal(new Unreachable(span));
    }

    /// <summary>The never-written field behind the lenient exits. Symbol-less, like field 0.</summary>
    private int DeclareZeroField()
    {
        var index = _stateTypes.Count;
        _stateTypes.Add(_returnType);
        _stateNames.Add("<zero>");
        return index;
    }

    // ------------------------------------------------------------------ closures

    /// <summary>
    /// A lambda: build the environment, register the function, produce the fat pointer.
    ///
    /// <para>The lifted function is NOT lowered here — it is only registered and gets its id immediately.
    /// That is the condition for a recursive or nested lambda to work at all: its <c>mkclosure</c> is
    /// settled before its body exists.</para>
    /// </summary>
    /// <summary>
    /// <c>(1, "a")</c> — an object with one field per element.
    ///
    /// <para>The same sequence as for a struct initializer, except that the fields go by position rather
    /// than by name. An opcode of its own would be a second way to build an object.</para>
    /// </summary>
    private TempId LowerTupleLiteral(TupleLitExpr expr)
    {
        if (LowerType(_types.TypeOf(expr), expr.Span) is not IrRefType type)
            throw Bug("tuple literal has no tuple type");

        var layout = _typeTable.Defs[type.Type.Value];

        var dest = _slots.NewTemp(type);
        _b.Emit(new NewObject(dest, type.Type, type, expr.Span));

        for (var i = 0; i < expr.Elements.Length; i++)
        {
            var value = LowerExprAs(expr.Elements[i], layout.FieldTypes[i]);
            _b.Emit(new StoreField(dest, type.Type, new FieldId(i), value, expr.Span));
        }

        _fresh.Add(dest);
        return dest;
    }

    private TempId LowerLambda(LambdaExpr lambda)
    {
        if (LowerType(_types.TypeOf(lambda), lambda.Span) is not IrFunctionType signature)
            throw Bug("lambda has no function type");

        var (captured, capturesThis) = _types.CapturesOf(lambda);

        // The values for the environment are evaluated HERE, in the enclosing frame — that is the core of
        // "captures on creation": a later call sees the state of now. For a boxed 'var' that state is the
        // CELL rather than its content, and that is exactly how both sides share the same variable.
        var fieldTypes = new IrType[captured.Count + (capturesThis ? 1 : 0)];
        var fieldNames = new string[fieldTypes.Length];
        var values = new TempId[fieldTypes.Length];

        for (var i = 0; i < captured.Count; i++)
        {
            var symbol = captured[i];
            fieldNames[i] = symbol.Name;
            (fieldTypes[i], values[i]) = LoadCaptured(symbol, lambda.Span);
        }

        if (capturesThis)
        {
            var slot = _thisSlot ?? throw Bug("lambda captures 'this' outside a method");
            var thisType = _thisType ?? throw Bug("captured 'this' has no type");
            var value = _slots.NewTemp(thisType);
            _b.Emit(new LoadLocal(value, slot, thisType, lambda.Span));

            fieldNames[^1] = "this";
            fieldTypes[^1] = thisType;
            values[^1] = value;
        }

        // Without captures there is no environment and no allocation — the common case for a filter such
        // as '(x) => x > 0'.
        IrType environment = fieldTypes.Length == 0
            ? VoidType
            : _typeTable.EnvironmentFor(_name, fieldTypes, fieldNames);

        var target = _lambdas.Register(lambda, _name, captured, capturesThis, environment,
            ReceiverForLambda(), _substitution);

        TempId? env = null;
        if (environment is IrRefType envType)
        {
            var instance = _slots.NewTemp(environment);
            _b.Emit(new NewObject(instance, envType.Type, environment, lambda.Span));
            for (var i = 0; i < values.Length; i++)
                _b.Emit(new StoreField(instance, envType.Type, new FieldId(i), values[i], lambda.Span));
            env = instance;
        }

        var dest = _slots.NewTemp(signature);
        _b.Emit(new MakeClosure(dest, target, env, signature, lambda.Span));
        return dest;
    }

    /// <summary>
    /// The value that goes into the environment for a captured symbol.
    ///
    /// <para>For a cell that is the cell itself rather than its content; otherwise the closure would have
    /// ended up with a copy and the sharing would silently have become by-value.</para>
    ///
    /// <para>When a symbol is captured that the ENCLOSING function already captured, it lies in its
    /// environment rather than in a slot. Nested lambdas therefore resolve their captures through the
    /// same chain an ordinary identifier takes.</para>
    /// </summary>
    private (IrType Type, TempId Value) LoadCaptured(Symbol symbol, Core.Span span)
    {
        if (_slots.TryLookup(symbol, out var slot))
        {
            var type = _slots.TypeOfLocal(slot); // for a cell: the cell type, which is what is wanted
            var value = _slots.NewTemp(type);
            _b.Emit(new LoadLocal(value, slot, type, span));
            return (type, value);
        }

        if (_captureFields.TryGetValue(symbol, out var field) && _envSlot is { } envSlot)
        {
            var envType = _slots.TypeOfLocal(envSlot);
            var fieldType = _typeTable.Defs[_envType!.Value.Value].FieldTypes[field];

            var holder = _slots.NewTemp(envType);
            _b.Emit(new LoadLocal(holder, envSlot, envType, span));

            var value = _slots.NewTemp(fieldType);
            _b.Emit(new LoadField(value, holder, _envType.Value, new FieldId(field), fieldType, span));
            return (fieldType, value);
        }

        throw Bug($"captured symbol '{symbol.Name}' is neither a slot nor an environment field");
    }

    /// <summary>The receiver type an inner lambda inherits when it captures <c>this</c>.</summary>
    private TypeSymbol? ReceiverForLambda() => _receiver;

    // ------------------------------------------------------------------ cells

    /// <summary>
    /// Reads a named variable. When it lives in a cell, the access goes through the cell's field, here
    /// exactly as in every closure sharing it.
    /// </summary>
    private TempId LoadValue(LocalId slot, Core.Span span)
    {
        if (!_cells.TryGetValue(slot, out var cell))
        {
            var plain = _slots.TypeOfLocal(slot);
            var direct = _slots.NewTemp(plain);
            _b.Emit(new LoadLocal(direct, slot, plain, span));
            return direct;
        }

        var holder = _slots.NewTemp(_slots.TypeOfLocal(slot));
        _b.Emit(new LoadLocal(holder, slot, _slots.TypeOfLocal(slot), span));

        var dest = _slots.NewTemp(cell.Value);
        _b.Emit(new LoadField(dest, holder, cell.Cell, new FieldId(0), cell.Value, span));
        return dest;
    }

    /// <summary>Writes a named variable, into its slot or into its cell.</summary>
    private void StoreValue(LocalId slot, TempId value, Core.Span span)
    {
        if (!_cells.TryGetValue(slot, out var cell))
        {
            _b.Emit(new StoreLocal(slot, value, span));
            return;
        }

        var holder = _slots.NewTemp(_slots.TypeOfLocal(slot));
        _b.Emit(new LoadLocal(holder, slot, _slots.TypeOfLocal(slot), span));
        _b.Emit(new StoreField(holder, cell.Cell, new FieldId(0), value, span));
    }

    /// <summary>The type of the VALUE in a slot, so for a cell its content rather than the cell. Every
    /// place that uses <c>TypeOfLocal</c> to type a value has to ask this question.</summary>
    private IrType ValueTypeOf(LocalId slot) =>
        _cells.TryGetValue(slot, out var cell) ? cell.Value : _slots.TypeOfLocal(slot);

    public IrFunction Run()
    {
        // A lambda has an expression OR a block instead of a body. The expression case is the common one
        // and needs no 'return' in the source; it is inserted here.
        if (_lambda is not null) return RunLambda();
        if (InCoroutine) return RunCoroutineBody();

        if (_decl!.Body is null) throw Bug("function has no body");

        // The function body is itself a scope with its own defers.
        if (LowerScope(_decl.Body))
        {
            // Control flow ran out of the body. For void that is the normal case and needs the implicit
            // 'ret'. For non-void the sema's return coverage (LYR-SEM0017) proved that every path returns,
            // so this point is reached only through a diverging construct such as 'while (true) { }',
            // whose exit edge never fires. 'unreachable' is the honest encoding of that.
            _b.Seal(IsVoid(_returnType)
                ? new Return(null, _decl.Body.Span)
                : new Unreachable(_decl.Body.Span));
        }

        // The receiver counts as a parameter: it occupies slot 0 and is passed as argument 0 at the call
        // site. Without it here the parameter convention is violated, and the verifier reports exactly
        // that ("call passes 2 arg(s), expected 1").
        return new IrFunction(_name, _returnType, _decl.Parameters.Length + (_thisSlot is null ? 0 : 1),
            _slots.Locals, _slots.Temps, _blocks)
        {
            Entry = new BlockId(0), Handlers = _handlers,
        };
    }

    /// <summary>
    /// The body of a coroutine: the written code, surrounded by a jump table.
    /// </summary>
    private IrFunction RunCoroutineBody()
    {
        var body = _decl!.Body ?? throw Bug("coroutine has no body");

        // bb0 belongs to the jump table and stays empty for now: the verifier requires the entry to be
        // the FIRST block, and which entry points exist is known only after the body. So the place is
        // reserved and filled later — the same two-phase shape as for the type id of a recursive type.
        var dispatch = _b.CurrentId;
        var start = _b.NewBlock();
        _b.SwitchTo(start);

        if (LowerScope(body))
        {
            // The body ran through: no 'yield' is left, so this call has no value of its own. What
            // that means depends on how it was called — the shared exit branches on the lenient
            // flag. Python reports StopIteration here for the same reason 'resume' panics.
            EmitCoroutineDoneExit(mark: true, body.Span);
        }

        BuildResumeDispatch(dispatch, start, body.Span);
        _typeTable.CompleteCoroutineState(_coroutineState!.Value,
            _stateTypes.ToArray(), _stateNames.ToArray());

        // Two parameters: the state object and the lenient flag. Everything else sits inside the
        // state.
        return new IrFunction(_name, _returnType, 2, _slots.Locals, _slots.Temps, _blocks)
        {
            Entry = dispatch, Handlers = _handlers,
        };
    }

    /// <summary>
    /// The body of a lifted lambda. Two forms: an expression IS the return value, a block delivers
    /// through <c>return</c>.
    /// </summary>
    private IrFunction RunLambda()
    {
        switch (_lambda!.Body)
        {
            case Block block:
                if (LowerScope(block))
                    _b.Seal(IsVoid(_returnType)
                        ? new Return(null, block.Span)
                        : new Unreachable(block.Span));
                break;

            case Expr expr:
                // A void context discards the value: '() => doStuff()' is allowed and only calls.
                if (IsVoid(_returnType))
                {
                    LowerExprOrVoid(expr);
                    if (!_b.IsSealed) _b.Seal(new Return(null, expr.Span));
                }
                else
                {
                    var value = LowerExprAs(expr, _returnType);
                    _b.Seal(new Return(value, expr.Span));
                }
                break;

            default:
                throw Bug($"lambda body is neither an expression nor a block ({_lambda.Body.GetType().Name})");
        }

        // The environment counts as parameter 0, the same arithmetic as for the receiver.
        return new IrFunction(_name, _returnType,
            _lambda.Parameters.Length + (_envSlot is null ? 0 : 1),
            _slots.Locals, _slots.Temps, _blocks)
        {
            Entry = new BlockId(0), Handlers = _handlers,
        };
    }

    // ------------------------------------------------------------------ statements

    /// <summary>Lowers the statements of a block. Returns false as soon as control flow ends; the
    /// remaining statements are then unreachable and are discarded.</summary>
    private bool LowerStatements(Block block)
    {
        foreach (var stmt in block.Statements)
            if (!LowerStmt(stmt)) return false;
        return true;
    }

    /// <summary>true means control flow falls through, false means the block is sealed.</summary>
    private bool LowerStmt(Stmt stmt)
    {
        switch (stmt)
        {
            // A nested block is a scope of its own: its defers run at its end rather than only at the
            // end of the function.
            case Block b: return LowerScope(b);
            case BindingStmt b: return LowerBinding(b);
            case DestructuringStmt d: return LowerDestructuring(d);
            // 'panic(…)' has the return type 'never' and seals its block. An expression can therefore
            // end control flow, and the return value has to report that, or the caller later tries to
            // seal the same block a second time.
            case ExprStmt e: LowerExprOrVoid(e.Expr); return !_b.IsSealed;

            // Only in the synthetic global initializer (see GlobalInitializer).
            case GlobalInitStmt g: LowerGlobalInit(g); return true;
            case IfStmt s: return LowerIf(s);
            case WhileStmt s: return LowerWhile(s);
            case DoWhileStmt s: return LowerDoWhile(s);
            case ReturnStmt s: return LowerReturn(s);
            case BreakStmt s: return LowerBreak(s);
            case ContinueStmt s: return LowerContinue(s);

            case ForInStmt s: return LowerForIn(s);
            case MatchStmt s:
                LowerMatch(s.Scrutinee, s.Arms, null, s.Span);
                return _matchFellThrough;
            case TryStmt s: return LowerTry(s);
            case ThrowStmt s: return LowerThrow(s);
            // 'defer' only registers; LowerScope places the bodies at the exits.
            case DeferStmt s: _defers.Peek().Add(s); return true;
            case YieldStmt s: return LowerYield(s);
            case ErrorStmt s: throw Bug($"error statement reached lowering at {s.Span}");

            default: throw Bug($"unhandled statement {stmt.GetType().Name}");
        }
    }


    // ------------------------------------------------------------------ exceptions and defer

    /// <summary>
    /// The <c>defer</c> statements accumulated per scope, outermost scope at the bottom.
    ///
    /// <para><c>defer</c> registers nothing at runtime: which bodies are due is settled at compile time,
    /// so the lowering places them directly at every exit. A runtime stack, as in Go, would need closures
    /// and would cost something on every path, including where there is nothing to do. The price is code
    /// duplication per exit.</para>
    /// </summary>
    private readonly Stack<List<DeferStmt>> _defers = new();

    /// <summary>
    /// The protected regions of this function, in creation order.
    ///
    /// <para>That order is already INNERMOST FIRST: an inner <c>try</c> is lowered completely before the
    /// outer one records its handler. Exactly this order is the contract while unwinding.</para>
    /// </summary>
    private readonly List<IrHandler> _handlers = new();

    private bool LowerThrow(ThrowStmt stmt)
    {
        // NO EmitAllPendingDefers here: a 'throw' unwinds, and while unwinding the defer bodies run
        // through the finally region of their scope. Doing both would run every body twice.
        //
        // The difference from 'return': a return leaves the scope normally, no region applies, and the
        // bodies have to stand inline.
        var value = LowerExpr(stmt.Value);

        // For a class type the concrete type is settled here, since there is no inheritance; for an
        // interface value the fat pointer carries it, and the runtime reads it there.
        var concrete = TypeOfExpr(stmt.Value) switch
        {
            IrRefType r => (TypeId?)r.Type,
            _ => null,
        };

        _b.Seal(new Throw(value, concrete, stmt.Span));
        return false;
    }

    /// <summary>
    /// <c>try { … } catch (e: T) { … }</c>.
    ///
    /// <para>The body occupies a CONTIGUOUS BLOCK RANGE. That is not an assumption but a consequence of
    /// how <see cref="BlockBuilder"/> assigns ids: everything arising during the body lies in between,
    /// nested constructs included. The handlers arise afterwards and therefore lie outside their own
    /// range.</para>
    ///
    /// <para>The caught value goes into a SLOT rather than onto the stack: at a block boundary the stack
    /// is empty, and a handler block is a block boundary. CIL pushes the value there and can afford to,
    /// because it does not have this invariant.</para>
    /// </summary>
    private bool LowerTry(TryStmt stmt)
    {
        // A block of its own for the body: the range has to start at a block boundary, or it would cover
        // code before the 'try' as well.
        var start = _b.NewBlock();
        _b.Seal(new Branch(start, stmt.Span));
        _b.SwitchTo(start);

        var bodyFallsThrough = LowerScope(stmt.Body);
        var bodyLast = _b.CurrentId;
        var end = new BlockId(_blocks.Count);

        // The merge block arises ONLY when someone reaches it. Created unconditionally, it would have no
        // predecessors for 'try { return … } catch (…) { return … }', and the verifier rejects
        // unreachable blocks, as there is no SimplifyCfg pass. The open ends are collected and sealed at
        // the end.
        var open = new List<BlockId>();
        if (bodyFallsThrough) open.Add(bodyLast);

        foreach (var clause in stmt.Catches)
        {
            var handler = _b.NewBlock();
            _b.SwitchTo(handler);

            LocalId? slot = null;
            TypeId? caught = null;

            if (clause.BindingType is { } declared)
            {
                // Through the bound symbol rather than through the TypeNode: the sema records the
                // resolution of a catch type in its own table (BindRef on the CatchClause), not in the
                // resolver's. Resolving the TypeNode again here would be a second truth about
                // visibility.
                var symbol = _types.RefOf(clause) as LocalSymbol
                    ?? throw Bug($"catch binding at {clause.Span} was not bound by the type checker");

                var type = LowerType(symbol.Type, declared.Span);
                switch (type)
                {
                    case IrRefType r:
                        caught = r.Type;
                        break;

                    // 'catch (e: Throwable)' IS the catch-all, written out: 'caught' stays null
                    // exactly as for the typeless form, and the VM builds the fat pointer. Any
                    // OTHER interface would need a conformance test during unwinding, which the
                    // handler table cannot express yet — the id comparison would silently catch
                    // nothing, so the boundary is a diagnostic instead.
                    case IrInterfaceType when symbol.Type is Sema.NamedRef nr
                        && ReferenceEquals(nr.Symbol,
                            _typeTable.Compilation.Builtins.LookupLocal("Throwable")):
                        caught = null;
                        break;
                    case IrInterfaceType:
                        throw NotSupported(
                            "catching a specific interface is not supported by this compiler "
                            + "version yet — catch the concrete classes, or 'catch (e)' for "
                            + "everything", clause.Span);

                    default:
                        throw NotSupported(
                            "catching a non-class type (only classes and interfaces are throwable)",
                            clause.Span);
                }

                slot = _slots.DeclareFor(symbol, type);
            }
            else if (clause.BindingName is not null)
            {
                // 'catch (e)' without a type catches EVERY Throwable: 'caught' stays null, and that is
                // what catch-all means in the handler table. The slot gets the type the sema already
                // gave the name: 'Throwable', so an interface type.
                //
                // A fat pointer therefore lies in the slot rather than a bare reference. Only the VM can
                // build it: which concrete type was thrown is settled at runtime, and it carries that in
                // the frame anyway, because the typed catch compares against it. Without the fat pointer
                // 'e.message()' would be a callvirt on a value that does not know its type.
                var symbol = _types.RefOf(clause) as LocalSymbol
                    ?? throw Bug($"catch binding at {clause.Span} was not bound by the type checker");

                slot = _slots.DeclareFor(symbol, LowerType(symbol.Type, clause.Span));
            }

            var handlerFallsThrough = LowerScope(clause.Body);
            var handlerLast = _b.CurrentId;
            if (handlerFallsThrough) open.Add(handlerLast);

            _handlers.Add(new IrHandler(start, end, IrHandlerKind.Catch, caught, handler, slot));
        }

        // Nobody falls through: no merge block, and control flow ends here. The return value says exactly
        // that, and the caller must not create another block afterwards.
        if (open.Count == 0) return false;

        var merge = _b.NewBlock();
        foreach (var id in open) _b.SealBlock(id, new Branch(merge, stmt.Span));

        _b.SwitchTo(merge);
        return true;
    }

    /// <summary>
    /// A scope with its own <c>defer</c>s: lower the body, then the registered bodies in LIFO order.
    /// </summary>
    private bool LowerScope(Block block)
    {
        // Whether this scope has defers stands in its OWN statements; a defer in a nested block belongs
        // there. Asking in advance saves every defer-free scope the extra block boundary, and that is
        // nearly all of them.
        var hasDefers = block.Statements.Any(st => st is DeferStmt);
        if (!hasDefers) return LowerPlainScope(block);

        // A block of its own: the protected region has to start at a block boundary.
        var start = _b.NewBlock();
        _b.Seal(new Branch(start, block.Span));
        _b.SwitchTo(start);

        _defers.Push(new List<DeferStmt>());
        List<DeferStmt> pending;
        bool fallsThrough;
        try
        {
            fallsThrough = LowerStatements(block);
            pending = _defers.Peek();

            // The normal path gets the bodies directly: no handler, no runtime cost.
            if (fallsThrough) EmitDefers(pending);
        }
        finally
        {
            _defers.Pop();
        }

        var end = new BlockId(_blocks.Count);
        var afterBody = _b.CurrentId;

        // And the same body once more as a finally region, for the case where an exception runs through
        // this scope. A defer runs on every scope exit, exceptions included; the normal exits are served
        // above, this one is not.
        //
        // The price is code duplication: the bodies stand once inline and once here. The alternative,
        // going through the region exclusively, would move the normal path into the unwinder too and
        // make every scope exit a handler pass.
        var cleanup = _b.NewBlock();
        _b.SwitchTo(cleanup);
        EmitDefers(pending);
        _b.Seal(new EndFinally(block.Span));

        _handlers.Add(new IrHandler(start, end, IrHandlerKind.Finally, null, cleanup, null));

        // After the body execution continues behind the region; otherwise the cursor stands in the
        // finally block, which is never reached on the normal path.
        if (fallsThrough)
        {
            var after = _b.NewBlock();
            _b.SealBlock(afterBody, new Branch(after, block.Span));
            _b.SwitchTo(after);
        }

        return fallsThrough;
    }

    /// <summary>A scope without a <c>defer</c>: nothing to guard, nothing to clean up.</summary>
    private bool LowerPlainScope(Block block)
    {
        _defers.Push(new List<DeferStmt>());
        try
        {
            return LowerStatements(block);
        }
        finally
        {
            _defers.Pop();
        }
    }

    /// <summary>LIFO: registered last runs first.</summary>
    private void EmitDefers(List<DeferStmt> pending)
    {
        for (var i = pending.Count - 1; i >= 0; i--) LowerStmt(pending[i].Body);
    }

    /// <summary>All open <c>defer</c>s, innermost first, before a <c>return</c> or <c>throw</c> that
    /// leaves several scopes at once. A <c>Stack&lt;T&gt;</c> enumerates from the top, so the order is
    /// right by itself.</summary>
    private void EmitAllPendingDefers()
    {
        // Over a COPY rather than over the stack itself: lowering a defer body enters a scope and pushes
        // a new entry onto exactly this stack, which invalidates the enumerator and throws in the middle
        // of the compiler.
        //
        // The order is preserved: Stack<T>.ToArray() yields top to bottom, so innermost scope first —
        // the same as the enumeration did.
        foreach (var scope in _defers.ToArray()) EmitDefers(scope);
    }

    /// <summary>The defers of every scope ABOVE the given stack depth, innermost first — what a
    /// <c>break</c> or <c>continue</c> owes the scopes it leaves, and only those.</summary>
    private void EmitPendingDefersAbove(int depth)
    {
        var scopes = _defers.ToArray(); // top-down; a copy for the same reentrancy reason as above
        for (var i = 0; i < scopes.Length - depth; i++) EmitDefers(scopes[i]);
    }

    private bool LowerBinding(BindingStmt binding)
    {
        if (_types.RefOf(binding) is not LocalSymbol local)
            throw Bug($"binding '{binding.Name}' was not bound by the type checker");

        var type = LowerType(local.Type, binding.Span);

        // In a coroutine EVERY local variable survives the next 'yield', so none lies in a frame slot.
        // Conservatively: it is not checked whether a local really lives across a 'yield'. A liveness
        // analysis would be an optimization that only saves object size, and its errors would show at
        // runtime.
        if (InCoroutine)
        {
            var field = DeclareStateField(local, binding.Name, type);
            if (binding.Initializer is not null)
                StoreStateField(field, LowerExprAs(binding.Initializer, type), binding.Span);
            return true;
        }

        // A captured 'var' lives in a cell, so the slot holds the cell, and it has to exist BEFORE anyone
        // writes into it. Hence the newobj here rather than at the first assignment: a 'var n: int;'
        // without an initializer is written later, and the cell would not be there yet.
        if (_types.IsBoxed(local))
        {
            var cellType = _typeTable.CellOf(type);
            var slotForCell = _slots.DeclareFor(local, cellType);
            _cells[slotForCell] = (cellType.Type, type);

            var cell = _slots.NewTemp(cellType);
            _b.Emit(new NewObject(cell, cellType.Type, cellType, binding.Span));
            _b.Emit(new StoreLocal(slotForCell, cell, binding.Span));

            if (binding.Initializer is not null)
                StoreValue(slotForCell, LowerExprAs(binding.Initializer, type), binding.Span);

            return true;
        }

        var slot = _slots.DeclareFor(local, type);

        // Without an initializer the slot stays unwritten: the definite-assignment analysis proved that
        // every read sees an assignment.
        if (binding.Initializer is not null)
            _b.Emit(new StoreLocal(slot, LowerExprAs(binding.Initializer, type), binding.Span));

        return true;
    }

    /// <summary>
    /// <c>let (a, b) = pair;</c> — evaluate the value once, then bind field by field.
    ///
    /// <para>Evaluating ONCE is the actual statement: <c>let (a, b) = f();</c> must not call <c>f</c>
    /// twice. The tuple therefore lands in a temp first and the bindings read from it rather than from
    /// the expression.</para>
    /// </summary>
    private bool LowerDestructuring(DestructuringStmt stmt)
    {
        if (LowerType(_types.TypeOf(stmt.Initializer), stmt.Span) is not IrRefType type)
            throw Bug("destructuring a value that is not a tuple");

        var source = LowerExprAs(stmt.Initializer, type);
        BindTupleElements(stmt.Pattern, source, type.Type, stmt.Span);
        return true;
    }

    /// <summary>
    /// Binds the names of a tuple pattern to the fields of an object, recursively, because patterns nest
    /// (<c>let (a, (b, c)) = …</c>).
    /// </summary>
    private void BindTupleElements(TuplePattern pattern, TempId source, TypeId type, Core.Span span)
    {
        var layout = _typeTable.Defs[type.Value];

        for (var i = 0; i < pattern.Elements.Length; i++)
        {
            var fieldType = layout.FieldTypes[i];

            switch (pattern.Elements[i])
            {
                // '_' binds nothing, so the field is not even read: an 'ldfld' whose result nobody uses
                // would be dead code in the bytecode.
                case WildcardPattern:
                    continue;

                case BindingPattern binding:
                {
                    if (_types.RefOf(binding) is not LocalSymbol local)
                        throw Bug($"'{binding.Name}' in a destructuring was not bound by the type checker");

                    var value = _slots.NewTemp(fieldType);
                    _b.Emit(new LoadField(value, source, type, new FieldId(i), fieldType, span));

                    var slot = _slots.DeclareFor(local, fieldType);
                    _b.Emit(new StoreLocal(slot, value, span));
                    break;
                }

                case TuplePattern nested:
                {
                    if (fieldType is not IrRefType inner)
                        throw Bug("nested tuple pattern on a field that is not a tuple");

                    var value = _slots.NewTemp(fieldType);
                    _b.Emit(new LoadField(value, source, type, new FieldId(i), fieldType, span));
                    BindTupleElements(nested, value, inner.Type, span);
                    break;
                }

                default:
                    throw NotSupported(
                        "this pattern in a destructuring binding (only names, '_' and nested "
                        + "tuples — a binding cannot fail, so it cannot test)", span);
            }
        }
    }

    private bool LowerReturn(ReturnStmt stmt)
    {
        // A bare 'return;' in a coroutine ends it (§10): the same exit as the body running
        // through, from the middle. Without this case it emitted a valueless 'ret' from a
        // T-returning body — malformed IR the Debug verifier caught and Release ran. A valued
        // return in a coroutine is LYR-SEM0039 and never reaches this point.
        if (InCoroutine)
        {
            EmitAllPendingDefers();
            EmitCoroutineDoneExit(mark: true, stmt.Span);
            return false;
        }

        // The return value is evaluated BEFORE the defer bodies: a 'defer' must not change the value a
        // 'return' has already determined. Go behaves the same way.
        var returned = stmt.Value is null ? null : (TempId?)LowerExprAs(stmt.Value, _returnType);
        EmitAllPendingDefers();
        _b.Seal(new Return(returned, stmt.Span));
        return false;
    }

    private bool LowerBreak(BreakStmt stmt)
    {
        if (_loops.Count == 0) throw Bug($"'break' outside a loop at {stmt.Span}");
        var loop = _loops.Peek();
        EmitPendingDefersAbove(loop.DeferDepth); // break leaves the body scope — its defers run first
        _b.Seal(new Branch(loop.BreakTarget, stmt.Span));
        return false;
    }

    private bool LowerContinue(ContinueStmt stmt)
    {
        if (_loops.Count == 0) throw Bug($"'continue' outside a loop at {stmt.Span}");
        var loop = _loops.Peek();
        EmitPendingDefersAbove(loop.DeferDepth); // continue ends the iteration — same exit path
        _b.Seal(new Branch(loop.ContinueTarget, stmt.Span));
        return false;
    }

    /// <summary>
    /// The merge block is created only once at least one branch falls through. For
    /// <c>if (c) { return 1; } else { return 2; }</c> none arises: it would have no predecessors and the
    /// verifier would report it as unreachable.
    /// </summary>
    private bool LowerIf(IfStmt stmt)
    {
        var condition = LowerExpr(stmt.Condition);
        var thenBlock = _b.NewBlock();

        if (stmt.Else is null)
        {
            // Without an else the false branch is the merge block; it is reachable through the false edge
            // and may therefore arise immediately.
            var merge = _b.NewBlock();
            _b.Seal(new CondBranch(condition, thenBlock, merge, stmt.Span));

            _b.SwitchTo(thenBlock);
            if (LowerStatements(stmt.Then)) _b.Seal(new Branch(merge, stmt.Then.Span));

            _b.SwitchTo(merge);
            return true;
        }

        var elseBlock = _b.NewBlock();
        _b.Seal(new CondBranch(condition, thenBlock, elseBlock, stmt.Span));

        _b.SwitchTo(thenBlock);
        var thenFallsThrough = LowerStatements(stmt.Then);
        var thenExit = _b.CurrentId; // after nested control flow this is no longer thenBlock

        _b.SwitchTo(elseBlock);
        var elseFallsThrough = LowerStmt(stmt.Else); // a block or an else-if
        var elseExit = _b.CurrentId;

        if (!thenFallsThrough && !elseFallsThrough) return false;

        var mergeBlock = _b.NewBlock();
        if (thenFallsThrough) _b.SealBlock(thenExit, new Branch(mergeBlock, stmt.Then.Span));
        if (elseFallsThrough) _b.SealBlock(elseExit, new Branch(mergeBlock, stmt.Else.Span));

        _b.SwitchTo(mergeBlock);
        return true;
    }

    /// <summary>
    /// <c>for (x in e) { … }</c> — a loop over <c>next()</c>.
    ///
    /// <para>The body runs as long as <c>next()</c> yields a value; <c>null</c> ends the loop. That is
    /// the entire protocol, and it is the same for a user-written iterator as for the built-in forms,
    /// where the compiler obtains the iterator by building an adapter from <c>std.iter</c>.</para>
    ///
    /// <para>ONE CALL, NOT THREE. The alternative — check <c>hasNext()</c>, then fetch <c>next()</c> —
    /// asks the same question twice and can fall out of step between the two. Rust and Python use one
    /// call for the same reason.</para>
    /// </summary>
    private bool LowerForIn(ForInStmt stmt)
    {
        if (_types.RefOf(stmt) is not LocalSymbol loopVar)
            throw Bug($"loop variable '{stmt.Variable}' was not bound by the type checker");

        var (iterator, iteratorType, owner, yieldOverride) = BuildIterator(stmt);
        var elementType = LowerType(loopVar.Type, stmt.Span);
        // What the iterator PRODUCES: the range adapters carry i64/u64 regardless of the
        // range's width, and the value converts back to the element type below.
        var yieldType = yieldOverride ?? elementType;

        // The iterator lives in a slot: it is read on every pass and changes while doing so, and a temp
        // would no longer be valid after the first block.
        var slot = _slots.DeclareSynthetic("iter", iteratorType);
        _b.Emit(new StoreLocal(slot, iterator, stmt.Span));

        var condBlock = _b.NewBlock();
        _b.Seal(new Branch(condBlock, stmt.Span));

        _b.SwitchTo(condBlock);
        var current = _slots.NewTemp(iteratorType);
        _b.Emit(new LoadLocal(current, slot, iteratorType, stmt.Span));

        var optional = new IrOptionalType(yieldType);
        var produced = _slots.NewTemp(optional);
        EmitNextCall(produced, current, iteratorType, owner, optional, stmt.Span);

        var hasValue = _slots.NewTemp(BoolType);
        _b.Emit(new OptIsSome(hasValue, produced, stmt.Span));

        var bodyBlock = _b.NewBlock();
        var exitBlock = _b.NewBlock(); // before the body: 'break' needs its target
        _b.Seal(new CondBranch(hasValue, bodyBlock, exitBlock, stmt.Span));

        _b.SwitchTo(bodyBlock);

        // The 'optget' cannot panic: it stands behind the 'optissome' that carried the proof — the same
        // division of labour as in flow narrowing.
        var value = _slots.NewTemp(yieldType);
        _b.Emit(new OptGet(value, produced, yieldType, stmt.Span));

        // A range over a smaller width gets its values back at that width; the values fit by
        // construction (the bounds came from the element type).
        if (!yieldType.Equals(elementType))
        {
            var narrowed = _slots.NewTemp(elementType);
            _b.Emit(new Lyric.Ir.Convert(narrowed, yieldType, elementType, value, stmt.Span));
            value = narrowed;
        }

        var variable = _slots.DeclareFor(loopVar, elementType);
        _b.Emit(new StoreLocal(variable, value, stmt.Span));

        _loops.Push(new LoopScope(_b, condBlock, exitBlock) { DeferDepth = _defers.Count });
        // Through LowerScope, not LowerStatements: the loop body is a SCOPE, and a defer in it
        // runs at every iteration's end (§7.5) — registered into the enclosing function it ran
        // once, with the last iteration's values (the 2.0.1 bug).
        if (LowerScope(stmt.Body)) _b.Seal(new Branch(condBlock, stmt.Body.Span));
        _loops.Pop();

        _b.SwitchTo(exitBlock);
        return true;
    }

    /// <summary>
    /// Obtains the iterator for a <c>for-in</c> head.
    ///
    /// <para>A value that satisfies <c>Iterator&lt;T&gt;</c> itself is used directly. The built-in forms
    /// get an adapter from <c>std.iter</c>: they have no declaration a conformance could hang on.</para>
    /// </summary>
    // 'Yield' is what the ITERATOR produces when that differs from the loop variable's element
    // type — the range adapters carry i64/u64 while the range may be over a smaller width; the
    // loop converts at the edges. Null everywhere else.
    private (TempId Value, IrType Type, GenericInstance? Owner, IrType? Yield) BuildIterator(ForInStmt stmt)
    {
        // Substituted, because a 'for-in' can stand in a monomorphized instance:
        // 'fn total<T :: [P]>(xs: T[]) { for (x in xs) … }'. Without the substitution the ArrayIterator
        // would be interned with the type PARAMETER, and the type table would look for a class named 'T'.
        var source = SubstituteType(_types.TypeOf(stmt.Iterable));

        // 'Iterable<T>' first: the container SAYS how to walk it and yields a fresh cursor on every call.
        // Two loops over the same list therefore do not disturb each other — if the list were its own
        // iterator, they would.
        //
        // The return type is 'Iterator<T>', so an INTERFACE, and 'next()' therefore goes through
        // callvirt. That is the price of the decoupling and the same route an iterator takes that is
        // available through its interface.
        if (_types.Iterable is { } iterable
            && TypeFacts.SymbolOf(source) is { } carrier
            && Conformance.Implements(carrier, iterable, _typeTable.Binding)
            && carrier.Members.LookupLocal("iter") is FunctionSymbol iterMethod
            && iterMethod.Declaration is FunctionDecl iterDecl)
        {
            var target = source is GenericInstance owning
                ? _instances.RequestMethod(iterMethod, iterDecl, owning, stmt.Span)
                : TryResolveFunction(iterMethod, out var direct)
                    ? direct
                    : throw NotSupported($"'{carrier.Name}.iter' was not lowered", stmt.Span);

            // 'Iterator<T>' with the CONCRETE element type rather than the definition: a generic instance
            // has its own slot table, and 'callvirt' reads the index from it. Where the element type
            // comes from is long known to the sema — it stands on the symbol of the loop variable, and
            // deriving it again here would be a second truth.
            var element = _types.RefOf(stmt) is LocalSymbol bound
                ? SubstituteType(bound.Type)
                : throw Bug($"for-in at {stmt.Span} has no bound loop variable");

            var iteratorDefinition = _types.IteratorInterface ?? throw NotSupported(
                "iterating (std.iter is not on the module path)", stmt.Span);

            var cursorType = new IrInterfaceType(
                _typeTable.Intern(iteratorDefinition, [element]));

            var cursor = _slots.NewTemp(cursorType);
            _b.Emit(new Call(cursor, target, [LowerExpr(stmt.Iterable)], stmt.Span));
            return (cursor, cursorType, null, null);
        }

        if (source is ArrayOf array)
        {
            var owner = new GenericInstance(
                _types.ArrayIterator ?? throw NotSupported(
                    "iterating an array (std.iter is not on the module path)", stmt.Span),
                [array.Element]);

            var type = _typeTable.Intern(owner.Definition, owner.Arguments);
            var instance = _slots.NewTemp(new IrRefType(type));
            _b.Emit(new NewObject(instance, type, new IrRefType(type), stmt.Span));
            _b.Emit(new StoreField(instance, type, new FieldId(0), LowerExpr(stmt.Iterable), stmt.Span));
            _b.Emit(new StoreField(instance, type, new FieldId(1), IntConstant(0, stmt.Span), stmt.Span));
            return (instance, new IrRefType(type), owner, null);
        }

        // A string is walked over its code points, since a 'char' IS a code point. The adapter gets them
        // as an array: 'toChars' extracts them ONCE. An iterator calling 'charAt' instead would have to
        // count from the front on every step and would make the loop quadratic; that is not visible in a
        // 'for (c in s)'.
        if (source is PrimitiveType { Kind: PrimitiveKind.String })
        {
            var symbol = _types.StringIterator ?? throw NotSupported(
                "iterating a string (std.iter is not on the module path)", stmt.Span);

            var type = _typeTable.Intern(symbol);
            // The compiler-bound edge behind 'for (c in s)'. Bound to the private twin since 2.0:
            // the deprecated pub form went with the cut, the native stayed.
            var chars = CallHelper("std.string.rawToChars", stmt.Span, LowerExpr(stmt.Iterable));

            var instance = _slots.NewTemp(new IrRefType(type));
            _b.Emit(new NewObject(instance, type, new IrRefType(type), stmt.Span));
            _b.Emit(new StoreField(instance, type, new FieldId(0), chars, stmt.Span));
            _b.Emit(new StoreField(instance, type, new FieldId(1), IntConstant(0, stmt.Span),
                stmt.Span));
            return (instance, new IrRefType(type), null, null);
        }

        if (source is RangeOf ro && stmt.Iterable is RangeExpr range)
        {
            // Four adapters, not one. Folding 'a..=b' into 'a..b+1' was the 2.0.1 bug — at the
            // type's maximum the '+1' wraps and the loop runs zero times (§7.2); the inclusive
            // adapters carry a done flag instead of arithmetic on the bound. And a full-width
            // uint range cannot ride the SIGNED adapters — a bound beyond 2^63 reinterprets
            // and the comparison calls a range crossing the sign bit empty — so uint has its
            // own pair. Smaller widths embed into the carrier order-preserving; the loop head
            // converts the yielded value back (Yield below).
            var unsigned = ro.Element is PrimitiveType { Kind: PrimitiveKind.Uint or PrimitiveKind.Uint64 };
            var symbol = (range.IsInclusive, unsigned) switch
            {
                (false, false) => _types.RangeIterator,
                (true, false) => _types.InclusiveRangeIterator,
                (false, true) => _types.UnsignedRangeIterator,
                (true, true) => _types.InclusiveUnsignedRangeIterator,
            } ?? throw NotSupported("iterating a range (std.iter is not on the module path)", stmt.Span);

            var type = _typeTable.Intern(symbol);
            var carrierType = new IrScalarType(unsigned ? IrScalar.U64 : IrScalar.I64);
            var boundType = LowerType(ro.Element, stmt.Span);

            TempId Bound(Expr e)
            {
                var raw = LowerExprAs(e, boundType);
                if (boundType.Equals(carrierType)) return raw;
                var widened = _slots.NewTemp(carrierType);
                _b.Emit(new Lyric.Ir.Convert(widened, boundType, carrierType, raw, stmt.Span));
                return widened;
            }

            var low = Bound(range.Low);
            var high = Bound(range.High);

            var instance = _slots.NewTemp(new IrRefType(type));
            _b.Emit(new NewObject(instance, type, new IrRefType(type), stmt.Span));
            _b.Emit(new StoreField(instance, type, new FieldId(0), low, stmt.Span));
            _b.Emit(new StoreField(instance, type, new FieldId(1), high, stmt.Span));
            if (range.IsInclusive)
            {
                var notDone = _slots.NewTemp(BoolType);
                _b.Emit(new Const(notDone, BoolType, new BoolConst(false), stmt.Span));
                _b.Emit(new StoreField(instance, type, new FieldId(2), notDone, stmt.Span));
            }
            return (instance, new IrRefType(type), null, carrierType);
        }

        if (source is PrimitiveType { Kind: PrimitiveKind.String })
            throw NotSupported(
                "iterating a string (std.iter has no adapter for it yet — a string has no "
                + "'length' to walk with)", stmt.Span);

        // A user-written iterator is used directly.
        var own = LowerType(source, stmt.Span);
        return (LowerExpr(stmt.Iterable), own,
            SubstituteType(source) as GenericInstance, null);
    }

    /// <summary>The <c>next()</c> call: virtual when the iterator is available through its interface,
    /// otherwise directly on the instance.</summary>
    private void EmitNextCall(TempId dest, TempId iterator, IrType iteratorType,
        GenericInstance? owner, IrType returns, Core.Span span)
    {
        // When the iterator is available through its interface, only the runtime decides which
        // implementation runs — the one case in which 'for-in' dispatches dynamically.
        if (iteratorType is IrInterfaceType iface)
        {
            var slots = _typeTable.MethodSlotsOf(iface.Type);
            _b.Emit(new CallVirt(dest, iface.Type, Array.IndexOf(slots, "next"), [iterator],
                returns, span));
            return;
        }

        var declaring = owner?.Definition ?? IteratorSymbolOf(iteratorType, span);
        if (declaring.Members.LookupLocal("next") is not FunctionSymbol method
            || method.Declaration is not FunctionDecl decl)
            throw NotSupported($"'{declaring.Name}' has no 'next' to iterate with", span);

        // A concrete iterator is called directly: which function runs is settled, and a callvirt would
        // only consult a table whose answer the compiler already knows.
        var target = owner is not null
            ? _instances.RequestMethod(method, decl, owner, span)
            : TryResolveFunction(method, out var direct)
                ? direct
                : throw NotSupported($"'{declaring.Name}.next' was not lowered", span);

        _b.Emit(new Call(dest, target, [iterator], span));
    }

    /// <summary>The symbol behind a non-generic iterator value.</summary>
    private TypeSymbol IteratorSymbolOf(IrType type, Core.Span span)
    {
        if (type is IrRefType reference)
            foreach (var (symbol, id) in _typeTable.Interned)
                if (id == reference.Type) return symbol;

        throw NotSupported("iterating a value that is not an object", span);
    }

    private TempId IntConstant(long value, Core.Span span)
    {
        var type = new IrScalarType(IrScalar.I64);
        var dest = _slots.NewTemp(type);
        _b.Emit(new Const(dest, type, new IntConst(unchecked((ulong)value)), span));
        return dest;
    }

    private bool LowerWhile(WhileStmt stmt)
    {
        var condBlock = _b.NewBlock();
        _b.Seal(new Branch(condBlock, stmt.Span));

        _b.SwitchTo(condBlock);
        var condition = LowerExpr(stmt.Condition);
        var condExit = _b.CurrentId; // the condition may have produced blocks itself (&&, ||)

        var bodyBlock = _b.NewBlock();
        var exitBlock = _b.NewBlock(); // has to stand before the body: 'break' needs its target
        _b.SealBlock(condExit, new CondBranch(condition, bodyBlock, exitBlock, stmt.Condition.Span));

        _b.SwitchTo(bodyBlock);
        _loops.Push(new LoopScope(_b, condBlock, exitBlock) { DeferDepth = _defers.Count });
        // Through LowerScope, not LowerStatements: the loop body is a SCOPE, and a defer in it
        // runs at every iteration's end (§7.5) — registered into the enclosing function it ran
        // once, with the last iteration's values (the 2.0.1 bug).
        if (LowerScope(stmt.Body)) _b.Seal(new Branch(condBlock, stmt.Body.Span));
        _loops.Pop();

        _b.SwitchTo(exitBlock);
        return true; // always reachable through the condition's false edge
    }

    /// <summary>
    /// <c>do { … } while (cond);</c> — the body runs at least once, the condition stands behind it.
    ///
    /// <para>That makes it the only loop whose condition can be unreachable. If the body terminates on
    /// every path (<c>do { return 1; } while (true);</c>) nobody arrives at it, and the verifier rejects
    /// an unreachable block, because there is no <c>SimplifyCfg</c> pass.</para>
    ///
    /// <para>The blocks therefore arise ON DEMAND (see <see cref="LoopScope"/>). The question is "did
    /// anyone jump here", not "does the body fall through": a <c>break</c> reaches the exit even from a
    /// body that does not fall through.</para>
    /// </summary>
    private bool LowerDoWhile(DoWhileStmt stmt)
    {
        var bodyBlock = _b.NewBlock();
        _b.Seal(new Branch(bodyBlock, stmt.Span));

        _b.SwitchTo(bodyBlock);
        var loop = new LoopScope(_b) { DeferDepth = _defers.Count };
        _loops.Push(loop);
        var fallsThrough = LowerScope(stmt.Body);
        _loops.Pop();

        // The condition is needed when the body falls through OR a 'continue' jumps to it. Otherwise it
        // does not exist, and neither does the false edge to the exit.
        if (fallsThrough || loop.ContinueRequested)
        {
            var condBlock = loop.ContinueTarget;
            if (fallsThrough) _b.Seal(new Branch(condBlock, stmt.Body.Span));

            _b.SwitchTo(condBlock);
            var condition = LowerExpr(stmt.Condition);
            _b.Seal(new CondBranch(condition, bodyBlock, loop.BreakTarget, stmt.Condition.Span));
        }

        // No exit: the loop is never left. Control flow does not fall through here, and that is what
        // this method reports upwards, rather than leaving a block nobody enters.
        if (!loop.BreakRequested) return false;

        _b.SwitchTo(loop.BreakTarget);
        return true;
    }

    // ------------------------------------------------------------------ expressions

    private TempId LowerExpr(Expr expr) =>
        LowerExprOrVoid(expr) ?? throw Bug($"expression at {expr.Span} produced no value");

    /// <summary>Returns null only for a call to a void function, the one expression without a value.
    /// Otherwise always a temp.</summary>
    private TempId? LowerExprOrVoid(Expr expr) =>
        _chainReceivers.TryGetValue(expr, out var alreadyUnwrapped) ? alreadyUnwrapped : expr switch
    {
        IntLiteralExpr e => LowerIntLiteral(e),
        FloatLiteralExpr e => LowerFloatLiteral(e),
        BoolLiteralExpr e => EmitConst(new BoolConst(e.Value), TypeOfExpr(e), e.Span),
        CharLiteralExpr e => EmitConst(new CharConst(e.CodePoint), TypeOfExpr(e), e.Span),
        StringLiteralExpr e => EmitConst(new StringConst(e.Value), TypeOfExpr(e), e.Span),
        IdentifierExpr e => LowerIdentifier(e),
        UnaryExpr e => LowerUnary(e),
        PostfixExpr e => LowerPostfix(e),
        BinaryExpr e => LowerBinary(e),
        AssignExpr e => LowerAssign(e),
        CastExpr e => LowerCast(e),
        CallExpr e => LowerCall(e),
        IfExpr e => LowerIfExpr(e),

        InterpolatedStringExpr e => LowerInterpolatedString(e),

        NullLiteralExpr e => LowerNull(e),
        LambdaExpr e => LowerLambda(e),
        TupleLitExpr e => LowerTupleLiteral(e),
        MatchExpr e => LowerMatch(e.Scrutinee, e.Arms, TypeOfExpr(e), e.Span)
                       ?? throw Bug($"match expression produced no value at {e.Span}"),
        MemberExpr e => LowerFieldRead(e),
        IndexExpr e => LowerIndexRead(e),
        ArrayLitExpr e => LowerArrayLiteral(e),
        StructInitExpr e => LowerObjectInit(e),
        RangeExpr e => throw NotSupported("range expression", e.Span),
        ResumeExpr e => LowerResume(e),
        ThisExpr e => LowerThis(e),
        AtIdentifierExpr e => throw NotSupported($"attribute '{e.Name}'", e.Span),
        ErrorExpr e => throw Bug($"error expression reached lowering at {e.Span}"),

        _ => throw Bug($"unhandled expression {expr.GetType().Name}")
    };

    private TempId LowerIntLiteral(IntLiteralExpr expr)
    {
        var type = TypeOfExpr(expr);

        // An untyped integer literal in float context IS a float value; it is not converted. A
        // `let f: float = 5;` therefore has to become a FloatConst — an IntConst with a float type would
        // be malformed, and the verifier says so.
        if (type is IrScalarType { Kind: IrScalar.F32 or IrScalar.F64 })
            return EmitConst(new FloatConst(expr.Value), type, expr.Span);

        // The encoding of IntConst is two's complement, zero-extended to 64 bits. The parser yields the
        // magnitude; a minus sign is a UnaryExpr(Neg) of its own.
        return EmitConst(new IntConst(expr.Value), type, expr.Span);
    }

    private TempId LowerFloatLiteral(FloatLiteralExpr expr)
    {
        var type = TypeOfExpr(expr);
        // f32 has to be narrowed here: a const of type f32 whose value is no f32 value would be
        // malformed, and the verifier reports it. The narrowing belongs in the lowering, so the value in
        // the bytecode is deterministically the same.
        var value = type is IrScalarType { Kind: IrScalar.F32 } ? (float)expr.Value : expr.Value;
        return EmitConst(new FloatConst(value), type, expr.Span);
    }

    private TempId LowerIdentifier(IdentifierExpr expr)
    {
        var symbol = _types.RefOf(expr) ?? throw Bug($"identifier '{expr.Name}' is unbound");

        // A module 'let' has no frame slot but a global one.
        if (TryLowerGlobalIdentifier(expr) is { } global) return global;

        // In a coroutine every variable lives in the state object.
        if (InCoroutine && _stateFields.TryGetValue(symbol, out var stateField))
            return Narrow(expr, LoadStateField(stateField, expr.Span), _stateTypes[stateField]);

        // In a lifted lambda a captured symbol lies in the environment rather than in a slot. Slots are
        // asked first: a local symbol of the same name IS a different symbol, and reference equality
        // keeps the two apart.
        if (!_slots.TryLookup(symbol, out var slot))
        {
            if (_captureFields.ContainsKey(symbol))
            {
                var (capturedType, capturedValue) = LoadCaptured(symbol, expr.Span);

                // When the captured thing is a cell, its content is what is at issue here rather than
                // the cell itself: the cell is a carrier, not a value of the program.
                if (capturedType is IrRefType reference && _typeTable.IsCell(reference.Type))
                {
                    var inner = _typeTable.Defs[reference.Type.Value].FieldTypes[0];
                    var unwrapped = _slots.NewTemp(inner);
                    _b.Emit(new LoadField(unwrapped, capturedValue, reference.Type, new FieldId(0),
                        inner, expr.Span));
                    return Narrow(expr, unwrapped, inner);
                }

                return Narrow(expr, capturedValue, capturedType);
            }

            // A declared function as a VALUE: 'map(o, double)' rather than
            // 'map(o, (n: int) => double(n))'.
            //
            // It is a closure without an environment, nothing more. 'MakeClosure' takes its environment
            // optionally — the common case '(x) => x > 0' captures nothing — and the VM decides from the
            // 'HasEnvironment' bit whether slot 0 is occupied.
            //
            // 'Reachability' already knows 'MakeClosure' as a root, so a function referenced only this
            // way does not fall victim to the reachability analysis.
            if (symbol is FunctionSymbol function)
            {
                // BEFORE the type computation: `TypeOfExpr` on a generic signature throws itself, with
                // "type parameter 'T' reached lowering unsubstituted" — a message about the compiler's
                // internals rather than about the program.
                if (function.Declaration is FunctionDecl { Generics.Length: > 0 })
                    throw NotSupported(
                        $"a generic function ('{expr.Name}') as a value — the type arguments have "
                        + "no call site to come from; wrap it in a lambda", expr.Span);

                if (TypeOfExpr(expr) is not IrFunctionType signature
                    || !TryResolveFunction(function, out var target))
                    throw NotSupported($"reference to '{expr.Name}' as a value", expr.Span);

                var closure = _slots.NewTemp(signature);
                _b.Emit(new MakeClosure(closure, target, null, signature, expr.Span));
                return closure;
            }

            throw NotSupported($"reference to '{expr.Name}' (only parameters, locals and constants)",
                expr.Span);
        }

        return Narrow(expr, LoadValue(slot, expr.Span), ValueTypeOf(slot));
    }

    /// <summary>
    /// Flow narrowing: after <c>if (x != null)</c> the sema says the type of x is T, while the place in
    /// memory still holds ?T — the narrowing is a statement about control flow, not about memory. It is
    /// redeemed here: the lowerer unwraps where the sema expects T.
    ///
    /// <para>That this is sound was proven by the sema, which narrows only where it excluded null. The
    /// <c>optget</c> can therefore never panic; it is the materialization of a proof already made.</para>
    ///
    /// <para>Pulled out when captures arrived: a captured <c>?T</c> needs the same narrowing as a local
    /// one, and two copies of the same four lines would have been two places it could have been missing
    /// from.</para>
    /// </summary>
    private TempId Narrow(Expr expr, TempId value, IrType type)
    {
        if (type is not IrOptionalType option || TypeOfExpr(expr) is IrOptionalType) return value;

        var narrowed = _slots.NewTemp(option.Inner);
        _b.Emit(new OptGet(narrowed, value, option.Inner, expr.Span));
        return narrowed;
    }

    private TempId LowerUnary(UnaryExpr expr)
    {
        if (expr.Operator is UnaryOp.PreInc or UnaryOp.PreDec)
            return LowerIncDec(expr.Operand, expr.Operator is UnaryOp.PreInc,
                yieldOldValue: false, expr.Span);

        var operand = LowerExpr(expr.Operand);
        var type = TypeOfExpr(expr);
        var dest = _slots.NewTemp(type);
        _b.Emit(new UnOp(dest, IrUnKindExtensions.FromAst(expr.Operator), type, operand, expr.Span));
        return dest;
    }

    private TempId LowerPostfix(PostfixExpr expr) => expr.Operator switch
    {
        PostfixOp.Inc => LowerIncDec(expr.Operand, increment: true, yieldOldValue: true, expr.Span),
        PostfixOp.Dec => LowerIncDec(expr.Operand, increment: false, yieldOldValue: true, expr.Span),
        PostfixOp.ForceUnwrap => LowerForceUnwrap(expr.Operand, expr.Span),
        _ => throw Bug($"unhandled postfix operator {expr.Operator}")
    };

    /// <summary><c>++</c> and <c>--</c> in both positions: prefix yields the new value, postfix the old.
    /// Both write the same store.</summary>
    private TempId LowerIncDec(Expr target, bool increment, bool yieldOldValue, Span span)
    {
        var slot = ResolveLocalTarget(target, "increment/decrement");
        var type = _slots.TypeOfLocal(slot);

        var oldValue = _slots.NewTemp(type);
        _b.Emit(new LoadLocal(oldValue, slot, type, span));

        var one = EmitConst(OneFor(type, span), type, span);
        var newValue = _slots.NewTemp(type);
        _b.Emit(new BinOp(newValue, increment ? IrBinKind.Add : IrBinKind.Sub, type,
            oldValue, one, span));
        _b.Emit(new StoreLocal(slot, newValue, span));

        return yieldOldValue ? oldValue : newValue;
    }

    private TempId LowerBinary(BinaryExpr expr)
    {
        if (expr.Operator is BinaryOp.LogicalAnd or BinaryOp.LogicalOr)
            return LowerShortCircuit(expr);
        if (expr.Operator is BinaryOp.Coalesce)
            return LowerCoalesce(expr);
        if (TryLowerNullTest(expr) is { } nullTest)
            return nullTest;

        // An operator on a conforming type IS a method call, built and checked by the sema. Lowering
        // the stored call routes through the ordinary dispatch, so every receiver shape — plain,
        // generic instance, extension, constraint — behaves exactly as the written call would. The
        // operands are the REAL operand nodes, lowered once, here.
        //
        // What to make of the result follows from the operator on this node: '==' is the call
        // itself, '!=' negates it, and the four orderings read the SIGN of what 'compare' answered —
        // against zero, with the same comparison instruction an 'int < int' emits.
        if (_types.OperatorCallOf(expr) is { } desugared)
        {
            var value = LowerCall(desugared)
                        ?? throw Bug($"operator method for '{expr.Operator}' returned no value");

            switch (expr.Operator)
            {
                // Equality and arithmetic ARE their calls; nothing follows.
                case BinaryOp.Eq or BinaryOp.Add or BinaryOp.Sub or BinaryOp.Mul or BinaryOp.Div:
                    return value;

                case BinaryOp.Ne:
                    var negated = _slots.NewTemp(BoolType);
                    _b.Emit(new UnOp(negated, IrUnKind.Not, BoolType, value, expr.Span));
                    return negated;

                default:
                    var zero = EmitConst(new IntConst(0),
                        new IrScalarType(IrScalar.I64), expr.Span);
                    return EmitBinary(IrBinKindExtensions.FromAst(expr.Operator),
                        TypeOfExpr(expr), value, zero, expr.Span);
            }
        }

        var kind = IrBinKindExtensions.FromAst(expr.Operator);
        var lhs = LowerExpr(expr.Left);
        var rhs = LowerExpr(expr.Right);
        return EmitBinary(kind, TypeOfExpr(expr), lhs, rhs, expr.Span);
    }

    /// <summary>
    /// Applies a binary operator to two lowered operands of <paramref name="type"/>.
    ///
    /// <para>THE ONLY PLACE THAT DECIDES WHAT AN OPERATOR BECOMES. <c>a + b</c> and <c>a += b</c> are the
    /// same operator on the same types and have to reach the same instruction; when the compound paths
    /// emitted their own <c>BinOp</c>, <c>s += "x"</c> produced <c>add string</c>, which no release build
    /// rejects and the VM evaluates as an integer addition of two references.</para>
    ///
    /// <para><paramref name="type"/> is the type of the RESULT, which for a comparison is <c>bool</c>
    /// while the operands are not — hence the comparison guard on the string branch.</para>
    /// </summary>
    private TempId EmitBinary(IrBinKind kind, IrType type, TempId lhs, TempId rhs, Span span)
    {
        // xs + ys and xs * n are built-in language semantics but NO BinOp: the add opcode would otherwise
        // stay polymorphic and would have to dispatch on the type at runtime — the same reasoning as for
        // string + string, only with an instruction of its own instead of a call.
        if (type is IrArrayType array)
        {
            var built = _slots.NewTemp(type);
            _b.Emit(kind switch
            {
                IrBinKind.Add => new ArrayConcat(built, lhs, rhs, array.Element, span),
                IrBinKind.Mul => new ArrayRepeat(built, lhs, rhs, array.Element, span),
                _ => throw NotSupported($"'{IrNames.Bin(kind)}' on arrays", span),
            });
            return built;
        }

        // '+' and '*' are overloaded for string; that is built-in semantics but NO BinOp — the add opcode
        // would otherwise be polymorphic and would have to dispatch on the type at runtime. It lowers to
        // a call in std.string, exactly as the f-string lowering assembles its parts.
        if (!kind.IsComparison() && type is IrScalarType { Kind: IrScalar.String })
            return kind switch
            {
                IrBinKind.Add => CallHelper("std.string.concat", span, lhs, rhs),
                IrBinKind.Mul => CallHelper("std.string.repeat", span, lhs, rhs),
                _ => throw NotSupported($"'{IrNames.Bin(kind)}' on strings", span),
            };

        var dest = _slots.NewTemp(type);
        _b.Emit(new BinOp(dest, kind, type, lhs, rhs, span));
        return dest;
    }

    /// <summary>
    /// <c>a &amp;&amp; b</c> and <c>a || b</c>: the right operand may run only conditionally, so control
    /// flow. The result travels through a synthetic local, because a temp may be defined only once.
    /// </summary>
    private TempId LowerShortCircuit(BinaryExpr expr)
    {
        var isAnd = expr.Operator is BinaryOp.LogicalAnd;
        var slot = _slots.DeclareSynthetic(isAnd ? "and" : "or", BoolType);

        var left = LowerExpr(expr.Left);
        _b.Emit(new StoreLocal(slot, left, expr.Left.Span));

        var rhsBlock = _b.NewBlock();
        var mergeBlock = _b.NewBlock();
        // '&&' evaluates the right side only on true, '||' only on false; the edges are swapped.
        _b.Seal(isAnd
            ? new CondBranch(left, rhsBlock, mergeBlock, expr.Span)
            : new CondBranch(left, mergeBlock, rhsBlock, expr.Span));

        _b.SwitchTo(rhsBlock);
        var right = LowerExpr(expr.Right);
        _b.Emit(new StoreLocal(slot, right, expr.Right.Span));
        _b.Seal(new Branch(mergeBlock, expr.Right.Span));

        _b.SwitchTo(mergeBlock);
        var dest = _slots.NewTemp(BoolType);
        _b.Emit(new LoadLocal(dest, slot, BoolType, expr.Span));
        return dest;
    }

    /// <summary>Like <see cref="LowerShortCircuit"/>, but with two writing branches. Both branches are
    /// expressions and are guaranteed to yield a value, so they always fall through — unlike the if
    /// STATEMENT, no case distinction is needed here.</summary>
    private TempId LowerIfExpr(IfExpr expr)
    {
        var type = TypeOfExpr(expr);
        var slot = _slots.DeclareSynthetic("if", type);

        var condition = LowerExpr(expr.Condition);
        var thenBlock = _b.NewBlock();
        var elseBlock = _b.NewBlock();
        _b.Seal(new CondBranch(condition, thenBlock, elseBlock, expr.Span));

        // `LowerExprAs` rather than `LowerExpr`: the branch type need not be the result type.
        // `if (c) 5 else null` is `?int`, and both branches need the target type — the `null` because it
        // has none of its own, and the `5` because it has to be wrapped.
        _b.SwitchTo(thenBlock);
        _b.Emit(new StoreLocal(slot, LowerExprAs(expr.Then, type), expr.Then.Span));
        var thenExit = _b.CurrentId;

        _b.SwitchTo(elseBlock);
        _b.Emit(new StoreLocal(slot, LowerExprAs(expr.Else, type), expr.Else.Span));
        var elseExit = _b.CurrentId;

        var mergeBlock = _b.NewBlock();
        _b.SealBlock(thenExit, new Branch(mergeBlock, expr.Then.Span));
        _b.SealBlock(elseExit, new Branch(mergeBlock, expr.Else.Span));

        _b.SwitchTo(mergeBlock);
        var dest = _slots.NewTemp(type);
        _b.Emit(new LoadLocal(dest, slot, type, expr.Span));
        return dest;
    }

    private TempId LowerAssign(AssignExpr expr)
    {
        if (expr.Target is MemberExpr member) return LowerFieldAssign(member, expr);
        if (expr.Target is IndexExpr indexed) return LowerElementAssign(indexed, expr);

        if (TryStateField(expr.Target, out var stateField))
            return LowerStateAssign(expr, stateField);

        if (TryCapturedCell(expr.Target, out var cell, out var cellType, out var cellValueType))
            return LowerCapturedAssign(expr, cell, cellType, cellValueType);

        var slot = ResolveLocalTarget(expr.Target, "assignment");

        if (expr.Operator is null)
        {
            // The slot type is the expected shape; otherwise 'var d: Damageable; d = p;' would put a bare
            // class reference into an interface slot.
            var value = LowerExprAs(expr.Value, ValueTypeOf(slot));
            StoreValue(slot, value, expr.Span);
            return value;
        }

        if (expr.Operator is BinaryOp.Coalesce) return LowerCoalesceAssign(slot, expr);

        if (expr.Operator is BinaryOp.LogicalAnd or BinaryOp.LogicalOr)
            throw NotSupported("short-circuit assignment ('&&=' / '||=')", expr.Span);

        // Through the operator interface when the sema desugared one: the stored call lowers the
        // real operand nodes — the identifier receiver loads once, exactly like the read below.
        if (_types.OperatorCallOf(expr) is { } operatorCall)
        {
            var combined = LowerCall(operatorCall)
                ?? throw Bug("operator compound returned no value");
            StoreValue(slot, combined, expr.Span);
            return combined;
        }

        var type = ValueTypeOf(slot);
        var current = LoadValue(slot, expr.Target.Span);

        var operand = LowerExpr(expr.Value);
        var result = EmitBinary(IrBinKindExtensions.FromAst(expr.Operator.Value), type,
            current, operand, expr.Span);
        StoreValue(slot, result, expr.Span);
        return result;
    }

    /// <summary>
    /// <c>resume co</c> — continue the coroutine and fetch the next value.
    ///
    /// <para>An ordinary <c>callind</c>: the coroutine value IS a function value over the state object.
    /// The jump table in the body makes the call continue where the last <c>yield</c> stopped; from here
    /// it looks like any other call, and that is the whole point of the transformation.</para>
    /// </summary>
    private TempId LowerResume(ResumeExpr expr)
    {
        if (LowerType(_types.TypeOf(expr.Coroutine), expr.Span) is not IrFunctionType signature)
            throw Bug("'resume' on a value that is not a coroutine");

        // Lenient = false: exhaustion panics, the form the specification promises for 'resume'.
        var coroutine = LowerExpr(expr.Coroutine);
        var strict = EmitConst(new BoolConst(false), BoolType, expr.Span);
        var dest = _slots.NewTemp(signature.Return);
        _b.Emit(new CallIndirect(dest, coroutine, [strict], signature.Return, expr.Span));
        _fresh.Add(dest);
        return dest;
    }

    /// <summary>
    /// <c>co.next()</c> — pull with the lenient flag set, then read the done state back through
    /// the compiler-bound native <c>std.core.coroutineIsDone</c>.
    ///
    /// <para>The pull comes FIRST: 'next' advances, and the question is whether THAT pull found
    /// the end. The import is interned per signature — the yield type stands in it, so the
    /// verifier checks the argument like any other, and the emitted entries bind against one
    /// registry implementation that compares tags. An older runtime rejects at binding with the
    /// import's name in the message, the format's designed forward path; a module that never
    /// calls 'next' carries no such import and keeps loading everywhere.</para>
    /// </summary>
    private TempId LowerCoroutineNext(MemberExpr member, CoroutineOf type, Core.Span span)
    {
        var yield = LowerType(type.Yield, span);
        var signature = TypeTable.CoroutineSignature(yield);

        var coroutine = LowerExpr(member.Target);
        var lenient = EmitConst(new BoolConst(true), BoolType, span);
        var raw = IsVoid(yield) ? (TempId?)null : _slots.NewTemp(yield);
        _b.Emit(new CallIndirect(raw, coroutine, [lenient], yield, span));

        var import = _imports.Intern(
            new IrImport("std.core.coroutineIsDone", [signature], BoolType));
        var done = _slots.NewTemp(BoolType);
        _b.Emit(new CallImport(done, import, [coroutine], span));

        // A void coroutine has no value to wrap; its 'next' answers whether it advanced.
        if (IsVoid(yield))
        {
            var advanced = _slots.NewTemp(BoolType);
            _b.Emit(new UnOp(advanced, IrUnKind.Not, BoolType, done, span));
            _fresh.Add(advanced);
            return advanced;
        }

        // '?T' assembles across blocks; the value crosses through a synthetic local — the rule
        // this IR replaces phi nodes with.
        var result = new IrOptionalType(yield);
        var slot = _slots.Declare("<next>", result);
        var carried = _slots.Declare("<pulled>", yield);
        _b.Emit(new StoreLocal(carried, raw!.Value, span));

        var none = _b.NewBlock();
        var some = _b.NewBlock();
        var join = _b.NewBlock();
        _b.Seal(new CondBranch(done, none, some, span));

        _b.SwitchTo(none);
        var empty = _slots.NewTemp(result);
        _b.Emit(new OptNone(empty, yield, span));
        _b.Emit(new StoreLocal(slot, empty, span));
        _b.Seal(new Branch(join, span));

        _b.SwitchTo(some);
        var value = _slots.NewTemp(yield);
        _b.Emit(new LoadLocal(value, carried, yield, span));
        var wrapped = _slots.NewTemp(result);
        _b.Emit(new OptSome(wrapped, value, yield, span));
        _b.Emit(new StoreLocal(slot, wrapped, span));
        _b.Seal(new Branch(join, span));

        _b.SwitchTo(join);
        var dest = _slots.NewTemp(result);
        _b.Emit(new LoadLocal(dest, slot, result, span));
        _fresh.Add(dest);
        return dest;
    }

    /// <summary>
    /// <c>yield x</c> — the point where the coroutine stops and later starts again.
    ///
    /// <para>Three steps: write the re-entry point into the state object, return the value, and remember
    /// the block AFTER it as a target. What stands after it runs only at the next <c>resume</c>, which is
    /// why the body of a coroutine is no longer one continuous control flow but a set of entry
    /// points.</para>
    ///
    /// <para>The re-entry point is written BEFORE leaving rather than after: there is no after. A
    /// <c>ret</c> ends the frame; the object is the only thing that remains.</para>
    /// </summary>
    private bool LowerYield(YieldStmt stmt)
    {
        if (!InCoroutine) throw Bug("'yield' outside a coroutine body reached the lowerer");

        // The next entry point has the number n+1: 0 means "not started yet".
        var point = _resumePoints.Count + 1;

        var marker = _slots.NewTemp(new IrScalarType(IrScalar.I32));
        _b.Emit(new Const(marker, new IrScalarType(IrScalar.I32), new IntConst((ulong)point), stmt.Span));
        StoreStateField(0, marker, stmt.Span);

        var value = stmt.Value is null
            ? null
            : (TempId?)LowerExprAs(stmt.Value, _returnType);

        _b.Seal(new Return(value, stmt.Span));

        // Execution continues here at the next 'resume'.
        var continuation = _b.NewBlock();
        _resumePoints.Add(continuation);
        _b.SwitchTo(continuation);

        return true;
    }

    /// <summary>
    /// The first block of a coroutine: it jumps to where the coroutine stopped.
    ///
    /// <para>It arises LAST — before that the entry points are unknown. That it is nevertheless the first
    /// is stated by <see cref="IrFunction.Entry"/>; the IR numbers blocks, it does not order them.</para>
    ///
    /// <para>A chain of comparisons rather than a jump table: the IR has no <c>switch</c> terminator, and
    /// introducing one for this alone would be an opcode for a single use case. At the sizes involved —
    /// one comparison per <c>yield</c> in the source — the difference is not measurable.</para>
    /// </summary>
    private void BuildResumeDispatch(BlockId dispatch, BlockId start, Core.Span span)
    {
        _b.SwitchTo(dispatch);

        var i32 = new IrScalarType(IrScalar.I32);
        var current = LoadStateField(0, span);

        // The end state first: -1 means "the body ran through", and what a further call gets is
        // the lenient flag's decision. Without this check the comparison chain would run into
        // nothing and the coroutine would start over — silently and wrongly.
        var ended = _slots.NewTemp(i32);
        _b.Emit(new Const(ended, i32, new IntConst(unchecked((ulong)(uint)-1)), span));

        var isEnded = _slots.NewTemp(BoolType);
        _b.Emit(new BinOp(isEnded, IrBinKind.Eq, BoolType, current, ended, span));

        var endedBlock = _b.NewBlock();
        var live = _b.NewBlock();
        _b.Seal(new CondBranch(isEnded, endedBlock, live, span));

        _b.SwitchTo(endedBlock);
        EmitCoroutineDoneExit(mark: false, span);

        _b.SwitchTo(live);

        for (var i = 0; i < _resumePoints.Count; i++)
        {
            var wanted = _slots.NewTemp(i32);
            _b.Emit(new Const(wanted, i32, new IntConst((ulong)(i + 1)), span));

            var matches = _slots.NewTemp(BoolType);
            // For a comparison BinOp.Type carries the RESULT type rather than that of the operands, the
            // same convention as for every other comparison in the lowering.
            _b.Emit(new BinOp(matches, IrBinKind.Eq, BoolType, current, wanted, span));

            var next = _b.NewBlock();
            _b.Seal(new CondBranch(matches, _resumePoints[i], next, span));
            _b.SwitchTo(next);
        }

        // No match means "not started yet": the body begins from the front.
        _b.Seal(new Branch(start, span));
    }

    /// <summary>Does this assignment target point at a variable in the state object?</summary>
    private bool TryStateField(Expr target, out int field)
    {
        field = -1;
        if (!InCoroutine || target is not IdentifierExpr identifier) return false;
        if (_types.RefOf(identifier) is not { } symbol) return false;
        return _stateFields.TryGetValue(symbol, out field);
    }

    /// <summary>An assignment to a variable in the state object: the same three forms as on the slot
    /// path, except that reading and writing happen where the variable survives the <c>yield</c>.
    /// </summary>
    private TempId LowerStateAssign(AssignExpr expr, int field)
    {
        var type = _stateTypes[field];

        if (expr.Operator is null)
        {
            var assigned = LowerExprAs(expr.Value, type);
            StoreStateField(field, assigned, expr.Span);
            return assigned;
        }

        if (expr.Operator is BinaryOp.Coalesce or BinaryOp.LogicalAnd or BinaryOp.LogicalOr)
            throw NotSupported($"'{expr.Operator}=' on a coroutine local", expr.Span);

        if (_types.OperatorCallOf(expr) is { } operatorCall)
        {
            var combined = LowerCall(operatorCall)
                ?? throw Bug("operator compound returned no value");
            StoreStateField(field, combined, expr.Span);
            return combined;
        }

        var current = LoadStateField(field, expr.Target.Span);
        var operand = LowerExpr(expr.Value);
        var result = EmitBinary(IrBinKindExtensions.FromAst(expr.Operator.Value), type,
            current, operand, expr.Span);
        StoreStateField(field, result, expr.Span);
        return result;
    }

    /// <summary>
    /// An assignment to a captured cell. The same three forms as on the slot path, except that reading
    /// and writing happen where the variable really lives.
    /// </summary>
    private TempId LowerCapturedAssign(AssignExpr expr, TempId cell, TypeId cellType, IrType type)
    {
        if (expr.Operator is null)
        {
            var assigned = LowerExprAs(expr.Value, type);
            _b.Emit(new StoreField(cell, cellType, new FieldId(0), assigned, expr.Span));
            return assigned;
        }

        if (expr.Operator is BinaryOp.Coalesce or BinaryOp.LogicalAnd or BinaryOp.LogicalOr)
            throw NotSupported($"'{expr.Operator}=' on a captured variable", expr.Span);

        if (_types.OperatorCallOf(expr) is { } operatorCall)
        {
            var combined = LowerCall(operatorCall)
                ?? throw Bug("operator compound returned no value");
            _b.Emit(new StoreField(cell, cellType, new FieldId(0), combined, expr.Span));
            return combined;
        }

        var current = _slots.NewTemp(type);
        _b.Emit(new LoadField(current, cell, cellType, new FieldId(0), type, expr.Target.Span));

        var operand = LowerExpr(expr.Value);
        var result = EmitBinary(IrBinKindExtensions.FromAst(expr.Operator.Value), type,
            current, operand, expr.Span);
        _b.Emit(new StoreField(cell, cellType, new FieldId(0), result, expr.Span));
        return result;
    }

    /// <summary>
    /// <c>obj.f = v</c> and <c>obj.f += v</c>.
    ///
    /// <para>THE OBJECT IS EVALUATED EXACTLY ONCE. For <c>+=</c> that is the difference between right and
    /// wrong as soon as the target expression has side effects: <c>next().f += 1</c> must not call
    /// <c>next()</c> twice. The reference is therefore lowered once into a temp and reused for reading
    /// and writing.</para>
    /// </summary>
    private TempId LowerFieldAssign(MemberExpr member, AssignExpr expr)
    {
        var (obj, type, field, fieldType) = ResolveFieldAccess(member);

        if (expr.Operator is null)
        {
            var assigned = LowerExprAs(expr.Value, fieldType);
            _b.Emit(new StoreField(obj, type, field, assigned, expr.Span));
            return assigned;
        }

        if (expr.Operator is BinaryOp.LogicalAnd or BinaryOp.LogicalOr or BinaryOp.Coalesce)
            throw NotSupported("short-circuit or coalescing assignment", expr.Span);

        var current = _slots.NewTemp(fieldType);
        _b.Emit(new LoadField(current, obj, type, field, fieldType, member.Span));

        var operand = LowerExpr(expr.Value);
        var result = EmitBinary(IrBinKindExtensions.FromAst(expr.Operator.Value), fieldType,
            current, operand, expr.Span);
        _b.Emit(new StoreField(obj, type, field, result, expr.Span));
        return result;
    }

    /// <summary><c>this</c> is slot 0. That it exists was checked by the sema (<c>LYR-SEM0008</c> in a
    /// static method), so a missing slot is a bug here.</summary>
    private TempId LowerThis(ThisExpr expr)
    {
        if (_thisSlot is not { } slot || _thisType is not { } type)
            throw Bug($"'this' reached lowering outside an instance method at {expr.Span}");

        var dest = _slots.NewTemp(type);
        _b.Emit(new LoadLocal(dest, slot, type, expr.Span));
        return dest;
    }

    /// <summary>
    /// <c>xs[i] = v</c> and <c>xs[i] += v</c>.
    ///
    /// <para>Array AND index are evaluated exactly once — for <c>+=</c> that is the difference between
    /// right and wrong as soon as either has side effects: <c>xs[next()] += 1</c> must not call
    /// <c>next()</c> twice.</para>
    /// </summary>
    private TempId LowerElementAssign(IndexExpr indexed, AssignExpr expr)
    {
        // 'xs[i] = v' on a container is 'Indexable<T>.set(i, v)'. A compound assignment ('xs[i] += 1') is
        // NOT covered here and reports as a scope boundary: it would need a read and a write with the
        // same index, and whether the index may be evaluated twice is a language question the spec does
        // not answer.
        if (TypeOfExpr(indexed.Target) is not IrArrayType)
        {
            if (expr.Operator is not null)
                throw NotSupported("compound assignment on a container (only on arrays)",
                    expr.Span);

            var stored = LowerExpr(expr.Value);
            if (LowerIndexableCall(indexed, "set", stored) is null)
                ResolveIndexAccess(indexed); // reports the scope boundary with the type name
            return stored;
        }

        var (array, index, element) = ResolveIndexAccess(indexed);

        if (expr.Operator is null)
        {
            // Through the EXPECTED type rather than bare: otherwise 'xs[i] = null' on a '(?T)[]' has no
            // target type for 'null' to take its shape from, and a 'T' in a '?T' slot would stay
            // unwrapped. The same rule as for 'stloc' in LowerAssign.
            var assigned = LowerExprAs(expr.Value, element);
            _b.Emit(new StoreElem(array, index, assigned, expr.Span));
            return assigned;
        }

        if (expr.Operator is BinaryOp.LogicalAnd or BinaryOp.LogicalOr or BinaryOp.Coalesce)
            throw NotSupported("short-circuit or coalescing assignment", expr.Span);

        var current = _slots.NewTemp(element);
        _b.Emit(new LoadElem(current, array, index, element, indexed.Span));

        var operand = LowerExpr(expr.Value);
        var result = EmitBinary(IrBinKindExtensions.FromAst(expr.Operator.Value), element,
            current, operand, expr.Span);
        _b.Emit(new StoreElem(array, index, result, expr.Span));
        return result;
    }

    // ------------------------------------------------------------------ enums

    /// <summary>
    /// The enum entry a value belongs to, or a scope boundary.
    ///
    /// <para>THE TYPE IS ASKED, NOT THE SYMBOL. <c>TypeFacts.SymbolOf</c> yields the definition for a
    /// <c>GenericInstance</c> and throws the type arguments away, which would land
    /// <c>Opt&lt;int&gt;</c> and <c>Opt&lt;string&gt;</c> on the same entry. The sema type of the
    /// expression carries them, so it comes from there.</para>
    /// </summary>
    private IrEnumType RequireEnum(Expr expr) => RequireEnum(_types.TypeOf(expr), expr.Span);

    private IrEnumType RequireEnum(LyrType type, Span span) => SubstituteType(type) switch
    {
        NamedRef { Symbol: { Kind: TypeSymbolKind.Enum } symbol } => _typeTable.EnumOf(symbol),
        GenericInstance { Definition.Kind: TypeSymbolKind.Enum } instance
            => new IrEnumType(_typeTable.Intern(instance.Definition, instance.Arguments)),
        _ => throw NotSupported($"'{TypeFacts.Display(type)}' is not an enum", span),
    };

    /// <summary><c>Shape.Circle(2.0)</c> and <c>Shape.Empty</c> — a tuple variant and a unit variant.
    /// The struct form <c>Triangle { a = … }</c> goes through <see cref="LowerObjectInit"/>.</summary>
    /// <param name="constructed">The expression whose type is the constructed INSTANCE: for
    /// <c>Shape.Circle(2.0)</c> the call, for the unit variant <c>Shape.Empty</c> the member itself. It
    /// does not stand at the target — <c>Opt.Some(5)</c> names its type arguments nowhere, and
    /// the sema resolved them from the context.</param>
    private TempId LowerVariantCall(MemberExpr callee, Expr[] arguments, Expr constructed, Span span)
    {
        // Which INSTANCE is constructed stands in the call's result type rather than at the target:
        // 'Opt.Some(5)' in a position with an expected 'Opt<int>' names the arguments nowhere, but the
        // sema resolved them.
        RefEnumSymbol(callee.Target, callee.Span);
        var enumType = RequireEnum(_types.TypeOf(constructed), span);
        var variant = _typeTable.VariantOf(enumType.Type, callee.Member, span);

        var fields = new TempId[arguments.Length];
        for (var i = 0; i < arguments.Length; i++) fields[i] = LowerExpr(arguments[i]);

        var dest = _slots.NewTemp(enumType);
        _b.Emit(new NewVariant(dest, variant, enumType.Type, fields, span));
        return dest;
    }

    /// <summary><c>Shape.Tri { a = 3, b = 4 }</c>. As with an object literal, writing happens in LAYOUT
    /// order while evaluation happens in source order, except that slot 0 is the tag and the payload
    /// fields start at 1.</summary>
    private TempId LowerStructVariant(StructInitExpr expr)
    {
        var variantName = expr.Path[^1];
        var enumType = RequireEnum(_types.TypeOf(expr), expr.Span);
        var variant = _typeTable.VariantOf(enumType.Type, variantName, expr.Span);
        var layout = _typeTable.Defs[variant.Value];

        var values = new Dictionary<string, TempId>(StringComparer.Ordinal);
        foreach (var field in expr.Fields) values[field.Name] = LowerExpr(field.Value);

        var fields = new TempId[layout.FieldNames.Length - 1];
        for (var i = 1; i < layout.FieldNames.Length; i++)
        {
            if (!values.TryGetValue(layout.FieldNames[i], out var value))
                throw NotSupported($"initializer omits field '{layout.FieldNames[i]}'", expr.Span);
            fields[i - 1] = value;
        }

        var dest = _slots.NewTemp(enumType);
        _b.Emit(new NewVariant(dest, variant, enumType.Type, fields, expr.Span));
        return dest;
    }

    private TypeSymbol RefEnumSymbol(Expr target, Span span)
    {
        var bound = _types.RefOf(target);
        if (bound is ImportBindingSymbol import) bound = import.Target;
        if (bound is TypeSymbol { Kind: TypeSymbolKind.Enum } symbol) return symbol;

        throw NotSupported("variant construction on something that is not an enum", span);
    }

    /// <summary>
    /// <c>match</c> as an expression and as a statement — the same code, only the result slot is missing
    /// in the statement case.
    ///
    /// <para>NO JUMP TABLE OPCODE. The tag is read, compared against a constant, and branched on as
    /// everywhere else. A jump table would be an optimization; the semantics are a chain of comparisons,
    /// and exhaustiveness was already proven by the sema (<c>LYR-SEM0050</c>), which is why the last arm
    /// needs no fallback.</para>
    /// </summary>
    /// <summary>
    /// <c>match</c> as an expression and as a statement, over enums AND over scalars.
    ///
    /// <para>NO OPCODE OF ITS OWN. A <c>match</c> branches over a sequence of tests like any other case
    /// distinction — over its tag for an enum, over the value itself otherwise. A jump table would be an
    /// optimization, not semantics.</para>
    ///
    /// <para>The last arm is taken unchecked only when its pattern is irrefutable (<c>_</c> or a plain
    /// binding) and it has no guard. The sema proved exhaustiveness, but a guard can still fail, and a
    /// literal arm at the end is exhaustive only because another arm covers the gap. The failure path
    /// then becomes <c>unreachable</c>: reachable in the CFG, impossible at runtime.</para>
    /// </summary>
    /// <summary>Whether the last lowered <c>match</c> continues behind itself. A return value would be
    /// cleaner, but <see cref="LowerMatch"/> already yields the result temp of the expression case, and a
    /// second channel for a question only the statement case asks would have widened every call
    /// site.</summary>
    private bool _matchFellThrough = true;

    private TempId? LowerMatch(Expr scrutinee, MatchArm[] arms, IrType? resultType, Span span)
    {
        var scrutineeType = TypeOfExpr(scrutinee);
        var value = LowerExpr(scrutinee);
        var slot = resultType is null ? (LocalId?)null : _slots.DeclareSynthetic("match", resultType);

        // For an enum the comparison goes over the tag rather than over the value: which variant is
        // present stands in slot 0 and nowhere else.
        TypeId? enumId = null;
        TempId subject = value;
        IrType subjectType = scrutineeType;

        if (scrutineeType is IrEnumType)
        {
            enumId = RequireEnum(scrutinee).Type;
            var tag = _slots.NewTemp(new IrScalarType(IrScalar.I64));
            _b.Emit(new EnumTag(tag, value, span));
            subject = tag;
            subjectType = new IrScalarType(IrScalar.I64);
        }

        // The merge block arises ONLY when an arm needs it. If none falls through — every arm returns,
        // throws or jumps — there is no control flow behind the 'match', and a created block would be
        // unreachable from the entry. The verifier rejects exactly that, and rightly: a block nobody can
        // reach is either dead or a lowering error.
        BlockId? merge = null;

        for (var i = 0; i < arms.Length; i++)
        {
            var arm = arms[i];
            var last = i == arms.Length - 1;

            // The last arm is not checked: the sema proved exhaustiveness (LYR-SEM0050), so it matches
            // when none before it did. The test would always be true, and for an enum it would be an
            // extra comparison per match.
            //
            // With a guard that does not hold: a guard can fail, and then a failure path is needed. It
            // becomes 'unreachable' — reachable in the CFG, impossible at runtime.
            var unconditional = last && arm.Guard is null;

            var body = _b.NewBlock();
            var next = unconditional ? (BlockId?)null : _b.NewBlock();

            if (unconditional) _b.Seal(new Branch(body, arm.Span));
            else EmitPatternBranch(arm.Pattern, subject, subjectType, enumId,
                body, next!.Value, arm.Span);

            _b.SwitchTo(body);

            // Bindings come before the guard: 'n if n > 0' needs 'n'.
            BindPattern(arm.Pattern, value, subjectType, enumId);

            if (arm.Guard is { } guard)
            {
                var guarded = _b.NewBlock();
                _b.Seal(new CondBranch(LowerExpr(guard), guarded, next!.Value, guard.Span));
                _b.SwitchTo(guarded);
            }

            if (LowerArm(arm, value, slot, resultType))
            {
                merge ??= _b.NewBlock();
                _b.Seal(new Branch(merge.Value, arm.Span));
            }

            if (next is { } fallthrough)
            {
                _b.SwitchTo(fallthrough);
                // After the last arm the failure path is impossible at runtime, because the sema proved
                // exhaustiveness. In the CFG it is reachable and therefore needs a terminator, and
                // 'unreachable' is exactly that statement.
                if (last) _b.Seal(new Unreachable(span));
            }
        }

        // No arm falls through — every one returns, throws or jumps. Control flow then ends here and the
        // merge block is unreachable.
        //
        // That is not an edge case but the usual pattern for a 'match' as a statement:
        // 'match (e) { A => { return 1; }, B => { return 2; } }'.
        if (merge is not { } after)
        {
            // No arm falls through: control flow ends here. The caller learns that through
            // _matchFellThrough and does not seal a second time.
            _matchFellThrough = false;
            return null;
        }

        _b.SwitchTo(after);
        _matchFellThrough = true;
        if (slot is not { } result || resultType is null) return null;

        var dest = _slots.NewTemp(resultType);
        _b.Emit(new LoadLocal(dest, result, resultType, span));
        return dest;
    }

    /// <summary>
    /// Branches to <paramref name="onMatch"/> or <paramref name="onFail"/> depending on whether the
    /// pattern matches. Seals the current block while doing so.
    ///
    /// <para>A BRANCH RATHER THAN A bool TEMP, and that is no matter of style: a range needs two
    /// comparisons, an or-pattern arbitrarily many, and combining them into a value would mean
    /// <c>and</c>/<c>or</c> on <c>bool</c> — both are integral in this IR, and the verifier says so. The
    /// same solution as for <c>&amp;&amp;</c> and <c>||</c>, which are control flow for the same reason
    /// rather than opcodes.</para>
    /// </summary>
    private void EmitPatternBranch(Pattern pattern, TempId subject, IrType subjectType,
        TypeId? enumId, BlockId onMatch, BlockId onFail, Span span)
    {
        switch (pattern)
        {
            // Catches everything: no test needed.
            case WildcardPattern:
                _b.Seal(new Branch(onMatch, span));
                return;

            // A tuple pattern cannot FAIL: the arity stands in the type and the sema checked it. It is
            // therefore pure binding, which BindPattern does rather than this branch. Patterns INSIDE
            // the tuple that could test are reported as a scope boundary by BindTupleElements.
            case TuplePattern:
                _b.Seal(new Branch(onMatch, span));
                return;

            case BindingPattern binding when _types.RefOf(pattern) is EnumVariantSymbol:
                _b.Seal(new CondBranch(EmitTagTest(enumId, binding.Name, subject, binding.Span),
                    onMatch, onFail, binding.Span));
                return;

            case BindingPattern:
                _b.Seal(new Branch(onMatch, span));
                return;

            case VariantPattern variant:
                _b.Seal(new CondBranch(EmitTagTest(enumId, variant.Path[^1], subject, variant.Span),
                    onMatch, onFail, variant.Span));
                return;

            // 'null' as a pattern is NO comparison but the question of a value's presence — the same
            // answer as for 'x == null' (TryLowerNullTest). A real equality comparison would need a null
            // value as an operand, and there is none; the verifier says so too ("equality comparison on
            // type ?…").
            case LiteralPattern { Literal: NullLiteralExpr } nullPattern:
            {
                if (subjectType is not IrOptionalType)
                    throw NotSupported("'null' pattern on a non-optional", nullPattern.Span);

                var isSome = _slots.NewTemp(BoolType);
                _b.Emit(new OptIsSome(isSome, subject, nullPattern.Span));
                var isNone = _slots.NewTemp(BoolType);
                _b.Emit(new UnOp(isNone, IrUnKind.Not, BoolType, isSome, nullPattern.Span));
                _b.Seal(new CondBranch(isNone, onMatch, onFail, nullPattern.Span));
                return;
            }

            case LiteralPattern literal:
            {
                var expected = LowerExprAs(literal.Literal, subjectType);
                var matches = _slots.NewTemp(BoolType);
                _b.Emit(new BinOp(matches, IrBinKind.Eq, BoolType, subject, expected, literal.Span));
                _b.Seal(new CondBranch(matches, onMatch, onFail, literal.Span));
                return;
            }

            // 'lo <= v' and then 'v <= hi': two blocks rather than one combination.
            case RangePattern range:
            {
                var low = LowerExprAs(range.Low, subjectType);
                var atLeast = _slots.NewTemp(BoolType);
                _b.Emit(new BinOp(atLeast, IrBinKind.Ge, BoolType, subject, low, range.Span));

                var upper = _b.NewBlock();
                _b.Seal(new CondBranch(atLeast, upper, onFail, range.Span));
                _b.SwitchTo(upper);

                var high = LowerExprAs(range.High, subjectType);
                var atMost = _slots.NewTemp(BoolType);
                _b.Emit(new BinOp(atMost, range.IsInclusive ? IrBinKind.Le : IrBinKind.Lt,
                    BoolType, subject, high, range.Span));
                _b.Seal(new CondBranch(atMost, onMatch, onFail, range.Span));
                return;
            }

            // Every alternative gets its own attempt; the first that matches wins.
            case OrPattern or:
            {
                for (var i = 0; i < or.Alternatives.Length; i++)
                {
                    var lastAlternative = i == or.Alternatives.Length - 1;
                    var nextAlternative = lastAlternative ? onFail : _b.NewBlock();

                    EmitPatternBranch(or.Alternatives[i], subject, subjectType, enumId,
                        onMatch, nextAlternative, or.Span);

                    if (!lastAlternative) _b.SwitchTo(nextAlternative);
                }

                return;
            }

            default:
                throw NotSupported($"a {pattern.GetType().Name} in a match", pattern.Span);
        }
    }

    private TempId EmitTagTest(TypeId? enumId, string variant, TempId tag, Span span)
    {
        if (enumId is not { } id)
            throw NotSupported("a variant pattern in a match over a non-enum", span);

        var expected = EmitConst(new IntConst((ulong)_typeTable.TagOf(id, variant, span)),
            new IrScalarType(IrScalar.I64), span);

        // The Type field of a comparison is its RESULT type (bool); the emitter looks the operand type up
        // in the temp table, because signed and unsigned are different opcodes.
        var matches = _slots.NewTemp(BoolType);
        _b.Emit(new BinOp(matches, IrBinKind.Eq, BoolType, tag, expected, span));
        return matches;
    }

    /// <summary>Binds what the pattern binds: a plain binding the value itself, a variant pattern its
    /// fields.</summary>
    private void BindPattern(Pattern pattern, TempId value, IrType valueType,
        TypeId? enumId)
    {
        if (pattern is BindingPattern binding && _types.RefOf(pattern) is LocalSymbol local)
        {
            var slotType = LowerType(local.Type, binding.Span);
            var slot = _slots.DeclareFor(local, slotType);

            // When an arm binds the rest of a '?T', the sema gives the name the NARROWED type 'T': the
            // arm is reachable only when a value is present. The value in the subject still carries
            // '?T', because the narrowing is a statement about control flow rather than about memory.
            // It is therefore unwrapped here, exactly as everywhere else the sema expects 'T'. The
            // 'optget' can never panic — the proof stands in the 'optissome' of the null arm before it.
            if (valueType is IrOptionalType && slotType is not IrOptionalType)
            {
                var unwrapped = _slots.NewTemp(slotType);
                _b.Emit(new OptGet(unwrapped, value, slotType, binding.Span));
                _b.Emit(new StoreLocal(slot, unwrapped, binding.Span));
                return;
            }

            _b.Emit(new StoreLocal(slot, value, binding.Span));
            return;
        }

        // A tuple pattern binds field by field, the same routine as for a destructuring binding. It has
        // to stand HERE rather than in the branching path: the last arm of a 'match' is not checked at
        // all, because the sema proved exhaustiveness, so no binding would run there.
        if (pattern is TuplePattern tuple)
        {
            // The type comes from the VALUE rather than from the pattern: '_' binds nothing and therefore
            // has none to read off.
            if (valueType is not IrRefType tupleType)
                throw Bug("tuple pattern on a value that is not a tuple");

            BindTupleElements(tuple, value, tupleType.Type, tuple.Span);
            return;
        }

        if (enumId is { } id) BindPatternFields(pattern, id, value);
    }

    /// <summary>Lowers the body of an arm. Returns whether it falls through.</summary>
    private bool LowerArm(MatchArm arm, TempId value, LocalId? slot, IrType? resultType)
    {
        if (arm.Body is Expr expr)
        {
            var produced = resultType is null ? LowerExprOrVoid(expr) : LowerExprAs(expr, resultType);
            if (slot is { } target && produced is { } v) _b.Emit(new StoreLocal(target, v, arm.Span));
            return true;
        }

        return LowerScope((Block)arm.Body);
    }

    /// <summary>The tag a pattern matches. A unit variant parses as a <see cref="BindingPattern"/>;
    /// whether it is a binding or a variant is known only to the sema, and it decided.</summary>
    private int TagOfPattern(TypeId enumId, Pattern pattern) => pattern switch
    {
        VariantPattern v => _typeTable.TagOf(enumId, v.Path[^1], v.Span),
        BindingPattern b when _types.RefOf(pattern) is EnumVariantSymbol
            => _typeTable.TagOf(enumId, b.Name, b.Span),
        WildcardPattern => throw NotSupported("'_' anywhere but in the last arm", pattern.Span),
        _ => throw NotSupported($"a {pattern.GetType().Name} in a match over an enum", pattern.Span),
    };

    /// <summary>Decomposes a variant: <c>enumas</c> narrows, after which every field is an ordinary
    /// <c>ldfld</c> with the variant's layout.</summary>
    private void BindPatternFields(Pattern pattern, TypeId enumId, TempId value)
    {
        if (pattern is not VariantPattern variant) return;
        if (variant.TupleElements is null && variant.StructFields is null) return;

        var variantType = _typeTable.VariantOf(enumId, variant.Path[^1], variant.Span);
        var narrowed = _slots.NewTemp(new IrRefType(variantType));
        _b.Emit(new EnumAs(narrowed, value, variantType, variant.Span));

        var layout = _typeTable.Defs[variantType.Value];

        if (variant.TupleElements is { } elements)
            for (var i = 0; i < elements.Length; i++)
                BindOne(elements[i], narrowed, variantType, new FieldId(i + 1), layout.FieldTypes[i + 1]);

        if (variant.StructFields is { } fields)
            foreach (var field in fields)
            {
                var index = Array.IndexOf(layout.FieldNames, field.Name);
                if (index < 0) throw NotSupported($"unknown field '{field.Name}' in a pattern", field.Span);

                // Short form `{ a, b }`: no sub-pattern, the field name IS the binding. The sema bound it
                // to a LocalSymbol, on the FieldPattern node itself.
                BindOne(field.Pattern ?? (Node)field, narrowed, variantType,
                    new FieldId(index), layout.FieldTypes[index], field.Name, field.Span);
            }
    }

    private void BindOne(Node? sub, TempId obj, TypeId variantType, FieldId field, IrType type,
        string? shorthandName = null, Span shorthandSpan = default)
    {
        // Bindings and '_' only; nested patterns need recursive decomposition and are a later stage.
        if (sub is null or WildcardPattern) return;

        var name = sub is BindingPattern binding ? binding.Name : shorthandName;
        if (name is null)
            throw NotSupported($"a nested {sub.GetType().Name} in a pattern", sub.Span);

        // The sema already gave the pattern binding a LocalSymbol; LowerIdentifier finds the slot again
        // through the same symbol later. A namespace of its own here would be a second truth about
        // scoping.
        if (_types.RefOf(sub) is not LocalSymbol local)
            throw Bug($"pattern binding '{name}' was not bound by the type checker");

        var span = sub.Span == default ? shorthandSpan : sub.Span;
        var slot = _slots.DeclareFor(local, type);
        var loaded = _slots.NewTemp(type);
        _b.Emit(new LoadField(loaded, obj, variantType, field, type, span));
        _b.Emit(new StoreLocal(slot, loaded, span));
    }

    // ------------------------------------------------------------------ optionals

    /// <summary>
    /// An expression at a position with an EXPECTED TYPE. Two things happen only here: <c>null</c> gets
    /// its type, having none of its own — the sema gives it <c>NullType</c> — and a <c>T</c> is wrapped
    /// into <c>?T</c>, because the language allows that direction implicitly.
    /// </summary>
    private TempId LowerExprAs(Expr expr, IrType expected)
    {
        if (expr is NullLiteralExpr)
        {
            if (expected is not IrOptionalType option)
                throw NotSupported("'null' outside an optional context", expr.Span);

            var none = _slots.NewTemp(expected);
            _b.Emit(new OptNone(none, option.Inner, expr.Span));
            return none;
        }

        return Coerce(LowerExpr(expr), TypeOfExpr(expr), expected, expr.Span);
    }

    /// <summary>A <c>null</c> without an expected type. Should never occur: every position where
    /// <c>null</c> is valid knows its target type and goes through <see cref="LowerExprAs"/>.</summary>
    private TempId LowerNull(NullLiteralExpr expr) =>
        throw NotSupported("'null' in a position without an expected type", expr.Span);

    /// <summary>
    /// <c>x != null</c> and <c>x == null</c> are NO comparisons but the question of a value's presence —
    /// exactly what <c>optissome</c> answers. A real comparison would demand a <c>null</c> value on the
    /// stack, and there is none: "no value" is an empty reference, not an operand.
    /// </summary>
    private TempId? TryLowerNullTest(BinaryExpr expr)
    {
        if (expr.Operator is not (BinaryOp.Eq or BinaryOp.Ne)) return null;

        var (option, _) = expr.Right is NullLiteralExpr ? (expr.Left, expr.Right)
            : expr.Left is NullLiteralExpr ? (expr.Right, expr.Left)
            : (null, null);
        if (option is null) return null;

        var value = LowerExpr(option);
        if (TypeOfExpr(option) is not IrOptionalType) throw NotSupported("null test on a non-optional", expr.Span);

        var isSome = _slots.NewTemp(BoolType);
        _b.Emit(new OptIsSome(isSome, value, expr.Span));
        if (expr.Operator is BinaryOp.Ne) return isSome;

        // '== null' is the negation. 'not' is the only opcode without a type tag: bool only.
        var isNone = _slots.NewTemp(BoolType);
        _b.Emit(new UnOp(isNone, IrUnKind.Not, BoolType, isSome, expr.Span));
        return isNone;
    }

    /// <summary>
    /// Adapts a value to the type its position expects. The language knows two implicit transitions, and
    /// both are materialized here:
    ///
    /// <para><c>T</c> to <c>?T</c> is implicit and becomes <c>optsome</c>.</para>
    ///
    /// <para>A class or enum value to its interface becomes <c>mkiface</c>: the interface value is a fat
    /// pointer carrying the concrete type, and that is settled at compile time exactly here. Later, at
    /// the <c>callvirt</c>, nobody knows which class it was any more, because an object carries no type
    /// tag.</para>
    ///
    /// <para>The order is not arbitrary: for <c>?SomeInterface</c> the interface has to arise first and
    /// the optional around it second, or a class reference would be wrapped and <c>optget</c> would yield
    /// something no <c>callvirt</c> can run on.</para>
    /// </summary>
    private TempId Coerce(TempId value, IrType from, IrType to, Span span)
    {
        var target = to is IrOptionalType outer ? outer.Inner : to;
        var source = from is IrOptionalType inner ? inner.Inner : from;

        // Value semantics. The binding point is where a struct value gets a new home; that is where the
        // copy happens, and only there. A freshly built value does not need it: it has no other owner to
        // detach from.
        if (target is IrStructType value_ && from is not IrOptionalType && !_fresh.Contains(value))
            value = CopyStructValue(value, value_, span);

        if (target is IrInterfaceType iface && source is not IrInterfaceType
            && from is not IrOptionalType)
        {
            // The way behind an interface is a binding point too: a struct is copied there, or the
            // interface value would share the slot array with its source and a mutation through the
            // interface would hit the original.
            if (source is IrStructType boxed && !_fresh.Contains(value))
                value = CopyStructValue(value, boxed, span);

            value = MakeInterfaceValue(value, source, iface, span);
            from = iface;
        }

        if (to is not IrOptionalType option || from is IrOptionalType) return value;

        var dest = _slots.NewTemp(to);
        _b.Emit(new OptSome(dest, value, option.Inner, span));
        return dest;
    }

    /// <summary>
    /// Creates an independent copy of a struct value.
    ///
    /// <para>The copy is recursive at runtime over nested structs and shallow over everything else: a
    /// field of type <c>class</c> or <c>T[]</c> carries a reference, and that is shared. The value is
    /// copied, not the world behind it.</para>
    /// </summary>
    private TempId CopyStructValue(TempId value, IrStructType type, Span span)
    {
        var dest = _slots.NewTemp(type);
        _b.Emit(new StructCopy(dest, value, type.Type, span));
        _fresh.Add(dest);
        return dest;
    }

    /// <summary>Lifts an object reference to its interface type.</summary>
    private TempId MakeInterfaceValue(TempId value, IrType concrete, IrInterfaceType iface,
        Span span)
    {
        var concreteId = concrete switch
        {
            IrRefType r => r.Type,
            IrStructType v => v.Type,
            IrEnumType e => e.Type,
            _ => throw NotSupported(
                "a value of this type cannot be used through an interface "
                + "(only classes, structs and enums)",
                span),
        };

        var dest = _slots.NewTemp(iface);
        _b.Emit(new MakeInterface(dest, value, concreteId, iface.Type, span));
        return dest;
    }

    private TempId LowerForceUnwrap(Expr operand, Span span)
    {
        var value = LowerExpr(operand);
        if (TypeOfExpr(operand) is not IrOptionalType option)
            throw NotSupported("'!' on a non-optional", span);

        var dest = _slots.NewTemp(option.Inner);
        _b.Emit(new OptGet(dest, value, option.Inner, span));
        return dest;
    }

    /// <summary>
    /// <c>a ?? b</c> — the right side is evaluated ONLY when there is no value on the left. Hence a
    /// branch rather than an instruction, exactly as for <c>&amp;&amp;</c> and <c>||</c>: a stack machine
    /// cannot transport an unevaluated expression.
    /// </summary>
    private TempId LowerCoalesce(BinaryExpr expr)
    {
        var type = TypeOfExpr(expr);
        var slot = _slots.DeclareSynthetic("coalesce", type);

        var option = LowerExpr(expr.Left);
        if (TypeOfExpr(expr.Left) is not IrOptionalType left)
            throw NotSupported("'??' on a non-optional", expr.Span);

        var test = _slots.NewTemp(BoolType);
        _b.Emit(new OptIsSome(test, option, expr.Span));

        var whenSome = _b.NewBlock();
        var whenNone = _b.NewBlock();
        var merge = _b.NewBlock();
        _b.Seal(new CondBranch(test, whenSome, whenNone, expr.Span));

        _b.SwitchTo(whenSome);
        var unwrapped = _slots.NewTemp(left.Inner);
        _b.Emit(new OptGet(unwrapped, option, left.Inner, expr.Span));
        _b.Emit(new StoreLocal(slot, Coerce(unwrapped, left.Inner, type, expr.Span), expr.Span));
        _b.Seal(new Branch(merge, expr.Span));

        _b.SwitchTo(whenNone);
        var fallback = LowerExpr(expr.Right);
        _b.Emit(new StoreLocal(slot, Coerce(fallback, TypeOfExpr(expr.Right), type, expr.Span), expr.Span));
        _b.Seal(new Branch(merge, expr.Span));

        _b.SwitchTo(merge);
        var dest = _slots.NewTemp(type);
        _b.Emit(new LoadLocal(dest, slot, type, expr.Span));
        return dest;
    }

    /// <summary>
    /// <c>x ??= v</c> — assigns only when <c>x</c> has no value.
    ///
    /// <para>Like <c>??</c> a branch rather than an opcode: the right side is evaluated only then, and an
    /// unevaluated expression cannot be transported on a stack machine. The only difference from
    /// <c>??</c> is that the result goes back into the existing slot rather than into a new one.</para>
    /// </summary>
    private TempId LowerCoalesceAssign(LocalId slot, AssignExpr expr)
    {
        var type = _slots.TypeOfLocal(slot);
        if (type is not IrOptionalType option)
            throw NotSupported("'??=' on a non-optional target", expr.Span);

        var current = _slots.NewTemp(type);
        _b.Emit(new LoadLocal(current, slot, type, expr.Target.Span));

        var test = _slots.NewTemp(BoolType);
        _b.Emit(new OptIsSome(test, current, expr.Span));

        var whenNone = _b.NewBlock();
        var merge = _b.NewBlock();
        // When there is already a value, the slot stays untouched, and the right side does not run.
        _b.Seal(new CondBranch(test, merge, whenNone, expr.Span));

        _b.SwitchTo(whenNone);
        _b.Emit(new StoreLocal(slot, LowerExprAs(expr.Value, type), expr.Span));
        _b.Seal(new Branch(merge, expr.Span));

        _b.SwitchTo(merge);
        var result = _slots.NewTemp(type);
        _b.Emit(new LoadLocal(result, slot, type, expr.Span));
        return result;
    }

    /// <summary>
    /// <c>a?.b</c> — a field access that does not happen when there is no value.
    ///
    /// <para>The result is always an optional: when <c>a</c> has no value it is <c>optnone</c>, otherwise
    /// the unwrapped field in <c>optsome</c>. That too is a branch — the field access must NOT run on an
    /// empty reference, and an opcode could not express that without branching itself.</para>
    /// </summary>
    /// <summary>
    /// <c>b?.get()</c> — the receiver is optional, so the result is too.
    ///
    /// <para>THE CALL RUNS THROUGH THE SAME RESOLUTION AS ANY OTHER, only with an already unwrapped
    /// receiver. A path of its own here would have to answer virtual dispatch, natives, extensions and
    /// generics a second time.</para>
    /// </summary>
    private TempId LowerOptionalCall(CallExpr expr, MemberExpr callee)
    {
        if (TypeOfExpr(expr) is not IrOptionalType result)
            throw NotSupported("'?.' with a call whose result is not an optional", expr.Span);

        if (TypeOfExpr(callee.Target) is not IrOptionalType target)
            throw NotSupported("'?.' on a non-optional", expr.Span);

        // The method's actual return type. 'TypeOfExpr(expr)' is no good for that: the sema gave the call
        // the chain type '?int', while the method yields 'int'.
        if (_types.TypeOf(callee) is not Sema.Optional { Inner: FnType signature })
            throw NotSupported("'?.' with a call on something that is not a method", expr.Span);

        var returned = LowerType(signature.Return, expr.Span);

        var slot = _slots.DeclareSynthetic("chain", result);
        var option = LowerExpr(callee.Target);

        var test = _slots.NewTemp(BoolType);
        _b.Emit(new OptIsSome(test, option, expr.Span));

        var whenSome = _b.NewBlock();
        var whenNone = _b.NewBlock();
        var merge = _b.NewBlock();
        _b.Seal(new CondBranch(test, whenSome, whenNone, expr.Span));

        _b.SwitchTo(whenSome);
        var unwrapped = _slots.NewTemp(target.Inner);

        // Cannot panic: the branch stands behind the 'optissome'. The same division of labour as for the
        // field access and for flow narrowing.
        _b.Emit(new OptGet(unwrapped, option, target.Inner, expr.Span));

        // The call then runs through LowerCall like any other. What it sees differently is exactly two
        // things, and both hang on the AST node rather than on a parameter chain: the receiver is already
        // unwrapped, and the return type is the method's.
        //
        // A call path of its own would have to answer virtual dispatch, generics, constraints, natives
        // and extensions a second time. A first attempt did that through a special case in the 'switch'
        // and promptly hid the generics detection, so 'b?.get()' on a 'Box<int>' was reported as
        // 'external or bodiless'.
        TempId? produced;
        _chainReceivers[callee.Target] = unwrapped;
        _chainResults[expr] = returned;
        try
        {
            produced = LowerCall(expr);
        }
        finally
        {
            _chainReceivers.Remove(callee.Target);
            _chainResults.Remove(expr);
        }

        if (produced is not { } value)
            throw NotSupported("'?.' with a call that returns nothing", expr.Span);

        // When the METHOD itself already yields an optional ('fn empty(): ?int'), its result is already
        // the result type: the sema collapsed '??int' to '?int'. Wrapping a second time would create a
        // level the language does not have.
        var stored = value;
        if (returned is not IrOptionalType)
        {
            stored = _slots.NewTemp(result);
            _b.Emit(new OptSome(stored, value, result.Inner, expr.Span));
        }

        _b.Emit(new StoreLocal(slot, stored, expr.Span));
        _b.Seal(new Branch(merge, expr.Span));

        _b.SwitchTo(whenNone);
        var none = _slots.NewTemp(result);
        _b.Emit(new OptNone(none, result.Inner, expr.Span));
        _b.Emit(new StoreLocal(slot, none, expr.Span));
        _b.Seal(new Branch(merge, expr.Span));

        _b.SwitchTo(merge);
        var dest = _slots.NewTemp(result);
        _b.Emit(new LoadLocal(dest, slot, result, expr.Span));
        return dest;
    }

    private TempId LowerOptionalMember(MemberExpr expr)
    {
        var resultType = TypeOfExpr(expr);
        if (resultType is not IrOptionalType result)
            throw NotSupported("'?.' whose result is not an optional", expr.Span);

        if (TypeOfExpr(expr.Target) is not IrOptionalType target)
            throw NotSupported("'?.' on a non-optional", expr.Span);

        var slot = _slots.DeclareSynthetic("chain", resultType);
        var option = LowerExpr(expr.Target);

        var test = _slots.NewTemp(BoolType);
        _b.Emit(new OptIsSome(test, option, expr.Span));

        var whenSome = _b.NewBlock();
        var whenNone = _b.NewBlock();
        var merge = _b.NewBlock();
        _b.Seal(new CondBranch(test, whenSome, whenNone, expr.Span));

        _b.SwitchTo(whenSome);
        var unwrapped = _slots.NewTemp(target.Inner);
        _b.Emit(new OptGet(unwrapped, option, target.Inner, expr.Span));

        // The 'optget' cannot panic: the branch stands behind the 'optissome', so the proof is made. The
        // same division of labour as in flow narrowing.
        var (type, field, fieldType) = ResolveFieldOn(target.Inner, expr);
        var value = _slots.NewTemp(fieldType);
        _b.Emit(new LoadField(value, unwrapped, type, field, fieldType, expr.Span));

        // When the FIELD itself is optional ('w: ?int'), its value is already the result type: the sema
        // collapsed '??int' to '?int'. Wrapping a second time would create a level the language does not
        // have.
        var stored = value;
        if (fieldType is not IrOptionalType)
        {
            stored = _slots.NewTemp(resultType);
            _b.Emit(new OptSome(stored, value, result.Inner, expr.Span));
        }

        _b.Emit(new StoreLocal(slot, stored, expr.Span));
        _b.Seal(new Branch(merge, expr.Span));

        _b.SwitchTo(whenNone);
        var none = _slots.NewTemp(resultType);
        _b.Emit(new OptNone(none, result.Inner, expr.Span));
        _b.Emit(new StoreLocal(slot, none, expr.Span));
        _b.Seal(new Branch(merge, expr.Span));

        _b.SwitchTo(merge);
        var dest = _slots.NewTemp(resultType);
        _b.Emit(new LoadLocal(dest, slot, resultType, expr.Span));
        return dest;
    }

    /// <summary>The field index and type on the unwrapped carrier. Separate from
    /// <c>ResolveFieldAccess</c>, because there the carrier expression itself is lowered; here it is
    /// already unwrapped.</summary>
    private (TypeId Type, FieldId Field, IrType FieldType) ResolveFieldOn(IrType carrier,
        MemberExpr expr)
    {
        if (_types.TypeOf(expr.Target) is not Optional option
            || TypeFacts.SymbolOf(option.Inner) is not { } named)
            throw NotSupported($"'?.{expr.Member}' on " +
                               $"'{TypeFacts.Display(_types.TypeOf(expr.Target))}'", expr.Span);

        var type = _typeTable.Intern(named);
        var field = _typeTable.FieldOf(named, expr.Member, expr.Span);
        return (type, field, _typeTable.Defs[type.Value].FieldTypes[field.Value]);
    }

    // ------------------------------------------------------------------ arrays

    /// <summary><c>[a, b, c]</c> — one instruction rather than three stores. The values lie on the stack
    /// in source order at the <c>newarr</c>.</summary>
    private TempId LowerArrayLiteral(ArrayLitExpr expr)
    {
        if (TypeOfExpr(expr) is not IrArrayType type)
            throw NotSupported("array literal of a non-array type", expr.Span);

        var elements = new TempId[expr.Elements.Length];
        for (var i = 0; i < expr.Elements.Length; i++) elements[i] = LowerExpr(expr.Elements[i]);

        var dest = _slots.NewTemp(type);
        _b.Emit(new NewArray(dest, type.Element, elements, expr.Span));
        return dest;
    }

    private TempId LowerIndexRead(IndexExpr expr)
    {
        // A container from std.collections goes through 'Indexable<T>.get(i)' — the same division of
        // labour as for 'for-in': the compiler knows ONE built-in form, the array, and everything else
        // runs through the interface.
        if (LowerIndexableCall(expr, "get", null) is { } viaInterface) return viaInterface;

        var (array, index, element) = ResolveIndexAccess(expr);
        var dest = _slots.NewTemp(element);
        _b.Emit(new LoadElem(dest, array, index, element, expr.Span));
        return dest;
    }

    /// <summary>
    /// <c>xs[i]</c> and <c>xs[i] = v</c> on a type satisfying <c>Indexable&lt;T&gt;</c>, as a call to
    /// <c>get</c> or <c>set</c>. Returns <c>null</c> when the carrier is an array; the built-in route
    /// through <c>ldelem</c> and <c>stelem</c> then applies.
    ///
    /// <para>The call goes DIRECTLY rather than virtually: the receiver type is statically settled, and
    /// for a generic instance the monomorphization has produced the method anyway. That is the same gain
    /// as in constraint dispatch — an interface does not automatically mean a vtable.</para>
    /// </summary>
    private TempId? LowerIndexableCall(IndexExpr expr, string method, TempId? value)
    {
        if (TypeOfExpr(expr.Target) is IrArrayType) return null;

        var carrier = SubstituteType(_types.TypeOf(expr.Target));
        if (TypeFacts.SymbolOf(carrier) is not { } owner) return null;
        if (owner.Members.LookupLocal(method) is not FunctionSymbol symbol) return null;
        if (symbol.Declaration is not FunctionDecl declaration) return null;

        var target = carrier is GenericInstance instance
            ? _instances.RequestMethod(symbol, declaration, instance, expr.Span)
            : TryResolveFunction(symbol, out var direct)
                ? direct
                : throw NotSupported($"'{owner.Name}.{method}' was not lowered", expr.Span);

        var receiver = LowerExpr(expr.Target);
        var index = LowerExprAs(expr.Index, new IrScalarType(IrScalar.I64));

        var arguments = value is { } stored
            ? new[] { receiver, index, stored }
            : new[] { receiver, index };

        // 'set' yields void, 'get' the element type.
        if (value is { } assigned)
        {
            _b.Emit(new Call(null, target, arguments, expr.Span));
            return assigned;
        }

        var dest = _slots.NewTemp(TypeOfExpr(expr));
        _b.Emit(new Call(dest, target, arguments, expr.Span));
        _fresh.Add(dest);
        return dest;
    }

    /// <summary>The shared part of reading and writing. <c>[i]</c> is built in on <c>T[]</c> only;
    /// everything else goes through the <c>Indexable&lt;T&gt;</c> interface.</summary>
    private (TempId Array, TempId Index, IrType Element) ResolveIndexAccess(IndexExpr expr)
    {
        if (TypeOfExpr(expr.Target) is not IrArrayType array)
            throw NotSupported($"indexing a '{TypeFacts.Display(_types.TypeOf(expr.Target))}' " +
                               "(only arrays; other containers need the Indexable interface)",
                expr.Span);

        var target = LowerExpr(expr.Target);
        var index = LowerExpr(expr.Index);
        return (target, index, array.Element);
    }

    private TempId LowerArrayLength(MemberExpr expr)
    {
        var array = LowerExpr(expr.Target);
        var dest = _slots.NewTemp(new IrScalarType(IrScalar.I64));
        _b.Emit(new ArrayLen(dest, array, expr.Span));
        return dest;
    }

    // ------------------------------------------------------------------ objects

    /// <summary>
    /// <c>Account { owner = a, balance = b }</c> becomes one <c>newobj</c> and one <c>storefield</c> per
    /// field.
    ///
    /// <para>WRITING HAPPENS IN DECLARATION ORDER, NOT IN WRITE ORDER. The initializers may stand in any
    /// order in the source, but the layout is the declaration, and only a fixed order makes the bytecode
    /// deterministic. The values are nevertheless evaluated in SOURCE order — with side effects that is
    /// the order the reader expects.</para>
    /// </summary>
    private TempId LowerObjectInit(StructInitExpr expr)
    {
        // 'Shape.Tri { a = 3, b = 4 }' and 'Ev<int>.Hit { … }' are struct variants. They look like an
        // object literal but are a variant construction and therefore go through newvariant. Which
        // INSTANCE is meant stands in the type of the expression.
        if (SubstituteType(_types.TypeOf(expr)) is NamedRef { Symbol.Kind: TypeSymbolKind.Enum }
            or GenericInstance { Definition.Kind: TypeSymbolKind.Enum })
            return LowerStructVariant(expr);

        // An initializer for an instance of a generic type ('Box<int> { v = 3 }'): the type arguments
        // decide the layout, so they have to be present when interning — and through the own
        // substitution, in case the calling function is itself an instance.
        TypeId type;
        TypeSymbol declaring;
        if (SubstituteType(_types.TypeOf(expr)) is GenericInstance instance
            && instance.Definition.Kind is TypeSymbolKind.Class or TypeSymbolKind.Struct)
        {
            type = _typeTable.Intern(instance.Definition, instance.Arguments);
            declaring = instance.Definition;
        }
        else if (_types.TypeOf(expr) is NamedRef
                 { Symbol.Kind: TypeSymbolKind.Class or TypeSymbolKind.Struct } named)
        {
            type = _typeTable.Intern(named.Symbol);
            declaring = named.Symbol;
        }
        else
        {
            throw NotSupported($"initializer for '{TypeFacts.Display(_types.TypeOf(expr))}' " +
                               "(only classes and structs are lowered)", expr.Span);
        }

        var layout = _typeTable.Defs[type.Value];

        // Evaluate all values first, in source order, then write in layout order.
        var values = new Dictionary<string, TempId>(StringComparer.Ordinal);
        foreach (var field in expr.Fields)
        {
            if (values.ContainsKey(field.Name))
                throw Bug($"duplicate initializer for '{field.Name}' reached lowering");
            // Adapt to the declared field type: a field of an interface type takes a class only as a fat
            // pointer.
            var fieldIndex = Array.IndexOf(layout.FieldNames, field.Name);
            values[field.Name] = fieldIndex >= 0
                ? LowerExprAs(field.Value, layout.FieldTypes[fieldIndex])
                : LowerExpr(field.Value);
        }

        // An omitted field gets its default, evaluated HERE at the construction site rather than once at
        // the type. A default is an expression, and storing it in the layout would mean writing an
        // expression into a type table.
        //
        // Without a default it stays an error: a silent zero value would be a guess, and the sema knows
        // no rule that allows it.
        var declaredFields = declaring.Declaration switch
        {
            ClassDecl c => c.Members.OfType<FieldDecl>().ToArray(),
            StructDecl v => v.Members.OfType<FieldDecl>().ToArray(),
            _ => [],
        };

        foreach (var field in declaredFields)
        {
            if (values.ContainsKey(field.Name)) continue;

            if (field.Default is null)
                throw NotSupported($"initializer omits field '{field.Name}', which has no default",
                    expr.Span);

            var index = Array.IndexOf(layout.FieldNames, field.Name);
            values[field.Name] = index >= 0
                ? LowerExprAs(field.Default, layout.FieldTypes[index])
                : LowerExpr(field.Default);
        }

        foreach (var name in layout.FieldNames)
            if (!values.ContainsKey(name))
                throw Bug($"field '{name}' of '{declaring.Name}' has neither a value nor a default");

        // A struct value is the same slot array as a class object at runtime, so 'newobj' serves both.
        // The difference lies solely in the binding points.
        IrType result = _typeTable.IsStruct(type)
            ? new IrStructType(type)
            : new IrRefType(type);
        var dest = _slots.NewTemp(result);
        _b.Emit(new NewObject(dest, type, result, expr.Span));

        for (var i = 0; i < layout.FieldNames.Length; i++)
            _b.Emit(new StoreField(dest, type, new FieldId(i), values[layout.FieldNames[i]], expr.Span));

        // Freshly built: this value belongs to nobody yet, and a copy when binding would be ballast.
        _fresh.Add(dest);
        return dest;
    }

    /// <summary>Fills a global slot. Occurs only in the synthetic initializer: in user source there is no
    /// assignment to a global, because they are all <c>let</c>.</summary>
    private void LowerGlobalInit(GlobalInitStmt stmt)
    {
        var (id, type) = _globals.Resolve(stmt.Symbol, stmt.Span);
        var value = LowerExprAs(stmt.Binding.Initializer!, type);
        _b.Emit(new StoreGlobal(id, value, stmt.Span));
    }

    /// <summary>A global slot through a bare name: a module <c>let</c> in the own or an imported
    /// module.</summary>
    private TempId? TryLowerGlobalIdentifier(IdentifierExpr expr)
    {
        var symbol = _types.RefOf(expr);
        if (symbol is ImportBindingSymbol import) symbol = import.Target;
        return symbol is GlobalSymbol global ? LowerGlobalRead(global, expr.Span) : null;
    }

    /// <summary>A global slot: a module <c>let</c> or a <c>static let</c>. Both are the same in the
    /// bytecode; the difference is only where the name is visible.</summary>
    private TempId LowerGlobalRead(GlobalSymbol symbol, Span span)
    {
        var (id, type) = _globals.Resolve(symbol, span);
        var dest = _slots.NewTemp(type);
        _b.Emit(new LoadGlobal(dest, id, type, span));
        return dest;
    }

    private TempId LowerFieldRead(MemberExpr expr)
    {
        // 'P.ZERO' is not a field read but a constant read: a 'static let' is a global slot rather than
        // an object slot.
        if (_types.RefOf(expr) is GlobalSymbol constant) return LowerGlobalRead(constant, expr.Span);

        // 'Shape.Empty' is a unit variant. It looks like a member access but is a construction without
        // arguments.
        if (_types.RefOf(expr) is EnumVariantSymbol)
            return LowerVariantCall(expr, [], expr, expr.Span);

        // '.length' on an array is built in: neither a field nor a method.
        if (expr.Member == "length" && TypeOfExpr(expr.Target) is IrArrayType)
            return LowerArrayLength(expr);

        // 'a?.b' accesses only when 'a' has a value.
        if (expr.IsOptional) return LowerOptionalMember(expr);

        var (obj, type, field, fieldType) = ResolveFieldAccess(expr);
        var dest = _slots.NewTemp(fieldType);
        _b.Emit(new LoadField(dest, obj, type, field, fieldType, expr.Span));
        return dest;
    }

    /// <summary>The shared part of reading and writing: evaluate the object and determine the type, the
    /// field index and the field type.</summary>
    private (TempId Object, TypeId Type, FieldId Field, IrType FieldType) ResolveFieldAccess(MemberExpr expr)
    {
        // The receiver may be an instance of a generic type ('Box<int>'). It then decides the layout
        // rather than the definition — 'Box<int>' and 'Box<string>' have different field types at the
        // same position.
        var target = SubstituteType(_types.TypeOf(expr.Target));

        var declaring = target switch
        {
            NamedRef { Symbol.Kind: TypeSymbolKind.Class or TypeSymbolKind.Struct } n => n.Symbol,
            GenericInstance { Definition.Kind: TypeSymbolKind.Class or TypeSymbolKind.Struct } g
                => g.Definition,
            _ => throw NotSupported($"member access '.{expr.Member}' on " +
                                    $"'{TypeFacts.Display(target)}'", expr.Span),
        };

        var obj = LowerExpr(expr.Target);
        var type = target is GenericInstance instance
            ? _typeTable.Intern(instance.Definition, instance.Arguments)
            : _typeTable.Intern(declaring);

        // Index and field type both come from the layout of THIS instance. Going through the symbol
        // would not work: 'Box' alone has no layout, only 'Box<int>' has one.
        var layout = _typeTable.Defs[type.Value];
        var index = Array.IndexOf(layout.FieldNames, expr.Member);
        if (index < 0)
            throw NotSupported($"'{declaring.Name}' has no field '{expr.Member}'", expr.Span);

        return (obj, type, new FieldId(index), layout.FieldTypes[index]);
    }

    private TempId LowerCast(CastExpr expr)
    {
        // A non-numeric cast IS the conversion call the sema stored — same seam as the operators.
        // The operand is the call's receiver and lowers exactly once, in there.
        if (_types.OperatorCallOf(expr) is { } conversion)
            return LowerCall(conversion)
                   ?? throw Bug("conversion method returned no value");

        var from = TypeOfExpr(expr.Operand);
        var to = TypeOfExpr(expr);
        var operand = LowerExpr(expr.Operand);

        // 'x as int' for x: int is legal Lyric but yields no meaningful opcode. The lowering elides the
        // identity; the verifier rejects it.
        if (IrType.Equal(from, to)) return operand;

        var dest = _slots.NewTemp(to);
        _b.Emit(new Lyric.Ir.Convert(dest, from, to, operand, expr.Span));
        return dest;
    }

    /// <summary>
    /// A call through a FUNCTION VALUE: <c>f(1)</c>, where <c>f</c> is a closure.
    ///
    /// <para>No default argument and no <c>params</c>: both are call-site transformations needing the
    /// DECLARATION of the callee, and a function value has none. The type <c>fn(int) -&gt; int</c> says
    /// "one argument", and there is nothing more to know. C# draws the same line at delegates, for the
    /// same reason.</para>
    /// </summary>
    /// <summary>
    /// A method call on an instance of a generic type.
    ///
    /// <para>The method is monomorphized PER TYPE INSTANCE: <c>Box&lt;int&gt;.get</c> and
    /// <c>Box&lt;string&gt;.get</c> are two functions. The substitution comes from the TYPE rather than
    /// from the call — <c>get()</c> has no type parameters of its own, its <c>T</c> is that of
    /// <c>Box</c>.</para>
    /// </summary>
    private TempId? LowerGenericMethodCall(MemberExpr member, GenericInstance owner, CallExpr expr)
    {
        if (_types.RefOf(member) is not FunctionSymbol method)
            throw NotSupported($"call to '{member.Member}' on " +
                               $"'{TypeFacts.Display(owner)}'", expr.Span);

        if (method.Declaration is not FunctionDecl declaration)
            throw NotSupported($"call to '{member.Member}' (no declaration)", expr.Span);

        var target = _instances.RequestMethod(method, declaration, owner, expr.Span);

        var receiver = LowerExpr(member.Target);
        var supplied = MaterializeArguments(declaration, expr.Arguments, member.Member, expr.Span);

        // MaterializeArguments yields already lowered values including defaults and 'params'; the
        // receiver comes before them, as in every method call.
        var args = new TempId[supplied.Length + 1];
        args[0] = receiver;
        Array.Copy(supplied, 0, args, 1, supplied.Length);

        var returns = ReturnTypeOfInstanceMethod(declaration, owner, expr.Span);
        if (IsVoid(returns))
        {
            _b.Emit(new Call(null, target, args, expr.Span));
            return null;
        }

        var dest = _slots.NewTemp(returns);
        _b.Emit(new Call(dest, target, args, expr.Span));
        _fresh.Add(dest);
        return dest;
    }

    /// <summary>
    /// <c>Pair&lt;int&gt;.of(3)</c> — a static method on a generic instance.
    ///
    /// <para>Apart from the missing receiver this is <see cref="LowerGenericMethodCall"/>: the same
    /// monomorphization request, the same substitution of the return type. It stands separately only
    /// because the receiver is parameter 0, and there is none here.</para>
    /// </summary>
    private TempId? LowerGenericStaticCall(MemberExpr member, GenericInstance owner, CallExpr expr)
    {
        if (_types.RefOf(member) is not FunctionSymbol method)
            throw NotSupported($"call to '{member.Member}' on " +
                               $"'{TypeFacts.Display(owner)}'", expr.Span);

        if (method.Declaration is not FunctionDecl declaration)
            throw NotSupported($"call to '{member.Member}' (no declaration)", expr.Span);

        var target = _instances.RequestMethod(method, declaration, owner, expr.Span);
        var args = MaterializeArguments(declaration, expr.Arguments, member.Member, expr.Span);

        var returns = ReturnTypeOfInstanceMethod(declaration, owner, expr.Span);
        if (IsVoid(returns))
        {
            _b.Emit(new Call(null, target, args, expr.Span));
            return null;
        }

        var dest = _slots.NewTemp(returns);
        _b.Emit(new Call(dest, target, args, expr.Span));
        _fresh.Add(dest);
        return dest;
    }

    /// <summary>The return type of a method seen from the instance: the <c>T</c> in <c>fn get(): T</c> is
    /// the type argument of the receiver.</summary>
    private IrType ReturnTypeOfInstanceMethod(FunctionDecl declaration, GenericInstance owner,
        Core.Span span) =>
        declaration.ReturnType is null
            ? VoidType
            : LowerWithOwner(declaration.ReturnType, owner, span);

    /// <summary>
    /// Lowers a written type in the context of a type instance — the substitution goes to the TYPE TABLE
    /// rather than rebuilding a second resolution here.
    /// </summary>
    /// <remarks>
    /// <para>A partial copy here was three times too short: first only the bare case
    /// (<c>fn get(): T</c>), then <c>?T</c>, then <c>T[]</c> — and <c>Iterator&lt;T&gt;</c> was still
    /// missing. Every method returning a GENERIC type was therefore not lowerable:
    /// <c>fn iter(): Iterator&lt;T&gt;</c> is the signature <c>Set&lt;T&gt;</c> failed on.</para>
    /// <para>The same answer as in <see cref="LowerSubstituted"/>: the table can do it completely — it
    /// uses the same stack when lowering the members of a generic instance. It only had to learn that a
    /// substitution applies here.</para>
    /// </remarks>
    private IrType LowerWithOwner(TypeNode node, GenericInstance owner, Core.Span span)
    {
        var mapping = new Dictionary<string, LyrType>(StringComparer.Ordinal);
        var n = Math.Min(owner.Definition.Generics.Length, owner.Arguments.Length);

        for (var i = 0; i < n; i++)
            // Through the OWN substitution: an argument of the instance may itself be a type parameter of
            // the calling function ('Box<T>' in 'wrap<T>').
            mapping[owner.Definition.Generics[i].Name] = SubstituteType(owner.Arguments[i]);

        using var scope = _typeTable.PushSubstitution(mapping);
        return _typeTable.Lower(node);
    }

    /// <summary>
    /// A method call on a TYPE PARAMETER WITH A CONSTRAINT.
    ///
    /// <para>The sema binds <c>x.price()</c> to the interface declaration — that is all it knows in a
    /// generic function. In an INSTANCE the substituted type is settled and with it the method that
    /// really runs: the dynamic dispatch becomes a direct call.</para>
    ///
    /// <para>That is the gain of monomorphization, which Rust and C++ collect the same way, and the
    /// reason a constraint needs no vtable here. A value available through its interface
    /// (<c>let p: P = item;</c>) still goes through <c>callvirt</c>; those are two different questions
    /// and therefore two paths.</para>
    /// </summary>
    private TempId? LowerConstraintCall(MemberExpr member, LyrType concrete, CallExpr expr)
    {
        // A BUILTIN as the substituted type: 'render(42)' with 'extend int :: [Display]'. Primitives have
        // no symbol in SymbolOf, and that stays so, because on it hangs the boundary that a scalar does
        // not fit into an interface slot, which would need boxing. It does not get in the way here: the
        // monomorphization substituted the type, the method is settled, and the call is direct. No fat
        // pointer ever arises, so no boxing is ever needed.
        if (TypeFacts.SymbolOf(concrete) is not { } owner)
        {
            if (_typeTable.BuiltinSymbolOf(concrete) is { } builtin
                && _typeTable.ExtensionMethod(builtin, member.Member) is { } extension
                && extension.Declaration is FunctionDecl extensionDecl
                && TryResolveFunction(extension, out var extensionTarget))
            {
                var self = LowerExpr(member.Target);
                var passed = MaterializeArguments(extensionDecl, expr.Arguments, member.Member,
                    expr.Span);
                var all = new TempId[passed.Length + 1];
                all[0] = self;
                passed.CopyTo(all, 1);

                var resultType = TypeOfExpr(expr);
                if (IsVoid(resultType))
                {
                    _b.Emit(new Call(null, extensionTarget, all, expr.Span));
                    return null;
                }

                var result = _slots.NewTemp(resultType);
                _b.Emit(new Call(result, extensionTarget, all, expr.Span));
                _fresh.Add(result);
                return result;
            }

            throw NotSupported($"call to '{member.Member}' on '{TypeFacts.Display(concrete)}'",
                expr.Span);
        }

        // An own member beats a default. When the concrete type does NOT have the method, it comes as a
        // default from the interface, and its 'this' is the interface type. No direct call leads there:
        // the receiver has to be lifted first, which is exactly what 'callvirt' does. The constraint
        // names the interface, so it is known.
        if (owner.Members.LookupLocal(member.Member) is not FunctionSymbol method
            || method.Declaration is not FunctionDecl declaration)
        {
            if (ReceiverType(member.Target) is TypeParamType parameter)
                foreach (var constraint in parameter.Param.Constraints)
                    if (_typeTable.ConstraintInterface(constraint) is { } iface
                        && iface.Members.LookupLocal(member.Member) is FunctionSymbol)
                    {
                        // The receiver is available as a class reference and 'callvirt' needs an interface
                        // value: lift first (mkiface), then call. The same as at every other place where
                        // a class moves into an interface slot.
                        var lifted = LowerExprAs(member.Target, _typeTable.InterfaceOf(iface));
                        return LowerVirtualCall(member, iface, expr, receiver: lifted);
                    }

            throw NotSupported(
                $"'{owner.Name}' has no '{member.Member}' — the constraint promises it, so this "
                + "is a lowering gap and not a program error", expr.Span);
        }

        // For a generic receiver the method belongs to the instance; otherwise it was lowered in pass 1
        // with all the others.
        var target = concrete is GenericInstance instance
            ? _instances.RequestMethod(method, declaration, instance, expr.Span)
            : TryResolveFunction(method, out var direct)
                ? direct
                : throw NotSupported($"'{owner.Name}.{member.Member}' was not lowered", expr.Span);

        var supplied = MaterializeArguments(declaration, expr.Arguments, member.Member, expr.Span);

        var args = new TempId[supplied.Length + 1];
        args[0] = LowerExpr(member.Target);
        Array.Copy(supplied, 0, args, 1, supplied.Length);

        var returns = concrete is GenericInstance owning
            ? ReturnTypeOfInstanceMethod(declaration, owning, expr.Span)
            : declaration.ReturnType is null ? VoidType : _typeTable.Lower(declaration.ReturnType);

        if (IsVoid(returns))
        {
            _b.Emit(new Call(null, target, args, expr.Span));
            return null;
        }

        var dest = _slots.NewTemp(returns);
        _b.Emit(new Call(dest, target, args, expr.Span));
        _fresh.Add(dest);
        return dest;
    }

    private TempId? LowerIndirectCall(CallExpr expr)
    {
        if (LowerType(_types.TypeOf(expr.Callee), expr.Callee.Span) is not IrFunctionType signature)
            throw Bug("indirect call on a non-function value");

        var callee = LowerExpr(expr.Callee);

        var args = new TempId[expr.Arguments.Length];
        for (var i = 0; i < args.Length; i++)
            args[i] = i < signature.Parameters.Length
                ? LowerExprAs(expr.Arguments[i], signature.Parameters[i])
                : LowerExpr(expr.Arguments[i]); // an arity error was already reported by the sema

        if (IsVoid(signature.Return))
        {
            _b.Emit(new CallIndirect(null, callee, args, signature.Return, expr.Span));
            return null;
        }

        var dest = _slots.NewTemp(signature.Return);
        _b.Emit(new CallIndirect(dest, callee, args, signature.Return, expr.Span));

        // The result belongs to nobody yet; for a struct that saves the structcopy when binding, exactly
        // as for an ordinary call.
        _fresh.Add(dest);
        return dest;
    }

    private TempId? LowerCall(CallExpr expr)
    {
        // 'b?.get()' — optional chaining with a call. The same branch as for the field access, except
        // that the 'some' branch holds a call rather than an 'ldfld'. The call itself lands back here
        // afterwards, with an unwrapped receiver.
        if (expr.Callee is MemberExpr { IsOptional: true } chained
            && !_chainReceivers.ContainsKey(chained.Target))
            return LowerOptionalCall(expr, chained);

        // 'co.next()' — the safe pull on a coroutine, built in like '.length' on an array. Before
        // the indirect-call check: the sema types the member as a function type with no symbol
        // behind it, which is exactly what the value-call test matches.
        if (expr.Callee is MemberExpr { Member: "next" } pull
            && SubstituteType(_types.TypeOf(pull.Target)) is CoroutineOf pulled)
            return LowerCoroutineNext(pull, pulled, expr.Span);

        // The receiver is parameter 0. For `p.get()` the 'p' therefore becomes the first argument; for
        // `P.new(…)` there is none and the call is an ordinary one. Both forms then run through the same
        // path — the difference lies solely in the argument list.
        TempId? receiver = null;
        string calleeName;
        Symbol? bound;

        // The callee is a VALUE rather than a declaration: a closure, a parameter of type
        // 'fn(…) -> …', a field, the result of another call.
        //
        // The type alone does NOT decide that: a declared function also has a function type, and so does
        // an enum variant with a payload ('Shape.Line(1.0)') — which is a constructor, not a value. What
        // makes the call indirect is the BINDING: it points at something HOLDING a function value, or at
        // nothing at all, as in 'mk()()'.
        //
        // Enumerated positively rather than negatively: a list of prohibitions would silently give the
        // wrong answer for every new kind of symbol, and in the dangerous direction.
        if (_types.TypeOf(expr.Callee) is FnType
            && _types.RefOf(expr.Callee) is null or LocalSymbol or ParameterSymbol
               or FieldSymbol or GlobalSymbol)
            return LowerIndirectCall(expr);

        switch (expr.Callee)
        {
            // Shape.Circle(2.0) and Opt<int>.Some(5) are tuple variants. Not a call but a construction,
            // and that holds regardless of how the target is written. The case therefore stands BEFORE
            // the static call: 'Opt<int>.Some' looks like a static method on an instance and is not.
            case MemberExpr member when _types.RefOf(member) is EnumVariantSymbol:
                return LowerVariantCall(member, expr.Arguments, expr, expr.Span);

            // 'Pair<int>.of(3)' — a static method on a generic INSTANCE. The target here is not a value
            // but a type path; there is no receiver, but there is an instantiation. The case stands
            // first, because every case below asks the type of the target EXPRESSION, and a type path
            // has none.
            case MemberExpr { Target: TypePathExpr } member
                when _types.TypeOf(((MemberExpr)expr.Callee).Target)
                     is Sema.NonValueType { Instance: { } owner }:
                // Through the OWN substitution, as the instance-method dispatch below does: in
                // 'fn make<T>()' the call 'List<T>.empty()' names the CALLER's T, and which type
                // that is only the enclosing instantiation knows. Unsubstituted it reached the
                // type table as a bare parameter and threw.
                return LowerGenericStaticCall(member,
                    SubstituteType(owner) as GenericInstance ?? owner, expr);

            // The receiver is an interface value: which implementation runs is settled only at runtime.
            // That is the language's only dynamic dispatch.
            case MemberExpr member
                when ReceiverType(member.Target) is NamedRef
                     { Symbol.Kind: TypeSymbolKind.Interface } iface:
                return LowerVirtualCall(member, iface.Symbol, expr);

            // A generic interface as the receiver ('Iterator<int>'): the same dynamic dispatch, except
            // that the slot table hangs on the INSTANCE — 'Iterator<int>' and 'Iterator<string>' are
            // different entries.
            case MemberExpr member
                when SubstituteType(ReceiverType(member.Target)) is GenericInstance
                     { Definition.Kind: TypeSymbolKind.Interface } genericIface:
                return LowerVirtualCall(member, genericIface, expr);

            // The receiver is a TYPE PARAMETER with a constraint: 'fn total<T :: [P]>(x: T)
            // { x.price(); }'. The sema binds 'price' to the interface, where it has no body. In an
            // instance T is settled, so there is a real method.
            case MemberExpr member
                when ReceiverType(member.Target) is TypeParamType parameter
                     && _substitution.ContainsKey(parameter.Param):
                return LowerConstraintCall(member, SubstituteType(ReceiverType(member.Target)),
                    expr);

            // An INTERFACE DEFAULT method on a concrete receiver: 'it.isFree()', where 'isFree' belongs
            // to the interface rather than to the struct. Its 'this' is the interface type, so no direct
            // call leads there — the receiver is lifted (mkiface) and then called virtually. The same
            // route LowerConstraintCall takes.
            //
            // 'An own member beats a default' sits in the LookupLocal condition: when the concrete type
            // has the method itself, this case falls through to the direct call.
            case MemberExpr member
                when ReceiverType(member.Target) is NamedRef
                     { Symbol: { Kind: TypeSymbolKind.Class or TypeSymbolKind.Struct
                         or TypeSymbolKind.Enum } concrete }
                     && concrete.Members.LookupLocal(member.Member) is not FunctionSymbol
                     && _typeTable.InterfaceProviding(concrete, member.Member) is { } provider:
                return LowerVirtualCall(member, provider, expr,
                    receiver: LowerExprAs(member.Target, _typeTable.InterfaceOf(provider)));

            case MemberExpr member
                when ReceiverType(member.Target) is NamedRef
                     { Symbol.Kind: TypeSymbolKind.Class or TypeSymbolKind.Struct
                         or TypeSymbolKind.Enum }:
                calleeName = member.Member;
                bound = _types.RefOf(member);
                receiver = LowerExpr(member.Target);
                break;

            // The receiver is an instance of a generic type: 'Box<int>.get()'. The method belongs to the
            // INSTANCE rather than to the definition, and its return type may be T.
            case MemberExpr member
                when SubstituteType(ReceiverType(member.Target)) is GenericInstance owner
                     && owner.Definition.Kind is TypeSymbolKind.Class or TypeSymbolKind.Struct:
                return LowerGenericMethodCall(member, owner, expr);

            // An extension on a builtin: 'n.double()' with 'extend int'. The receiver is a scalar and
            // therefore NO NamedRef; without this case it falls into the type or module branch below,
            // which attaches no receiver, and the verifier reports a call with one argument too few.
            //
            // A scalar as parameter 0 needs nothing new: no boxing, no fat pointer, no dispatch. Which
            // function runs is statically settled — that is the whole difference between an inherent
            // extension and one through an interface.
            case MemberExpr member
                when ReceiverType(member.Target) is PrimitiveType
                     && _types.RefOf(member) is FunctionSymbol:
                calleeName = member.Member;
                bound = _types.RefOf(member);
                receiver = LowerExpr(member.Target);
                break;

            case MemberExpr member: // a type or module target: P.new(…), console.println(…)
                calleeName = member.Member;
                bound = _types.RefOf(member);
                break;

            case IdentifierExpr callee:
                calleeName = callee.Name;
                bound = _types.RefOf(callee);
                break;

            default:
                throw NotSupported("call target (only functions and methods)", expr.Callee.Span);
        }

        // A selective import binds through an ImportBindingSymbol; the actual target lies beneath it.
        // Without unwrapping, `import std.io.console { println };` looks different from a call in the
        // same module although it is the same function.
        if (bound is ImportBindingSymbol binding) bound = binding.Target;

        if (bound is not FunctionSymbol symbol)
            throw NotSupported($"call to '{calleeName}' (not a function or method)", expr.Span);

        // Natively backed, by the stdlib or the host: its own instruction type and its own index space.
        //
        // For a METHOD on a host type the import carries the receiver as parameter 0, the same convention
        // as every other method. It stands in 'receiver', because the call 'e.damage(30)' does not have
        // it in the argument list.
        if (_imports.IsNative(symbol))
            return LowerImportCall(_imports.Intern(symbol), expr.Arguments, expr.Span, receiver);

        // 'panic' is a language builtin and therefore has no module it is declared in; the resolver puts
        // it into the root scope. It is bound like any other native, through its symbolic name.
        if (symbol.Name == "panic" && !_functions.ContainsKey(symbol))
        {
            var message = expr.Arguments.Length == 1
                ? LowerExprAs(expr.Arguments[0], new IrScalarType(IrScalar.String))
                : throw NotSupported("panic with other than one argument", expr.Span);

            CallHelper("std.core.panic", expr.Span, message);

            // panic never returns, its return type being 'never'. The block ends here: everything behind
            // it would be dead code, and the verifier rejects unreachable blocks.
            _b.Seal(new Unreachable(expr.Span));
            return null;
        }

        if (symbol.Declaration is not FunctionDecl generic)
            throw NotSupported($"call to '{calleeName}' (no declaration to read parameters from)",
                expr.Span);

        // Generic: not the declaration is called but an INSTANCE of it. Which one is said by the type
        // arguments the sema inferred at the call site; deriving them a second time would be a second
        // truth about the same question.
        FunctionId target;
        if (symbol.Generics.Length > 0)
        {
            // A type argument may itself BE a type parameter when the calling function is already an
            // instance: in 'wrap<T>' the call 'id(x)' calls the instance 'id<T>', and which T that is is
            // known only to the own substitution.
            var typeArguments = _types.TypeArgumentsOf(expr)
                .Select(t => t is TypeParamType p && _substitution.TryGetValue(p.Param, out var b)
                    ? b : t)
                .ToArray();

            target = _instances.Request(symbol, generic, calleeName, null,
                typeArguments, _typeTable, expr.Span);
        }
        else if (!TryResolveFunction(symbol, out target))
        {
            throw NotSupported($"call to '{calleeName}' (external or bodiless)", expr.Span);
        }

        if (symbol.Declaration is not FunctionDecl declaration)
            throw NotSupported($"call to '{calleeName}' (no declaration to read parameters from)",
                expr.Span);

        // The type arguments of this call site, by name — the form in which the type table keeps
        // substitutions. Only that way can the parameter type 'Iterator<T>' become 'Iterator<int>' and
        // the coercion from class to interface arise.
        var calleeSubstitution = symbol.Generics.Length > 0
            ? NamedSubstitutionFor(symbol, _types.TypeArgumentsOf(expr)) : null;

        var supplied = MaterializeArguments(declaration, expr.Arguments, calleeName, expr.Span,
            calleeSubstitution);

        // The receiver comes first: the order is the IR's parameter convention and has to match the one
        // in which FunctionLowerer allocated the slots.
        var offset = receiver is null ? 0 : 1;
        var args = new TempId[supplied.Length + offset];
        if (receiver is { } self) args[0] = self;
        supplied.CopyTo(args, offset);

        var returnType = TypeOfExpr(expr);
        if (IsVoid(returnType))
        {
            _b.Emit(new Call(null, target, args, expr.Span));
            return null;
        }

        var dest = _slots.NewTemp(returnType);
        _b.Emit(new Call(dest, target, args, expr.Span));
        _fresh.Add(dest);
        return dest;
    }

    /// <summary>
    /// The argument list as the callee expects it: exactly one value per declared parameter.
    ///
    /// <para>Here the two forms arise that look different at the source than in the IR: a <c>params</c>
    /// array and an omitted default.</para>
    ///
    /// <para>The default expression is evaluated AT THE CALL SITE rather than once at the callee, the
    /// same choice as in C#. Otherwise it would have to be lowered in a context where the caller's
    /// arguments are not visible.</para>
    /// </summary>
    private TempId[] MaterializeArguments(FunctionDecl callee, Expr[] provided, string name,
        Span span, IReadOnlyDictionary<string, LyrType>? calleeSubstitution = null)
    {
        var parameters = callee.Parameters;
        var args = new TempId[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];

            // 'params xs: T[]' collects everything from here on. The sema allows it only on the last
            // parameter, so the rest really is the rest.
            if (parameter.IsParams)
            {
                args[i] = CollectVariadic(parameter, provided, i, span);
                return args;
            }

            if (i < provided.Length)
            {
                args[i] = LowerArgument(provided[i], parameter, calleeSubstitution);
                continue;
            }

            if (parameter.Default is { } fallback)
            {
                args[i] = LowerArgument(fallback, parameter, calleeSubstitution);
                continue;
            }

            // The sema checked the arity; landing here would mean it slipped through.
            throw Bug($"call to '{name}' at {span} passes {provided.Length} argument(s) but " +
                      $"parameter '{parameter.Name}' has no default");
        }

        if (provided.Length > parameters.Length)
            throw Bug($"call to '{name}' at {span} passes {provided.Length} argument(s) for " +
                      $"{parameters.Length} parameter(s)");

        return args;
    }

    /// <summary>
    /// The remaining arguments as an array: <c>sum(1, 2, 3)</c> becomes <c>sum([1, 2, 3])</c>.
    ///
    /// <para>A READY-MADE ARRAY PASSES THROUGH AS A WHOLE: <c>sum(xs)</c> with <c>xs: int[]</c> is the
    /// array itself, not an array with an array inside. Without this route a variadic function could not
    /// delegate to another. Recognisable from the type of the single remaining argument — nothing more
    /// is needed, because an element never has the same type as the array taking it.</para>
    /// </summary>
    private TempId CollectVariadic(Param parameter, Expr[] provided, int from, Span span)
    {
        if (_typeTable.Lower(parameter.Type) is not IrArrayType array)
            throw NotSupported($"'params {parameter.Name}' whose type is not an array",
                parameter.Span);

        var rest = provided.Length > from ? provided[from..] : [];

        if (rest.Length == 1 && IrType.Equal(TypeOfExpr(rest[0]), array))
            return LowerExpr(rest[0]);

        var elements = new TempId[rest.Length];
        for (var i = 0; i < elements.Length; i++)
            elements[i] = LowerExprAs(rest[i], array.Element);

        var dest = _slots.NewTemp(array);
        _b.Emit(new NewArray(dest, array.Element, elements, span));
        return dest;
    }

    /// <summary>
    /// An argument adapted to the DECLARED parameter type.
    ///
    /// <para>Without this step a class stays a class even when the parameter is an interface, and the
    /// callee gets a bare reference instead of a fat pointer. The verifier catches that, but as a type
    /// mismatch deep in the callee rather than as what it is: a missing coercion at the call site.</para>
    /// </summary>
    private TempId LowerArgument(Expr argument, Param parameter,
        IReadOnlyDictionary<string, LyrType>? calleeSubstitution = null)
    {
        // Only the lowering of the parameter type is shielded: a type this compiler build does not know
        // is reported by the function itself anyway, and complaining twice would be noise. The coercion
        // stands DELIBERATELY outside: inside the try, the catch swallows a missing 'mkiface' and turns a
        // diagnostic into malformed IR.
        IrType expected;
        try
        {
            // Lowered under the substitution OF THE CALLEE rather than the own one: in
            // 'fn count<T>(source: Iterator<T>)' the written parameter type is 'Iterator<T>', and which T
            // is meant is known only to the call site.
            //
            // Without it, lowering the parameter type throws on the unresolved T, the catch below fires,
            // and the argument passes WITHOUT a coercion — a class lands where an interface value has to
            // stand. The verifier reports that as malformed IR, so the compiler crashes instead of
            // diagnosing.
            using (calleeSubstitution is null
                       ? null : _typeTable.PushSubstitution(calleeSubstitution))
                expected = _typeTable.Lower(parameter.Type);
        }
        catch (UnsupportedConstructException)
        {
            return LowerExpr(argument);
        }

        return LowerExprAs(argument, expected);
    }

    /// <summary>
    /// The dynamic call. The receiver is an interface value and carries its concrete type along; the
    /// runtime uses that to look up the vtable.
    ///
    /// <para>The slot rather than the name is the same decision as for the field index: Lyric is
    /// statically typed and has no monkey patching, so the position is fixed at compile time. A name
    /// lookup with an inline cache would solve a problem this language does not have.</para>
    /// </summary>
    private TempId? LowerVirtualCall(MemberExpr member, GenericInstance instance, CallExpr expr) =>
        LowerVirtualCall(member, instance.Definition, expr,
            _typeTable.Intern(instance.Definition, instance.Arguments));

    private TempId? LowerVirtualCall(MemberExpr member, TypeSymbol iface, CallExpr expr,
        TypeId? instanceType = null, TempId? receiver = null)
    {
        // For a generic interface the slot table hangs on the INSTANCE; the slot INDEX is the same,
        // because it comes from the declaration and holds for all instances.
        var interfaceId = instanceType ?? _typeTable.InterfaceOf(iface).Type;

        // Read the slot from the entry of THIS instance: going through the symbol would intern 'Src'
        // without type arguments, and that has no entry.
        var slots = _typeTable.MethodSlotsOf(interfaceId);
        var slot = Array.IndexOf(slots, member.Member);
        if (slot < 0)
            throw NotSupported($"interface '{iface.Name}' has no method '{member.Member}'",
                member.Span);

        var args = new TempId[expr.Arguments.Length + 1];

        // The receiver may already be lifted, for instance for a constraint whose default method goes
        // through the interface.
        args[0] = receiver ?? LowerExpr(member.Target);

        // The signature stands on the interface rather than on an implementation: it is the contract.
        var declaration = iface.Members.LookupLocal(member.Member) is FunctionSymbol method
            ? method.Declaration as FunctionDecl
            : null;

        for (var i = 0; i < expr.Arguments.Length; i++)
            args[i + 1] = declaration is not null && i < declaration.Parameters.Length
                ? LowerExprAs(expr.Arguments[i], _typeTable.Lower(declaration.Parameters[i].Type))
                : LowerExpr(expr.Arguments[i]);

        var returnType = TypeOfExpr(expr);
        if (IsVoid(returnType))
        {
            _b.Emit(new CallVirt(null, interfaceId, slot, args, returnType, expr.Span));
            return null;
        }

        var dest = _slots.NewTemp(returnType);
        _b.Emit(new CallVirt(dest, interfaceId, slot, args, returnType, expr.Span));
        return dest;
    }

    /// <summary>A call to a natively backed function. The signature comes from the import table rather
    /// than from a function: an import has no body.</summary>
    /// <param name="receiver">For a method on a host type the receiver, which becomes parameter 0.
    /// <c>null</c> for every free function.</param>
    private TempId? LowerImportCall(ImportId target, Expr[] arguments, Span span,
        TempId? receiver = null)
    {
        var import = _imports.Used[target.Value];
        var offset = receiver is null ? 0 : 1;

        // The shape carries the DECLARED parameters; the import carries the flattened wire
        // signature. Without a shape — runtime helpers, generated host functions — the two
        // coincide.
        var shape = _imports.ShapeOf(target);
        if (arguments.Length + offset != (shape?.Params.Length ?? import.ParamTypes.Length))
            throw NotSupported($"call to '{import.Name}' with default or variadic arguments", span);

        var args = new List<TempId>(import.ParamTypes.Length);
        if (receiver is { } self) args.Add(self);
        for (var i = 0; i < arguments.Length; i++)
        {
            var value = LowerExpr(arguments[i]);
            if (shape?.Params[i + offset] is not { Struct: { } structType } flat)
            {
                args.Add(value);
                continue;
            }

            // A struct crosses as its fields, read at the call: the same snapshot a by-value
            // pass would take, without the copy — and without the object, once the scalarizer
            // has dissolved the operand.
            for (var f = 0; f < flat.Fields.Length; f++)
            {
                var field = _slots.NewTemp(flat.Fields[f]);
                _b.Emit(new LoadField(field, value, structType, new FieldId(f), flat.Fields[f],
                    span));
                args.Add(field);
            }
        }

        // A struct RETURN: the hidden buffer goes in as the trailing argument, the host fills
        // its slots, and the expression's value is a fresh COPY of the buffer — value semantics
        // is what makes the shared buffer safe (any binding copies), and the scalarizer is what
        // makes the copy free when it never escapes.
        if (shape?.Return is { } returned)
        {
            var bufferType = new IrStructType(returned.Struct);
            var buffer = _slots.NewTemp(bufferType);
            _b.Emit(new LoadGlobal(buffer, _imports.ResultBuffer(target, _globals), bufferType,
                span));
            args.Add(buffer);

            _b.Emit(new CallImport(null, target, [.. args], span));

            var copied = _slots.NewTemp(bufferType);
            _b.Emit(new StructCopy(copied, buffer, returned.Struct, span));
            return copied;
        }

        if (IsVoid(import.ReturnType))
        {
            _b.Emit(new CallImport(null, target, [.. args], span));
            return null;
        }

        var dest = _slots.NewTemp(import.ReturnType);
        _b.Emit(new CallImport(dest, target, [.. args], span));
        return dest;
    }

    /// <summary>
    /// A direct call to a runtime helper through its fixed name.
    ///
    /// <para>The lowering references <c>std.string.concat</c> and <c>std.core.panic</c> without anyone
    /// having imported the modules — the same model as Roslyn's reference to <c>String.Concat</c>. Used
    /// by f-strings, by <c>+</c> and <c>*</c> on <c>string</c>, and by <c>panic</c>.</para>
    /// </summary>
    private TempId CallHelper(string name, Span span, params TempId[] args)
    {
        if (!_imports.TryFind(name, out var import))
            throw NotSupported(
                $"the runtime helper '{name}' (is the standard library on the module path?)", span);

        var target = _imports.Intern(import);
        if (IsVoid(import.ReturnType))
        {
            _b.Emit(new CallImport(null, target, args, span));
            return default;
        }

        var dest = _slots.NewTemp(import.ReturnType);
        _b.Emit(new CallImport(dest, target, args, span));
        return dest;
    }

    /// <summary>
    /// An f-string becomes a chain of <c>concat</c> and the <c>fromXxx</c> converters. No arrays and no
    /// varargs — the IR can do neither, and this way it does not need to. Roslyn does the same for
    /// <c>$"…"</c> without a format spec.
    /// </summary>
    private TempId LowerInterpolatedString(InterpolatedStringExpr expr)
    {
        var stringType = new IrScalarType(IrScalar.String);
        var parts = new List<TempId>();
        var pendingText = new System.Text.StringBuilder();

        void FlushText(Span span)
        {
            if (pendingText.Length == 0) return;
            parts.Add(EmitConst(new StringConst(pendingText.ToString()), stringType, span));
            pendingText.Clear();
        }

        foreach (var segment in expr.Segments)
        {
            switch (segment)
            {
                case InterpText text:
                    // The parser stores the text pieces raw (see InterpText); the escapes are resolved
                    // here, and the doubled braces of the f-string form fold to one — '{{' and '}}'
                    // are the grammar's literal-brace escape, and they exist only in THESE chunks.
                    // Adjacent pieces collect into one constant.
                    pendingText.Append(Escapes.Resolve(
                        text.Text.Replace("{{", "{").Replace("}}", "}")));
                    break;

                case InterpHole hole:
                    FlushText(hole.Span);
                    parts.Add(hole.FormatSpec is { } spec
                        ? FormattedValue(hole.Expr, spec)
                        : ToStringValue(hole.Expr));
                    break;

                default:
                    throw Bug($"unhandled interpolation segment {segment.GetType().Name}");
            }
        }
        FlushText(expr.Span);

        if (parts.Count == 0) return EmitConst(new StringConst(string.Empty), stringType, expr.Span);

        var result = parts[0];
        for (var i = 1; i < parts.Count; i++)
            result = CallHelper("std.string.concat", expr.Span, result, parts[i]);
        return result;
    }

    /// <summary>A hole with a format spec: <c>{avg:N2}</c> becomes <c>std.fmt.formatFloat(avg, "N2")</c>.
    ///
    /// <para>The spec is a LITERAL and is passed as a constant rather than as part of the function name.
    /// Otherwise every spec would need its own import declaration, and <c>{x:N2}</c> and <c>{x:N3}</c>
    /// would be two different functions.</para>
    ///
    /// <para>Without a spec the <c>fromXxx</c> converters remain: a format call that only rebuilds the
    /// default would be a second route to the same result.</para></summary>
    private TempId FormattedValue(Expr expr, string spec)
    {
        var value = LowerExpr(expr);
        var type = TypeOfExpr(expr);
        if (type is not IrScalarType scalar)
            throw NotSupported("formatting a non-scalar value", expr.Span);

        var stringType = new IrScalarType(IrScalar.String);
        var specValue = EmitConst(new StringConst(spec), stringType, expr.Span);

        var helper = scalar.Kind switch
        {
            IrScalar.String => "std.fmt.formatString",
            IrScalar.Bool => "std.fmt.formatBool",
            IrScalar.Char => "std.fmt.formatChar",
            IrScalar.F32 or IrScalar.F64 => "std.fmt.formatFloat",
            _ when IsUnsignedScalar(scalar.Kind) => "std.fmt.formatUint",
            _ when IsIntegerScalar(scalar.Kind) => "std.fmt.formatInt",
            _ => throw NotSupported("formatting a non-scalar value", expr.Span),
        };

        return CallHelper(helper, expr.Span, WidenForHelper(value, scalar.Kind, expr.Span), specValue);
    }

    /// <summary>A hole in an f-string as a string. Strings stay as they are; everything else goes through
    /// the matching converter — the names distinguish by source type, because Lyric has no
    /// Overloading hat.</summary>
    private TempId ToStringValue(Expr expr)
    {
        var value = LowerExpr(expr);
        var type = TypeOfExpr(expr);
        if (type is not IrScalarType scalar)
            throw NotSupported($"interpolating a non-scalar value", expr.Span);

        return scalar.Kind switch
        {
            IrScalar.String => value,
            IrScalar.Bool => CallHelper("std.string.fromBool", expr.Span, value),
            IrScalar.Char => CallHelper("std.string.fromChar", expr.Span, value),
            IrScalar.F32 or IrScalar.F64 => CallHelper("std.string.fromFloat", expr.Span,
                WidenForHelper(value, scalar.Kind, expr.Span)),
            // Unsigned first: 'fromInt' reinterprets a large uint as a negative number. Measured,
            // f"{u}" with u = uint64.MaxValue previously yielded "-1".
            _ when IsUnsignedScalar(scalar.Kind) => CallHelper("std.string.fromUint", expr.Span,
                WidenForHelper(value, scalar.Kind, expr.Span)),
            _ when IsIntegerScalar(scalar.Kind) => CallHelper("std.string.fromInt", expr.Span,
                WidenForHelper(value, scalar.Kind, expr.Span)),
            _ => throw NotSupported($"interpolating a non-scalar value", expr.Span),
        };
    }

    /// <summary>
    /// Widens a scalar to the signature of its converter: integers to <c>i64</c>, <c>f32</c> to
    /// <c>f64</c>.
    ///
    /// <para>The converters in <c>std.string</c> and <c>std.fmt</c> are called <c>fromInt</c> and
    /// <c>fromFloat</c>, singular, because Lyric has no overloading. There is therefore exactly ONE
    /// signature per kind, and it takes the widest type. Whoever passes an <c>int8</c> has to widen it
    /// first.</para>
    ///
    /// <para>Without this step <c>f"{x}"</c> with <c>x: int8</c> crashes the compiler in the IR verifier
    /// ("arg 0 is i8, expected i64") — with a stack trace instead of a diagnostic, and for every type
    /// except <c>int</c> and <c>float</c>.</para>
    ///
    /// <para>KNOWN LIMIT: a <c>uint</c> beyond <c>int64.MaxValue</c> is printed as a negative number, the
    /// bit-pattern cast to <c>i64</c> reinterpreting it. That is not a crash but wrong output, and the fix
    /// would be a <c>fromUint</c> of its own.</para>
    /// </summary>
    private TempId WidenForHelper(TempId value, IrScalar kind, Span span)
    {
        var widened = kind switch
        {
            IrScalar.F32 => IrScalar.F64,
            // A uint8 becomes u64 rather than i64: the intermediate step through a signed type would
            // reinterpret the top bit.
            _ when IsUnsignedScalar(kind) => IrScalar.U64,
            _ when IsIntegerScalar(kind) => IrScalar.I64,
            _ => kind,
        };

        if (widened == kind) return value;

        var from = new IrScalarType(kind);
        var to = new IrScalarType(widened);
        var dest = _slots.NewTemp(to);
        _b.Emit(new Lyric.Ir.Convert(dest, from, to, value, span));
        return dest;
    }

    // ------------------------------------------------------------------ helpers

    private TempId EmitConst(IrConstValue value, IrType type, Span span)
    {
        var dest = _slots.NewTemp(type);
        _b.Emit(new Const(dest, type, value, span));
        return dest;
    }

    /// <summary>
    /// Does this assignment target point at a CAPTURED CELL? It then lies not in a slot of this function
    /// but in a field of its environment, and the write has to go there, or the closure would write into
    /// a copy and the semantics would silently be by-value.
    /// </summary>
    private bool TryCapturedCell(Expr target, out TempId cell, out TypeId cellType,
        out IrType valueType)
    {
        cell = default; cellType = default; valueType = VoidType;

        if (target is not IdentifierExpr identifier) return false;
        if (_types.RefOf(identifier) is not { } symbol) return false;
        if (_slots.TryLookup(symbol, out _)) return false;
        if (!_captureFields.ContainsKey(symbol)) return false;

        var (type, value) = LoadCaptured(symbol, target.Span);
        if (type is not IrRefType reference || !_typeTable.IsCell(reference.Type))
            throw Bug($"assignment to captured '{identifier.Name}', which is not a cell — the " +
                      "sema should have boxed it (ADR-018) or rejected the assignment");

        cell = value;
        cellType = reference.Type;
        valueType = _typeTable.Defs[reference.Type.Value].FieldTypes[0];
        return true;
    }

    private LocalId ResolveLocalTarget(Expr target, string what)
    {
        if (target is not IdentifierExpr identifier)
            throw NotSupported($"{what} target (only parameters and locals)", target.Span);

        var symbol = _types.RefOf(identifier) ?? throw Bug($"identifier '{identifier.Name}' is unbound");
        if (!_slots.TryLookup(symbol, out var slot))
            throw NotSupported($"{what} of '{identifier.Name}'", target.Span);

        return slot;
    }

    private static IrConstValue OneFor(IrType type, Span span) => type switch
    {
        IrScalarType { Kind: IrScalar.F32 or IrScalar.F64 } => new FloatConst(1.0),
        IrScalarType s when IsIntegerScalar(s.Kind) => new IntConst(1),
        _ => throw NotSupported("increment/decrement on a non-numeric type", span)
    };

    /// <summary>Unsigned: decides which converter turns a value into text.</summary>
    /// <remarks>Without this distinction every integer goes through <c>fromInt</c>, and a <c>uint</c>
    /// beyond <c>int64.MaxValue</c> appears as a negative number. Not a crash but wrong output.</remarks>
    private static bool IsUnsignedScalar(IrScalar kind) => kind is
        IrScalar.U8 or IrScalar.U16 or IrScalar.U32 or IrScalar.U64;

    private static bool IsIntegerScalar(IrScalar kind) => kind is
        IrScalar.I8 or IrScalar.I16 or IrScalar.I32 or IrScalar.I64 or
        IrScalar.U8 or IrScalar.U16 or IrScalar.U32 or IrScalar.U64;

    private static bool IsVoid(IrType type) => type is IrScalarType { Kind: IrScalar.Void };

    private IrType TypeOfExpr(Expr expr) =>
        _chainResults.TryGetValue(expr, out var chained)
            ? chained
            : LowerType(_types.TypeOf(expr), expr.Span);

    /// <summary>
    /// The sema type of a call receiver, unwrapped when it comes from a <c>?.</c> chain.
    ///
    /// <para>The case distinction in <see cref="LowerCall"/> asks the STATIC type of the target, and in
    /// the chain that is <c>?Box</c> rather than <c>Box</c>. Without this place it would find neither the
    /// class nor the generic instance nor the interface.</para>
    /// </summary>
    private LyrType ReceiverType(Expr target) =>
        _chainReceivers.ContainsKey(target) && _types.TypeOf(target) is Sema.Optional option
            ? option.Inner
            : _types.TypeOf(target);

    /// <summary>
    /// A sema type to an IR type. The substitution hook of the monomorphization sits here and nothing
    /// else: the mapping itself lives in <see cref="TypeTable.Lower(Sema.LyrType, Core.Span)"/>.
    ///
    /// <para>Not both here, because both were here once. This method used to be a second, complete copy
    /// of the same mapping and, like every second answer to the same question, drifted from the first:
    /// <c>T[]</c> and <c>?T</c> stood here and were missing there, which made a module <c>let</c> with an
    /// array untranslatable while the same expression worked inside a function. The difference is now the
    /// substitution alone.</para>
    /// </summary>
    private IrType LowerType(LyrType type, Span span)
    {
        // Substitute first, then map. Recursively, so 'Box<T>', '?T' and 'T[]' see the instance's
        // arguments too: the TypeTable does not know this function's substitution.
        var concrete = SubstituteType(type);

        // A type parameter the own substitution does not know: that is the boundary of the
        // monomorphization and belongs reported here, where the name is still known.
        if (concrete is TypeParamType parameter)
            throw NotSupported($"type parameter '{parameter.Param.Name}'", span);

        return _typeTable.Lower(concrete, span);
    }

    /// <summary>The return type comes from the syntactic <see cref="TypeNode"/>, because the sema does
    /// not put it into <see cref="TypeResult"/>. The resolution lives in the <see cref="TypeTable"/>, the
    /// same place that resolves field and parameter types, so a factory <c>static fn new(): P</c> yields
    /// the same type as a field of type <c>P</c>.</summary>
    /// <summary>Substitutes the type arguments of this instance into a type, recursively, because an
    /// argument may itself be composite (<c>Box&lt;T[]&gt;</c>).</summary>
    /// <summary>
    /// The id under which a function is callable. Two sources, and the order matters: written functions
    /// have their id from pass 1, an extension method gets it ONLY HERE, at its first call.
    ///
    /// <para>That is the point: an extension that is never called does not reach the bytecode. Without
    /// the distinction every program carries the five Display extensions from <c>std.core</c>, because
    /// that module is always loaded.</para>
    /// </summary>
    private bool TryResolveFunction(FunctionSymbol symbol, out FunctionId id)
    {
        if (_functions.TryGetValue(symbol, out id)) return true;
        if (_typeTable.Extensions is not { } table) return false;
        if (table.TryGet(symbol, out id)) return true;

        if (_typeTable.ExtensionOwnerOf(symbol) is not { } owner) return false;
        if (symbol.Declaration is not FunctionDecl decl || decl.Body is null) return false;
        if (decl.Generics.Length > 0) return false;

        id = table.Request(symbol, decl, owner.Module, owner.TargetName,
            decl.IsStatic ? null : owner.Target,
            decl.IsStatic ? null : owner.TargetNode);
        return true;
    }

    /// <summary>
    /// Substitutes the type arguments of this instance everywhere a type parameter stands.
    ///
    /// <para>BEING COMPLETE IS NOT OPTIONAL HERE. This function was a partial copy three times — first
    /// <c>?T</c> was missing, then <c>T[]</c>, then <c>Box&lt;T&gt;</c>, and every time the error looked
    /// like a new one. Whoever adds a type constructor here adds it here too: an unsubstituted parameter
    /// otherwise arrives as "unsubstituted" in the <see cref="TypeTable"/>, far from its cause.</para>
    /// </summary>
    private LyrType SubstituteType(LyrType type) => type switch
    {
        TypeParamType p when _substitution.TryGetValue(p.Param, out var bound) => bound,
        ArrayOf a => new ArrayOf(SubstituteType(a.Element), a.Size),
        Optional o => new Optional(SubstituteType(o.Inner)),
        Sema.TupleOf t => new Sema.TupleOf(t.Elements.Select(SubstituteType).ToArray()),
        FnType f => new FnType(
            f.Parameters.Select(SubstituteType).ToArray(), SubstituteType(f.Return)),
        CoroutineOf c => new CoroutineOf(SubstituteType(c.Yield)),
        GenericInstance g => new GenericInstance(g.Definition,
            g.Arguments.Select(SubstituteType).ToArray()),
        _ => type,
    };

    private static Core.Span SpanOfDecl(FunctionDecl decl) => decl.Span;

    private IrType LowerDeclaredReturnType()
    {
        if (_decl!.ReturnType is null) return VoidType;

        // In a monomorphized instance the written return type may be a type parameter ('fn id<T>(x: T): T')
        // or contain one ('fn next(): ?T'). The parameters go through the LyrType path and meet the
        // substitution there; the return type comes syntactically and has to meet it here, or the type
        // table would look for a class named 'T'.
        return _substitution.Count > 0
            ? LowerSubstituted(_decl.ReturnType)
            : _typeTable.Lower(_decl.ReturnType);
    }

    /// <summary>
    /// Lowers a written type with the substitution of this instance, RECURSIVELY, because a type
    /// parameter can sit deep: <c>?T</c>, <c>T[]</c>.
    ///
    /// <para>Handling only the bare case sufficed for <c>fn get(): T</c> and fell over at
    /// <c>fn next(): ?T</c> — which is the signature of every iterator.</para>
    /// </summary>
    private IrType LowerSubstituted(TypeNode node)
    {
        // The substitution goes to the TYPE TABLE rather than rebuilding a second resolution here. The
        // table can do it completely — it uses the same stack when lowering the members of a generic
        // instance. It only had to learn that a substitution applies here.
        using var scope = _typeTable.PushSubstitution(NamedSubstitution());
        return _typeTable.Lower(node);
    }

    /// <summary>
    /// The type arguments of a call site as a name map for the type table.
    /// </summary>
    /// <remarks>A type argument may itself be a type parameter of the CALLING function
    /// (<c>wrap&lt;T&gt;</c> calls <c>id&lt;T&gt;</c>), so every one goes through the own substitution
    /// before it enters the map.</remarks>
    private Dictionary<string, LyrType> NamedSubstitutionFor(
        FunctionSymbol callee, IReadOnlyList<LyrType> typeArguments)
    {
        var mapping = new Dictionary<string, LyrType>(StringComparer.Ordinal);
        var n = Math.Min(callee.Generics.Length, typeArguments.Count);

        for (var i = 0; i < n; i++)
            mapping[callee.Generics[i].Name] = SubstituteType(typeArguments[i]);

        return mapping;
    }

    /// <summary>The substitution of this instance with names as keys, the form in which the type table
    /// keeps it. It knows no <see cref="GenericParamSymbol"/>, because it resolves written types, where
    /// only names stand.</summary>
    private Dictionary<string, LyrType> NamedSubstitution()
    {
        var mapping = new Dictionary<string, LyrType>(StringComparer.Ordinal);
        foreach (var (parameter, bound) in _substitution) mapping[parameter.Name] = bound;
        return mapping;
    }

    /// <summary>A scope boundary: valid Lyric for which the backend part is still missing. Turned by
    /// <see cref="ModuleLowerer"/> into a <c>LYR-IR0001</c> diagnostic with file, line and column, so no
    /// position is written into the text here; the DiagnosticEngine renders it.</summary>
    private static UnsupportedConstructException NotSupported(string what, Span span) =>
        new($"{what} is not supported by this compiler version yet", span);

    /// <summary>An internal inconsistency: the compiler is broken, not the source.</summary>
    private InternalCompilationException Bug(string message) =>
        new($"lowering: {message} (in '{_name}')");
}
