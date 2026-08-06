using Lyric.AST;
using Lyric.Core;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Ir.Lowering;

/// <summary>
/// Lowert <b>eine</b> Funktion vom typgeprüften AST in eine <see cref="IrFunction"/>. Ein Objekt pro
/// Funktion (wie <c>IrVerifier.FunctionVerifier</c>), weil Slots, Blöcke und Loop-Stack
/// funktionslokal sind und mit der Funktion sterben.
///
/// <para><b>Statements liefern <c>bool</c>: „fällt der Kontrollfluss durch?"</b> Das ist die
/// tragende Signatur-Entscheidung. Ohne sie kann man nicht entscheiden, ob ein Merge-Block
/// angelegt werden darf — und ein Merge-Block ohne Prädecessoren ist unerreichbar, was der
/// Verifier ablehnt (bewusst: kein <c>SimplifyCfg</c>-Pass in v1). Aus demselben Grund bricht
/// <see cref="LowerStatements"/> ab, sobald ein Statement nicht durchfällt: Code nach einem
/// <c>return</c> darf keinen Block erzeugen.</para>
///
/// <para><b>Werte über Blockgrenzen laufen durch Locals, nicht durch Temps.</b> Ein Temp wird
/// genau einmal definiert, kann also nicht „das Ergebnis aus zwei Zweigen" tragen. if-Ausdruck
/// und <c>&amp;&amp;</c>/<c>||</c> legen darum ein synthetisches Local an, schreiben in beiden Zweigen
/// hinein und lesen es im Merge-Block. Genau deshalb braucht diese IR kein <c>Phi</c>: das
/// Ziel ist eine Stack-VM mit Local-Slots (ADR-001), ein Phi müsste beim Emittieren ohnehin
/// wieder zu Store/Load werden.</para>
///
/// <para>Was P4 <b>nicht</b> lowert (die IR kann es nicht ausdrücken): Nullable, struct/class/enum,
/// Arrays, Tupel, <c>match</c>, <c>for-in</c>, Lambdas, Exceptions, <c>defer</c>, Coroutinen,
/// Generics, Stdlib-/Extern-Calls, f-Strings. Das ist gültiges Lyric, also eine <b>Diagnose</b>
/// (<c>LYR-IR0001</c>) mit Datei/Zeile/Spalte und kein Absturz — siehe
/// <see cref="UnsupportedConstructException"/>. Interne Inkonsistenzen bleiben davon getrennt und
/// werfen weiterhin <see cref="InternalCompilationException"/>.</para>
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

    /// <summary>Slot des Empfängers (immer 0), oder <c>null</c> bei freier bzw. static-Funktion.</summary>
    private readonly LocalId? _thisSlot;
    private IrType? _thisType;

    /// <summary>Der Empfaengertyp dieser Funktion — ein Lambda in ihrem Rumpf erbt ihn,
    /// wenn es <c>this</c> faengt.</summary>
    private readonly TypeSymbol? _receiver;

    /// <summary>Typargumente der Instanz, für die gelowert wird. In P4 immer leer — der Haken sitzt
    /// in <see cref="LowerType"/>, damit die Worklist-Monomorphisierung später nur die Map füllen
    /// muss und nicht den ganzen Ausdrucks-Pfad umbauen.</summary>
    private readonly IReadOnlyDictionary<GenericParamSymbol, LyrType> _substitution;

    /// <summary>
    /// Temps, die einen <b>frisch gebauten</b> Wert halten — Ergebnis eines <c>newobj</c> oder
    /// eines Aufrufs.
    ///
    /// <para>Nur fuer Structs relevant, und dort die Grenze zwischen richtig und verschwenderisch:
    /// ein Wert, den niemand sonst haelt, muss beim Binden nicht kopiert werden. Ohne diese
    /// Unterscheidung bekaeme jedes <c>let p = P { … };</c> ein <c>structcopy</c> direkt hinter
    /// sein <c>newobj</c> — korrekt, aber offensichtlich sinnlos, und in jeder Disassembly zu
    /// sehen.</para>
    /// </summary>
    private readonly HashSet<TempId> _fresh = new();

    /// <summary>Die angehobenen Lambdas des Moduls. Ein Lambda im Rumpf meldet sich hier an und
    /// bekommt sofort seine Id, lange bevor sein eigener Rumpf gelowert wird.</summary>
    private readonly LambdaTable _lambdas;

    /// <summary>
    /// Slots, die eine <b>Zelle</b> halten statt eines Wertes (ADR-018) — je Slot der Typ der
    /// Zelle und der Typ dessen, was darin liegt.
    ///
    /// <para>Ein solcher Slot verhaelt sich fuer den ganzen Rest des Lowerings unauffaellig: nur
    /// <see cref="LoadValue"/> und <see cref="StoreValue"/> wissen davon, und sie sind die
    /// einzigen Stellen, die <c>ldloc</c>/<c>stloc</c> auf benannte Variablen schreiben. Ohne
    /// diese Buendelung muesste jede der rund fuenfzehn Zugriffsstellen die Frage selbst stellen,
    /// und die eine, die es vergisst, laesst Closure und Funktion verschiedene Werte sehen.</para>
    /// </summary>
    private readonly Dictionary<LocalId, (TypeId Cell, IrType Value)> _cells = new();

    /// <summary>Beim Lowern eines Lambdas: welches Environment-Feld haelt welches gefangene
    /// Symbol. Ausserhalb eines Lambdas leer.</summary>
    private readonly Dictionary<Symbol, int> _captureFields =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>Slot des Environments (immer 0, wenn es eins gibt) und sein Typ.</summary>
    private readonly LocalId? _envSlot;
    private readonly TypeId? _envType;

    /// <summary>Das Lambda, das gerade gelowert wird — <c>null</c> bei einer geschriebenen
    /// Funktion. Traegt den Rumpf, weil ein Lambda keinen <see cref="FunctionDecl"/> hat.</summary>
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
        TypeSymbol? receiver = null)
    {
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

        // Der Empfänger ist Parameter 0 und wird VOR den deklarierten Parametern belegt — die
        // Parameter-Konvention der IR ist positionsbasiert, ein späterer Slot wäre ein
        // Falsch-Slot-Read in der VM. Denselben Weg geht CIL mit 'this'.
        if (receiver is not null)
        {
            // Ein Enum-Empfänger ist der Enum-Typ, nicht eine seiner Varianten — welche vorliegt,
            // entscheidet erst das 'match' im Rumpf.
            // In einer Interface-Default-Methode ist 'this' der Interface-Typ selbst — welche
            // Implementierung dahintersteckt, weiss erst die Laufzeit. Ein 'this.foo()' darin wird
            // damit zu einem callvirt, und das ist die richtige Antwort.
            _thisType = receiver.Kind switch
            {
                TypeSymbolKind.Enum => _typeTable.EnumOf(receiver),
                TypeSymbolKind.Interface => _typeTable.InterfaceOf(receiver),
                // Der Empfaenger einer struct-Methode ist der Wert selbst. Dass er eine Kopie ist,
                // hat der Aufrufer erledigt — 'mut fn' mutiert damit nur diese Kopie, genau wie
                // Sprache.md §3.2 es beschreibt.
                TypeSymbolKind.Struct => _typeTable.StructOf(receiver),
                _ => _typeTable.RefTo(receiver),
            };
            _thisSlot = _slots.Declare("this", _thisType);
        }

        // Parameter-Konvention: die ersten ParamCount Locals SIND die Parameter, in Reihenfolge.
        // Ohne sie trägt die IR nirgends Parameter-Typen und ein Call wäre nicht typprüfbar.
        foreach (var p in decl.Parameters)
        {
            // 'params' und Default-Werte sind reine AUFRUFSTELLEN-Themen: der Callee sieht ein
            // gewoehnliches T[] bzw. einen gewoehnlichen Parameter. Materialisiert werden beide
            // dort, wo der Aufruf steht — siehe MaterializeArguments.
            //
            // Die Alternative (Callee baut sich seine Defaults selbst) haette bedeutet, dass ein
            // Default-Ausdruck einmal pro Funktion statt einmal pro Aufruf gelowert wird; bei
            // 'params' waere die Signatur zusaetzlich variadisch geworden, und das kann diese IR
            // nicht. C# entscheidet aus demselben Grund an der Aufrufstelle.

            if (_types.RefOf(p) is not ParameterSymbol ps)
                throw Bug($"parameter '{p.Name}' was not bound by the type checker");
            _slots.DeclareFor(ps, LowerType(ps.Type, p.Span));
        }
    }

    /// <summary>
    /// Der Lowerer fuer ein <b>angehobenes Lambda</b> (ADR-018).
    ///
    /// <para>Eine eigene Factory statt eines synthetischen <see cref="FunctionDecl"/>: der
    /// GlobalInitializer darf sich einen bauen, weil seine Statements echte AST-Knoten sind. Hier
    /// ginge das nicht — die Sema hat die <c>LambdaParam</c>-Knoten an ihre Symbole gebunden, und
    /// nachgebaute <c>Param</c>-Knoten waeren andere Objekte ohne Bindung. Der Umweg haette also
    /// eine zweite Symbolaufloesung gebraucht.</para>
    /// </summary>
    public static FunctionLowerer ForLambda(LambdaExpr lambda, string name,
        IReadOnlyList<Symbol> captures, bool capturesThis, IrType environmentType,
        TypeSymbol? receiver, TypeResult types,
        IReadOnlyDictionary<FunctionSymbol, FunctionId> functions, ImportTable imports,
        TypeTable typeTable, GlobalTable globals, LambdaTable lambdas) =>
        new(lambda, name, captures, capturesThis, environmentType, receiver, types, functions,
            imports, typeTable, globals, lambdas);

    private FunctionLowerer(LambdaExpr lambda, string name, IReadOnlyList<Symbol> captures,
        bool capturesThis, IrType environmentType, TypeSymbol? receiver, TypeResult types,
        IReadOnlyDictionary<FunctionSymbol, FunctionId> functions, ImportTable imports,
        TypeTable typeTable, GlobalTable globals, LambdaTable lambdas)
    {
        _lambda = lambda;
        _receiver = receiver;
        _name = name;
        _types = types;
        _functions = functions;
        _imports = imports;
        _typeTable = typeTable;
        _globals = globals;
        _lambdas = lambdas;
        _substitution = ModuleLowerer.NoSubstitution;
        _b = new BlockBuilder(_blocks);

        _returnType = _types.TypeOf(lambda) is FnType fn
            ? LowerType(fn.Return, lambda.Span)
            : throw Bug("lambda has no function type");

        // Das Environment ist Parameter 0 — dieselbe Position, die 'this' bei einer Methode
        // belegt (ADR-014). Damit ist ein Closure-Aufruf ein gewoehnlicher Aufruf, und die VM
        // braucht fuer 'callind' keinen zweiten Frame-Aufbau.
        if (environmentType is IrRefType env)
        {
            _envType = env.Type;
            _envSlot = _slots.Declare("<env>", environmentType);

            for (var i = 0; i < captures.Count; i++) _captureFields[captures[i]] = i;

            // 'this' liegt hinter den benannten Captures, wenn es gefangen wird — es ist kein
            // Symbol (es hat keine Deklaration), also braucht es einen eigenen Platz statt eines
            // Eintrags in derselben Map.
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
    /// Der Lowerer fuer den <b>Rumpf einer Coroutine</b> (Sprache.md §8).
    ///
    /// <para>Er sieht aus wie eine gewoehnliche Funktion mit einem Parameter — dem Zustandsobjekt
    /// — und dem Yield-Typ als Rueckgabe. Genau das ist er auch: <c>resume</c> ist ein
    /// gewoehnlicher Aufruf. Die Coroutine steckt allein darin, WO die Variablen liegen und dass
    /// der erste Block ein Sprungverteiler ist.</para>
    /// </summary>
    public static FunctionLowerer ForCoroutineBody(FunctionDecl decl, string name, TypeId state,
        IrType yieldType, TypeSymbol? receiver, TypeResult types,
        IReadOnlyDictionary<FunctionSymbol, FunctionId> functions, ImportTable imports,
        TypeTable typeTable, GlobalTable globals, LambdaTable lambdas) =>
        new(decl, name, state, yieldType, receiver, types, functions, imports, typeTable, globals,
            lambdas);

    private FunctionLowerer(FunctionDecl decl, string name, TypeId state, IrType yieldType,
        TypeSymbol? receiver, TypeResult types,
        IReadOnlyDictionary<FunctionSymbol, FunctionId> functions, ImportTable imports,
        TypeTable typeTable, GlobalTable globals, LambdaTable lambdas)
    {
        _decl = decl;
        _name = name;
        _types = types;
        _functions = functions;
        _imports = imports;
        _typeTable = typeTable;
        _globals = globals;
        _lambdas = lambdas;
        _receiver = receiver;
        _substitution = ModuleLowerer.NoSubstitution;
        _b = new BlockBuilder(_blocks);
        _coroutineState = state;
        _returnType = yieldType;

        // Slot 0 haelt das Zustandsobjekt. Es ist das einzige, was in einem Frame-Slot liegt —
        // alles andere muss den naechsten 'yield' ueberleben und wohnt deshalb darin.
        _stateSlot = _slots.Declare("<state>", new IrRefType(state));

        // Feld 0 ist der Wiedereintrittspunkt. Er gehoert keinem Symbol, deshalb steht er hier
        // und nicht in _stateFields.
        _stateTypes.Add(new IrScalarType(IrScalar.I32));
        _stateNames.Add("<resume>");

        // 'this' und die Parameter ueberleben den ersten 'yield' genauso wie jedes Local — die
        // Fabrik hat sie beim Erzeugen hineingeschrieben.
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

    /// <summary>Feldindex des gefangenen <c>this</c> im Environment, falls gefangen.</summary>
    private readonly int? _capturedThisField;

    // ------------------------------------------------------------------ Coroutinen (Sprache.md §8)

    /// <summary>
    /// Der Zustandstyp, wenn hier ein <b>Coroutine-Rumpf</b> gelowert wird — sonst <c>null</c>.
    ///
    /// <para>Im Coroutine-Modus liegen Parameter und Locals nicht in Frame-Slots, sondern in
    /// Feldern dieses Objekts: ein Frame endet bei jedem <c>yield</c>, das Objekt nicht. Slot 0
    /// ist der Wiedereintrittspunkt.</para>
    /// </summary>
    private readonly TypeId? _coroutineState;

    /// <summary>Der Slot, der das Zustandsobjekt haelt — Parameter 0 im Coroutine-Modus.</summary>
    private LocalId _stateSlot;

    /// <summary>Symbol zu Feldindex im Zustandsobjekt. Waechst waehrend des Lowerings; das Layout
    /// wird danach nachgetragen (siehe <see cref="TypeTable.CompleteCoroutineState"/>).</summary>
    private readonly Dictionary<Symbol, int> _stateFields = new(ReferenceEqualityComparer.Instance);

    private readonly List<IrType> _stateTypes = new();
    private readonly List<string> _stateNames = new();

    /// <summary>Die Bloecke, an denen ein <c>resume</c> wieder einsteigt — Index n gehoert zum
    /// n-ten <c>yield</c>. Der Sprungverteiler entsteht daraus, wenn alle bekannt sind.</summary>
    private readonly List<BlockId> _resumePoints = new();

    private bool InCoroutine => _coroutineState is not null;

    /// <summary>Legt ein Feld im Zustandsobjekt an und liefert seinen Index.</summary>
    private int DeclareStateField(Symbol symbol, string name, IrType type)
    {
        var index = _stateTypes.Count;
        _stateTypes.Add(type);
        _stateNames.Add(name);
        _stateFields[symbol] = index;
        return index;
    }

    /// <summary>Das Zustandsobjekt selbst — es liegt in einem gewoehnlichen Slot, weil es sich
    /// waehrend eines Laufs nicht aendert.</summary>
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

    // ------------------------------------------------------------------ Closures (ADR-018)

    /// <summary>
    /// Ein Lambda: Environment bauen, Funktion anmelden, Fat Pointer erzeugen.
    ///
    /// <para>Die gehobene Funktion wird hier <b>nicht</b> gelowert — sie wird nur angemeldet und
    /// bekommt sofort ihre Id. Das ist die Bedingung dafuer, dass ein rekursives oder
    /// verschachteltes Lambda ueberhaupt geht: sein <c>mkclosure</c> steht fest, bevor sein Rumpf
    /// existiert.</para>
    /// </summary>
    private TempId LowerLambda(LambdaExpr lambda)
    {
        if (LowerType(_types.TypeOf(lambda), lambda.Span) is not IrFunctionType signature)
            throw Bug("lambda has no function type");

        var (captured, capturesThis) = _types.CapturesOf(lambda);

        // Die Werte fuer das Environment werden HIER ausgewertet, im umgebenden Frame — das ist
        // der Kern von „faengt beim Erzeugen ein": ein spaeterer Aufruf sieht den Stand von jetzt.
        // Bei einem geboxten 'var' ist dieser Stand die ZELLE, nicht ihr Inhalt; genau dadurch
        // teilen beide Seiten dieselbe Variable.
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

        // Ohne Captures gibt es kein Environment und keine Allokation — der haeufige Fall bei
        // einem Filter wie '(x) => x > 0'.
        IrType environment = fieldTypes.Length == 0
            ? VoidType
            : _typeTable.EnvironmentFor(_name, fieldTypes, fieldNames);

        var target = _lambdas.Register(lambda, _name, captured, capturesThis, environment,
            ReceiverForLambda());

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
    /// Der Wert, der fuer ein gefangenes Symbol ins Environment wandert.
    ///
    /// <para>Bei einer Zelle ist das die Zelle selbst und nicht ihr Inhalt — sonst waere die
    /// Closure bei einer Kopie gelandet, und ADR-018 waere still zu by-value geworden.</para>
    ///
    /// <para>Wird ein Symbol gefangen, das die UMGEBENDE Funktion selbst schon gefangen hat, liegt
    /// es dort im Environment und nicht in einem Slot. Verschachtelte Lambdas loesen ihre Captures
    /// deshalb ueber dieselbe Kette auf, die auch ein gewoehnlicher Bezeichner nimmt.</para>
    /// </summary>
    private (IrType Type, TempId Value) LoadCaptured(Symbol symbol, Core.Span span)
    {
        if (_slots.TryLookup(symbol, out var slot))
        {
            var type = _slots.TypeOfLocal(slot); // bei einer Zelle: der Zelltyp — genau richtig
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

    /// <summary>Der Empfaenger-Typ, den ein inneres Lambda erbt, wenn es <c>this</c> faengt.</summary>
    private TypeSymbol? ReceiverForLambda() => _receiver;

    // ------------------------------------------------------------------ Zellen (ADR-018)

    /// <summary>
    /// Liest eine benannte Variable. Liegt sie in einer Zelle, geht der Zugriff ueber deren Feld —
    /// und zwar hier genauso wie in jeder Closure, die sie teilt.
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

    /// <summary>Schreibt eine benannte Variable — in ihren Slot oder in ihre Zelle.</summary>
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

    /// <summary>Der Typ des <b>Wertes</b> in einem Slot — bei einer Zelle also der Inhalt, nicht
    /// die Zelle. Jede Stelle, die bisher <c>TypeOfLocal</c> benutzt hat, um einen Wert zu typen,
    /// muss diese Frage stellen.</summary>
    private IrType ValueTypeOf(LocalId slot) =>
        _cells.TryGetValue(slot, out var cell) ? cell.Value : _slots.TypeOfLocal(slot);

    public IrFunction Run()
    {
        // Ein Lambda hat statt eines Rumpfes einen Ausdruck ODER einen Block (Doku §11). Der
        // Ausdrucks-Fall ist der haeufige und braucht kein 'return' im Quelltext — hier wird es
        // eingesetzt.
        if (_lambda is not null) return RunLambda();
        if (InCoroutine) return RunCoroutineBody();

        if (_decl!.Body is null) throw Bug("function has no body");

        // Der Funktionsrumpf ist selbst ein Scope mit eigenen defers.
        if (LowerScope(_decl.Body))
        {
            // Der Kontrollfluss ist aus dem Body gelaufen. Bei void ist das der Normalfall und
            // braucht das implizite 'ret'. Bei non-void hat die Return-Coverage der Sema
            // (LYR-SEM0017) bewiesen, dass jeder Pfad returnt — hierher kommt man dann nur über
            // einen divergierenden Konstrukt wie 'while (true) { }', dessen Exit-Kante nie
            // feuert. 'unreachable' ist genau dessen ehrliche Kodierung.
            _b.Seal(IsVoid(_returnType)
                ? new Return(null, _decl.Body.Span)
                : new Unreachable(_decl.Body.Span));
        }

        // Der Empfänger zählt als Parameter — er belegt Slot 0 und wird an der Aufrufstelle als
        // Argument 0 übergeben. Ohne ihn hier wäre die Parameter-Konvention verletzt, und der
        // Verifier meldet genau das ("call passes 2 arg(s), expected 1").
        return new IrFunction(_name, _returnType, _decl.Parameters.Length + (_thisSlot is null ? 0 : 1),
            _slots.Locals, _slots.Temps, _blocks)
        {
            Entry = new BlockId(0), Handlers = _handlers,
        };
    }

    /// <summary>
    /// Der Rumpf einer Coroutine: der geschriebene Code, umgeben von einem Sprungverteiler.
    /// </summary>
    private IrFunction RunCoroutineBody()
    {
        var body = _decl!.Body ?? throw Bug("coroutine has no body");

        var start = _b.NewBlock();
        _b.SwitchTo(start);

        if (LowerScope(body))
        {
            // Der Rumpf ist durchgelaufen: die Coroutine ist fertig. -1 merkt sich das, und
            // 'resume' liest es (Sprache.md §8) — der Wert selbst wird nie benutzt.
            var i32 = new IrScalarType(IrScalar.I32);
            var done = _slots.NewTemp(i32);
            // -1 als Zweierkomplement in 32 Bit: der Verifier prueft, dass der Wert in die
            // deklarierte Breite passt.
            _b.Emit(new Const(done, i32, new IntConst(unchecked((ulong)(uint)-1)), body.Span));
            StoreStateField(0, done, body.Span);

            _b.Seal(IsVoid(_returnType)
                ? new Return(null, body.Span)
                : new Return(ZeroOf(_returnType, body.Span), body.Span));
        }

        var dispatch = BuildResumeDispatch(start, body.Span);
        _typeTable.CompleteCoroutineState(_coroutineState!.Value,
            _stateTypes.ToArray(), _stateNames.ToArray());

        // Ein Parameter: das Zustandsobjekt. Alles andere steckt darin.
        return new IrFunction(_name, _returnType, 1, _slots.Locals, _slots.Temps, _blocks)
        {
            Entry = dispatch, Handlers = _handlers,
        };
    }

    /// <summary>
    /// Der Wert, den eine <b>durchgelaufene</b> Coroutine formal zurueckgibt.
    ///
    /// <para>Er ist nie beobachtbar — <c>resume</c> liest den Zustand, bevor es ruft, und wirft
    /// bei -1 (Sprache.md §8). Der Rumpf muss trotzdem etwas zurueckgeben, weil er eine
    /// gewoehnliche Funktion ist und der Verifier keinen Pfad ohne <c>ret</c> durchlaesst.</para>
    ///
    /// <para>Nur Skalare: fuer einen Referenz- oder Enum-Yield-Typ gaebe es hier keinen
    /// natuerlichen Wert, und einen zu erfinden hiesse, einen Nullwert in die Sprache zu tragen,
    /// den sie nicht hat. Die saubere Loesung ist ein optionaler Rueckgabetyp fuer die
    /// Rumpf-Funktion; sie gehoert zu <c>resume</c> und damit in den naechsten Slice.</para>
    /// </summary>
    private TempId ZeroOf(IrType type, Core.Span span)
    {
        if (type is not IrScalarType scalar)
            throw NotSupported(
                "a coroutine yielding a non-scalar type (the state machine needs a value for the "
                + "path where the body runs out)", span);

        var dest = _slots.NewTemp(type);
        _b.Emit(new Const(dest, type, scalar.Kind switch
        {
            IrScalar.F32 or IrScalar.F64 => new FloatConst(0.0),
            IrScalar.Bool => new BoolConst(false),
            IrScalar.String => new StringConst(""),
            _ => new IntConst(0),
        }, span));
        return dest;
    }

    /// <summary>
    /// Der Rumpf eines angehobenen Lambdas. Zwei Formen (Doku §11): ein Ausdruck <b>ist</b> der
    /// Rueckgabewert, ein Block liefert ueber <c>return</c>.
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
                // Ein void-Kontext verwirft den Wert: '() => doStuff()' ist erlaubt und ruft nur.
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

        // Das Environment zaehlt als Parameter 0 — dieselbe Rechnung wie beim Empfaenger.
        return new IrFunction(_name, _returnType,
            _lambda.Parameters.Length + (_envSlot is null ? 0 : 1),
            _slots.Locals, _slots.Temps, _blocks)
        {
            Entry = new BlockId(0), Handlers = _handlers,
        };
    }

    // ------------------------------------------------------------------ Statements

    /// <summary>Lowert die Statements eines Blocks. Liefert false, sobald der Kontrollfluss
    /// endet — die restlichen Statements sind dann unerreichbar und werden verworfen.</summary>
    private bool LowerStatements(Block block)
    {
        foreach (var stmt in block.Statements)
            if (!LowerStmt(stmt)) return false;
        return true;
    }

    /// <summary>true = Kontrollfluss fällt durch, false = Block ist versiegelt.</summary>
    private bool LowerStmt(Stmt stmt)
    {
        switch (stmt)
        {
            // Ein geschachtelter Block ist ein eigener Scope — seine defers laufen an
            // seinem Ende, nicht erst am Funktionsende (Sprache.md §5).
            case Block b: return LowerScope(b);
            case BindingStmt b: return LowerBinding(b);
            // 'panic(…)' hat Rueckgabetyp 'never' (§9) und versiegelt seinen Block. Ein
            // Ausdruck kann den Kontrollfluss also beenden — der Rueckgabewert muss das melden,
            // sonst versucht der Aufrufer spaeter, denselben Block ein zweites Mal zu versiegeln.
            case ExprStmt e: LowerExprOrVoid(e.Expr); return !_b.IsSealed;

            // Nur im synthetischen Global-Initialisierer (siehe GlobalInitializer).
            case GlobalInitStmt g: LowerGlobalInit(g); return true;
            case IfStmt s: return LowerIf(s);
            case WhileStmt s: return LowerWhile(s);
            case DoWhileStmt s: return LowerDoWhile(s);
            case ReturnStmt s: return LowerReturn(s);
            case BreakStmt s: return LowerBreak(s);
            case ContinueStmt s: return LowerContinue(s);

            case ForInStmt s: throw NotSupported("'for-in' (needs Iterator)", s.Span);
            case MatchStmt s: LowerMatch(s.Scrutinee, s.Arms, null, s.Span); return true;
            case TryStmt s: return LowerTry(s);
            case ThrowStmt s: return LowerThrow(s);
            // 'defer' registriert nur — die Rumpfe setzt LowerScope an die Ausgaenge.
            case DeferStmt s: _defers.Peek().Add(s); return true;
            case YieldStmt s: return LowerYield(s);
            case ErrorStmt s: throw Bug($"error statement reached lowering at {s.Span}");

            default: throw Bug($"unhandled statement {stmt.GetType().Name}");
        }
    }


    // ------------------------------------------------------------------ Exceptions und defer

    /// <summary>
    /// Die pro Scope aufgelaufenen <c>defer</c>-Statements, aeusserster Scope zuunterst.
    ///
    /// <para><c>defer</c> registriert nichts zur Laufzeit: welche Rumpfe faellig sind, steht zur
    /// Compile-Zeit fest, also setzt das Lowering sie direkt an jeden Ausgang. Ein Laufzeit-Stack
    /// (Gos Modell) braeuchte Closures — die gibt es erst in P6 — und kostete auf jedem Pfad
    /// etwas, auch dort, wo nichts zu tun ist. Der Preis ist Code-Duplikation je Ausgang.</para>
    /// </summary>
    private readonly Stack<List<DeferStmt>> _defers = new();

    /// <summary>
    /// Die geschuetzten Regionen dieser Funktion, in Entstehungsreihenfolge.
    ///
    /// <para>Die ist bereits <b>innerste zuerst</b>: ein inneres <c>try</c> wird vollstaendig
    /// gelowert, bevor das aeussere seinen Handler antraegt. Genau diese Reihenfolge ist der
    /// Vertrag beim Abwickeln.</para>
    /// </summary>
    private readonly List<IrHandler> _handlers = new();

    private bool LowerThrow(ThrowStmt stmt)
    {
        // KEIN EmitAllPendingDefers hier: ein 'throw' wickelt ab, und beim Abwickeln laufen die
        // defer-Rumpfe ueber die finally-Region ihres Scopes. Beides zu tun liesse jeden Rumpf
        // zweimal laufen — genau das war der Fall, bevor die Regionen da waren.
        //
        // Der Unterschied zu 'return': ein return verlaesst den Scope normal, da greift keine
        // Region, und die Rumpfe muessen inline stehen.
        var value = LowerExpr(stmt.Value);

        // Bei einem Klassentyp steht der konkrete Typ hier fest (ADR-003: keine Inheritance);
        // bei einem Interface-Wert traegt ihn der Fat Pointer, und die Runtime liest ihn dort.
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
    /// <para>Der Rumpf belegt einen <b>zusammenhaengenden Blockbereich</b>. Das ist keine Annahme,
    /// sondern eine Folge davon, wie <see cref="BlockBuilder"/> Ids vergibt: alles, was waehrend
    /// des Rumpfes entsteht, liegt dazwischen — auch verschachtelte Konstrukte. Die Handler
    /// entstehen danach und liegen damit ausserhalb ihres eigenen Bereichs.</para>
    ///
    /// <para>Der gefangene Wert geht in einen <b>Slot</b>, nicht auf den Stack: an einer
    /// Blockgrenze ist der Stack leer (Bytecode.md §4), und ein Handler-Block ist eine
    /// Blockgrenze. CIL schiebt den Wert dort auf den Stack und kann sich das leisten, weil es
    /// diese Invariante nicht hat.</para>
    /// </summary>
    private bool LowerTry(TryStmt stmt)
    {
        // Eigener Block fuer den Rumpf: der Bereich muss an einer Blockgrenze anfangen, sonst
        // deckte er auch Code vor dem 'try' ab.
        var start = _b.NewBlock();
        _b.Seal(new Branch(start, stmt.Span));
        _b.SwitchTo(start);

        var bodyFallsThrough = LowerScope(stmt.Body);
        var bodyLast = _b.CurrentId;
        var end = new BlockId(_blocks.Count);

        var merge = _b.NewBlock();
        if (bodyFallsThrough) _b.SealBlock(bodyLast, new Branch(merge, stmt.Span));

        foreach (var clause in stmt.Catches)
        {
            var handler = _b.NewBlock();
            _b.SwitchTo(handler);

            LocalId? slot = null;
            TypeId? caught = null;

            if (clause.BindingType is { } declared)
            {
                // Ueber das gebundene Symbol, nicht ueber den TypeNode: die Sema legt die
                // Auflegung eines catch-Typs in ihre eigene Tabelle (BindRef auf dem
                // CatchClause), nicht in die des Resolvers. Den TypeNode hier noch einmal
                // aufzuloesen waere eine zweite Wahrheit ueber Sichtbarkeit.
                var symbol = _types.RefOf(clause) as LocalSymbol
                    ?? throw Bug($"catch binding at {clause.Span} was not bound by the type checker");

                var type = LowerType(symbol.Type, declared.Span);
                caught = type switch
                {
                    IrRefType r => r.Type,
                    IrInterfaceType i => i.Type,
                    _ => throw NotSupported(
                        "catching a non-class type (only classes and interfaces are throwable)",
                        clause.Span),
                };

                slot = _slots.DeclareFor(symbol, type);
            }
            else if (clause.BindingName is { } anyName)
            {
                // 'catch (e)' ohne Typ faengt jeden Throwable. Ein Slot dafuer braeuchte den
                // Throwable-Typ als Interface, und das haengt an derselben Luecke wie die
                // Builtin-Konformanz — sie kommt mit der Stdlib (M8).
                throw NotSupported(
                    $"'catch ({anyName})' without a type (an untyped catch binding needs the " +
                    "Throwable interface, which arrives with the stdlib in M8)", clause.Span);
            }

            var handlerFallsThrough = LowerScope(clause.Body);
            if (handlerFallsThrough) _b.Seal(new Branch(merge, clause.Span));

            _handlers.Add(new IrHandler(start, end, IrHandlerKind.Catch, caught, handler, slot));
        }

        _b.SwitchTo(merge);
        return true;
    }

    /// <summary>
    /// Ein Scope mit eigenen <c>defer</c>s: Rumpf lowern, danach die registrierten Rumpfe in
    /// <b>LIFO</b>-Reihenfolge (Sprache.md §5).
    /// </summary>
    private bool LowerScope(Block block)
    {
        // Ob dieser Scope defers hat, steht in seinen EIGENEN Statements — ein defer in einem
        // geschachtelten Block gehoert dorthin. Die Vorabfrage spart jedem defer-freien Scope die
        // zusaetzliche Blockgrenze, und das sind fast alle.
        var hasDefers = block.Statements.Any(st => st is DeferStmt);
        if (!hasDefers) return LowerPlainScope(block);

        // Eigener Block: die geschuetzte Region muss an einer Blockgrenze anfangen.
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

            // Der normale Pfad bekommt die Rumpfe direkt — kein Handler, keine Laufzeitkosten.
            if (fallsThrough) EmitDefers(pending);
        }
        finally
        {
            _defers.Pop();
        }

        var end = new BlockId(_blocks.Count);
        var afterBody = _b.CurrentId;

        // Und derselbe Rumpf noch einmal als finally-Region, fuer den Fall, dass eine Exception
        // durch diesen Scope hindurchlaeuft. Sprache.md §5 verlangt „laeuft auf jedem Scope-Exit
        // (auch bei Exception)"; die normalen Ausgaenge sind oben bedient, dieser hier nicht.
        //
        // Der Preis ist Code-Duplikation: die Rumpfe stehen einmal inline und einmal hier. Die
        // Alternative — ausschliesslich ueber die Region gehen — verlagerte auch den normalen
        // Pfad in den Unwinder und machte jeden Scope-Exit zu einem Handler-Durchlauf.
        var cleanup = _b.NewBlock();
        _b.SwitchTo(cleanup);
        EmitDefers(pending);
        _b.Seal(new EndFinally(block.Span));

        _handlers.Add(new IrHandler(start, end, IrHandlerKind.Finally, null, cleanup, null));

        // Nach dem Rumpf geht es hinter der Region weiter — der Cursor steht sonst im
        // finally-Block, der auf dem normalen Pfad nie erreicht wird.
        if (fallsThrough)
        {
            var after = _b.NewBlock();
            _b.SealBlock(afterBody, new Branch(after, block.Span));
            _b.SwitchTo(after);
        }

        return fallsThrough;
    }

    /// <summary>Ein Scope ohne <c>defer</c>: nichts zu bewachen, nichts nachzuraeumen.</summary>
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

    /// <summary>LIFO: zuletzt registriert laeuft zuerst.</summary>
    private void EmitDefers(List<DeferStmt> pending)
    {
        for (var i = pending.Count - 1; i >= 0; i--) LowerStmt(pending[i].Body);
    }

    /// <summary>Alle offenen <c>defer</c>s, innerste zuerst — vor einem <c>return</c> oder
    /// <c>throw</c>, das mehrere Scopes auf einmal verlaesst. Ein <c>Stack&lt;T&gt;</c> zaehlt von
    /// oben auf, die Reihenfolge stimmt also von selbst.</summary>
    private void EmitAllPendingDefers()
    {
        foreach (var scope in _defers) EmitDefers(scope);
    }

    private bool LowerBinding(BindingStmt binding)
    {
        if (_types.RefOf(binding) is not LocalSymbol local)
            throw Bug($"binding '{binding.Name}' was not bound by the type checker");

        var type = LowerType(local.Type, binding.Span);

        // In einer Coroutine ueberlebt JEDE lokale Variable den naechsten 'yield' — also liegt
        // keine in einem Frame-Slot. Konservativ: es wird nicht geprueft, ob ein Local wirklich
        // ueber ein 'yield' hinweg lebt. Die Lebendigkeitsanalyse waere eine Optimierung, die nur
        // Objektgroesse spart, und ihre Fehler faenden erst zur Laufzeit auf.
        if (InCoroutine)
        {
            var field = DeclareStateField(local, binding.Name, type);
            if (binding.Initializer is not null)
                StoreStateField(field, LowerExprAs(binding.Initializer, type), binding.Span);
            return true;
        }

        // Ein gefangenes 'var' lebt in einer Zelle (ADR-018) — der Slot haelt dann die Zelle, und
        // sie muss existieren, BEVOR irgendjemand hineinschreibt. Deshalb steht das newobj hier
        // und nicht bei der ersten Zuweisung: ein 'var n: int;' ohne Initialisierer wird spaeter
        // beschrieben, und dort waere die Zelle sonst noch nicht da.
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

        // Ohne Initializer bleibt der Slot ungeschrieben: die Definite-Assignment-Analyse hat
        // bewiesen, dass jeder Read eine Zuweisung sieht.
        if (binding.Initializer is not null)
            _b.Emit(new StoreLocal(slot, LowerExprAs(binding.Initializer, type), binding.Span));

        return true;
    }

    private bool LowerReturn(ReturnStmt stmt)
    {
        // Der Rueckgabewert wird VOR den defer-Rumpfen ausgewertet: 'defer' darf den Wert nicht
        // mehr aendern, den 'return' bereits bestimmt hat (Go haelt es genauso).
        var returned = stmt.Value is null ? null : (TempId?)LowerExprAs(stmt.Value, _returnType);
        EmitAllPendingDefers();
        _b.Seal(new Return(returned, stmt.Span));
        return false;
    }

    private bool LowerBreak(BreakStmt stmt)
    {
        if (_loops.Count == 0) throw Bug($"'break' outside a loop at {stmt.Span}");
        _b.Seal(new Branch(_loops.Peek().BreakTarget, stmt.Span));
        return false;
    }

    private bool LowerContinue(ContinueStmt stmt)
    {
        if (_loops.Count == 0) throw Bug($"'continue' outside a loop at {stmt.Span}");
        _b.Seal(new Branch(_loops.Peek().ContinueTarget, stmt.Span));
        return false;
    }

    /// <summary>
    /// Der Merge-Block wird erst angelegt, wenn mindestens ein Zweig durchfällt. Bei
    /// <c>if (c) { return 1; } else { return 2; }</c> entsteht keiner — er hätte keine
    /// Prädecessoren und der Verifier würde ihn als unerreichbar melden.
    /// </summary>
    private bool LowerIf(IfStmt stmt)
    {
        var condition = LowerExpr(stmt.Condition);
        var thenBlock = _b.NewBlock();

        if (stmt.Else is null)
        {
            // Ohne else ist der false-Zweig der Merge-Block; er ist über die false-Kante
            // garantiert erreichbar und darf deshalb sofort entstehen.
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
        var thenExit = _b.CurrentId; // nach verschachteltem Kontrollfluss nicht mehr thenBlock

        _b.SwitchTo(elseBlock);
        var elseFallsThrough = LowerStmt(stmt.Else); // Block oder else-if
        var elseExit = _b.CurrentId;

        if (!thenFallsThrough && !elseFallsThrough) return false;

        var mergeBlock = _b.NewBlock();
        if (thenFallsThrough) _b.SealBlock(thenExit, new Branch(mergeBlock, stmt.Then.Span));
        if (elseFallsThrough) _b.SealBlock(elseExit, new Branch(mergeBlock, stmt.Else.Span));

        _b.SwitchTo(mergeBlock);
        return true;
    }

    private bool LowerWhile(WhileStmt stmt)
    {
        var condBlock = _b.NewBlock();
        _b.Seal(new Branch(condBlock, stmt.Span));

        _b.SwitchTo(condBlock);
        var condition = LowerExpr(stmt.Condition);
        var condExit = _b.CurrentId; // die Bedingung kann selbst Blöcke erzeugt haben (&&, ||)

        var bodyBlock = _b.NewBlock();
        var exitBlock = _b.NewBlock(); // muss vor dem Body stehen: 'break' braucht sein Ziel
        _b.SealBlock(condExit, new CondBranch(condition, bodyBlock, exitBlock, stmt.Condition.Span));

        _b.SwitchTo(bodyBlock);
        _loops.Push(new LoopScope(ContinueTarget: condBlock, BreakTarget: exitBlock));
        if (LowerStatements(stmt.Body)) _b.Seal(new Branch(condBlock, stmt.Body.Span));
        _loops.Pop();

        _b.SwitchTo(exitBlock);
        return true; // über die false-Kante der Bedingung immer erreichbar
    }

    private bool LowerDoWhile(DoWhileStmt stmt)
    {
        var bodyBlock = _b.NewBlock();
        var condBlock = _b.NewBlock(); // 'continue' springt zur Bedingung, nicht an den Body-Anfang
        var exitBlock = _b.NewBlock();
        _b.Seal(new Branch(bodyBlock, stmt.Span));

        _b.SwitchTo(bodyBlock);
        _loops.Push(new LoopScope(ContinueTarget: condBlock, BreakTarget: exitBlock));
        if (LowerStatements(stmt.Body)) _b.Seal(new Branch(condBlock, stmt.Body.Span));
        _loops.Pop();

        _b.SwitchTo(condBlock);
        var condition = LowerExpr(stmt.Condition);
        _b.Seal(new CondBranch(condition, bodyBlock, exitBlock, stmt.Condition.Span));

        _b.SwitchTo(exitBlock);
        return true;
    }

    // ------------------------------------------------------------------ Ausdrücke

    private TempId LowerExpr(Expr expr) =>
        LowerExprOrVoid(expr) ?? throw Bug($"expression at {expr.Span} produced no value");

    /// <summary>Liefert null nur für den Aufruf einer void-Funktion — der einzige Ausdruck ohne
    /// Wert. Sonst immer ein Temp.</summary>
    private TempId? LowerExprOrVoid(Expr expr) => expr switch
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
        MatchExpr e => LowerMatch(e.Scrutinee, e.Arms, TypeOfExpr(e), e.Span)
                       ?? throw Bug($"match expression produced no value at {e.Span}"),
        MemberExpr e => LowerFieldRead(e),
        IndexExpr e => LowerIndexRead(e),
        ArrayLitExpr e => LowerArrayLiteral(e),
        TupleLitExpr e => throw NotSupported("tuple literal", e.Span),
        StructInitExpr e => LowerObjectInit(e),
        RangeExpr e => throw NotSupported("range expression", e.Span),
        ResumeExpr e => throw NotSupported("'resume'", e.Span),
        ThisExpr e => LowerThis(e),
        AtIdentifierExpr e => throw NotSupported($"attribute '{e.Name}'", e.Span),
        ErrorExpr e => throw Bug($"error expression reached lowering at {e.Span}"),

        _ => throw Bug($"unhandled expression {expr.GetType().Name}")
    };

    private TempId LowerIntLiteral(IntLiteralExpr expr)
    {
        var type = TypeOfExpr(expr);

        // Ein untypisiertes Ganzzahl-Literal in Float-Kontext IST ein Float-Wert, es wird nicht
        // konvertiert (Sprache.md §6.5). `let f: float = 5;` muss also ein FloatConst werden —
        // ein IntConst mit Float-Typ wäre malformed, und der Verifier sagt das auch.
        if (type is IrScalarType { Kind: IrScalar.F32 or IrScalar.F64 })
            return EmitConst(new FloatConst(expr.Value), type, expr.Span);

        // Die Kodierung von IntConst ist Zweierkomplement, nullerweitert auf 64 Bit. Der Parser
        // liefert die Magnitude; ein Minuszeichen ist ein eigener UnaryExpr(Neg).
        return EmitConst(new IntConst(expr.Value), type, expr.Span);
    }

    private TempId LowerFloatLiteral(FloatLiteralExpr expr)
    {
        var type = TypeOfExpr(expr);
        // f32 muss hier verengt werden: ein Const vom Typ f32, dessen Wert kein f32-Wert ist,
        // wäre malformed (und der Verifier meldet es). Die Verengung gehört ins Lowering, damit
        // der Wert im Bytecode deterministisch derselbe ist.
        var value = type is IrScalarType { Kind: IrScalar.F32 } ? (float)expr.Value : expr.Value;
        return EmitConst(new FloatConst(value), type, expr.Span);
    }

    private TempId LowerIdentifier(IdentifierExpr expr)
    {
        var symbol = _types.RefOf(expr) ?? throw Bug($"identifier '{expr.Name}' is unbound");

        // Ein Modul-'let' hat keinen Frame-Slot, sondern einen globalen.
        if (TryLowerGlobalIdentifier(expr) is { } global) return global;

        // In einer Coroutine liegt jede Variable im Zustandsobjekt.
        if (InCoroutine && _stateFields.TryGetValue(symbol, out var stateField))
            return Narrow(expr, LoadStateField(stateField, expr.Span), _stateTypes[stateField]);

        // In einem angehobenen Lambda liegt ein gefangenes Symbol im Environment, nicht in einem
        // Slot. Erst Slots fragen: ein gleichnamiges lokales Symbol IST ein anderes Symbol, und
        // die Referenzgleichheit haelt beide auseinander.
        if (!_slots.TryLookup(symbol, out var slot))
        {
            if (_captureFields.ContainsKey(symbol))
            {
                var (capturedType, capturedValue) = LoadCaptured(symbol, expr.Span);

                // Ist das Gefangene eine Zelle, steht hier ihr Inhalt zur Debatte, nicht sie
                // selbst — die Zelle ist Transportmittel, kein Wert des Programms.
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

            throw NotSupported($"reference to '{expr.Name}' (only parameters, locals and constants)",
                expr.Span);
        }

        return Narrow(expr, LoadValue(slot, expr.Span), ValueTypeOf(slot));
    }

    /// <summary>
    /// Flow-Narrowing (§7): nach <c>if (x != null)</c> sagt die Sema fuer x den Typ T, die Stelle
    /// im Speicher haelt aber weiter ?T — die Einengung ist eine Aussage ueber den Kontrollfluss,
    /// keine ueber den Speicher. Hier wird sie eingeloest: der Lowerer packt aus, wo die Sema T
    /// erwartet.
    ///
    /// <para>Dass das sicher ist, hat die Sema bewiesen — sie engt nur ein, wo sie null
    /// ausgeschlossen hat. Das <c>optget</c> kann deshalb nie panicken; es ist die
    /// Materialisierung eines schon gefuehrten Beweises.</para>
    ///
    /// <para>Herausgezogen, als Captures dazukamen: ein gefangenes <c>?T</c> braucht dieselbe
    /// Einengung wie ein lokales, und zwei Kopien derselben vier Zeilen waeren zwei Orte gewesen,
    /// an denen sie haette fehlen koennen.</para>
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

    /// <summary><c>++</c>/<c>--</c> in beiden Stellungen: Prefix liefert den neuen Wert, Postfix den
    /// alten. Beide schreiben denselben Store.</summary>
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

        var kind = IrBinKindExtensions.FromAst(expr.Operator);
        var lhs = LowerExpr(expr.Left);
        var rhs = LowerExpr(expr.Right);
        var type = TypeOfExpr(expr);

        // xs + ys und xs * n sind eingebaute Sprachsemantik (Sprache.md §6.5), aber KEIN BinOp:
        // der add-Opcode bliebe sonst polymorph und müsste zur Laufzeit Typ-Dispatch machen —
        // dieselbe Begründung wie bei string + string, nur mit eigener Instruktion statt Call.
        if (type is IrArrayType result)
        {
            var built = _slots.NewTemp(type);
            _b.Emit(kind switch
            {
                IrBinKind.Add => new ArrayConcat(built, lhs, rhs, result.Element, expr.Span),
                IrBinKind.Mul => new ArrayRepeat(built, lhs, rhs, result.Element, expr.Span),
                _ => throw NotSupported($"'{IrNames.Bin(kind)}' on arrays", expr.Span),
            });
            return built;
        }

        // Sprache.md §6.5 ueberlaedt '+' und '*' fuer string; das ist eingebaute Semantik, aber
        // KEIN BinOp — sonst waere der add-Opcode polymorph und muesste zur Laufzeit
        // Typ-Dispatch machen (gegen ADR-013). Es lowert zu einem Call in std.string, genau wie
        // das f-String-Lowering seine Teile zusammensetzt.
        if (!kind.IsComparison() && type is IrScalarType { Kind: IrScalar.String })
            return kind switch
            {
                IrBinKind.Add => CallHelper("std.string.concat", expr.Span, lhs, rhs),
                IrBinKind.Mul => CallHelper("std.string.repeat", expr.Span, lhs, rhs),
                _ => throw NotSupported($"'{IrNames.Bin(kind)}' on strings", expr.Span),
            };

        var dest = _slots.NewTemp(type);
        _b.Emit(new BinOp(dest, kind, type, lhs, rhs, expr.Span));
        return dest;
    }

    /// <summary>
    /// <c>a &amp;&amp; b</c> / <c>a || b</c>: der rechte Operand darf nur bedingt laufen, also
    /// Kontrollfluss. Das Ergebnis fließt über ein synthetisches Local, weil ein Temp nur einmal
    /// definiert werden darf.
    /// </summary>
    private TempId LowerShortCircuit(BinaryExpr expr)
    {
        var isAnd = expr.Operator is BinaryOp.LogicalAnd;
        var slot = _slots.DeclareSynthetic(isAnd ? "and" : "or", BoolType);

        var left = LowerExpr(expr.Left);
        _b.Emit(new StoreLocal(slot, left, expr.Left.Span));

        var rhsBlock = _b.NewBlock();
        var mergeBlock = _b.NewBlock();
        // '&&' wertet rechts nur bei true aus, '||' nur bei false — die Kanten sind getauscht.
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

    /// <summary>Wie <see cref="LowerShortCircuit"/>, nur mit zwei schreibenden Zweigen. Beide
    /// Zweige sind Ausdrücke und liefern garantiert einen Wert (Sprache.md §6.2), fallen also
    /// immer durch — anders als beim if-<i>Statement</i> braucht es hier keine Fallunterscheidung.</summary>
    private TempId LowerIfExpr(IfExpr expr)
    {
        var type = TypeOfExpr(expr);
        var slot = _slots.DeclareSynthetic("if", type);

        var condition = LowerExpr(expr.Condition);
        var thenBlock = _b.NewBlock();
        var elseBlock = _b.NewBlock();
        _b.Seal(new CondBranch(condition, thenBlock, elseBlock, expr.Span));

        _b.SwitchTo(thenBlock);
        _b.Emit(new StoreLocal(slot, LowerExpr(expr.Then), expr.Then.Span));
        var thenExit = _b.CurrentId;

        _b.SwitchTo(elseBlock);
        _b.Emit(new StoreLocal(slot, LowerExpr(expr.Else), expr.Else.Span));
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
            // Der Slot-Typ ist die erwartete Form — sonst landete bei 'var d: Damageable; d = p;'
            // eine nackte Klassenreferenz in einem Interface-Slot.
            var value = LowerExprAs(expr.Value, ValueTypeOf(slot));
            StoreValue(slot, value, expr.Span);
            return value;
        }

        if (expr.Operator is BinaryOp.Coalesce) return LowerCoalesceAssign(slot, expr);

        if (expr.Operator is BinaryOp.LogicalAnd or BinaryOp.LogicalOr)
            throw NotSupported("short-circuit assignment ('&&=' / '||=')", expr.Span);

        var type = ValueTypeOf(slot);
        var current = LoadValue(slot, expr.Target.Span);

        var operand = LowerExpr(expr.Value);
        var result = _slots.NewTemp(type);
        _b.Emit(new BinOp(result, IrBinKindExtensions.FromAst(expr.Operator.Value), type,
            current, operand, expr.Span));
        StoreValue(slot, result, expr.Span);
        return result;
    }

    /// <summary>
    /// <c>yield x</c> — der Punkt, an dem die Coroutine aufhoert und spaeter wieder anfaengt.
    ///
    /// <para>Drei Schritte: den Wiedereintrittspunkt ins Zustandsobjekt schreiben, den Wert
    /// zurueckgeben, und den Block DANACH als Ziel merken. Was danach steht, laeuft erst beim
    /// naechsten <c>resume</c> — deshalb ist der Rumpf einer Coroutine kein durchgehender
    /// Kontrollfluss mehr, sondern eine Menge von Einstiegspunkten.</para>
    ///
    /// <para>Der Wiedereintrittspunkt wird <b>vor</b> dem Verlassen geschrieben und nicht danach:
    /// es gibt kein Danach. Ein <c>ret</c> beendet den Frame; das Objekt ist das Einzige, was
    /// bleibt.</para>
    /// </summary>
    private bool LowerYield(YieldStmt stmt)
    {
        if (!InCoroutine) throw Bug("'yield' outside a coroutine body reached the lowerer");

        // Der naechste Einstiegspunkt hat die Nummer n+1: 0 ist "noch nicht gestartet".
        var point = _resumePoints.Count + 1;

        var marker = _slots.NewTemp(new IrScalarType(IrScalar.I32));
        _b.Emit(new Const(marker, new IrScalarType(IrScalar.I32), new IntConst((ulong)point), stmt.Span));
        StoreStateField(0, marker, stmt.Span);

        var value = stmt.Value is null
            ? null
            : (TempId?)LowerExprAs(stmt.Value, _returnType);

        _b.Seal(new Return(value, stmt.Span));

        // Hier geht es beim naechsten 'resume' weiter.
        var continuation = _b.NewBlock();
        _resumePoints.Add(continuation);
        _b.SwitchTo(continuation);

        return true;
    }

    /// <summary>
    /// Der erste Block einer Coroutine: springt dorthin, wo sie aufgehoert hat.
    ///
    /// <para>Er entsteht <b>zuletzt</b> — vorher sind die Einstiegspunkte nicht bekannt. Dass er
    /// trotzdem der erste ist, sagt <see cref="IrFunction.Entry"/>; die IR nummeriert Bloecke, sie
    /// ordnet sie nicht.</para>
    ///
    /// <para>Eine Kette von Vergleichen und keine Sprungtabelle: die IR hat keinen
    /// <c>switch</c>-Terminator, und ihn allein hierfuer einzufuehren waere ein Opcode fuer einen
    /// einzigen Anwendungsfall. Bei den Groessenordnungen, um die es geht — ein Vergleich je
    /// <c>yield</c> im Quelltext —, ist der Unterschied nicht messbar.</para>
    /// </summary>
    private BlockId BuildResumeDispatch(BlockId start, Core.Span span)
    {
        var dispatch = _b.NewBlock();
        _b.SwitchTo(dispatch);

        var i32 = new IrScalarType(IrScalar.I32);
        var current = LoadStateField(0, span);

        for (var i = 0; i < _resumePoints.Count; i++)
        {
            var wanted = _slots.NewTemp(i32);
            _b.Emit(new Const(wanted, i32, new IntConst((ulong)(i + 1)), span));

            var matches = _slots.NewTemp(BoolType);
            _b.Emit(new BinOp(matches, IrBinKind.Eq, i32, current, wanted, span));

            var next = _b.NewBlock();
            _b.Seal(new CondBranch(matches, _resumePoints[i], next, span));
            _b.SwitchTo(next);
        }

        // Kein Treffer heisst "noch nicht gestartet" — der Rumpf beginnt von vorn. Der Fall
        // "durchgelaufen" gehoert nicht hierher: ihn faengt 'resume' ab, bevor es ruft.
        _b.Seal(new Branch(start, span));
        return dispatch;
    }

    /// <summary>Zeigt dieses Zuweisungsziel auf eine Variable im Zustandsobjekt?</summary>
    private bool TryStateField(Expr target, out int field)
    {
        field = -1;
        if (!InCoroutine || target is not IdentifierExpr identifier) return false;
        if (_types.RefOf(identifier) is not { } symbol) return false;
        return _stateFields.TryGetValue(symbol, out field);
    }

    /// <summary>Zuweisung an eine Variable im Zustandsobjekt — dieselben drei Formen wie beim
    /// Slot-Pfad, nur dass gelesen und geschrieben wird, wo die Variable den <c>yield</c>
    /// ueberlebt.</summary>
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

        var current = LoadStateField(field, expr.Target.Span);
        var operand = LowerExpr(expr.Value);
        var result = _slots.NewTemp(type);
        _b.Emit(new BinOp(result, IrBinKindExtensions.FromAst(expr.Operator.Value), type,
            current, operand, expr.Span));
        StoreStateField(field, result, expr.Span);
        return result;
    }

    /// <summary>
    /// Zuweisung an eine gefangene Zelle. Dieselben drei Formen wie beim Slot-Pfad, nur dass
    /// gelesen und geschrieben wird, wo die Variable wirklich lebt.
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

        var current = _slots.NewTemp(type);
        _b.Emit(new LoadField(current, cell, cellType, new FieldId(0), type, expr.Target.Span));

        var operand = LowerExpr(expr.Value);
        var result = _slots.NewTemp(type);
        _b.Emit(new BinOp(result, IrBinKindExtensions.FromAst(expr.Operator.Value), type,
            current, operand, expr.Span));
        _b.Emit(new StoreField(cell, cellType, new FieldId(0), result, expr.Span));
        return result;
    }

    /// <summary>
    /// <c>obj.f = v</c> und <c>obj.f += v</c>.
    ///
    /// <para><b>Das Objekt wird genau einmal ausgewertet.</b> Bei <c>+=</c> ist das der Unterschied
    /// zwischen richtig und falsch, sobald der Ziel-Ausdruck Seiteneffekte hat: <c>next().f += 1</c>
    /// darf <c>next()</c> nicht zweimal rufen. Deshalb wird die Referenz einmal in ein Temp gelowert
    /// und für Lesen und Schreiben wiederverwendet.</para>
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

        if (fieldType is IrScalarType { Kind: IrScalar.String })
            throw NotSupported("string concatenation/repetition (lowers to a call)", expr.Span);

        var current = _slots.NewTemp(fieldType);
        _b.Emit(new LoadField(current, obj, type, field, fieldType, member.Span));

        var operand = LowerExpr(expr.Value);
        var result = _slots.NewTemp(fieldType);
        _b.Emit(new BinOp(result, IrBinKindExtensions.FromAst(expr.Operator.Value), fieldType,
            current, operand, expr.Span));
        _b.Emit(new StoreField(obj, type, field, result, expr.Span));
        return result;
    }

    /// <summary><c>this</c> ist der Slot 0. Dass er existiert, hat die Sema geprüft
    /// (<c>LYR-SEM0008</c> in einer static-Methode) — hier ist ein fehlender Slot ein Bug.</summary>
    private TempId LowerThis(ThisExpr expr)
    {
        if (_thisSlot is not { } slot || _thisType is not { } type)
            throw Bug($"'this' reached lowering outside an instance method at {expr.Span}");

        var dest = _slots.NewTemp(type);
        _b.Emit(new LoadLocal(dest, slot, type, expr.Span));
        return dest;
    }

    /// <summary>
    /// <c>xs[i] = v</c> und <c>xs[i] += v</c>.
    ///
    /// <para>Array <b>und</b> Index werden genau einmal ausgewertet — bei <c>+=</c> ist das der
    /// Unterschied zwischen richtig und falsch, sobald einer von beiden Seiteneffekte hat:
    /// <c>xs[next()] += 1</c> darf <c>next()</c> nicht zweimal rufen.</para>
    /// </summary>
    private TempId LowerElementAssign(IndexExpr indexed, AssignExpr expr)
    {
        var (array, index, element) = ResolveIndexAccess(indexed);

        if (expr.Operator is null)
        {
            var assigned = LowerExpr(expr.Value);
            _b.Emit(new StoreElem(array, index, assigned, expr.Span));
            return assigned;
        }

        if (expr.Operator is BinaryOp.LogicalAnd or BinaryOp.LogicalOr or BinaryOp.Coalesce)
            throw NotSupported("short-circuit or coalescing assignment", expr.Span);

        if (element is IrScalarType { Kind: IrScalar.String } or IrArrayType)
            throw NotSupported("compound assignment on strings or arrays (lowers to a call)", expr.Span);

        var current = _slots.NewTemp(element);
        _b.Emit(new LoadElem(current, array, index, element, indexed.Span));

        var operand = LowerExpr(expr.Value);
        var result = _slots.NewTemp(element);
        _b.Emit(new BinOp(result, IrBinKindExtensions.FromAst(expr.Operator.Value), element,
            current, operand, expr.Span));
        _b.Emit(new StoreElem(array, index, result, expr.Span));
        return result;
    }

    // ------------------------------------------------------------------ Enums (§3.4)

    /// <summary>Der Enum-Typ, zu dem ein Wert gehört — oder eine Scope-Grenze.</summary>
    private (TypeSymbol Symbol, IrEnumType Type) RequireEnum(Expr expr)
    {
        if (_types.TypeOf(expr) is not NamedRef { Symbol.Kind: TypeSymbolKind.Enum } named)
            throw NotSupported($"'{TypeFacts.Display(_types.TypeOf(expr))}' is not an enum", expr.Span);

        return (named.Symbol, _typeTable.EnumOf(named.Symbol));
    }

    /// <summary><c>Shape.Circle(2.0)</c> und <c>Shape.Empty</c> — eine Tuple- bzw. Unit-Variante.
    /// Die Struct-Form <c>Triangle { a = … }</c> läuft über <see cref="LowerObjectInit"/>.</summary>
    private TempId LowerVariantCall(MemberExpr callee, Expr[] arguments, Span span)
    {
        var (symbol, type) = (RefEnumSymbol(callee.Target, callee.Span), (IrEnumType?)null);
        var enumType = _typeTable.EnumOf(symbol);
        var variant = _typeTable.VariantOf(symbol, callee.Member, span);

        var fields = new TempId[arguments.Length];
        for (var i = 0; i < arguments.Length; i++) fields[i] = LowerExpr(arguments[i]);

        var dest = _slots.NewTemp(enumType);
        _b.Emit(new NewVariant(dest, variant, enumType.Type, fields, span));
        return dest;
    }

    /// <summary><c>Shape.Tri { a = 3, b = 4 }</c>. Wie beim Objekt-Literal wird in
    /// <b>Layout</b>-Reihenfolge geschrieben, ausgewertet aber in Quelltext-Reihenfolge — nur dass
    /// Slot 0 das Tag ist und die Nutzfelder bei 1 beginnen.</summary>
    private TempId LowerStructVariant(StructInitExpr expr, TypeSymbol owner)
    {
        var variantName = expr.Path[^1];
        var variant = _typeTable.VariantOf(owner, variantName, expr.Span);
        var layout = _typeTable.Defs[variant.Value];
        var enumType = _typeTable.EnumOf(owner);

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
    /// <c>match</c> als Ausdruck und als Statement — derselbe Code, nur der Ergebnis-Slot fehlt im
    /// Statement-Fall.
    ///
    /// <para><b>Kein Sprungtabellen-Opcode.</b> Gelesen wird das Tag, verglichen wird mit einer
    /// Konstante, verzweigt wird wie überall sonst. Eine Sprungtabelle wäre eine Optimierung —
    /// die Semantik ist eine Kette von Vergleichen, und die Exhaustivität hat die Sema bereits
    /// bewiesen (<c>LYR-SEM0050</c>), weshalb der letzte Arm ohne Fallback auskommt.</para>
    /// </summary>
    /// <summary>
    /// <c>match</c> als Ausdruck und als Statement, über Enums <b>und</b> über Skalare.
    ///
    /// <para><b>Kein eigener Opcode.</b> Ein <c>match</c> verzweigt über eine Folge von Tests wie
    /// jede andere Fallunterscheidung — bei einem Enum über sein Tag, sonst über den Wert selbst.
    /// Eine Sprungtabelle wäre eine Optimierung, keine Semantik.</para>
    ///
    /// <para><b>Der letzte Arm wird nur dann ungeprüft übernommen, wenn sein Muster
    /// unwiderlegbar ist</b> (<c>_</c> oder eine reine Bindung) und er keinen Guard hat. Die Sema
    /// hat Exhaustivität bewiesen — aber ein Guard kann trotzdem fehlschlagen, und ein
    /// Literal-Arm am Ende ist nur deshalb erschöpfend, weil ein anderer Arm die Lücke deckt.
    /// Der Fehlpfad wird dann <c>unreachable</c>: erreichbar im CFG, unmöglich zur Laufzeit.</para>
    /// </summary>
    private TempId? LowerMatch(Expr scrutinee, MatchArm[] arms, IrType? resultType, Span span)
    {
        var scrutineeType = TypeOfExpr(scrutinee);
        var value = LowerExpr(scrutinee);
        var slot = resultType is null ? (LocalId?)null : _slots.DeclareSynthetic("match", resultType);

        // Bei einem Enum wird über das Tag verglichen, nicht über den Wert — welche Variante
        // vorliegt, steht in Slot 0 und sonst nirgends.
        TypeSymbol? enumSymbol = null;
        TempId subject = value;
        IrType subjectType = scrutineeType;

        if (scrutineeType is IrEnumType)
        {
            enumSymbol = RequireEnum(scrutinee).Symbol;
            var tag = _slots.NewTemp(new IrScalarType(IrScalar.I64));
            _b.Emit(new EnumTag(tag, value, span));
            subject = tag;
            subjectType = new IrScalarType(IrScalar.I64);
        }

        var merge = _b.NewBlock();
        var reachesMerge = false;

        for (var i = 0; i < arms.Length; i++)
        {
            var arm = arms[i];
            var last = i == arms.Length - 1;

            // Der letzte Arm wird nicht geprueft: die Sema hat Exhaustivitaet bewiesen
            // (LYR-SEM0050), also passt er, wenn keiner davor gepasst hat. Der Test waere immer
            // wahr — und bei einem Enum ein zusaetzlicher Vergleich pro match.
            //
            // Mit Guard gilt das nicht: ein Guard kann fehlschlagen, und dann braucht es einen
            // Fehlpfad. Der wird 'unreachable' — im CFG erreichbar, zur Laufzeit unmoeglich.
            var unconditional = last && arm.Guard is null;

            var body = _b.NewBlock();
            var next = unconditional ? (BlockId?)null : _b.NewBlock();

            if (unconditional) _b.Seal(new Branch(body, arm.Span));
            else EmitPatternBranch(arm.Pattern, subject, subjectType, enumSymbol,
                body, next!.Value, arm.Span);

            _b.SwitchTo(body);

            // Bindungen stehen vor dem Guard: 'n if n > 0' braucht 'n'.
            BindPattern(arm.Pattern, value, enumSymbol);

            if (arm.Guard is { } guard)
            {
                var guarded = _b.NewBlock();
                _b.Seal(new CondBranch(LowerExpr(guard), guarded, next!.Value, guard.Span));
                _b.SwitchTo(guarded);
            }

            if (LowerArm(arm, value, slot, resultType))
            {
                _b.Seal(new Branch(merge, arm.Span));
                reachesMerge = true;
            }

            if (next is { } fallthrough)
            {
                _b.SwitchTo(fallthrough);
                // Nach dem letzten Arm ist der Fehlpfad zur Laufzeit unmöglich — die Sema hat
                // Exhaustivität bewiesen. Im CFG ist er erreichbar, also braucht er einen
                // Terminator, und 'unreachable' ist genau die Aussage.
                if (last) _b.Seal(new Unreachable(span));
            }
        }

        if (!reachesMerge) throw NotSupported("a match where no arm falls through", span);

        _b.SwitchTo(merge);
        if (slot is not { } result || resultType is null) return null;

        var dest = _slots.NewTemp(resultType);
        _b.Emit(new LoadLocal(dest, result, resultType, span));
        return dest;
    }

    /// <summary>
    /// Verzweigt nach <paramref name="onMatch"/> oder <paramref name="onFail"/>, je nachdem ob das
    /// Muster passt. Versiegelt dabei den aktuellen Block.
    ///
    /// <para><b>Verzweigung statt eines bool-Temps</b>, und das ist keine Stilfrage: ein Range
    /// braucht zwei Vergleiche, ein Or-Pattern beliebig viele, und die zu einem Wert zu
    /// verknuepfen hiesse <c>and</c>/<c>or</c> auf <c>bool</c> — beide sind in dieser IR
    /// ganzzahlig, und der Verifier sagt das auch. Dieselbe Loesung wie bei <c>&amp;&amp;</c> und
    /// <c>||</c>, die aus demselben Grund Kontrollfluss sind und keine Opcodes.</para>
    /// </summary>
    private void EmitPatternBranch(Pattern pattern, TempId subject, IrType subjectType,
        TypeSymbol? enumSymbol, BlockId onMatch, BlockId onFail, Span span)
    {
        switch (pattern)
        {
            // Faengt alles — kein Test noetig.
            case WildcardPattern:
                _b.Seal(new Branch(onMatch, span));
                return;

            case BindingPattern binding when _types.RefOf(pattern) is EnumVariantSymbol:
                _b.Seal(new CondBranch(EmitTagTest(enumSymbol, binding.Name, subject, binding.Span),
                    onMatch, onFail, binding.Span));
                return;

            case BindingPattern:
                _b.Seal(new Branch(onMatch, span));
                return;

            case VariantPattern variant:
                _b.Seal(new CondBranch(EmitTagTest(enumSymbol, variant.Path[^1], subject, variant.Span),
                    onMatch, onFail, variant.Span));
                return;

            case LiteralPattern literal:
            {
                var expected = LowerExprAs(literal.Literal, subjectType);
                var matches = _slots.NewTemp(BoolType);
                _b.Emit(new BinOp(matches, IrBinKind.Eq, BoolType, subject, expected, literal.Span));
                _b.Seal(new CondBranch(matches, onMatch, onFail, literal.Span));
                return;
            }

            // 'lo <= v' und dann 'v <= hi' — zwei Blocke statt einer Verknuepfung.
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

            // Jede Alternative bekommt ihren eigenen Versuch; die erste, die passt, gewinnt.
            case OrPattern or:
            {
                for (var i = 0; i < or.Alternatives.Length; i++)
                {
                    var lastAlternative = i == or.Alternatives.Length - 1;
                    var nextAlternative = lastAlternative ? onFail : _b.NewBlock();

                    EmitPatternBranch(or.Alternatives[i], subject, subjectType, enumSymbol,
                        onMatch, nextAlternative, or.Span);

                    if (!lastAlternative) _b.SwitchTo(nextAlternative);
                }

                return;
            }

            default:
                throw NotSupported($"a {pattern.GetType().Name} in a match", pattern.Span);
        }
    }

    private TempId EmitTagTest(TypeSymbol? enumSymbol, string variant, TempId tag, Span span)
    {
        if (enumSymbol is null)
            throw NotSupported("a variant pattern in a match over a non-enum", span);

        var expected = EmitConst(new IntConst((ulong)_typeTable.TagOf(enumSymbol, variant, span)),
            new IrScalarType(IrScalar.I64), span);

        // Das Type-Feld eines Vergleichs ist sein ERGEBNIS-Typ (bool); den Operandentyp schlaegt
        // der Emitter in der Temp-Tabelle nach, weil signed/unsigned verschiedene Opcodes sind.
        var matches = _slots.NewTemp(BoolType);
        _b.Emit(new BinOp(matches, IrBinKind.Eq, BoolType, tag, expected, span));
        return matches;
    }

    /// <summary>Bindet, was das Muster bindet: eine reine Bindung den Wert selbst, ein
    /// Varianten-Muster seine Felder.</summary>
    private void BindPattern(Pattern pattern, TempId value, TypeSymbol? enumSymbol)
    {
        if (pattern is BindingPattern binding && _types.RefOf(pattern) is LocalSymbol local)
        {
            var slot = _slots.DeclareFor(local, LowerType(local.Type, binding.Span));
            _b.Emit(new StoreLocal(slot, value, binding.Span));
            return;
        }

        if (enumSymbol is not null) BindPatternFields(pattern, enumSymbol, value);
    }

    /// <summary>Lowert den Rumpf eines Arms. Liefert „faellt durch".</summary>
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

    /// <summary>Der Tag, auf den ein Muster passt. Eine Unit-Variante parst als
    /// <see cref="BindingPattern"/> — ob sie eine Bindung oder eine Variante ist, weiß erst die
    /// Sema, und die hat es entschieden.</summary>
    private int TagOfPattern(TypeSymbol symbol, Pattern pattern) => pattern switch
    {
        VariantPattern v => _typeTable.TagOf(symbol, v.Path[^1], v.Span),
        BindingPattern b when _types.RefOf(pattern) is EnumVariantSymbol
            => _typeTable.TagOf(symbol, b.Name, b.Span),
        WildcardPattern => throw NotSupported("'_' anywhere but in the last arm", pattern.Span),
        _ => throw NotSupported($"a {pattern.GetType().Name} in a match over an enum", pattern.Span),
    };

    /// <summary>Zerlegt eine Variante: <c>enumas</c> engt ein, danach ist jedes Feld ein
    /// gewöhnliches <c>ldfld</c> mit dem Layout der Variante.</summary>
    private void BindPatternFields(Pattern pattern, TypeSymbol symbol, TempId value)
    {
        if (pattern is not VariantPattern variant) return;
        if (variant.TupleElements is null && variant.StructFields is null) return;

        var variantType = _typeTable.VariantOf(symbol, variant.Path[^1], variant.Span);
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

                // Kurzform `{ a, b }`: kein Untermuster, der Feldname IST die Bindung. Die Sema
                // hat sie an ein LocalSymbol gebunden — an den FieldPattern-Knoten selbst.
                BindOne(field.Pattern ?? (Node)field, narrowed, variantType,
                    new FieldId(index), layout.FieldTypes[index], field.Name, field.Span);
            }
    }

    private void BindOne(Node? sub, TempId obj, TypeId variantType, FieldId field, IrType type,
        string? shorthandName = null, Span shorthandSpan = default)
    {
        // Nur Bindungen und '_' — verschachtelte Muster brauchen rekursive Dekomposition und sind
        // eine eigene Ausbaustufe.
        if (sub is null or WildcardPattern) return;

        var name = sub is BindingPattern binding ? binding.Name : shorthandName;
        if (name is null)
            throw NotSupported($"a nested {sub.GetType().Name} in a pattern", sub.Span);

        // Die Sema hat der Muster-Bindung schon ein LocalSymbol gegeben — über dasselbe Symbol
        // findet LowerIdentifier den Slot später wieder. Ein eigener Namensraum hier wäre eine
        // zweite Wahrheit über Scoping.
        if (_types.RefOf(sub) is not LocalSymbol local)
            throw Bug($"pattern binding '{name}' was not bound by the type checker");

        var span = sub.Span == default ? shorthandSpan : sub.Span;
        var slot = _slots.DeclareFor(local, type);
        var loaded = _slots.NewTemp(type);
        _b.Emit(new LoadField(loaded, obj, variantType, field, type, span));
        _b.Emit(new StoreLocal(slot, loaded, span));
    }

    // ------------------------------------------------------------------ Optionals (§7)

    /// <summary>
    /// Ein Ausdruck an einer Position mit <b>erwartetem Typ</b>. Zwei Dinge passieren nur hier:
    /// <c>null</c> bekommt seinen Typ (es hat keinen eigenen — die Sema gibt ihm <c>NullType</c>),
    /// und ein <c>T</c> wird zu <c>?T</c> verpackt, weil §6.5 die Richtung implizit erlaubt.
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

    /// <summary>Ein <c>null</c> ohne erwarteten Typ. Sollte nie vorkommen — jede Position, an der
    /// <c>null</c> gültig ist, kennt ihren Zieltyp und geht über <see cref="LowerExprAs"/>.</summary>
    private TempId LowerNull(NullLiteralExpr expr) =>
        throw NotSupported("'null' in a position without an expected type", expr.Span);

    /// <summary>
    /// <c>x != null</c> und <c>x == null</c> sind <b>keine</b> Vergleiche, sondern die Frage nach
    /// der Anwesenheit eines Wertes — genau das, was <c>optissome</c> beantwortet. Ein echter
    /// Vergleich würde einen <c>null</c>-Wert auf den Stack verlangen, und den gibt es nicht:
    /// „kein Wert" ist eine leere Referenz, kein Operand.
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

        // '== null' ist die Verneinung. 'not' ist der einzige Opcode ohne Typ-Tag — nur bool.
        var isNone = _slots.NewTemp(BoolType);
        _b.Emit(new UnOp(isNone, IrUnKind.Not, BoolType, isSome, expr.Span));
        return isNone;
    }

    /// <summary>
    /// Passt einen Wert an den Typ an, den seine Position erwartet. Zwei implizite Uebergaenge
    /// kennt die Sprache, und beide werden hier materialisiert:
    ///
    /// <para><c>T</c> → <c>?T</c> ist implizit (Sprache.md §6.5) und wird zu <c>optsome</c>.</para>
    ///
    /// <para>Ein Klassen- oder Enum-Wert → sein Interface wird zu <c>mkiface</c>: der Interface-Wert
    /// ist ein Fat Pointer, der den konkreten Typ mitfuehrt, und der steht genau hier zur
    /// Compile-Zeit fest. Spaeter — am <c>callvirt</c> — weiss niemand mehr, welche Klasse es war,
    /// weil ein Objekt kein Typ-Tag traegt (M6/P1).</para>
    ///
    /// <para>Die Reihenfolge ist nicht beliebig: bei <c>?SomeInterface</c> muss erst das Interface
    /// entstehen und dann das Optional darum, sonst verpackte man eine Klassenreferenz und
    /// <c>optget</c> lieferte etwas, worauf kein <c>callvirt</c> laufen kann.</para>
    /// </summary>
    private TempId Coerce(TempId value, IrType from, IrType to, Span span)
    {
        var target = to is IrOptionalType outer ? outer.Inner : to;
        var source = from is IrOptionalType inner ? inner.Inner : from;

        // Wert-Semantik (Sprache.md §3.2). Der Bindepunkt ist die Stelle, an der ein struct-Wert
        // eine neue Heimat bekommt — genau dort wird kopiert, und nur dort. Ein frisch gebauter
        // Wert braucht es nicht: er hat noch keinen anderen Besitzer, von dem er sich loesen
        // muesste.
        if (target is IrStructType value_ && from is not IrOptionalType && !_fresh.Contains(value))
            value = CopyStructValue(value, value_, span);

        if (target is IrInterfaceType iface && source is not IrInterfaceType
            && from is not IrOptionalType)
        {
            // Auch der Weg hinter ein Interface ist ein Bindepunkt: ein struct wird dabei
            // kopiert, sonst teilte der Interface-Wert das Slot-Array mit seiner Quelle und eine
            // Mutation ueber das Interface schluege auf das Original durch.
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
    /// Legt eine unabhaengige Kopie eines struct-Wertes an.
    ///
    /// <para>Die Kopie ist zur Laufzeit rekursiv ueber verschachtelte Structs und flach ueber
    /// alles andere: ein Feld vom Typ <c>class</c> oder <c>T[]</c> traegt eine Referenz, und die
    /// wird geteilt. Kopiert wird der Wert, nicht die Welt dahinter.</para>
    /// </summary>
    private TempId CopyStructValue(TempId value, IrStructType type, Span span)
    {
        var dest = _slots.NewTemp(type);
        _b.Emit(new StructCopy(dest, value, type.Type, span));
        _fresh.Add(dest);
        return dest;
    }

    /// <summary>Hebt eine Objektreferenz auf ihren Interface-Typ.</summary>
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
    /// <c>a ?? b</c> — die rechte Seite wird <b>nur</b> ausgewertet, wenn links kein Wert steht.
    /// Deshalb Verzweigung statt Instruktion, genau wie bei <c>&amp;&amp;</c> und <c>||</c>: eine
    /// Stack-Maschine kann keinen unausgewerteten Ausdruck transportieren.
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
    /// <c>x ??= v</c> — weist nur zu, wenn <c>x</c> keinen Wert hat.
    ///
    /// <para>Wie <c>??</c> eine Verzweigung und kein Opcode: die rechte Seite wird <b>nur dann</b>
    /// ausgewertet, und ein unausgewerteter Ausdruck laesst sich auf einer Stack-Maschine nicht
    /// transportieren. Der Unterschied zu <c>??</c> ist allein, dass das Ergebnis in den
    /// vorhandenen Slot zurueckgeht statt in einen neuen.</para>
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
        // Hat es schon einen Wert, bleibt der Slot unberuehrt — auch die rechte Seite laeuft dann
        // nicht.
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
    /// <c>a?.b</c> — Feldzugriff, der bei „kein Wert" keinen macht.
    ///
    /// <para>Ergebnis ist immer ein Optional (Sprache.md §7): hat <c>a</c> keinen Wert, ist es
    /// <c>optnone</c>, sonst das ausgepackte Feld in <c>optsome</c>. Auch das ist eine
    /// Verzweigung — der Feldzugriff darf bei einer leeren Referenz <b>nicht</b> laufen, und ein
    /// Opcode koennte das nicht ausdruecken, ohne selbst zu verzweigen.</para>
    /// </summary>
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

        // Das 'optget' kann nicht panicken: der Zweig steht hinter dem 'optissome', der Beweis
        // ist also gefuehrt. Dieselbe Arbeitsteilung wie beim Flow-Narrowing in P2b.
        var (type, field, fieldType) = ResolveFieldOn(target.Inner, expr);
        var value = _slots.NewTemp(fieldType);
        _b.Emit(new LoadField(value, unwrapped, type, field, fieldType, expr.Span));

        var wrapped = _slots.NewTemp(resultType);
        _b.Emit(new OptSome(wrapped, value, result.Inner, expr.Span));
        _b.Emit(new StoreLocal(slot, wrapped, expr.Span));
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

    /// <summary>Feldindex und -typ am ausgepackten Traeger. Getrennt von
    /// <c>ResolveFieldAccess</c>, weil dort der Traeger-Ausdruck selbst gelowert wird — hier liegt
    /// er schon ausgepackt vor.</summary>
    private (TypeId Type, FieldId Field, IrType FieldType) ResolveFieldOn(IrType carrier,
        MemberExpr expr)
    {
        if (_types.TypeOf(expr.Target) is not Optional { Inner: NamedRef named })
            throw NotSupported($"'?.{expr.Member}' on " +
                               $"'{TypeFacts.Display(_types.TypeOf(expr.Target))}'", expr.Span);

        var type = _typeTable.Intern(named.Symbol);
        var field = _typeTable.FieldOf(named.Symbol, expr.Member, expr.Span);
        return (type, field, _typeTable.Defs[type.Value].FieldTypes[field.Value]);
    }

    // ------------------------------------------------------------------ Arrays (ADR-016)

    /// <summary><c>[a, b, c]</c> — eine Instruktion, nicht drei Stores. Die Werte liegen beim
    /// <c>newarr</c> in Quelltext-Reihenfolge auf dem Stack.</summary>
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
        var (array, index, element) = ResolveIndexAccess(expr);
        var dest = _slots.NewTemp(element);
        _b.Emit(new LoadElem(dest, array, index, element, expr.Span));
        return dest;
    }

    /// <summary>Gemeinsamer Teil von Lesen und Schreiben. <c>[i]</c> ist heute nur auf <c>T[]</c>
    /// gültig; für alles andere sieht ADR-016 das <c>Indexable&lt;T&gt;</c>-Interface vor, das
    /// Interfaces (P3) und Generics (P8) voraussetzt.</summary>
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

    // ------------------------------------------------------------------ Objekte (Sprache.md §3.3)

    /// <summary>
    /// <c>Account { owner = a, balance = b }</c> → ein <c>newobj</c> und ein <c>storefield</c> je
    /// Feld.
    ///
    /// <para><b>Geschrieben wird in Deklarations-, nicht in Schreibreihenfolge.</b> Die
    /// Initialisierer dürfen im Quelltext beliebig stehen; das Layout ist aber die Deklaration, und
    /// nur eine feste Ordnung macht den Bytecode deterministisch (ADR-013). Die Werte werden
    /// trotzdem in <b>Quelltext</b>-Reihenfolge ausgewertet — bei Seiteneffekten ist das die
    /// Reihenfolge, die der Leser erwartet.</para>
    /// </summary>
    private TempId LowerObjectInit(StructInitExpr expr)
    {
        // 'Shape.Tri { a = 3, b = 4 }' — eine Struct-Variante. Sieht aus wie ein Objekt-Literal,
        // ist aber eine Varianten-Konstruktion und geht deshalb über newvariant.
        if (_types.TypeOf(expr) is NamedRef { Symbol.Kind: TypeSymbolKind.Enum } owner)
            return LowerStructVariant(expr, owner.Symbol);

        if (_types.TypeOf(expr) is not NamedRef
            { Symbol.Kind: TypeSymbolKind.Class or TypeSymbolKind.Struct } named)
            throw NotSupported($"initializer for '{TypeFacts.Display(_types.TypeOf(expr))}' " +
                               "(only classes and structs are lowered)", expr.Span);

        var type = _typeTable.Intern(named.Symbol);
        var layout = _typeTable.Defs[type.Value];

        // Erst alle Werte auswerten (Quelltext-Reihenfolge), dann in Layout-Reihenfolge schreiben.
        var values = new Dictionary<string, TempId>(StringComparer.Ordinal);
        foreach (var field in expr.Fields)
        {
            if (values.ContainsKey(field.Name))
                throw Bug($"duplicate initializer for '{field.Name}' reached lowering");
            // An den deklarierten Feldtyp anpassen: ein Feld vom Typ eines Interfaces nimmt eine
            // Klasse nur als Fat Pointer auf.
            var fieldIndex = Array.IndexOf(layout.FieldNames, field.Name);
            values[field.Name] = fieldIndex >= 0
                ? LowerExprAs(field.Value, layout.FieldTypes[fieldIndex])
                : LowerExpr(field.Value);
        }

        // Ein weggelassenes Feld bekommt seinen Default — ausgewertet HIER, an der
        // Konstruktionsstelle, nicht einmal beim Typ. Ein Default ist ein Ausdruck; ihn im Layout
        // abzulegen hiesse, einen Ausdruck in eine Typtabelle zu schreiben.
        //
        // Ohne Default bleibt es beim Fehler: ein stillschweigender Nullwert waere geraten, und
        // die Sema kennt keine Regel, die das erlaubt.
        var declaredFields = named.Symbol.Declaration switch
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
                throw Bug($"field '{name}' of '{named.Symbol.Name}' has neither a value nor a default");

        // Ein struct-Wert ist zur Laufzeit dasselbe Slot-Array wie ein Klassenobjekt — 'newobj'
        // taugt fuer beides. Der Unterschied steckt allein in den Bindepunkten.
        IrType result = _typeTable.IsStruct(type)
            ? new IrStructType(type)
            : new IrRefType(type);
        var dest = _slots.NewTemp(result);
        _b.Emit(new NewObject(dest, type, result, expr.Span));

        for (var i = 0; i < layout.FieldNames.Length; i++)
            _b.Emit(new StoreField(dest, type, new FieldId(i), values[layout.FieldNames[i]], expr.Span));

        // Frisch gebaut: dieser Wert gehoert noch niemandem, eine Kopie beim Binden waere Ballast.
        _fresh.Add(dest);
        return dest;
    }

    /// <summary>Fuellt einen globalen Slot. Kommt ausschliesslich im synthetischen Initialisierer
    /// vor — im Nutzer-Quelltext gibt es keine Zuweisung an ein Global, weil sie alle <c>let</c>
    /// sind (§2.3).</summary>
    private void LowerGlobalInit(GlobalInitStmt stmt)
    {
        var (id, type) = _globals.Resolve(stmt.Symbol, stmt.Span);
        var value = LowerExprAs(stmt.Binding.Initializer!, type);
        _b.Emit(new StoreGlobal(id, value, stmt.Span));
    }

    /// <summary>Ein globaler Slot ueber einen blossen Namen — ein Modul-<c>let</c> im eigenen
    /// oder importierten Modul.</summary>
    private TempId? TryLowerGlobalIdentifier(IdentifierExpr expr)
    {
        var symbol = _types.RefOf(expr);
        if (symbol is ImportBindingSymbol import) symbol = import.Target;
        return symbol is GlobalSymbol global ? LowerGlobalRead(global, expr.Span) : null;
    }

    /// <summary>Ein globaler Slot — Modul-<c>let</c> oder <c>static let</c>. Beide sind
    /// dasselbe im Bytecode; der Unterschied ist nur, wo der Name sichtbar ist.</summary>
    private TempId LowerGlobalRead(GlobalSymbol symbol, Span span)
    {
        var (id, type) = _globals.Resolve(symbol, span);
        var dest = _slots.NewTemp(type);
        _b.Emit(new LoadGlobal(dest, id, type, span));
        return dest;
    }

    private TempId LowerFieldRead(MemberExpr expr)
    {
        // 'P.ZERO' ist keine Feld-, sondern eine Konstanten-Lesung: ein 'static let' ist ein
        // globaler Slot, kein Objekt-Slot.
        if (_types.RefOf(expr) is GlobalSymbol constant) return LowerGlobalRead(constant, expr.Span);

        // 'Shape.Empty' — eine Unit-Variante. Sie sieht aus wie ein Member-Zugriff, ist aber eine
        // Konstruktion ohne Argumente.
        if (_types.RefOf(expr) is EnumVariantSymbol)
            return LowerVariantCall(expr, [], expr.Span);

        // '.length' auf einem Array ist eingebaut (ADR-016), kein Feld und keine Methode.
        if (expr.Member == "length" && TypeOfExpr(expr.Target) is IrArrayType)
            return LowerArrayLength(expr);

        // 'a?.b' greift nur zu, wenn 'a' einen Wert hat (Sprache.md §7).
        if (expr.IsOptional) return LowerOptionalMember(expr);

        var (obj, type, field, fieldType) = ResolveFieldAccess(expr);
        var dest = _slots.NewTemp(fieldType);
        _b.Emit(new LoadField(dest, obj, type, field, fieldType, expr.Span));
        return dest;
    }

    /// <summary>Gemeinsamer Teil von Lesen und Schreiben: das Objekt auswerten und Typ, Feldindex
    /// und Feldtyp bestimmen.</summary>
    private (TempId Object, TypeId Type, FieldId Field, IrType FieldType) ResolveFieldAccess(MemberExpr expr)
    {
        if (_types.TypeOf(expr.Target) is not NamedRef
            { Symbol.Kind: TypeSymbolKind.Class or TypeSymbolKind.Struct } named)
            throw NotSupported($"member access '.{expr.Member}' on " +
                               $"'{TypeFacts.Display(_types.TypeOf(expr.Target))}'", expr.Span);

        var obj = LowerExpr(expr.Target);
        var type = _typeTable.Intern(named.Symbol);
        var field = _typeTable.FieldOf(named.Symbol, expr.Member, expr.Span);
        return (obj, type, field, _typeTable.Defs[type.Value].FieldTypes[field.Value]);
    }

    private TempId LowerCast(CastExpr expr)
    {
        var from = TypeOfExpr(expr.Operand);
        var to = TypeOfExpr(expr);
        var operand = LowerExpr(expr.Operand);

        // 'x as int' bei x: int ist legales Lyric, ergibt aber keinen sinnvollen Opcode. Das
        // Lowering elidiert die Identität — der Verifier lehnt sie ab.
        if (IrType.Equal(from, to)) return operand;

        var dest = _slots.NewTemp(to);
        _b.Emit(new Lyric.Ir.Convert(dest, from, to, operand, expr.Span));
        return dest;
    }

    /// <summary>
    /// Ein Aufruf ueber einen <b>Funktionswert</b>: <c>f(1)</c>, wo <c>f</c> eine Closure ist.
    ///
    /// <para>Kein Default-Argument und kein <c>params</c>: beide sind Aufrufstellen-Transformationen
    /// (P5b-5), die die DEKLARATION des Aufgerufenen brauchen — ein Funktionswert hat keine. Der
    /// Typ <c>fn(int) -&gt; int</c> sagt „ein Argument", und mehr gibt es nicht zu wissen. Dieselbe
    /// Grenze zieht C# bei Delegates, aus demselben Grund.</para>
    /// </summary>
    private TempId? LowerIndirectCall(CallExpr expr)
    {
        if (LowerType(_types.TypeOf(expr.Callee), expr.Callee.Span) is not IrFunctionType signature)
            throw Bug("indirect call on a non-function value");

        var callee = LowerExpr(expr.Callee);

        var args = new TempId[expr.Arguments.Length];
        for (var i = 0; i < args.Length; i++)
            args[i] = i < signature.Parameters.Length
                ? LowerExprAs(expr.Arguments[i], signature.Parameters[i])
                : LowerExpr(expr.Arguments[i]); // Aritaetsfehler hat die Sema schon gemeldet

        if (IsVoid(signature.Return))
        {
            _b.Emit(new CallIndirect(null, callee, args, signature.Return, expr.Span));
            return null;
        }

        var dest = _slots.NewTemp(signature.Return);
        _b.Emit(new CallIndirect(dest, callee, args, signature.Return, expr.Span));

        // Das Ergebnis gehoert noch niemandem — bei einem struct spart das den structcopy beim
        // Binden, genau wie bei einem gewoehnlichen Call.
        _fresh.Add(dest);
        return dest;
    }

    private TempId? LowerCall(CallExpr expr)
    {
        // Der Empfänger ist Parameter 0 (ADR-014). Bei `p.get()` wird 'p' also zum ersten Argument;
        // bei `P.new(…)` gibt es keinen, und der Aufruf ist ein gewöhnlicher Call. Beide Formen
        // laufen danach durch denselben Pfad — der Unterschied steckt allein in der Argumentliste.
        TempId? receiver = null;
        string calleeName;
        Symbol? bound;

        // Der Aufgerufene ist ein WERT und keine Deklaration: eine Closure, ein Parameter vom Typ
        // 'fn(…) -> …', ein Feld, das Ergebnis eines anderen Aufrufs.
        //
        // Der Typ allein entscheidet das NICHT: eine deklarierte Funktion hat ebenfalls einen
        // Funktionstyp, und eine Enum-Variante mit Payload ('Shape.Line(1.0)') auch — sie ist ein
        // Konstruktor, kein Wert. Was den indirekten Aufruf ausmacht, ist die BINDUNG: er zeigt
        // auf etwas, das einen Funktionswert HAELT, oder auf gar nichts wie bei 'mk()()'.
        //
        // Positiv aufgezaehlt statt negativ: eine Liste von Verboten haette bei jeder neuen
        // Symbolart stillschweigend die falsche Antwort gegeben, und zwar in die gefaehrliche
        // Richtung — 'Shape.Line(1.0)' wurde so zu einem callind auf einen Enum-Wert.
        if (_types.TypeOf(expr.Callee) is FnType
            && _types.RefOf(expr.Callee) is null or LocalSymbol or ParameterSymbol
               or FieldSymbol or GlobalSymbol)
            return LowerIndirectCall(expr);

        switch (expr.Callee)
        {
            // Empfaenger ist ein Interface-Wert: welche Implementierung laeuft, steht erst zur
            // Laufzeit fest. Das ist der einzige dynamische Dispatch der Sprache.
            case MemberExpr member
                when _types.TypeOf(member.Target) is NamedRef
                     { Symbol.Kind: TypeSymbolKind.Interface } iface:
                return LowerVirtualCall(member, iface.Symbol, expr);

            case MemberExpr member
                when _types.TypeOf(member.Target) is NamedRef
                     { Symbol.Kind: TypeSymbolKind.Class or TypeSymbolKind.Struct
                         or TypeSymbolKind.Enum }:
                calleeName = member.Member;
                bound = _types.RefOf(member);
                receiver = LowerExpr(member.Target);
                break;

            // Shape.Circle(2.0) — eine Tuple-Variante. Kein Call, sondern eine Konstruktion.
            case MemberExpr member when _types.RefOf(member) is EnumVariantSymbol:
                return LowerVariantCall(member, expr.Arguments, expr.Span);

            case MemberExpr member: // Typ- oder Modul-Ziel: P.new(…), console.println(…)
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

        // Ein selektiver Import bindet über ein ImportBindingSymbol; das eigentliche Ziel liegt
        // darunter. Ohne das Auspacken sieht `import std.io.console { println };` anders aus als
        // ein Aufruf im selben Modul, obwohl es dieselbe Funktion ist.
        if (bound is ImportBindingSymbol binding) bound = binding.Target;

        if (bound is not FunctionSymbol symbol)
            throw NotSupported($"call to '{calleeName}' (not a function or method)", expr.Span);

        // Nativ hinterlegt (Stdlib): eigener Instruktionstyp, eigener Indexraum.
        if (_imports.IsNative(symbol))
            return LowerImportCall(_imports.Intern(symbol), expr.Arguments, expr.Span);

        // 'panic' ist ein Sprach-Built-in (§9) und hat deshalb kein Modul, in dem es deklariert
        // waere — der Resolver legt es in den Wurzel-Scope. Gebunden wird es wie jeder andere
        // Native, ueber seinen symbolischen Namen.
        if (symbol.Name == "panic" && !_functions.ContainsKey(symbol))
        {
            var message = expr.Arguments.Length == 1
                ? LowerExprAs(expr.Arguments[0], new IrScalarType(IrScalar.String))
                : throw NotSupported("panic with other than one argument", expr.Span);

            CallHelper("std.core.panic", expr.Span, message);

            // panic kehrt nie zurueck (Rueckgabetyp 'never'). Der Block endet hier — alles
            // dahinter waere toter Code, und der Verifier lehnt unerreichbare Bloecke ab.
            _b.Seal(new Unreachable(expr.Span));
            return null;
        }

        if (!_functions.TryGetValue(symbol, out var target))
            throw NotSupported($"call to '{calleeName}' (external, generic or bodiless)", expr.Span);

        if (symbol.Declaration is not FunctionDecl declaration)
            throw NotSupported($"call to '{calleeName}' (no declaration to read parameters from)",
                expr.Span);

        var supplied = MaterializeArguments(declaration, expr.Arguments, calleeName, expr.Span);

        // Der Empfänger steht vorn — die Reihenfolge ist die Parameter-Konvention der IR und
        // muss zu der passen, in der FunctionLowerer die Slots angelegt hat.
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
    /// Die Argumentliste, wie der Callee sie erwartet: genau ein Wert je deklariertem Parameter.
    ///
    /// <para>Hier entstehen die beiden Formen, die an der Quelle anders aussehen als in der
    /// Signatur — <b>Default-Werte</b> für weggelassene Trailing-Parameter und das
    /// <b>params</b>-Array für den Rest. Beides ist eine Aufrufstellen-Transformation: die IR
    /// kennt keine variadischen Signaturen und keine optionalen Parameter, und sie soll auch
    /// keine kennen. Nach dieser Methode ist ein Aufruf ein Aufruf.</para>
    ///
    /// <para>Der Default-Ausdruck wird <b>an der Aufrufstelle</b> ausgewertet, nicht einmal beim
    /// Callee — dieselbe Wahl wie in C#. Sonst müsste er in einem Kontext gelowert werden, in dem
    /// die Argumente des Aufrufers nicht sichtbar sind.</para>
    /// </summary>
    private TempId[] MaterializeArguments(FunctionDecl callee, Expr[] provided, string name,
        Span span)
    {
        var parameters = callee.Parameters;
        var args = new TempId[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];

            // 'params xs: T[]' sammelt alles ab hier. Die Sema erlaubt es nur am letzten
            // Parameter (§3.1), also ist der Rest wirklich der Rest.
            if (parameter.IsParams)
            {
                args[i] = CollectVariadic(parameter, provided, i, span);
                return args;
            }

            if (i < provided.Length)
            {
                args[i] = LowerArgument(provided[i], parameter);
                continue;
            }

            if (parameter.Default is { } fallback)
            {
                args[i] = LowerArgument(fallback, parameter);
                continue;
            }

            // Die Sema hat die Arity geprueft; hier zu landen hiesse, sie waere durchgerutscht.
            throw Bug($"call to '{name}' at {span} passes {provided.Length} argument(s) but " +
                      $"parameter '{parameter.Name}' has no default");
        }

        if (provided.Length > parameters.Length)
            throw Bug($"call to '{name}' at {span} passes {provided.Length} argument(s) for " +
                      $"{parameters.Length} parameter(s)");

        return args;
    }

    /// <summary>
    /// Die restlichen Argumente als Array — <c>sum(1, 2, 3)</c> wird zu <c>sum([1, 2, 3])</c>.
    ///
    /// <para><b>Ein fertiges Array darf als Ganzes durch</b>: <c>sum(xs)</c> mit <c>xs: int[]</c>
    /// ist das Array selbst, kein Array mit einem Array darin. Ohne diesen Weg koennte eine
    /// variadische Funktion an keine andere delegieren. Erkennbar am Typ des einzigen
    /// verbleibenden Arguments — mehr braucht es nicht, weil ein Element nie denselben Typ hat wie
    /// das Array, das es aufnimmt (§3.1).</para>
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
    /// Ein Argument, angepasst an den <b>deklarierten</b> Parametertyp.
    ///
    /// <para>Ohne diesen Schritt bliebe eine Klasse eine Klasse, auch wenn der Parameter ein
    /// Interface ist — und der Aufgerufene bekaeme eine nackte Referenz statt eines Fat Pointers.
    /// Der Verifier faengt das, aber als Typ-Mismatch tief im Callee statt als das, was es ist:
    /// eine fehlende Coercion am Aufrufort.</para>
    /// </summary>
    private TempId LowerArgument(Expr argument, Param parameter)
    {
        // Nur das Lowern des Parametertyps wird abgeschirmt — ein Typ, den dieser Compiler-Stand
        // nicht kennt, meldet ohnehin gleich die Funktion selbst, und hier doppelt zu klagen waere
        // Laerm. Die Coercion steht BEWUSST ausserhalb: als sie mit im try lag, verschluckte der
        // catch ein fehlendes 'mkiface' und machte aus einer Diagnose malformed IR — der Fehler
        // tauchte dann als Verifier-Befund tief im Aufrufer auf.
        IrType expected;
        try
        {
            expected = _typeTable.Lower(parameter.Type);
        }
        catch (UnsupportedConstructException)
        {
            return LowerExpr(argument);
        }

        return LowerExprAs(argument, expected);
    }

    /// <summary>
    /// Der dynamische Aufruf. Der Empfaenger ist ein Interface-Wert und traegt seinen konkreten
    /// Typ mit sich; die Runtime schlaegt damit in der vtable nach.
    ///
    /// <para>Der Slot statt des Namens ist dieselbe Entscheidung wie beim Feldindex (P1): Lyric ist
    /// statisch typisiert und kennt kein Monkey-Patching, also steht die Position zur Compile-Zeit
    /// fest. Ein Namens-Lookup mit Inline-Cache loeste ein Problem, das diese Sprache nicht hat.</para>
    /// </summary>
    private TempId? LowerVirtualCall(MemberExpr member, TypeSymbol iface, CallExpr expr)
    {
        var interfaceType = _typeTable.InterfaceOf(iface);
        var slot = _typeTable.SlotOf(iface, member.Member, member.Span);

        var args = new TempId[expr.Arguments.Length + 1];
        args[0] = LowerExpr(member.Target);

        // Die Signatur steht am Interface, nicht an einer Implementierung — sie ist der Vertrag.
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
            _b.Emit(new CallVirt(null, interfaceType.Type, slot, args, returnType, expr.Span));
            return null;
        }

        var dest = _slots.NewTemp(returnType);
        _b.Emit(new CallVirt(dest, interfaceType.Type, slot, args, returnType, expr.Span));
        return dest;
    }

    /// <summary>Aufruf einer nativ hinterlegten Funktion. Die Signatur kommt aus der Import-Tabelle,
    /// nicht aus einer Funktion — ein Import hat keinen Rumpf.</summary>
    private TempId? LowerImportCall(ImportId target, Expr[] arguments, Span span)
    {
        var import = _imports.Used[target.Value];
        if (arguments.Length != import.ParamTypes.Length)
            throw NotSupported($"call to '{import.Name}' with default or variadic arguments", span);

        var args = new TempId[arguments.Length];
        for (var i = 0; i < arguments.Length; i++) args[i] = LowerExpr(arguments[i]);

        if (IsVoid(import.ReturnType))
        {
            _b.Emit(new CallImport(null, target, args, span));
            return null;
        }

        var dest = _slots.NewTemp(import.ReturnType);
        _b.Emit(new CallImport(dest, target, args, span));
        return dest;
    }

    /// <summary>
    /// Direkter Aufruf eines Runtime-Helfers ueber seinen festen Namen.
    ///
    /// <para>Das Lowering referenziert <c>std.string.concat</c> und <c>std.core.panic</c>, ohne
    /// dass jemand die Module importiert haette — dasselbe Modell wie Roslyns Verweis auf
    /// <c>String.Concat</c>. Benutzt von f-Strings, von <c>string</c>-<c>+</c>/<c>*</c> (§6.5) und
    /// von <c>panic</c> (§9).</para>
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
    /// f-String → Kette aus <c>concat</c> und den <c>fromXxx</c>-Wandlern. Keine Arrays, keine
    /// Varargs — beides kann die IR nicht, und so braucht sie es auch nicht. Roslyn macht es für
    /// <c>$"…"</c> ohne Format-Spec genauso.
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
                    // Der Parser speichert die Textstücke roh (siehe InterpText) — hier werden die
                    // Escapes aufgelöst. Benachbarte Stücke sammeln sich zu einer Konstante.
                    pendingText.Append(Escapes.Resolve(text.Text));
                    break;

                case InterpHole hole:
                    if (hole.FormatSpec is not null)
                        throw NotSupported($"format spec '{hole.FormatSpec}' (std.fmt arrives with M8)",
                            hole.Span);

                    FlushText(hole.Span);
                    parts.Add(ToStringValue(hole.Expr));
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

    /// <summary>Ein Loch im f-String als string. Strings bleiben, wie sie sind; alles andere geht
    /// durch den passenden Wandler — die Namen unterscheiden nach Quelltyp, weil Lyric kein
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
            IrScalar.F32 or IrScalar.F64 => CallHelper("std.string.fromFloat", expr.Span, value),
            _ when IsIntegerScalar(scalar.Kind) => CallHelper("std.string.fromInt", expr.Span, value),
            _ => throw NotSupported($"interpolating a non-scalar value", expr.Span),
        };
    }

    // ------------------------------------------------------------------ Helfer

    private TempId EmitConst(IrConstValue value, IrType type, Span span)
    {
        var dest = _slots.NewTemp(type);
        _b.Emit(new Const(dest, type, value, span));
        return dest;
    }

    /// <summary>
    /// Zeigt dieses Zuweisungsziel auf eine <b>gefangene Zelle</b> (ADR-018)? Dann liegt es nicht
    /// in einem Slot dieser Funktion, sondern in einem Feld ihres Environments — und der Schreib-
    /// vorgang muss dorthin, sonst schriebe die Closure in eine Kopie und die Semantik waere
    /// still by-value.
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

    private static bool IsIntegerScalar(IrScalar kind) => kind is
        IrScalar.I8 or IrScalar.I16 or IrScalar.I32 or IrScalar.I64 or
        IrScalar.U8 or IrScalar.U16 or IrScalar.U32 or IrScalar.U64;

    private static bool IsVoid(IrType type) => type is IrScalarType { Kind: IrScalar.Void };

    private IrType TypeOfExpr(Expr expr) => LowerType(_types.TypeOf(expr), expr.Span);

    /// <summary>
    /// Sema-Typ → IR-Typ. Hier sitzt der Substitutions-Haken der Monomorphisierung: ein
    /// Typ-Parameter wird über die Instanz-Substitution aufgelöst. In P4 ist die Map immer leer,
    /// der Zweig also inaktiv — aber er sitzt an der einzigen Stelle, die ihn braucht, statt
    /// später über den gesamten Ausdrucks-Pfad nachgerüstet werden zu müssen.
    /// </summary>
    private IrType LowerType(LyrType type, Span span)
    {
        if (type is TypeParamType parameter)
        {
            if (!_substitution.TryGetValue(parameter.Param, out var bound))
                throw NotSupported($"type parameter '{parameter.Param.Name}'", span);
            type = bound;
        }

        // Eine Klasse wird zur Referenz auf ihren Tabellen-Eintrag; das Layout entsteht dabei
        // einmalig in der TypeTable. Alles andere (Struct, Enum, Array, Tupel, …) ist weiterhin
        // außerhalb dieses Compiler-Stands.
        if (type is NamedRef { Symbol.Kind: TypeSymbolKind.Class } named)
            return _typeTable.RefTo(named.Symbol);

        if (type is NamedRef { Symbol.Kind: TypeSymbolKind.Struct } valueType)
            return _typeTable.StructOf(valueType.Symbol);

        if (type is NamedRef { Symbol.Kind: TypeSymbolKind.Enum } enumType)
            return _typeTable.EnumOf(enumType.Symbol);

        // Ein Interface als Werttyp: Lyrics 'dyn'. Zur Laufzeit ein Fat Pointer aus Objekt und
        // konkretem Typindex — siehe IrInterfaceType.
        if (type is NamedRef { Symbol.Kind: TypeSymbolKind.Interface } interfaceType)
            return _typeTable.InterfaceOf(interfaceType.Symbol);

        // T[] mit fester Länge (ADR-016). Der Elementtyp steht inline; T[N] gibt es nicht mehr.
        if (type is ArrayOf array)
            return new IrArrayType(LowerType(array.Element, span));

        // ?T (Sprache.md §7). Nicht schachtelbar — die Sema kollabiert ??T bereits, hier ist es
        // trotzdem eine Grenze statt einer stillen Annahme.
        if (type is Optional optional)
        {
            var inner = LowerType(optional.Inner, span);
            if (inner is IrOptionalType)
                throw NotSupported("a nested optional '??T' (optionals do not nest)", span);
            return new IrOptionalType(inner);
        }

        // fn(A, B) -> R: ein Funktionswert (ADR-018). Traegt seine Signatur strukturell und
        // braucht deshalb keinen Eintrag in der Typtabelle.
        if (type is FnType signature)
            return new IrFunctionType(
                signature.Parameters.Select(p => LowerType(p, span)).ToArray(),
                LowerType(signature.Return, span));

        if (type is not PrimitiveType)
            throw NotSupported($"type '{TypeFacts.Display(type)}'", span);

        return TypeLowering.Lower(type);
    }

    /// <summary>Der Rückgabetyp kommt aus dem syntaktischen <see cref="TypeNode"/>, weil die Sema
    /// ihn nicht in <see cref="TypeResult"/> ablegt. Die Auflösung liegt in der
    /// <see cref="TypeTable"/> — dieselbe Stelle, die auch Feld- und Parametertypen auflöst, damit
    /// eine Fabrik <c>static fn new(): P</c> denselben Typ liefert wie ein Feld vom Typ
    /// <c>P</c>.</summary>
    private IrType LowerDeclaredReturnType() =>
        _decl!.ReturnType is null ? VoidType : _typeTable.Lower(_decl.ReturnType);

    /// <summary>Scope-Grenze: gültiges Lyric, für das der Backend-Teil noch fehlt. Wird von
    /// <see cref="ModuleLowerer"/> zu einer <c>LYR-IR0001</c>-Diagnose mit Datei/Zeile/Spalte —
    /// deshalb hier keine Position in den Text schreiben, die rendert die DiagnosticEngine.</summary>
    private static UnsupportedConstructException NotSupported(string what, Span span) =>
        new($"{what} is not supported by this compiler version yet", span);

    /// <summary>Interne Inkonsistenz — der Compiler ist kaputt, nicht der Quelltext.</summary>
    private InternalCompilationException Bug(string message) =>
        new($"lowering: {message} (in '{_name}')");
}
