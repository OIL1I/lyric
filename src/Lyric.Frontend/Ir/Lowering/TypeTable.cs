using Lyric.AST;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Ir.Lowering;

/// <summary>
/// Ordnet jedem gelowerten <c>class</c> eine <see cref="TypeId"/> zu und baut sein Layout.
///
/// <para><b>Interniert wird bei Bedarf</b>, wie bei <see cref="ImportTable"/>: eine deklarierte,
/// nie benutzte Klasse gehört nicht in die Typ-Tabelle des Bytecodes. Die Reihenfolge ergibt sich
/// aus der Lowering-Reihenfolge und ist damit deterministisch (ADR-013).</para>
///
/// <para><b>Die Id wird vor dem Layout vergeben.</b> Das ist der ganze Trick, der
/// <c>class Node { next: Node }</c> möglich macht: beim Betreten wird der Platz reserviert und die
/// Id eingetragen, erst danach werden die Feldtypen gelowert. Ein rekursiver Verweis findet die Id
/// dann schon vor und terminiert, statt sich selbst erneut zu internieren. Dieselbe Zwei-Phasen-
/// Form wie beim Funktions-Lowering (Pass 1 vergibt die <see cref="FunctionId"/>s, Pass 2 lowert),
/// und aus demselben Grund.</para>
///
/// <para><b>Feldreihenfolge kommt aus dem AST, nicht aus der Symboltabelle.</b> Der Feldindex ist
/// der Slot im Objekt — er muss die Deklarationsreihenfolge sein, und die garantiert nur die
/// AST-Liste. Eine Symboltabelle ist eine Map; sich auf ihre Aufzählungsreihenfolge zu verlassen
/// hieße, ein Layout an ein Implementierungsdetail zu hängen.</para>
/// </summary>
internal sealed class TypeTable
{
    private readonly Dictionary<TypeSymbol, TypeId> _assigned = new(ReferenceEqualityComparer.Instance);
    private readonly List<IrTypeDef> _defs = new();
    private readonly Dictionary<TypeSymbol, UnsupportedConstructException> _failed =
        new(ReferenceEqualityComparer.Instance);
    /// <summary>Variantennamen je Enum-Eintrag — nur fürs Lowering, im Bytecode steht der Index.</summary>
    private readonly Dictionary<int, string[]> _variantNames = new();
    private readonly BindingResult _binding;

    /// <param name="binding">Der Resolver hat jeden <c>NamedType</c> bereits an sein Symbol
    /// gebunden. Diese Tabelle zu benutzen statt Namen selbst aufzulösen ist keine Bequemlichkeit:
    /// eine zweite Auflösung wäre eine zweite Wahrheit über Sichtbarkeit und Schattierung.</param>
    public TypeTable(BindingResult binding) => _binding = binding;

    public List<IrTypeDef> Defs => _defs;

    /// <summary>Das Interface hinter einem Constraint (<c>T :: [P]</c>). Der Lowerer braucht es,
    /// um eine Default-Methode zu finden, die der konkrete Typ nicht selbst hat — die Aufloesung
    /// gehoert hierher, weil hier das Binding liegt.</summary>
    public TypeSymbol? ConstraintInterface(TypeNode node) => Conformance.InterfaceOf(node, _binding);

    /// <summary>Eine Zelle je Elementtyp — <c>&lt;cell:int&gt;</c> gibt es genau einmal, egal wie
    /// viele Variablen darin leben.</summary>
    private readonly List<(IrType Element, TypeId Id)> _cells = new();

    /// <summary>
    /// Instanzen generischer Typen, unter ihrem vollen Namen (<c>Box&lt;int&gt;</c>).
    ///
    /// <para>Getrennt von <see cref="_assigned"/>, weil dort das SYMBOL der Schluessel ist —
    /// <c>Box&lt;int&gt;</c> und <c>Box&lt;string&gt;</c> teilen sich eines und sind trotzdem zwei
    /// Typen mit verschiedenem Layout (§12).</para>
    /// </summary>
    private readonly Dictionary<string, TypeId> _instances = new(StringComparer.Ordinal);

