namespace Lyric.Core;

/// <summary>
/// Was ein <c>char</c> sein darf (Sprache.md §4, ADR-022).
///
/// <para><b>Warum das hier liegt und nicht dort, wo es gebraucht wird.</b> Die Regel wird an zwei
/// weit auseinanderliegenden Stellen gebraucht: die Sema prüft ein <c>char</c>-Literal beim
/// Übersetzen, die VM prüft jedes gerechnete Ergebnis beim Ausführen. Beide gehören in
/// verschiedene Assemblies und dürfen einander nicht kennen — <c>Lyric.Core</c> ist die einzige,
/// die beide sehen (ADR-017).</para>
///
/// <para>Zwei Kopien derselben Zahlengrenze wären genau der Fehler, der in diesem Projekt schon
/// viermal aufgetreten ist: eine Frage, zwei Antworten, und irgendwann driften sie. Hier würde die
/// Drift bedeuten, dass ein Literal übersetzt, das die VM nicht erzeugen darf.</para>
/// </summary>
public static class Unicode
{
    /// <summary>Der größte gültige Codepoint.</summary>
    public const long MaxCodepoint = 0x10FFFF;

    /// <summary>Erstes und letztes Surrogat. Sie stehen für die Hälften eines UTF-16-Paares und
    /// sind für sich genommen kein Zeichen — deshalb auch kein gültiger <c>char</c>.</summary>
    public const long FirstSurrogate = 0xD800;
    public const long LastSurrogate = 0xDFFF;

    /// <summary>
    /// Ist das ein Unicode-Codepoint?
    /// </summary>
    /// <remarks>
    /// Nimmt <c>long</c> und nicht <c>uint</c>, damit ein negatives Zwischenergebnis
    /// (<c>'a' - 1000</c>) als solches auffällt statt als riesige vorzeichenlose Zahl
    /// durchzurutschen.
    /// </remarks>
    public static bool IsCodepoint(long value) =>
        value is >= 0 and <= MaxCodepoint
        && value is < FirstSurrogate or > LastSurrogate;

    /// <summary>Die Erklärung, warum ein Wert keiner ist — für Diagnose und Panic-Meldung, damit
    /// beide dieselbe Sprache sprechen.</summary>
    public static string DescribeRange() =>
        $"valid: 0..0x{MaxCodepoint:X}, excluding the surrogate range " +
        $"0x{FirstSurrogate:X}..0x{LastSurrogate:X}";
}
