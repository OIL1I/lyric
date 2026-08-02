namespace Lyric.Vm;

/// <summary>
/// Laufzeit-Diagnostik (`LYR-VM####`).
///
/// <para>Zwei Klassen, die man auseinanderhalten muss: <b>User-Fehler</b> (Division durch Null,
/// zu tiefe Rekursion) sind echte Programmfehler und werden gemeldet. <b>Compiler-Bugs</b>
/// (<c>unreachable</c> erreicht) sind hier ebenfalls Meldungen statt Abstürze — die Runtime kennt
/// den Unterschied nicht mehr, weil beim Laden validiert wurde und sie danach vertraut.</para>
/// </summary>
public static class VmDiagnostics
{
    /// <summary>Das Modul hat keine Start-Sektion — es ist eine Bibliothek, kein Programm.</summary>
    public const string NoEntryPoint = "LYR-VM0001";

    /// <summary>Ganzzahlige Division oder Restbildung durch Null.</summary>
    public const string DivisionByZero = "LYR-VM0002";

    /// <summary>Eine <c>unreachable</c>-Instruktion wurde ausgeführt. Der Compiler hat behauptet,
    /// dieser Punkt sei nicht erreichbar — ein Lowering-Bug, kein User-Fehler.</summary>
    public const string UnreachableExecuted = "LYR-VM0003";

    /// <summary>Aufruftiefe überschritten. In Lyric die Form, in der sich Endlos-Rekursion zeigt.</summary>
    public const string CallDepthExceeded = "LYR-VM0004";

    /// <summary>Das Modul verlangt Imports, aber die Runtime bindet noch keine.</summary>
    public const string ImportsNotBound = "LYR-VM0005";
}

/// <summary>Bricht die Ausführung ab. Trägt den Code mit, damit die Aufrufstelle eine Diagnose
/// bauen kann, ohne den Text zu parsen — dasselbe Muster wie
/// <c>MalformedBytecodeException</c>.</summary>
public sealed class LyricRuntimeException : Exception
{
    public string Code { get; }

    public LyricRuntimeException(string code, string message) : base(message) => Code = code;
}