    /// <summary>Symbol und Id jeder Instanz — fuer die Impl-Tabelle, die wissen muss, welche
    /// Klasse welches Interface erfuellt (auch wenn beide Instanzen sind).</summary>
    private readonly List<(TypeSymbol Symbol, TypeId Id)> _instanceSymbols = new();

    /// <summary>
    /// Die Typargumente, die gerade eingesetzt werden, waehrend das Layout einer Instanz entsteht.
    ///
    /// <para>Ein Stapel, weil Layouts sich schachteln: <c>Box&lt;Pair&lt;int&gt;&gt;</c> lowert das
    /// Feld <c>v: T</c> zu <c>Pair&lt;int&gt;</c>, und dessen Felder brauchen dann DESSEN
    /// Substitution, nicht die von <c>Box</c>.</para>
    /// </summary>
    private readonly Stack<IReadOnlyDictionary<string, LyrType>> _substitutions = new();

    /// <summary>Ist dieser Typ eine Zelle? Gefragt wird das beim Lesen eines Captures: eine
    /// gefangene Zelle transportiert eine Variable, und was das Programm sehen will, ist ihr
    /// Inhalt.</summary>
    public bool IsCell(TypeId id) => _cells.Any(c => c.Id == id);

    /// <summary>
    /// Der Typ, in dem ein gefangenes <c>var</c> lebt (ADR-018): ein Objekt mit <b>einem</b> Feld.
    ///
    /// <para>Bewusst kein eigener IR-Typ und kein eigener Opcode. Eine Zelle ist ein Objekt, also
    /// tun es <c>newobj</c>, <c>ldfld 0</c> und <c>stfld 0</c> — der Verifier prueft sie damit wie
    /// jedes andere Objekt, der Disassembler zeigt sie ohne Sonderfall, und das Bytecode-Format
    /// bleibt unveraendert. Ein <c>newcell</c>/<c>ldcell</c>/<c>stcell</c>-Trio waere ein zweiter
    /// Mechanismus fuer „Feld eines Objekts".</para>
    ///
    /// <para>Interniert, weil zwei Zellen desselben Elementtyps ununterscheidbar sind. Das haelt
    /// die Typtabelle klein, wenn eine Funktion mehrere <c>var</c>-Captures hat.</para>
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
    /// Reserviert den Zustandstyp einer Coroutine — <b>ohne Layout</b> (Sprache.md §8).
    ///
    /// <para>Dieselbe Zwei-Phasen-Form wie bei einer rekursiven Klasse, und aus demselben Grund:
    /// die Id muss stehen, bevor das Layout bekannt ist. Welche Locals ein <c>yield</c>
    /// ueberleben, weiss man erst, wenn der Rumpf gelowert ist — und der Rumpf braucht die Id
    /// schon bei seinem ersten Feldzugriff.</para>
    ///
    /// <para>Slot 0 ist der <b>Wiedereintrittspunkt</b>: 0 heisst „noch nicht gestartet", n der
    /// Block hinter dem n-ten <c>yield</c>, -1 „durchgelaufen". Danach kommen Parameter und
    /// Locals.</para>
    /// </summary>
    public TypeId ReserveCoroutineState(string name)
    {
        var id = new TypeId(_defs.Count);
        _defs.Add(new IrTypeDef($"<coro:{name}>", [], []));
        return id;
    }

    /// <summary>Traegt das Layout nach, sobald der Rumpf gelowert ist.</summary>
    public void CompleteCoroutineState(TypeId id, IrType[] fieldTypes, string[] fieldNames) =>
        _defs[id.Value] = _defs[id.Value] with { FieldTypes = fieldTypes, FieldNames = fieldNames };

