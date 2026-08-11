using Lyric.Bytecode;
using Lyric.Compiler;
using Lyric.Core;
using Lyric.Vm;

namespace Lyric.Embedding;

/// <summary>
/// Eine Lyric-Laufzeit unter der Kontrolle eines .NET-Hosts (M10, ADR-007).
///
/// <para><b>Der Host entscheidet, was ein Skript darf.</b> Das ist der ganze Unterschied zum
/// Standalone-Modus, wo <c>lyric run</c> alles gewaehrt — dort laeuft das eigene Programm des
/// Nutzers, und eine Trust-Boundary gaebe es zwischen ihm und sich selbst. Hier gibt es eine, und
/// sie liegt genau hier.</para>
///
/// <para><b>Warum diese Klasse beide Bibliotheken braucht.</b> <see cref="Compile"/> ist Frontend,
/// <see cref="Run"/> ist Runtime, und der Zustand lebt dazwischen — dieselbe Anforderung, die
/// <c>lyrrepl</c> zum ersten Binary mit beiden Seiten gemacht hat (ADR-021). Deshalb ist
/// <c>lyrembed</c> eine eigene Assembly und nicht Teil von <c>lyrrt</c>: die Runtime kennt das
/// Format nur ueber die Leseseite, und das muss so bleiben, sonst ist ADR-013 („jemand schreibt
/// eine zweite Runtime allein aus der Spec") nur noch eine Behauptung.</para>
///
/// <para><b>Eine Instanz ist eine Sandbox.</b> Zwei VMs im selben Prozess teilen nichts — weder
/// Capabilities noch Registry noch geladene Module. Ein Host mit mehreren Mods gibt jedem seine
/// eigene; dass das wirklich so ist, haelt ein Test fest.</para>
///
/// <para>Was E1 <b>nicht</b> kann: Host-Funktionen (E3), Host-Typen (E4), eine Funktion aus dem
/// Skript rufen (E2), Hot-Reload (E5). Ein Skript kann hier nur, was die Stdlib ihm erlaubt — und
/// die Voreinstellung gewaehrt davon nichts, was eine Capability kostet.</para>
/// </summary>
public sealed class LangVm
{
    /// <summary>Der Modulname, unter dem registrierte Host-Funktionen sichtbar sind. Ein Skript
    /// schreibt <c>import host;</c> oder <c>import host { playSound };</c>.</summary>
    public const string HostModule = "host";

    private readonly HostOptions _options;
    private readonly NativeRegistry _natives;
    private readonly Dictionary<string, HostFunction> _hostFunctions = new(StringComparer.Ordinal);

    /// <param name="options">Voreinstellung: <b>Sandbox</b> — keine Capability, keine Ausgabe.
    /// Siehe <see cref="HostOptions"/>.</param>
    public LangVm(HostOptions? options = null)
    {
        _options = options ?? new HostOptions();
        _natives = NativeRegistry.CreateDefault(
            _options.Output ?? TextWriter.Null,
            _options.Error ?? TextWriter.Null);
    }

    /// <summary>Was Skripte dieser VM duerfen.</summary>
    public Capability Capabilities => _options.Capabilities;

