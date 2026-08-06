namespace Lyric.Bytecode;

/// <summary>
/// Diagnostik-Codes des Bytecode-Lesers (`LYR-BC####`).
///
/// <para>Anders als bei <c>LYR-IR0001</c> gibt es hier <b>mehrere</b> Codes, und das ist kein
/// Widerspruch: die IR-Codes markieren vorübergehende Lücken im Backend, diese hier sind
/// dauerhafte Fehlerklassen einer Datei, die es so immer geben wird. „Falsches Magic" und
/// „Stack-Disziplin verletzt" verlangen vom Leser einer Fehlermeldung völlig Verschiedenes.</para>
///
/// <para>Alle beschreiben denselben Moment: <b>Load-Zeit</b>. ADR-013 verlangt, dass ein Modul beim
/// Laden vollständig validiert wird und danach ohne Sicherheitschecks laufen kann (WASM-Modell) —
/// jeder dieser Codes ist ein Grund, ein Modul gar nicht erst anzunehmen.</para>
/// </summary>
public static class BytecodeDiagnostics
{
    /// <summary>Die Datei beginnt nicht mit <c>LYRB</c> — kein .lyrbc.</summary>
    public const string BadMagic = "LYR-BC0001";

    /// <summary>Major-Version unbekannt. Bis v1.0 gibt es dafür keinen Migrationspfad (ADR-013).</summary>
    public const string UnsupportedVersion = "LYR-BC0002";

    /// <summary>Datei endet mitten in einer Struktur, oder eine Sektions-Länge passt nicht zu
    /// ihrem Inhalt.</summary>
    public const string Truncated = "LYR-BC0003";

    /// <summary>Ein Index zeigt ins Leere: String-Pool, Funktion, Block oder Local-Slot.</summary>
    public const string IndexOutOfRange = "LYR-BC0004";

    /// <summary>Unbekannter Opcode, Typ-Tag oder Sektions-Aufbau.</summary>
    public const string UnknownEncoding = "LYR-BC0005";

    /// <summary>Stack-Disziplin verletzt: Unterlauf, Tiefe ≠ 0 an einer Blockgrenze, oder mehr als
    /// die im Funktionskopf angekündigte Maximaltiefe.</summary>
    public const string StackDiscipline = "LYR-BC0006";
}

/// <summary>
/// Eine Datei, die kein gültiges Modul ist. Trägt den Diagnostik-Code mit, damit der öffentliche
/// Einstieg daraus eine Diagnose bauen kann, ohne den Text zu parsen.
///
/// <para>Bewusst keine <c>InternalCompilationException</c>: eine kaputte Datei ist kein
/// Compiler-Bug. Der Leser muss auf beliebigen Bytes robust sein — er ist die Stelle, an der
/// nicht vertrauenswürdige Eingaben ins System kommen.</para>
/// </summary>
public sealed class MalformedBytecodeException : Exception
{
    public string Code { get; }

    public MalformedBytecodeException(string code, string message) : base(message) => Code = code;
}