    /// <summary>
    /// Der Typ des Environments einer Closure: ein Objekt, dessen Felder die gefangenen Werte
    /// sind (ADR-018).
    ///
    /// <para><b>Nicht</b> interniert, anders als eine Zelle: zwei Lambdas mit gleich geformten
    /// Captures fangen trotzdem verschiedene Variablen, und ihre Environments zu teilen haette
    /// keinen Nutzen — es gibt nie zwei Instanzen desselben Environment-Typs, die man sparen
    /// koennte.</para>
    ///
    /// <para>Der Name taucht in Disassembly und Diagnosen auf und traegt deshalb den Namen der
    /// Funktion, zu der das Lambda gehoert.</para>
    /// </summary>
    public IrRefType EnvironmentFor(string lambdaName, IrType[] fieldTypes, string[] fieldNames)
    {
        var id = new TypeId(_defs.Count);
        _defs.Add(new IrTypeDef($"<env:{lambdaName}>", fieldTypes, fieldNames));
        return new IrRefType(id);
    }

    /// <summary>Der Typ eines Wertes dieser Klasse: eine Referenz, kein eingebetteter Wert
    /// (Sprache.md §3.3).</summary>
    public IrRefType RefTo(TypeSymbol symbol) => new(Intern(symbol));

    /// <summary>Der Typ eines Enum-Wertes: eine Referenz auf den Enum-Eintrag, nicht auf eine
    /// Variante. Welche Variante vorliegt, steht zur Laufzeit in ihrem Slot 0.</summary>
    public IrEnumType EnumOf(TypeSymbol symbol) => new(Intern(symbol));

    /// <summary>Der Typ eines ueber ein Interface angesprochenen Wertes (Lyrics <c>dyn</c>).</summary>
    public IrInterfaceType InterfaceOf(TypeSymbol symbol) => new(Intern(symbol));

    /// <summary>Der Typ eines <c>struct</c>-Wertes: dasselbe Layout wie eine Klasse, aber
    /// Wert-Semantik (Sprache.md §3.2).</summary>
    public IrStructType StructOf(TypeSymbol symbol) => new(Intern(symbol));

    /// <summary>Ist dieser Tabellen-Eintrag ein Wert-Typ? Das Lowering fragt das, um zu
    /// entscheiden, ob an einem Bindepunkt ein <c>structcopy</c> noetig ist.</summary>
    public bool IsStruct(TypeId id) => _defs[id.Value].IsStruct;

    /// <summary>
    /// Der Methoden-Slot eines Interfaces. Der <b>Index</b> ist der Vertrag, nicht der Name: er
    /// steht zur Compile-Zeit fest, weil Lyric statisch typisiert ist und kein Monkey-Patching
    /// kennt. Genau wie beim Feldindex einer Klasse.
    /// </summary>
    public int SlotOf(TypeSymbol interfaceSymbol, string method, Core.Span span)
    {
        var def = _defs[Intern(interfaceSymbol).Value];
        var index = Array.IndexOf(def.MethodSlots, method);
        if (index >= 0) return index;

        throw new UnsupportedConstructException(
            $"interface '{interfaceSymbol.Name}' has no method '{method}'", span);
    }

    /// <summary>Die Slot-Namen eines Interfaces, in Deklarationsreihenfolge. Der
    /// <see cref="ModuleLowerer"/> braucht sie, um die vtable-Zeilen zu fuellen.</summary>
    public string[] MethodSlotsOf(TypeId id) => _defs[id.Value].MethodSlots;

    /// <summary>Alle bisher internierten Typen mit ihrem Symbol — die Grundlage der Impl-Tabelle.
    /// Nur was interniert wurde, steht im Bytecode, und nur dafuer braucht es vtable-Zeilen.</summary>
    public IEnumerable<(TypeSymbol Symbol, TypeId Id)> Interned =>
        _assigned.Select(pair => (pair.Key, pair.Value)).Concat(_instanceSymbols);

