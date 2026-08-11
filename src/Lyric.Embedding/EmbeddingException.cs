using Lyric.Core;

namespace Lyric.Embedding;

/// <summary>
/// Eine Uebersetzung, die nicht durchging.
///
/// <para><b>Die Diagnosen haengen als Daten daran, nicht als vorgerenderter Text.</b> Ein Host
/// will sie in seiner eigenen Oberflaeche zeigen — im Editor-Fenster einer Engine, in einer
/// Mod-Konsole, als JSON. Waere hier nur ein String, muesste er ihn zurueckparsen, und die
/// Positionen waeren verloren. <see cref="DiagnosticEngine.RenderText"/> steht ihm weiterhin
/// offen, wenn er genau das will.</para>
///
/// <para>Ein <c>panic</c> aus einem laufenden Skript ist <b>nicht</b> diese Ausnahme, sondern
/// weiterhin <c>LyricPanic</c> aus der Runtime: der Unterschied zwischen „das Skript ist kein
/// gueltiges Programm" und „das Skript hat seinen eigenen Vertrag gebrochen" ist genau der, den
/// §17.1 zieht, und er gehoert nicht eingeebnet.</para>
/// </summary>
public sealed class EmbeddingException : Exception
{
    internal EmbeddingException(string message, IReadOnlyList<Diagnostic> diagnostics)
        : base(message) => Diagnostics = diagnostics;

    /// <summary>Alles, was die Uebersetzung gemeldet hat — in derselben deterministischen
    /// Reihenfolge wie auf der Kommandozeile.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
}
