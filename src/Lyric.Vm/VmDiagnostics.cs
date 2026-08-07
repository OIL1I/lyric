namespace Lyric.Vm;

/// <summary>
/// Laufzeit-Diagnostik (`LYR-VM####`).
///
/// <para>Zwei Klassen, und die Trennung ist die entlang von <c>Sprache.md</c> §9:
/// <b>Panics</b> sind Programmierfehler im laufenden Programm — nicht catchbar, mit Backtrace
/// (<see cref="LyricPanic"/>). <b>Ladefehler</b> treten auf, bevor die erste Instruktion läuft; sie
/// haben keinen Backtrace, weil es noch keinen Aufruf-Stack gibt
/// (<see cref="LyricRuntimeException"/>).</para>
///
/// <para>Bewusst <b>kein</b> dritter Fehlermechanismus neben <c>panic</c> und typisierten
/// Exceptions: Division durch Null ist ein Programmierfehler und damit ein Panic, kein
/// Sonderfall der VM (Regel 2, ein Mechanismus pro Konzept).</para>
/// </summary>
public static class VmDiagnostics
{
    /// <summary>Das Modul hat keine Start-Sektion — es ist eine Bibliothek, kein Programm.</summary>
    public const string NoEntryPoint = "LYR-VM0001";

    /// <summary>Ganzzahlige Division oder Restbildung durch Null. Panic: Fließkomma folgt IEEE
    /// (Inf/NaN) und ist kein Fehler.</summary>
    public const string DivisionByZero = "LYR-VM0002";

    /// <summary>Eine <c>unreachable</c>-Instruktion wurde ausgeführt. Der Compiler hat behauptet,
    /// dieser Punkt sei nicht erreichbar — ein Lowering-Bug, kein User-Fehler.</summary>
    public const string UnreachableExecuted = "LYR-VM0003";

    /// <summary>Aufruftiefe überschritten. In Lyric die Form, in der sich Endlos-Rekursion zeigt.</summary>
    public const string CallDepthExceeded = "LYR-VM0004";

    /// <summary>Das Modul verlangt Imports, aber die Runtime bindet noch keine.</summary>
    public const string ImportsNotBound = "LYR-VM0005";

    /// <summary>Das Modul verlangt eine Capability, die diese VM nicht gewaehrt (ADR-007).
    ///
    /// <para>Der Code liegt im CAP-Bereich und nicht bei den VM-Fehlern: er beschreibt keine
    /// kaputte Datei, sondern eine Richtlinienentscheidung des Hosts. Dasselbe Modul laeuft
    /// anderswo einwandfrei.</para></summary>
    public const string CapabilityDenied = "LYR-CAP0001";

    /// <summary>Element-Index außerhalb der Array-Grenzen. Anders als Typ- und Feldindizes ist er
    /// ein Laufzeitwert und beim Laden nicht prüfbar (ADR-016) — also ein <c>panic</c> (§9).</summary>
    public const string IndexOutOfRange = "LYR-VM0006";

    /// <summary>Force-Unwrap (<c>expr!</c>) auf einem <c>?T</c> ohne Wert (Sprache.md §7).</summary>
    public const string NullDereference = "LYR-VM0007";

    /// <summary><c>enumas</c> auf eine Variante, die der Wert nicht ist. Der Compiler beweist das
    /// über <c>match</c>; die Prüfung bleibt, weil ein <c>.lyrbc</c> auch aus fremder Quelle
    /// kommen kann.</summary>
    public const string WrongVariant = "LYR-VM0008";

    /// <summary>Kein vtable-Eintrag fuer (konkreter Typ, Interface, Slot). Im regulaeren Weg
    /// unerreichbar — der Loader prueft jedes <c>mkiface</c> gegen die Impls-Sektion (ADR-013).
    /// Erreichbar nur, wenn ein Host ein Modul unter Umgehung des Readers zusammensetzt.</summary>
    public const string NoImplementation = "LYR-VM0009";

    /// <summary>Eine Exception hat den Einstiegspunkt verlassen, ohne gefangen zu werden.
    ///
    /// <para>Statisch sollte das nicht vorkommen: die Sema erzwingt, dass ein Aufruf einer
    /// <c>throws</c>-Funktion entweder selbst deklariert wird oder von einem <c>try</c> umgeben
    /// ist, und <c>main</c> darf laut Entry-Contract (§11) nichts deklarieren. Erreichbar bleibt
    /// es ueber ein von Hand gebautes Modul — und dann ist es ein Abbruch wie ein panic.</para>
    /// </summary>
    public const string UncaughtException = "LYR-VM0010";

    /// <summary>Ein <c>panic(msg)</c> aus dem Programm (Sprache.md §9). Nicht catchbar; die
    /// Meldung ist die des Aufrufers.</summary>
    public const string Panicked = "LYR-VM0011";
}

/// <summary>
/// Ein <c>panic</c> (Sprache.md §9): das Programm hat einen Vertrag gebrochen und läuft nicht
/// weiter. Nicht mit <c>try</c>/<c>catch</c> abfangbar — dafür gibt es typisierte Exceptions.
///
/// <para>Trägt den Lyric-Aufruf-Stack mit. Der wird beim Verlassen der Interpreter-Schleife
/// angehängt, weil nur dort die Frames bekannt sind; die Rechenoperation selbst weiß nichts von
/// ihrem Aufrufer.</para>
/// </summary>
public sealed class LyricPanic : Exception
{
    public string Code { get; }

    /// <summary>Funktionsnamen vom Ort des Panics aufwärts. Leer, solange der Panic die
    /// Interpreter-Schleife noch nicht verlassen hat.</summary>
    public IReadOnlyList<string> CallStack { get; init; } = Array.Empty<string>();

    public LyricPanic(string code, string message) : base(message) => Code = code;

    public LyricPanic WithCallStack(IReadOnlyList<string> callStack) =>
        new(Code, Message) { CallStack = callStack };
}

/// <summary>Das Modul kann gar nicht erst gestartet werden — kein Einstiegspunkt, ungebundene
/// Imports. Kein Panic: es läuft noch nichts, was abstürzen könnte.</summary>
public sealed class LyricRuntimeException : Exception
{
    public string Code { get; }

    public LyricRuntimeException(string code, string message) : base(message) => Code = code;
}