    public bool IsInterface(TypeId id) => _defs[id.Value].IsInterface;

    /// <summary>Der Types-Index einer Variante. Sie ist ein eigener Layout-Eintrag (ADR-016s
    /// Nachbar-Entscheidung, Bytecode.md §2); ihr Slot 0 ist das Tag.</summary>
    public TypeId VariantOf(TypeSymbol enumSymbol, string variantName, Core.Span span)
    {
        var id = Intern(enumSymbol);
        var index = Array.IndexOf(_variantNames[id.Value], variantName);
        if (index >= 0) return _defs[id.Value].Variants[index];

        throw new UnsupportedConstructException(
            $"'{enumSymbol.Name}' has no variant '{variantName}'", span);
    }

    /// <summary>Die Tag-Nummer einer Variante — ihr Index in der Deklarationsreihenfolge.</summary>
    public int TagOf(TypeSymbol enumSymbol, string variantName, Core.Span span)
    {
        var index = Array.IndexOf(_variantNames[Intern(enumSymbol).Value], variantName);
        if (index >= 0) return index;

        throw new UnsupportedConstructException(
            $"'{enumSymbol.Name}' has no variant '{variantName}'", span);
    }

    public TypeId Intern(TypeSymbol symbol) => Intern(symbol, []);

    /// <summary>
    /// Die <see cref="TypeId"/> eines Typs — bei einem generischen die seiner <b>Instanz</b> fuer
    /// genau diese Typargumente (§12).
    ///
    /// <para><c>Box&lt;int&gt;</c> und <c>Box&lt;string&gt;</c> bekommen verschiedene Eintraege mit
    /// eigenem Layout. Das ist dieselbe Monomorphisierung wie bei Funktionen und aus demselben
    /// Grund: die VM kennt keine Typen zur Laufzeit, also muss ein Feld-Layout zur Compile-Zeit
    /// feststehen.</para>
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
                // Interface und Enum haben eigene Eintragsformen (Methoden-Slots bzw. Varianten)
                // und kein Feld-Layout. Sie muessen deshalb ihre eigenen Pfade nehmen — nur die
                // Substitution ist gemeinsam.
                if (symbol.Kind == TypeSymbolKind.Interface)
                {
                    var id = InternInterface(symbol, instanceName);
                    _instances[instanceName] = id;
                    _instanceSymbols.Add((symbol, id));
                    return id;
                }

                if (symbol.Kind == TypeSymbolKind.Enum)
                {
                    var id = InternEnum(symbol);
                    _instances[instanceName] = id;
                    _instanceSymbols.Add((symbol, id));
                    return id;
                }

                return InternLayout(symbol, instanceName, _instances);
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
        // Ein Typ, dessen Layout schon einmal gescheitert ist, scheitert wieder — mit derselben
        // Meldung. Ohne das bliebe der Platzhalter aus dem ersten Versuch stehen, und der zweite
        // Aufrufer läse ein Layout mit FieldNames == null: eine NullReferenceException im Compiler
        // statt einer Diagnose. Genau das passierte bei `examples/bank.lyr`, dessen Account einen
        // Feld-Default hat.
        if (_failed.TryGetValue(symbol, out var failure))
            throw new UnsupportedConstructException(failure.Message, failure.Span);

        if (_assigned.TryGetValue(symbol, out var existing)) return existing;

        if (symbol.Kind == TypeSymbolKind.Enum) return InternEnum(symbol);
        if (symbol.Kind == TypeSymbolKind.Interface) return InternInterface(symbol);

        if (symbol.Kind is not (TypeSymbolKind.Class or TypeSymbolKind.Struct))
            throw new UnsupportedConstructException(
                $"type '{symbol.Name}' ({Describe(symbol.Kind)}) is not supported by this compiler version yet",
                SpanOf(symbol));


