using Lyric.Bytecode;
using Lyric.Core;

namespace Lyric.Vm;

/// <summary>
/// Der vollstaendige Weg von <c>.lyrbc</c>-Bytes zu einem Prozess-Exit-Code: laden, validieren,
/// ausfuehren, Panics und Laufzeitfehler rendern.
///
/// <para>Liegt bewusst <b>hier</b> und nicht in einem CLI-Projekt (ADR-017). Die Exit-Code-Regel
/// ist normativ — <c>Sprache.md</c> §11 macht den Rueckgabewert von <c>main</c> zum Exit-Code,
/// §9 macht einen <c>panic</c> zum Abbruch — und der Runner-Vertrag in <c>docs/Bytecode.md</c>
/// verlangt sie identisch von <i>jeder</i> Runtime. Eine normative Regel gehoert in die
/// Referenz-Runtime, nicht in einen von drei Kommandozeilen-Wrappern; saesse sie im CLI, haetten
/// <c>lyrvm</c> und <c>lyric</c> je eine eigene Kopie und damit zwei Wahrheiten ueber „was heisst
/// Exit-Code 101".</para>
/// </summary>
public static class VmHost
{
    /// <summary>
    /// Fuehrt ein bereits geladenes Modul aus und liefert den Prozess-Exit-Code.
    ///
    /// <para><paramref name="output"/> traegt ausschliesslich Programmausgabe,
    /// <paramref name="error"/> ausschliesslich Diagnosen und Backtraces — der Runner-Vertrag
    /// verbietet die Vermischung, weil ein aufrufendes Werkzeug sonst die Ausgabe eines
    /// Lyric-Programms nicht von der Klage der Runtime trennen kann.</para>
    /// </summary>
    public static int Execute(BytecodeModule module, TextWriter output, TextWriter error) =>
        Execute(module, [], output, error);

    /// <param name="arguments">Die Programm-Argumente aus dem Runner-Vertrag (Bytecode.md §9) —
    /// alles nach dem ersten <c>--</c>.</param>
    public static int Execute(BytecodeModule module, IReadOnlyList<string> arguments,
        TextWriter output, TextWriter error)
    {
        try
        {
            var natives = NativeRegistry.CreateDefault(output, error);

            // §11: Exit-Code ist 0..255. Wie jedes POSIX-System nehmen wir das niedrigste Byte.
            return (int)(Interpreter.Run(module, arguments, natives).AsI64 & 0xFF);
        }
        catch (LyricPanic panic)
        {
            // §9: ein panic druckt einen Backtrace und beendet die VM. Nicht catchbar.
            error.WriteLine($"panic [{panic.Code}]: {panic.Message}");
            foreach (var frame in panic.CallStack) error.WriteLine($"    in {frame}");
            return ExitCodes.Panic;
        }
        catch (LyricRuntimeException ex)
        {
            var engine = new DiagnosticEngine(new SourceManager());
            engine.Report(ex.Code, Severity.Error, default, ex.Message);
            engine.RenderText(error);
            return ExitCodes.Failure;
        }
    }

    /// <summary>
    /// Laedt Bytes und fuehrt sie aus. <c>null</c> als Lade-Ergebnis heisst: die Validierung hat
    /// abgelehnt (ADR-013 prueft beim Laden, nicht beim Aufruf), die Diagnosen stehen dann schon
    /// auf <paramref name="error"/>.
    /// </summary>
    public static int Execute(byte[] bytes, TextWriter output, TextWriter error)
    {
        var module = Load(bytes, error);
        return module is null ? ExitCodes.Failure : Execute(module, output, error);
    }

    /// <summary>
    /// Liest und validiert Bytes vollstaendig, ohne auszufuehren — die Grundlage von
    /// <c>lyrvm verify</c> und <c>lyrvm disasm</c>. Rendert eigene Diagnosen und liefert
    /// <c>null</c>, wenn das Modul abgelehnt wurde.
    /// </summary>
    public static BytecodeModule? Load(byte[] bytes, TextWriter error)
    {
        var engine = new DiagnosticEngine(new SourceManager());
        var module = BytecodeReader.Read(bytes, engine);
        engine.RenderText(error);
        return module;
    }

    /// <summary>
    /// Alles, was diese Runtime beim Laden prueft, aber ohne eine Instruktion auszufuehren:
    /// Format-Validierung <b>und</b> Import-Bindung.
    ///
    /// <para>Die Import-Bindung gehoert dazu, weil sie sonst nirgends sichtbar wird: sie laeuft
    /// heute erst am Anfang von <see cref="Interpreter.Run"/>. Ein Modul, das ein unbekanntes
    /// Native importiert, ist aber schon vor dem Start ungueltig (ADR-013) — und genau das ist
    /// die Frage, die eine Fremd-Runtime an ihre eigene Konformanz stellen muss.</para>
    /// </summary>
    public static int Verify(byte[] bytes, TextWriter output, TextWriter error)
    {
        var module = Load(bytes, error);
        if (module is null) return ExitCodes.Failure;

        try
        {
            NativeRegistry.CreateDefault(output, error).Bind(module);
        }
        catch (LyricRuntimeException ex)
        {
            var engine = new DiagnosticEngine(new SourceManager());
            engine.Report(ex.Code, Severity.Error, default, ex.Message);
            engine.RenderText(error);
            return ExitCodes.Failure;
        }

        return ExitCodes.Success;
    }
}
