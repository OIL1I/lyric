namespace Lyric.Core;

/// <summary>
/// Die Schritte der Pipeline aus <c>ROADMAP.md</c> §Pipeline, so wie sie ein Nutzer sieht.
///
/// <para>Bewusst <b>nicht</b> feiner: die Phasengrenzen liegen genau dort, wo
/// <c>SourceCompiler</c> die Bibliotheken ohnehin nacheinander aufruft. Eine feinere Granularitaet
/// muesste einen Fortschritts-Begriff durch Lexer, Parser, Resolver und Sema reichen — ein
/// Querschnittsbelang quer durch sechs Bibliotheken, fuer eine Anzeige.</para>
/// </summary>
public enum Phase
{
    /// <summary>Quelldatei von der Platte lesen.</summary>
    Read,

    /// <summary>Tokenisieren und parsen.</summary>
    Parse,

    /// <summary>Importierte Module nachladen (Stdlib, spaeter auch User-Module).</summary>
    Load,

    /// <summary>Namen aufloesen, Symboltabellen bauen.</summary>
    Resolve,

    /// <summary>Typpruefung.</summary>
    Check,

    /// <summary>AST → Mid-IR.</summary>
    Lower,

    /// <summary>IR-Invarianten pruefen. Eigene Phase, weil <c>STATUS.md</c> seit M5 behauptet, das
    /// sei ~90 % der Lowering-Zeit — eine Handmessung, die seither nie nachgeprueft wurde. Ab
    /// jetzt steht die Zahl bei jedem <c>--verbose</c>-Lauf da.</summary>
    Verify,

    /// <summary>IR → <c>.lyrbc</c>-Bytes.</summary>
    Emit,
}

/// <summary>
/// Welche Phasen <b>dieser Build</b> wirklich durchlaeuft.
///
/// <para>Die Liste ist keine Konstante: der Verifier laeuft nur in Debug-Builds (Vorbild ist
/// LLVMs Verifier in Assert-Builds, Begruendung bei <c>ModuleLowerer.VerifyByDefault</c>). Sie
/// steht hier und nicht im Frontend, weil auch die Werkzeug-Tests sie brauchen — sie fahren die
/// Binaries als Prozesse und referenzieren das Frontend bewusst nicht.</para>
///
/// <para>Zweimal hingeschrieben war sie schon: der Test der <c>--verbose</c>-Tabelle trug die
/// Phasenliste als Literal und war deshalb in Release rot, waehrend Debug gruen blieb. Dieselbe
/// Lehre wie bei <see cref="Unicode"/> — eine Regel, die zwei Stellen kennen muessen, gehoert an
/// die eine, die beide sehen.</para>
/// </summary>
public static class Pipeline
{
    /// <summary>Prueft dieser Build die IR-Invarianten nach dem Lowering?</summary>
    public static bool VerifiesIr =>
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>Die Phasen in Pipeline-Reihenfolge, ohne die, die dieser Build ueberspringt.</summary>
    public static IReadOnlyList<Phase> OfThisBuild { get; } =
        Enum.GetValues<Phase>()
            .Where(phase => phase != Phase.Verify || VerifiesIr)
            .ToArray();
}

/// <summary>Wie eine Phase in der Ausgabe heisst.</summary>
public static class PhaseNames
{
    /// <summary>Kurzform fuer die Zeittabelle (kleingeschrieben, wie ein Kommando).</summary>
    public static string Short(Phase phase) => phase switch
    {
        Phase.Read => "read",
        Phase.Parse => "parse",
        Phase.Load => "load",
        Phase.Resolve => "resolve",
        Phase.Check => "check",
        Phase.Lower => "lower",
        Phase.Verify => "verify",
        Phase.Emit => "emit",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "unhandled phase"),
    };

    /// <summary>Verlaufsform fuer die Live-Zeile („was tut der Compiler gerade").</summary>
    public static string Progressive(Phase phase) => phase switch
    {
        Phase.Read => "Reading",
        Phase.Parse => "Parsing",
        Phase.Load => "Loading",
        Phase.Resolve => "Resolving",
        Phase.Check => "Checking",
        Phase.Lower => "Lowering",
        Phase.Verify => "Verifying",
        Phase.Emit => "Emitting",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "unhandled phase"),
    };
}
