using Lyric.AST;
using Lyric.Core;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Ir.Lowering;

/// <summary>
/// Einstieg ins Lowering: typgeprüfte Compilation → <see cref="IrModule"/>.
///
/// <para><b>Zwei Pässe.</b> Pass 1 vergibt jeder zu lowernden Funktion ihre
/// <see cref="FunctionId"/>, Pass 2 lowert die Bodies. Ohne die Trennung scheitert jeder
/// Vorwärts-Call und jede (wechselseitige) Rekursion, weil das Ziel beim Lowern des Calls noch
/// keine Id hätte. Dieselbe Lösung wie das 2-Pass-Deklarieren im Resolver — dasselbe Problem an
/// anderer Stelle.</para>
///
/// <para><b>Der Verifier läuft als Abnahme.</b> Ein Befund ist ein Bug in diesem Lowering, keine
/// User-Diagnose, deshalb wirft <see cref="IrVerifier.VerifyOrThrow"/>. In Tests und Debug-Builds
/// immer an; für Release-Builds kann der Aufrufer ihn abschalten (Vorbild: LLVMs Verifier in
/// Assert-Builds).</para>
///
/// <para><b>Was übersprungen wird</b>: bodylose Deklarationen (nichts zu lowern) und generische
/// Funktionen. Letztere brauchen die Worklist-Monomorphisierung — pro konkretem Typargument-Tupel
/// eine Instanz, ausgehend von den Wurzeln. Ein Call auf eine übersprungene Funktion findet keine
/// Id und meldet das als <c>LYR-IR0001</c> statt still falschen Code zu erzeugen.</para>
/// </summary>
public static class ModuleLowerer
{
    /// <summary>Wie oft die nachgelagerten Tabellen abwechselnd geleert werden, bevor der Compiler
    /// aufgibt. Jede Runde muss etwas Neues liefern, sonst bricht die Schleife ohnehin ab — die
    /// Grenze faengt nur den Fall, dass sich zwei Tabellen endlos gegenseitig fuettern.</summary>
    private const int MaxLoweringRounds = 100;