        // Klasse und struct teilen sich das gesamte Layout-Verfahren; sie unterscheiden sich
        // ausschliesslich in der Bindungs-Semantik, und die steckt im Lowering, nicht hier.
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
    /// Baut das Layout und traegt es ein. Fuer eine Instanz ist <paramref name="registry"/> die
    /// Instanz-Map und <paramref name="name"/> traegt die Typargumente; sonst zaehlt das Symbol.
    /// </summary>
    private TypeId InternLayout(TypeSymbol symbol, string name, Dictionary<string, TypeId>? registry)
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

        // Platz reservieren UND Id eintragen, bevor die Feldtypen gelowert werden — siehe
        // Klassen-Doku. Der Platzhalter wird unten überschrieben; sichtbar wird er nie, weil
        // Lower(field) nur die Id braucht, nicht das Layout.
        var id = new TypeId(_defs.Count);
        if (registry is null) _assigned[symbol] = id;
        else { registry[name] = id; _instanceSymbols.Add((symbol, id)); }
        _defs.Add(default);

        try
        {
            var fields = members.OfType<FieldDecl>().ToArray();
            var names = new string[fields.Length];
            var types = new IrType[fields.Length];

            for (var i = 0; i < fields.Length; i++)
            {
                // Ein Feld-Default gehoert NICHT ins Layout: er ist ein Ausdruck, kein Typ, und
                // wird an der Konstruktionsstelle ausgewertet — dort, wo auch die explizit
                // angegebenen Werte entstehen. Im Bytecode steht davon nichts.
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
            // Die Id NICHT zurückgeben: zwischenzeitlich kann ein Feldtyp weitere Typen interniert
            // haben, deren Ids sonst verschöben. Stattdessen den Fehlschlag merken — das Modul wird
            // ohnehin verworfen (ModuleLowerer liefert null), die Tabelle muss nur konsistent
            // bleiben, bis alle Funktionen ihre Diagnose abgesetzt haben.
            _failed[symbol] = ex;
            throw;
        }
    }

    /// <summary>
    /// Ein Enum wird zu <b>einem</b> Enum-Eintrag plus <b>je einem Layout-Eintrag pro Variante</b>
    /// (Bytecode.md §2). Slot 0 jeder Variante ist ihr Tag; die Nutzfelder folgen ab Slot 1.
    ///
    /// <para>Wie bei einer Klasse wird die Id <b>vor</b> den Varianten vergeben — ein Enum darf
    /// sich über eine Variante selbst nennen (<c>enum Tree { Leaf, Node(Tree, Tree) }</c>), und
    /// ohne die vorgezogene Id liefe das in eine Endlosschleife.</para>
    /// </summary>
    private TypeId InternEnum(TypeSymbol symbol)
    {
        if (symbol.Generics.Length > 0)
            throw new UnsupportedConstructException(
                $"generic enum '{symbol.Name}' is not supported by this compiler version yet",
                SpanOf(symbol));

        if (symbol.Declaration is not EnumDecl decl)
            throw new UnsupportedConstructException(
                $"enum '{symbol.Name}' has no declaration to read its variants from", SpanOf(symbol));

        var id = new TypeId(_defs.Count);
        _assigned[symbol] = id;
        _defs.Add(default);
        _variantNames[id.Value] = decl.Variants.Select(v => v.Name).ToArray();

        try
        {
            var variants = new TypeId[decl.Variants.Length];
            for (var i = 0; i < decl.Variants.Length; i++)
                variants[i] = InternVariant(symbol, decl.Variants[i]);

            _defs[id.Value] = new IrTypeDef(symbol.Name, [], []) { Variants = variants };
            return id;
        }
        catch (UnsupportedConstructException ex)
        {
            _failed[symbol] = ex;
            throw;
        }
    }

