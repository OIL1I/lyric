using Lyric.Core;

namespace Lyric.Embedding;

/// <summary>
/// Was ein Host beim Erzeugen einer <see cref="LangVm"/> festlegt.
///
/// <para><b>Die Voreinstellung ist Sandbox</b> (<see cref="Capability.None"/>) — Doku §20.3 sagt
/// „Embed-Mode default-sandbox", und eine Voreinstellung, die mehr erlaubt als der Satz
/// verspricht, waere die gefaehrliche Richtung: ein Host, der versehentlich Dateizugriff bekommt,
/// merkt es nie, weil es ja funktioniert. Der umgekehrte Fehler meldet sich sofort mit einer
/// Diagnose, die sagt, welche Capability fehlt.</para>
/// </summary>
public sealed record HostOptions
{
    /// <summary>Was Skripte dieser VM duerfen. Voreinstellung: nichts.</summary>
    public Capability Capabilities { get; init; } = Capability.None;

    /// <summary>Wo die Stdlib liegt. <c>null</c> nimmt das Verzeichnis neben dem Binary.</summary>
    public string? StdlibRoot { get; init; }

    /// <summary>
    /// Wohin ein Skript schreibt.
    ///
    /// <para>Voreinstellung ist <see cref="TextWriter.Null"/> und nicht die Konsole: ein
    /// eingebettetes Skript gehoert dem Host, und in dessen Ausgabe zu schreiben ist eine
    /// Entscheidung, die er trifft — nicht eine, die er widerrufen muss.</para>
    /// </summary>
    public TextWriter? Output { get; init; }

    /// <inheritdoc cref="Output"/>
    public TextWriter? Error { get; init; }
}
