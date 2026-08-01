using Lyric.Core;

namespace Lyric.Ir.Lowering;

/// <summary>
/// Diagnostik-Codes des Lowerings (`LYR-IR####`, ROADMAP §Diagnostik-Code-Bereiche).
///
/// <para><b>Bewusst genau ein Code.</b> Die Versuchung wäre, pro fehlendem Konstrukt einen zu
/// vergeben („LYR-IR0002: Lambdas") — aber Codes sind stabile Bezeichner und die Lücken sind
/// vorübergehend. Ein Code, der verschwindet sobald Lambdas gelowert werden, war nie einer.
/// Stabil ist die <i>Kategorie</i>: „dieser Compiler-Stand kann das noch nicht". Welches Konstrukt
/// gemeint ist, steht in der Nachricht.</para>
///
/// <para><c>LYR-IR0002..0010</c> bleiben frei für echte, dauerhafte Lowering-Fehler — falls es sie
/// je gibt. Das meiste, was das Lowering ablehnen könnte, hat die Sema schon abgefangen.</para>
/// </summary>
internal static class LoweringDiagnostics
{
    /// <summary>Ein Konstrukt oder Typ, den dieser Compiler-Stand noch nicht lowern kann.</summary>
    public const string NotSupported = "LYR-IR0001";
}

/// <summary>
/// Signalisiert eine Scope-Grenze des Lowerings — <b>kein</b> Compiler-Bug, sondern gültiges
/// Lyric, für das der Backend-Teil noch fehlt. Trägt ihren Span mit, damit
/// <see cref="ModuleLowerer"/> daraus eine echte Diagnose mit Datei/Zeile/Spalte machen kann.
///
/// <para>Die Trennung ist der Punkt: <see cref="InternalCompilationException"/> heißt weiterhin
/// „der Compiler ist kaputt" und behält Stacktrace und Wurf-Semantik. Diese hier heißt „du hast
/// etwas geschrieben, das ich noch nicht übersetzen kann" und wird zu einer normalen
/// Fehlermeldung.</para>
/// </summary>
internal sealed class UnsupportedConstructException : Exception
{
    public Span Span { get; }

    public UnsupportedConstructException(string message, Span span) : base(message) => Span = span;
}