    /// <summary>
    /// Macht eine .NET-Funktion fuer Skripte dieser VM sichtbar — als <c>host.&lt;name&gt;</c>.
    ///
    /// <para><b>Vor dem Uebersetzen aufzurufen.</b> Die Signatur wird aus dem Delegaten abgeleitet
    /// und als Deklaration in ein synthetisches Modul <c>host</c> geschrieben; der Compiler sieht
    /// sie dort wie jede Stdlib-Deklaration. Wer nach dem <see cref="Compile"/> registriert, hat
    /// ein Skript uebersetzt, das den Namen noch nicht kannte.</para>
    ///
    /// <para>Das Skript muss <c>host</c> <b>importieren</b>. §2.2 kennt keinen impliziten
    /// Namensraum, und einen dafuer einzufuehren waere ein Sonderweg fuer genau eine Sorte
    /// Funktion — <c>Doku.md</c> §21 zeigte das bis heute anders, und das war nie baubar.</para>
    /// </summary>
    /// <exception cref="ArgumentException">Ein Parameter- oder Rueckgabetyp kann die Grenze nicht
    /// ueberqueren, oder der Name ist schon vergeben.</exception>
    public void RegisterFunction(string name, Delegate implementation)
    {
        var function = HostFunction.From(name, implementation);

        // Kein stilles Ueberschreiben. Zwei Registrierungen desselben Namens sind ein Fehler im
        // Host, und welche gewinnt, waere sonst eine Frage der Reihenfolge.
        if (!_hostFunctions.TryAdd(name, function))
            throw new ArgumentException(
                $"a host function named '{name}' is already registered", nameof(name));

        var qualified = $"{HostModule}.{name}";
        if (function.ReturnsValue)
            _natives.Register(qualified, function.ParameterTags, function.ReturnTag,
                function.Bridge);
        else
            _natives.Register(qualified, function.ParameterTags, TypeTag.Void, function.Bridge);
    }

    /// <summary>Der Quelltext des synthetischen <c>host</c>-Moduls — <c>null</c>, solange nichts
    /// registriert ist.
    ///
    /// <para>Oeffentlich, weil er die beste Antwort auf „welche Signatur hat meine Funktion in
    /// Lyric?" ist: er steht als Lyric-Code da und ist genau das, wogegen das Skript uebersetzt.
    /// </para></summary>
    public string? HostModuleSource
    {
        get
        {
            if (_hostFunctions.Count == 0) return null;

            // Sortiert und nicht in Registrierungsreihenfolge: derselbe Satz Funktionen ergibt
            // denselben Quelltext, also dieselben Bytes. ADR-013 verlangt reproduzierbare
            // Ausgabe, und ein Modul, dessen Inhalt von einer Aufrufreihenfolge abhaengt, waere
            // die eine Stelle, an der das nicht mehr gilt.
            var declarations = string.Join(Environment.NewLine, _hostFunctions.Values
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .Select(f => f.Declaration));

            return $"""
                // Erzeugt von LangVm.RegisterFunction — was der Host diesem Skript anbietet.
                module {HostModule};

                {declarations}
                """;
        }
    }

    /// <summary>
    /// Uebersetzt Quelltext aus dem Speicher.
    /// </summary>
    /// <param name="moduleName">Der Modulname. <b>Pflicht</b>, weil §2.1 ihn sonst aus dem
    /// Dateipfad ableitete und es hier keinen gibt. Zwei Skripte unter demselben Namen
    /// kollidierten still; ob zwei Mods dasselbe Modul sind, weiss nur der Host.</param>
    /// <exception cref="EmbeddingException">Die Uebersetzung hat Fehler gemeldet.</exception>
    public ScriptModule Compile(string source, string moduleName) =>
        Build(ScriptSource.FromText(moduleName, source), moduleName);

    /// <summary>Uebersetzt eine Datei. Der Modulname folgt dem Pfad (§2.1).</summary>
    /// <inheritdoc cref="Compile(string, string)"/>
    public ScriptModule CompileFile(string path) =>
        Build(ScriptSource.FromDisk(path), Path.GetFileNameWithoutExtension(path));