    /// <summary>
    /// Ein Interface wird zu einem Eintrag <b>ohne Felder</b>, der nur seine Methoden-Slots
    /// benennt. Die Reihenfolge kommt aus der Deklaration, nicht aus der Symboltabelle: der Slot
    /// ist ein Vertrag, die Aufzaehlungsreihenfolge einer Map ist ein Implementierungsdetail —
    /// dieselbe Regel wie bei den Feldern einer Klasse.
    ///
    /// <para>Aufgenommen werden <b>alle</b> deklarierten Methoden, abstrakte wie Default. Ein
    /// Default belegt einen Slot, weil eine Klasse ihn ueberschreiben darf; ohne Slot waere er
    /// nicht ueberschreibbar.</para>
    /// </summary>
    private TypeId InternInterface(TypeSymbol symbol) => InternInterface(symbol, symbol.Name);

    /// <param name="name">Bei einer Instanz der volle Name (<c>Iterator&lt;int&gt;</c>) — er steht
    /// in Disassembly und Diagnosen, und zwei Instanzen desselben Interfaces sollen dort
    /// unterscheidbar sein.</param>
    private TypeId InternInterface(TypeSymbol symbol, string name)
    {
        if (symbol.Declaration is not InterfaceDecl decl)
            throw new UnsupportedConstructException(
                $"interface '{symbol.Name}' has no declaration to read its methods from",
                SpanOf(symbol));

        var slots = decl.Members.OfType<FunctionDecl>().Select(m => m.Name).ToArray();
        if (slots.Length == 0)
            throw new UnsupportedConstructException(
                $"interface '{symbol.Name}' declares no methods; an empty interface has nothing "
                + "to dispatch on", SpanOf(symbol));

        var id = new TypeId(_defs.Count);

        // Ein generisches Interface hat pro Instanz einen eigenen Eintrag; nur das
        // nicht-generische traegt sich unter seinem Symbol ein. Sonst bekaeme
        // 'Iterator<string>' die Id von 'Iterator<int>'.
        if (symbol.Generics.Length == 0) _assigned[symbol] = id;

        _defs.Add(new IrTypeDef(name, [], []) { MethodSlots = slots });
        return id;
    }

    private TypeId InternVariant(TypeSymbol owner, EnumVariant variant)
    {
        // Slot 0 ist das Tag. Es steht im Layout, damit der Feldzugriff nach dem 'enumas' ein
        // gewoehnliches ldfld bleibt — die Variante ist dann eine ganz normale Klasse.
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
        _defs.Add(new IrTypeDef($"{owner.Name}.{variant.Name}", types.ToArray(), names.ToArray()));
        return id;
    }

    /// <summary>Findet den Feldindex. Der Name existiert nur hier und in der Diagnose; im Bytecode
    /// steht ausschließlich die Position.</summary>
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
        NamedRef { Symbol.Kind: TypeSymbolKind.Class } n => RefTo(n.Symbol),
        NamedRef { Symbol.Kind: TypeSymbolKind.Struct } n => StructOf(n.Symbol),
        NamedRef { Symbol.Kind: TypeSymbolKind.Enum } n => EnumOf(n.Symbol),
        NamedRef { Symbol.Kind: TypeSymbolKind.Interface } n => InterfaceOf(n.Symbol),

        // Ein Funktionstyp traegt seine Signatur strukturell und braucht deshalb keinen Eintrag in
        // dieser Tabelle — anders als jeder benannte Typ. Rekursiv gelowert, weil Parameter und
        // Rueckgabe selbst Klassen, Enums oder wieder Funktionen sein duerfen.
        // Eine Instanz eines generischen Typs: 'Box<int>' ist ein eigener Tabellen-Eintrag mit
        // eigenem Layout (§12).
        GenericInstance g => InstanceType(g, span),

        // Coroutine<T> ist ein Funktionswert ohne Parameter — siehe FunctionLowerer.LowerType.
        CoroutineOf c => new IrFunctionType([], Lower(c.Yield, span)),

