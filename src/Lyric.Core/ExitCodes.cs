namespace Lyric.Core;

/// <summary>
/// Die Prozess-Exit-Codes der Toolchain — normativ, nicht Geschmack.
///
/// <para><c>Sprache.md</c> §11 macht den Rueckgabewert von <c>main</c> zum Exit-Code, §9 macht
/// einen <c>panic</c> zum Abbruch, und der Runner-Vertrag in <c>docs/Bytecode.md</c> verlangt
/// dieselben Zahlen von <b>jeder</b> Runtime — auch von einer fremden. Sie liegen deshalb in
/// <c>Lyric.Core</c>, dem einzigen Projekt, das <c>lyrc</c>, <c>lyrvm</c> und <c>lyric</c>
/// gemeinsam haben (ADR-017). Eine Kopie je Binary waeren drei Wahrheiten darueber, was 101
/// bedeutet.</para>
///
/// <para>Bewusst in Kauf genommen: ein Programm, das selbst <c>return 101;</c> schreibt, ist von
/// einem <c>panic</c> nicht unterscheidbar. Das ist unvermeidbar, sobald man beides in einen
/// Byte-Kanal presst, und Rust lebt mit derselben Kollision.</para>
/// </summary>
public static class ExitCodes
{
    /// <summary>Alles gut.</summary>
    public const int Success = 0;

    /// <summary>Lade-, Validierungs-, Compile- oder IO-Fehler: das Programm lief nie an.</summary>
    public const int Failure = 1;

    /// <summary>Falscher Kommandozeilen-Aufruf. Getrennt von <see cref="Failure"/>, damit ein
    /// Skript „du hast mich falsch gerufen" von „deine Datei ist kaputt" unterscheiden kann.</summary>
    public const int Usage = 2;

    /// <summary>Ein <c>panic</c> (§9). Nicht 1, damit ein Skript einen Absturz von einem
    /// regulaeren <c>return 1;</c> unterscheiden kann — Rusts Konvention.</summary>
    public const int Panic = 101;
}
