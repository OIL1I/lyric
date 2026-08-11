using Lyric.Bytecode;
using Lyric.Core;

namespace Lyric.Vm;

/// <summary>
/// Ein geladenes, gebundenes und initialisiertes Modul — bereit, <b>mehrfach</b> aufgerufen zu
/// werden.
///
/// <para><b>Warum es das gibt.</b> <see cref="Interpreter.Run"/> beantwortet „fuehre dieses
/// Programm aus" und ist danach fertig. Ein Host beantwortet eine andere Frage: „rufe diese
/// Funktion, und dann nochmal" (M10/E2). Der Unterschied sind die <b>Globals</b> — der
/// Initialisierer laeuft einmal (Bytecode.md §Globals), und was er hinterlaesst, muss den Aufruf
/// ueberleben. Ein <c>Call</c>, das jedes Mal neu laedt, waere kein Aufruf, sondern ein
/// Programmstart mit anderem Namen.</para>
///
/// <para><b>Eine Instanz ist der Zustand.</b> Zwei <see cref="LoadedProgram"/>e desselben Moduls
/// teilen nichts — das ist die Eigenschaft, die einen Host mit mehreren Mods traegt, und die
/// <c>Reload</c> (E5) spaeter benutzt: neu laden heisst neue Instanz, und ADR-025s Zusage („der
/// Initialisierer laeuft neu") faellt dann von selbst heraus.</para>
/// </summary>
public sealed class LoadedProgram
{
    private readonly BytecodeModule _module;
    private readonly Interpreter.Prepared[] _prepared;
    private readonly DispatchTable _dispatch;
    private readonly NativeRegistry.BoundNative[] _natives;
    private readonly LyrValue[] _globals;

    private LoadedProgram(BytecodeModule module, Interpreter.Prepared[] prepared,
        DispatchTable dispatch, NativeRegistry.BoundNative[] natives, LyrValue[] globals)
    {
        _module = module;
        _prepared = prepared;
        _dispatch = dispatch;
        _natives = natives;
        _globals = globals;
    }

    /// <summary>Das Modul, aus dem diese Instanz stammt — fuer Namens- und Signatur-Lookups.
    /// </summary>
    public BytecodeModule Module => _module;

    /// <summary>
    /// Laedt, bindet, initialisiert.
    /// </summary>
    /// <exception cref="LyricRuntimeException">Eine Capability fehlt, oder ein Import laesst sich
    /// nicht binden.</exception>
    public static LoadedProgram Load(BytecodeModule module, NativeRegistry? natives = null,
        Capability granted = Capability.All)
    {
        // ZUERST, vor allem anderen: was diese VM nicht gewaehrt, laeuft hier gar nicht erst an.
        //
        // Die Pruefung gehoert hierher und nicht in den Compiler. ADR-007 nennt die Resolve-Zeit,
        // und dort gibt es die fruehe Meldung — aber ein '.lyrbc' kann von woanders kommen, und
        // ein Host, der fremden Bytecode laedt, hat den Compiler nie gesehen. Der Bedarf steht
        // deshalb IM Modul (ADR-013), und die Durchsetzung passiert beim Laden.
        var missing = module.Capabilities & ~(ulong)granted;
        if (missing != 0)
            throw new LyricRuntimeException(VmDiagnostics.CapabilityDenied,
                $"module requires capability '{CapabilityTable.Describe((Capability)missing)}', "
                + "which this runtime does not grant");

        var prepared = new Interpreter.Prepared[module.Functions.Count];
        for (var i = 0; i < prepared.Length; i++)
            prepared[i] = Interpreter.Prepared.From(module.Functions[i],
                module.Handlers.Where(h => h.Function == i).ToArray());

        // Bindung beim Laden: fehlt ein Native, wird das Modul abgelehnt, bevor eine
        // Instruktion laeuft.
        var bound = (natives ?? new NativeRegistry()).Bind(module);
        var dispatch = DispatchTable.Build(module);

        // Globale Slots. Ein String-Slot startet mit dem leeren String statt mit einer leeren
        // Referenz — dieselbe Regel wie bei Objektfeldern (§6.6): kein Wert ist je nicht da.
        var globals = new LyrValue[module.Globals.Count];
        for (var i = 0; i < globals.Length; i++)
            if (module.Globals[i].Tag == TypeTag.String) globals[i] = LyrValue.FromString(string.Empty);

        var program = new LoadedProgram(module, prepared, dispatch, bound, globals);

        // Die Init-Funktion laeuft VOR allem anderen (Bytecode.md §Globals) und genau EINMAL. Ihr
        // Ergebnis wird verworfen — sie ist void; was zaehlt, sind die Slots, die sie hinterlaesst.
        if (module.GlobalInit is { } init && init >= module.Imports.Count)
            program.Execute(init - module.Imports.Count);

        return program;
    }