        FnType f => new IrFunctionType(
            f.Parameters.Select(p => Lower(p, span)).ToArray(), Lower(f.Return, span)),

        _ => TypeLowering.Lower(type)
    };

    /// <summary>
    /// Ein syntaktisch geschriebener Typ (Feld, Parameter, Rückgabetyp). Ein Klassentyp interniert
    /// rekursiv — das terminiert, weil <see cref="Intern"/> die Id vor dem Layout vergibt.
    /// </summary>
    public IrType Lower(TypeNode node) => Lower(node, node.Span);

    private IrType Lower(TypeNode node, Core.Span span)
    {
        // T[] (ADR-016). Eine Size im Typ gibt es nicht mehr — T[N] ist aus v1 gestrichen, die
        // Länge ist eine Eigenschaft des Wertes.
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

        // 'fn(A, B) -> R' — geschrieben in Parameter- und Rueckgabepositionen. Braucht keinen
        // Tabelleneintrag: der Typ traegt seine Signatur selbst.
        if (node is FunctionType signature)
            return new IrFunctionType(
                signature.Parameters.Select(p => Lower(p, p.Span)).ToArray(),
                Lower(signature.ReturnType, signature.ReturnType.Span));

        if (node is NamedType named)
        {
            // Ein Typ-Parameter im Layout einer Instanz: 'v: T' in 'Box<int>' ist ein int. Die
            // Frage muss VOR der Symbolaufloesung kommen — 'T' ist kein Typ, den man finden
            // koennte.
            if (named.TypeArguments.Length == 0 && _substitutions.Count > 0
                && _substitutions.Peek().TryGetValue(named.Path[^1], out var substituted))
                return Lower(substituted, span);

            if (named.TypeArguments.Length == 0
                && TypeFacts.FromBuiltinName(named.Path[^1]) is { } primitive)
                return TypeLowering.Lower(primitive);

            var bound = _binding.Resolve(named);
            if (bound is ImportBindingSymbol import) bound = import.Target;

            // Geschriebene Typargumente ('Box<int>' als Feld- oder Parametertyp): sie werden
            // gelowert, BEVOR die Instanz interniert wird — ein Argument kann selbst ein
            // Typ-Parameter der umgebenden Instanz sein ('Box<T>' in 'Pair<T>').
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

            if (bound is TypeSymbol { Kind: TypeSymbolKind.Enum } enumType) return EnumOf(enumType);
            if (bound is TypeSymbol { Kind: TypeSymbolKind.Interface } iface) return InterfaceOf(iface);
            if (bound is TypeSymbol { Kind: TypeSymbolKind.Struct } value) return StructOf(value);
            if (bound is TypeSymbol type) return RefTo(type);
        }

        throw new UnsupportedConstructException(
            "a non-primitive field type is not supported by this compiler version yet", node.Span);
    }

    /// <summary>
    /// Ein geschriebenes Typargument als <see cref="LyrType"/> — das, was <see cref="Intern"/> als
    /// Schluessel braucht.
    ///
    /// <para>Nicht ueber <see cref="Lower(TypeNode)"/>: der liefert einen IR-Typ, und aus dem
    /// laesst sich der Sema-Typ nicht zurueckgewinnen. Der Name einer Instanz muss aber aus
    /// Sema-Typen gebildet werden, sonst hiessen <c>Box&lt;int&gt;</c> und <c>Box&lt;int64&gt;</c>
    /// verschieden, obwohl sie dasselbe sind.</para>
    /// </summary>
    /// <summary>Der IR-Typ einer Instanz — Referenz, Wert, Enum oder Interface, je nach dem, was
    /// die Definition ist.</summary>
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
            if (bound is TypeSymbol symbol) return new NamedRef(symbol);
        }

        if (node is ArrayType { Size: null } array) return new ArrayOf(Resolve(array.Element, span), null);
        if (node is NullableType option) return new Optional(Resolve(option.Inner, span));

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