    internal static readonly Dictionary<GenericParamSymbol, LyrType> NoSubstitution =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Läuft der Verifier, wenn der Aufrufer nichts anderes sagt? In Debug-Builds ja, im Release
    /// nein — Vorbild ist LLVMs Verifier in Assert-Builds.
    ///
    /// <para>Gemessen an 400 Funktionen / 18 400 Instruktionen: Lowering mit Verifikation 30 ms,
    /// ohne 2,8 ms. Die Prüfung ist also <b>90 % der Gesamtzeit</b>, nicht der vernachlässigbare
    /// Posten, nach dem sie aussieht — das meiste davon steckt im Availability-Dataflow, der pro
    /// Block HashSets alloziert und zum Fixpunkt iteriert.</para>
    ///
    /// <para>Das Risiko bleibt beherrschbar, weil der Bytecode-Leser beim Laden ohnehin
    /// vollständig validiert (<c>LYR-BC####</c>). Ein Lowering-Bug im Release-Compiler äußert sich
    /// also nicht als still falscher Code, sondern spätestens beim Laden — nur mit schlechterer
    /// Fehlermeldung als ein Verifier-Befund.</para>
    /// </summary>
    public static bool VerifyByDefault =>
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>Lowert die Compilation. Liefert <c>null</c>, wenn Scope-Grenzen als
    /// <c>LYR-IR0001</c> gemeldet wurden — dann steht die Ursache in
    /// <paramref name="de"/>.</summary>
    /// <param name="verify"><c>null</c> = <see cref="VerifyByDefault"/>. Tests setzen den Wert
    /// explizit, damit ihr Ergebnis nicht von der Build-Konfiguration abhängt.</param>
    public static IrModule? Lower(Compilation compilation, BindingResult binding, TypeResult types,
        DiagnosticEngine de, bool? verify = null)
    {
        // Receiver == null: freie Funktion oder 'static fn'. Sonst der Typ, dessen Instanz als
        // Parameter 0 übergeben wird (ADR-014).
        var pending = new List<(FunctionDecl Decl, string Name, TypeSymbol? Receiver, TypeNode? ExtendTarget)>();
        var ids = new Dictionary<FunctionSymbol, FunctionId>(ReferenceEqualityComparer.Instance);
        var imports = new ImportTable();
        var typeTable = new TypeTable(binding) { Compilation = compilation };
        var globals = new GlobalTable();
        FunctionId? entry = null;
        var failed = false;

        // Pass 1 — Funktionstabelle. Die Reihenfolge ist Modul- dann Deklarations-Reihenfolge und
        // damit deterministisch: FunctionIds landen als Indizes im Bytecode (ADR-013).
        foreach (var module in compilation.Modules)
        {
            foreach (var decl in compilation.AstOf(module).Declarations)
            {
                if (decl is not FunctionDecl function) continue;
                if (function.Generics.Length > 0) continue;
                if (module.Members.LookupLocal(function.Name) is not FunctionSymbol symbol) continue;

                // Rumpflos in einem Stdlib-Modul = native Deklaration. Die Signatur steht in Lyric,
                // die Implementierung liegt im Host und wird beim Laden über den Namen gebunden.
                // In User-Code hat die Sema das schon als LYR-SEM0051 abgelehnt.
                if (function.Body is null)
                {
                    if (!compilation.IsNative(module)) continue;

                    // Gefangen, nicht geworfen: eine native Signatur mit einem Typ, den das
                    // Lowering nicht kennt, ist eine Scope-Grenze wie jede andere — und der
                    // Nutzer soll eine Diagnose mit Position sehen statt eines Compiler-Absturzes.
                    try
                    {
                        imports.Declare(symbol, new IrImport(
                        NameMangling.ForFunction(module, function.Name),
                            function.Parameters.Select(p => DeclaredTypes.Lower(p.Type)).ToArray(),
                            DeclaredTypes.Lower(function.ReturnType)));
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

                // Entry-Contract (Sprache.md §11): genau ein 'main' pro Executable. Dass es
                // eindeutig ist, hat die Sema geprüft — hier wird es nur festgehalten.
                if (function.Name != "main") continue;

                if (function.Parameters.Length == 0) { entry = id; continue; }

                // §11 kennt zwei Formen: 'fn main(): int' und 'fn main(args: string[]): int'. Die
                // zweite bekommt ihr Array von der Runtime; welche Form vorliegt, liest diese aus
                // der Signatur des Einstiegs — die Funktionstabelle traegt sie ohnehin, also
                // braucht das Format dafuer kein Flag.
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

            // Methoden sind gewöhnliche Funktionen mit dem Empfänger als Parameter 0 — dieselbe
            // Konvention wie CIL. Der Unterschied zwischen Instanz- und static-Methode ist damit
            // allein die Parameterliste, und P3 muss für die vtable nur noch entscheiden, WELCHE
            // Funktion gerufen wird, nicht wie sie aussieht.
            foreach (var decl in compilation.AstOf(module).Declarations)
            {
                // Klassen und Enums tragen beide Methoden; für das Lowering sind sie derselbe Fall
                // (Empfänger als Parameter 0), nur die Member-Liste steckt woanders im AST.
                var (typeName, members) = decl switch
                {
                    ClassDecl c when c.Generics.Length == 0 => (c.Name, c.Members),
                    StructDecl v when v.Generics.Length == 0 => (v.Name, v.Members),
                    EnumDecl e when e.Generics.Length == 0 => (e.Name, e.Methods.Cast<Decl>().ToArray()),
                    // Default-Methoden eines Interfaces sind gewoehnliche Funktionen mit dem
                    // Empfaenger als Parameter 0 — nur dass dessen statischer Typ das Interface
                    // selbst ist. Ein 'this.foo()' darin wird damit zu einem callvirt, und das ist
                    // richtig: welche Implementierung laeuft, steht erst zur Laufzeit fest.
                    // Abstrakte Methoden (ohne Rumpf) fallen unten durch die Body-Pruefung.
                    InterfaceDecl i when i.Generics.Length == 0 => (i.Name, i.Members.Cast<Decl>().ToArray()),
                    _ => (null, null),
                };
                if (typeName is null || members is null) continue;
                if (module.Members.LookupLocal(typeName) is not TypeSymbol type) continue;

                foreach (var member in members)
                {
                    if (member is not FunctionDecl method) continue;
                    if (method.Generics.Length > 0 || method.Body is null) continue;
                    if (type.Members.LookupLocal(method.Name) is not FunctionSymbol symbol) continue;

                    ids[symbol] = new FunctionId(pending.Count);
                    pending.Add((method, NameMangling.ForMethod(module, typeName, method.Name),
                        method.IsStatic ? null : type, null));
                }
            }
        }

        // Extend-Bloecke bekommen hier KEINE Ids. Eine Extension-Methode wird erst bei ihrem
        // ersten Aufruf angefordert (ExtensionTable) — dieselbe Worklist-Form wie bei Lambdas und
        // monomorphisierten Instanzen, und aus demselben Grund: im Bytecode soll nur stehen, was
        // benutzt wird. Bis M8/S1a standen sie hier, was harmlos war, solange Extensions nur in
        // Nutzer-Programmen vorkamen; mit den Display-Extensions in 'std.core' — einem Modul, das
        // immer geladen wird — trug ploetzlich jedes Programm fuenf ungenutzte Funktionen.

        // Globals werden VOR den Rumpfen gesammelt: eine Funktion darf eine Konstante lesen, die
        // weiter unten im Quelltext steht. Dieselbe Zwei-Phasen-Form wie bei den FunctionIds.
        try
        {
            globals.Collect(compilation, types, typeTable);
        }
        catch (UnsupportedConstructException ex)
        {
            de.Report(LoweringDiagnostics.NotSupported, Severity.Error, ex.Span, ex.Message);
            return null;
        }

        // Angehobene Lambdas kommen ganz ans Ende der Funktionsliste — hinter die geschriebenen
        // Funktionen UND hinter den Global-Initialisierer. Die Reihenfolge ist kein Geschmack:
        // die Position IST die FunctionId (ADR-013), und ein Lambda im Initialisierer
        // (`let f = () => 1;`) wuerde sonst seine eigene Id verschieben.
        // Coroutine-Rumpfe kommen hinter die geschriebenen Funktionen und den Initialisierer,
        // Lambdas dahinter — die Position IST die FunctionId (ADR-013), also muss die Reihenfolge
        // festliegen, bevor der erste Rumpf gelowert wird.
        // Alle drei Sorten nachgelagerter Funktionen teilen sich EINEN Zaehler: sie wachsen
        // gleichzeitig und unbegrenzt, also kann keine einen eigenen Bereich reservieren.
        var nextId = new FunctionIds(pending.Count + (globals.IsEmpty ? 0 : 1));
        var coroutines = new CoroutineTable(nextId);
        var instances = new InstanceTable(nextId);
        var lambdas = new LambdaTable(nextId);
        var extensions = new ExtensionTable(nextId);
        typeTable.Extensions = extensions;

        // Pass 2 — Bodies. Scope-Grenzen werden gemeldet, nicht geworfen: der Nutzer soll alle
        // fehlenden Konstrukte seines Programms in einem Durchlauf sehen, nicht eines pro Aufruf.
        var functions = new List<IrFunction>(pending.Count);
        var reported = new HashSet<(Span Span, string Message)>();
        foreach (var (decl, name, receiver, extendTarget) in pending)
        {
            try
            {
                // Eine Coroutine wird zu ZWEI Funktionen: die Fabrik traegt den geschriebenen
                // Namen und liefert ein Zustandsobjekt, der Rumpf wird angemeldet und hinten
                // angehaengt (Sprache.md §8).
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
                // Eine Scope-Grenze im Layout eines Typs trifft jede Funktion, die ihn benutzt —
                // gemeldet werden soll sie einmal. Der Nutzer soll alle FEHLENDEN KONSTRUKTE seines
                // Programms sehen, nicht jede Stelle, an der dasselbe fehlt.
                if (reported.Add((ex.Span, ex.Message)))
                    de.Report(LoweringDiagnostics.NotSupported, Severity.Error, ex.Span, ex.Message);
                failed = true;
            }
        }

        // Eine übersprungene Funktion würde die FunctionIds der folgenden verschieben — der
        // Modulaufbau ist damit nicht mehr rettbar. Kein Teilergebnis zurückgeben.
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

        // Die nachgelagerten Funktionen: Coroutine-Rumpfe, monomorphisierte Instanzen und
        // angehobene Lambdas. Jede Sorte kann beim Lowern die anderen anfordern, deshalb wird
        // dreimal abwechselnd geleert, bis nichts mehr nachkommt — und am Ende nach Id sortiert,
        // weil die Position in der Liste die Id IST (ADR-013).
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

        // Die vtable-Zeilen ZUERST, denn sie koennen eine Extension anfordern, die bisher niemand
        // gerufen hat: 'extend A :: [I]' wird gebraucht, sobald ein A in einem I-Slot landet —
        // auch wenn die Methode im Quelltext nirgends direkt steht.
        var impls = BuildImpls(typeTable, binding, compilation, ids, extensions, instances,
            de, ref failed);
        if (failed) return null;

        var late = new List<(FunctionId Id, IrFunction Function)>();
        try
        {
            for (var round = 0; round < MaxLoweringRounds; round++)
            {
                var before = late.Count;
                // Alle drei, nicht nur zwei: eine vtable-Zeile fuer eine generische Instanz
                // fordert deren Methode an (ListIterator<int>.next), und die entsteht erst durch
                // die Monomorphisierung. Fehlte 'instances' hier, zeigte die Zeile auf eine
                // FunctionId, die niemand gefuellt hat — der Verifier meldet das als
                // "targets f7, which is out of range".
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

        // Types nach dem Lowering eingesammelt, nicht davor: die Tabelle enthält nur, was
        // tatsächlich benutzt wurde — eine deklarierte, nie instanziierte Klasse gehört nicht in
        // den Bytecode. Gleiche Regel wie bei den Imports.
        var result = new IrModule(functions)
        {
            EntryFunction = entry, Imports = imports.Used, Types = typeTable.Defs,
            Globals = globals.Defs, GlobalInit = globalInit,
            Capabilities = RequiredCapabilities(compilation),
            Impls = impls,
        };
        if (failed) return null;
        if (verify ?? VerifyByDefault) IrVerifier.VerifyOrThrow(result);
        return result;
    }

    /// <summary>
    /// Was dieses Programm an Capabilities verlangt: die Vereinigung ueber alle geladenen Module
    /// (ADR-007, Doku §20.1).
    ///
    /// <para>Gezaehlt wird <b>geladen</b>, nicht <b>importiert</b>: ein Modul, das <c>std.os</c>
    /// importiert, zieht es in die Compilation, und sein Bedarf gehoert zum Programm — auch wenn
    /// die Hauptdatei den Namen nie nennt. Wer nur die Import-Zeilen der Wurzel zaehlte, haette
    /// eine Luecke, die genau eine Indirektion tief ist.</para>
    /// </summary>
    private static Capability RequiredCapabilities(Compilation compilation)
    {
        var needed = Capability.None;
        foreach (var module in compilation.Modules)
            needed |= CapabilityTable.RequiredForImport(module.FullName);
        return needed;
    }

    /// <summary>
    /// Die vtable-Zeilen: fuer jede internierte Klasse und jedes internierte Interface, das sie
    /// implementiert, Slot fuer Slot die Zielfunktion.
    ///
    /// <para><b>Nach</b> dem Lowering, weil erst dann feststeht, welche Typen ueberhaupt im
    /// Bytecode landen — dieselbe Regel wie bei Types und Imports: eine deklarierte, nie benutzte
    /// Klasse gehoert nicht hinein. Interfaces sind zu diesem Zeitpunkt bereits interniert, weil
    /// jedes <c>mkiface</c> und <c>callvirt</c> ihre Id schon beim Lowern gebraucht hat.</para>
    ///
    /// <para><b>Die Aufloesungsreihenfolge faellt hier, nicht zur Laufzeit</b> (Sprache.md §3.5:
    /// eigenes Member vor Interface-Default). Der Dispatch findet damit einen fertigen
    /// Funktionsindex vor und muss nichts suchen.</para>
    ///
    /// <para>Deterministisch sortiert: die Zeilen landen als Sektion im Bytecode, und ADR-013
    /// verlangt byte-identischen Output bei gleichem Input. Die Aufzaehlungsreihenfolge eines
    /// Dictionary erfuellt das nicht.</para>
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
                // Konformanz kann deklariert sein ODER aus einem 'extend T :: [I]' kommen
                // (§3.6). Die vtable-Zeile ist dieselbe — welcher der beiden Wege sie begruendet
                // hat, ist zur Laufzeit nicht mehr unterscheidbar und soll es auch nicht sein.
                var viaExtension = ExtendBlocksFor(compilation, type, iface, binding);
                if (!Conformance.Implements(type, iface, binding) && viaExtension.Count == 0)
                    continue;

                var slots = typeTable.MethodSlotsOf(ifaceId);
                var methods = new FunctionId[slots.Length];
                var complete = true;

                for (var i = 0; i < slots.Length; i++)
                {
                    // Eigenes Member schlaegt Default — Sprache.md §3.5.
                    // Reihenfolge ist §3.5/§3.6: eigenes Member, dann Extension, dann der
                    // Default des Interfaces. Eine Extension-Methode steht NICHT in
                    // 'type.Members' — sie gehoert dem extend-Block, nicht dem Zieltyp.
                    // Bei einer generischen Instanz gehoert die Methode der INSTANZ, nicht der
                    // Definition: 'ListIterator<int>.next' entsteht erst durch die
                    // Monomorphisierung, und die Definition hat keine lowerbare Fassung.
                    //
                    // Das fiel bis M8/S5 nicht auf, weil 'for-in' ueber 'ArrayIterator<T>' den
                    // DIREKTEN Pfad nimmt und die vtable nie befragt. Erst ein 'iter()', das
                    // einen Interface-Wert liefert, braucht sie.
                    var target = ResolveInInstance(typeTable, typeId, slots[i], instances)
                                 ?? Resolve(type, slots[i], ids)
                                 ?? ResolveInExtensions(viaExtension, slots[i], extensions)
                                 ?? Resolve(iface, slots[i], ids);
                    if (target is { } id) { methods[i] = id; continue; }

                    // Die Sema hat Konformanz bereits geprueft (LYR-SEM). Fehlt hier trotzdem
                    // etwas, ist es eine Lowering-Luecke — etwa eine generische oder rumpflose
                    // Implementierung, die Pass 1 uebersprungen hat.
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
    /// Ist das eine Coroutine, und was liefert sie? Der Typ steht syntaktisch da:
    /// <c>Coroutine&lt;T&gt;</c> ist ein eingebauter Typ (Sprache.md §8), keine Bibliotheksklasse,
    /// und v1 kennt keine anderen generischen Typen — eine Verwechslung ist damit ausgeschlossen.
    /// </summary>
    internal static TypeNode? CoroutineYield(FunctionDecl decl) =>
        decl.ReturnType is NamedType { TypeArguments.Length: 1 } named
        && named.Path[^1] == "Coroutine"
            ? named.TypeArguments[0]
            : null;

    /// <summary>Die sichtbaren <c>extend T :: [I]</c>-Bloecke, die genau diese Konformanz
    /// herstellen. Leer heisst: wenn ueberhaupt, dann ist sie deklariert.</summary>
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

    /// <summary>Die Methode einer generischen Instanz, ueber die Monomorphisierung angefordert.
    /// <c>null</c>, wenn der Typ nicht generisch ist oder die Methode nicht hat.</summary>
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

            // Fordert an, falls noch nicht geschehen — eine vtable-Zeile ist eine Benutzung.
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
