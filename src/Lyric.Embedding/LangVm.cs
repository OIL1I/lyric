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
    private readonly HostOptions _options;
    private readonly NativeRegistry _natives;

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