    /// <summary>
    /// Fuehrt das <c>main</c> des Moduls aus und liefert seinen Exit-Code (§11).
    ///
    /// <para>Die Capability-Pruefung passiert <b>hier</b> und nicht beim Uebersetzen: der Bedarf
    /// steht im Modul (ADR-013), und ein Host, der fremde Bytes laedt, hat den Compiler nie
    /// gesehen. <see cref="Compile"/> meldet denselben Mangel frueher und freundlicher — aber
    /// verlassen muss man sich auf diese Stelle.</para>
    /// </summary>
    /// <exception cref="ScriptException">Kein Einstiegspunkt, oder eine Capability fehlt.
    /// </exception>
    /// <exception cref="ScriptPanicException">Das Skript hat seinen eigenen Vertrag gebrochen
    /// (§17.1).</exception>
    public int Run(ScriptModule module, params string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(module);
        try
        {
            return (int)Interpreter
                .Run(module.Loaded, arguments, _natives, _options.Capabilities)
                .AsI64;
        }
        catch (LyricPanic panic)
        {
            // Uebersetzt an der Host-Grenze, nicht durchgereicht: 'LyricPanic' lebt in lyrrt, und
            // ein Host referenziert lyrembed. Siehe ScriptException.
            throw new ScriptPanicException(panic.Code, panic.Message, panic);
        }
        catch (LyricRuntimeException runtime)
        {
            throw new ScriptException(runtime.Code, runtime.Message, runtime);
        }
    }

    /// <summary>Uebersetzt und fuehrt aus — der bequeme Weg fuer ein Skript, das nur einmal
    /// laeuft.</summary>
    /// <inheritdoc cref="Run(ScriptModule, string[])"/>
    public int RunScript(string path, params string[] arguments) =>
        Run(CompileFile(path), arguments);

    /// <summary>
    /// Laedt ein Modul und laesst seinen Konstanten-Initialisierer laufen — die Form, aus der ein
    /// Host <b>Funktionen ruft</b> (E2).
    ///
    /// <para>Getrennt von <see cref="Run"/>, weil die beiden verschiedene Fragen beantworten:
    /// <c>Run</c> fuehrt ein Programm einmal aus, eine Instanz lebt weiter. Der Unterschied sind
    /// die Modul-Konstanten — sie werden einmal berechnet, und jeder Aufruf danach sieht
    /// denselben Stand.</para>
    ///
    /// <para>Ein Modul <b>ohne</b> Einstiegspunkt ist hier der Normalfall und kein Fehler: genau
    /// dafuer ist die Start-Sektion optional.</para>
    /// </summary>
    /// <exception cref="ScriptException">Eine Capability fehlt, oder ein Import laesst sich nicht
    /// binden.</exception>
    public ScriptInstance Instantiate(ScriptModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        try
        {
            return new ScriptInstance(module,
                LoadedProgram.Load(module.Loaded, _natives, _options.Capabilities));
        }
        catch (LyricPanic panic)
        {
            // Der Konstanten-Initialisierer laeuft beim Laden und ist gewoehnlicher Lyric-Code —
            // er kann panicken wie jeder andere.
            throw new ScriptPanicException(panic.Code, panic.Message, panic);
        }
        catch (LyricRuntimeException runtime)
        {
            throw new ScriptException(runtime.Code, runtime.Message, runtime);
        }
    }

    private ScriptModule Build(ScriptSource source, string name)
    {
        var result = SourceCompiler.Compile(source, new CompilerOptions
        {
            StdlibRoot = _options.StdlibRoot,
            NativeModules = HostModuleSource is { } host
                ? new Dictionary<string, string>(StringComparer.Ordinal) { [HostModule] = host }
                : null,
        });

        // 'Ok' und nicht 'Bytes is not null': eine Uebersetzung kann Bytes liefern UND Fehler
        // gemeldet haben, und in dem Fall sind die Bytes Raten.
        if (!result.Ok || result.Bytes is null)
            throw new EmbeddingException(
                $"'{name}' did not compile ({result.Diagnostics.ErrorCount} error(s))",
                result.Diagnostics.Diagnostics);

        // Beim Uebersetzen bereits laden und validieren, nicht erst beim Ausfuehren. Ein Host, der
        // zehn Mods laedt und den elften nie startet, soll trotzdem beim zehnten erfahren, dass er
        // kaputt ist — und nicht mitten im Spiel.
        return new ScriptModule(name, result.Bytes, BytecodeReader.ReadOrThrow(result.Bytes));
    }
}