    /// <summary>Hat dieses Modul einen Einstiegspunkt (§11)?</summary>
    public bool HasEntryPoint => _module.Start is not null;

    /// <summary>Fuehrt <c>main</c> aus und liefert seinen Rueckgabewert.</summary>
    /// <exception cref="LyricRuntimeException">Kein Einstiegspunkt.</exception>
    public LyrValue RunEntry(IReadOnlyList<string> arguments)
    {
        if (_module.Start is not { } start)
            throw new LyricRuntimeException(VmDiagnostics.NoEntryPoint,
                "module has no start section — it is a library, not a program");

        // Start indiziert den gemeinsamen Raum (erst Imports, dann Funktionen), '_prepared' nur die
        // definierten Funktionen — siehe Bytecode.md §Start (Id 7). Ein Einstieg im Import-Bereich
        // waere ein Modul, dessen main eine Host-Funktion ist; der Loader laesst das durch, die
        // Runtime kann es nicht ausfuehren.
        var entry = start - _module.Imports.Count;
        if (entry < 0)
            throw new LyricRuntimeException(VmDiagnostics.NoEntryPoint,
                $"start index {start} points into the import table — an entry point must be a "
                + "function defined in this module");

        // §11 kennt zwei Einstiegsformen. WELCHE vorliegt, steht in der Signatur — die
        // Funktionstabelle traegt sie ohnehin, also braucht die Start-Sektion dafuer kein Flag.
        //
        // Der Loader hat bereits geprueft, dass ein Parameter ein 'string[]' ist; hier wird nur
        // noch gezaehlt.
        LyrValue[] entryArgs = _module.Functions[entry].ParamCount == 0
            ? []
            : [Interpreter.ArgumentArray(arguments)];

        return Execute(entry, entryArgs);
    }

    /// <summary>
    /// Sucht eine definierte Funktion ueber ihren vollqualifizierten Namen
    /// (<c>&lt;modul&gt;.&lt;name&gt;</c>). <c>-1</c>, wenn es sie nicht gibt.
    ///
    /// <para>Der Name ist vollqualifiziert, weil die Funktionstabelle eines Moduls auch alles
    /// enthaelt, was aus der Stdlib mitgezogen wurde. Ein Lookup auf den blossen Namen fuende bei
    /// <c>length</c> ebenso gut <c>std.string.length</c> — und zwar je nach Reihenfolge mal so
    /// und mal so.</para>
    /// </summary>
    public int IndexOfFunction(string qualifiedName)
    {
        for (var i = 0; i < _module.Functions.Count; i++)
            if (string.Equals(_module.Functions[i].Name, qualifiedName, StringComparison.Ordinal))
                return i;
        return -1;
    }

    /// <summary>Fuehrt die Funktion an <paramref name="index"/> aus. Die Argumente landen in den
    /// Parameter-Slots; Aritaet und Typen pruefen die Aufrufer (der Host kennt sie aus der
    /// Funktionstabelle).</summary>
    public LyrValue Invoke(int index, params LyrValue[] arguments) => Execute(index, arguments);

    private LyrValue Execute(int index, LyrValue[]? arguments = null) =>
        Interpreter.Execute(_prepared, index, _module.Strings, _module.Types, _dispatch,
            _natives, _globals, arguments);
}
