namespace Lyric.Embedding;

/// <summary>
/// Ein Skript ist zur Laufzeit gescheitert.
///
/// <para><b>Warum die Embedding-API eigene Ausnahmen hat.</b> Die Runtime wirft
/// <c>LyricRuntimeException</c> und <c>LyricPanic</c>, und beide leben in <c>lyrrt</c> — einer
/// Assembly, die ein Host <b>nicht referenziert</b>. Er bekaeme also Ausnahmen, die er nicht
/// benennen und damit nicht gezielt fangen kann; uebrig bliebe <c>catch (Exception)</c>. Eine
/// Oberflaeche, deren Fehler man nur pauschal fangen kann, ist keine.</para>
///
/// <para>Aufgefallen beim Schreiben des ersten Tests — und zwar deshalb, weil das Testprojekt
/// bewusst <b>nur</b> <c>lyrembed</c> referenziert. Haette es lyrrt mitgenommen, waere der Test
/// gruen gewesen und die Luecke unsichtbar geblieben.</para>
///
/// <para>Die urspruengliche Ausnahme haengt als <see cref="Exception.InnerException"/> daran;
/// nichts geht verloren.</para>
/// </summary>
public class ScriptException : Exception
{
    internal ScriptException(string code, string message, Exception? inner)
        : base(message, inner) => Code = code;

    /// <summary>Der Diagnose-Code (<c>LYR-VM####</c>), stabil ueber Versionen hinweg.</summary>
    public string Code { get; }
}

/// <summary>
/// Das Skript hat seinen eigenen Vertrag gebrochen: Division durch Null, ein Force-Unwrap auf
/// <c>null</c>, ein ausdrueckliches <c>panic</c> (§9, §17.1).
///
/// <para>Getrennt von <see cref="ScriptException"/>, weil die Unterscheidung fuer den Host zaehlt:
/// „das Modul darf das nicht" ist eine Konfigurationsfrage und beantwortbar, „das Skript hat einen
/// Bug" ist eine Meldung an dessen Autor. Sie einzuebnen hiesse, die Linie aus §17.1 an der
/// Host-Grenze wieder aufzugeben.</para>
/// </summary>
public sealed class ScriptPanicException : ScriptException
{
    internal ScriptPanicException(string code, string message, Exception? inner)
        : base(code, message, inner) { }
}
