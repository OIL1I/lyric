using System.Diagnostics;
using System.Globalization;

namespace Lyric.Core;

/// <summary>
/// Der <b>einzige</b> Schreiber auf stderr, solange ein Kommando laeuft.
///
/// <para>Warum ein Besitzer und nicht zwei: steht eine Fortschrittszeile auf dem Schirm und
/// rendert jemand parallel eine Diagnose, schreibt der Fehler mitten in die Zeile. Zwei
/// unabhaengige Schreiber auf demselben Strom erzeugen genau diesen Salat, und zwar nur manchmal
/// - also die Sorte Fehler, die man erst beim Vorfuehren sieht. Deshalb geht auch die
/// Diagnose-Ausgabe hier durch: sie loescht die Zeile, bevor sie schreibt.
/// </para>
///
/// <para>Fortschritt geht <b>nie</b> auf stdout. Dort steht die Ausgabe des Lyric-Programms
/// (Runner-Vertrag §9.3) oder ein angeforderter Dump; beides muss maschinell lesbar bleiben.</para>
///
/// <para>Die Anzeige ist reines ASCII. Eine Windows-Konsole mit anderer Codepage macht aus
/// huebschen Glyphen Muell, und die Zeile steht ohnehin nur Sekundenbruchteile.</para>
/// </summary>
public sealed class TerminalOutput : IDisposable
{
    /// <summary>Loescht ab Cursor bis Zeilenende (ANSI EL). Als Konstante, damit die
    /// Escape-Sequenz genau einmal im Quelltext steht.</summary>
    private const string EraseToEndOfLine = "\u001b[K";

    /// <summary>
    /// So lange darf ein Lauf dauern, ohne dass etwas angezeigt wird.
    ///
    /// <para>Ohne diese Schwelle blitzt die Zeile bei einem 40-Zeilen-Programm kurz auf und ist
    /// wieder weg - Flimmern statt Information. Mit ihr verhaelt sich das Werkzeug im schnellen
    /// Fall wie <c>go build</c>: es sagt nichts, weil es nichts zu sagen gab. Cargo macht es
    /// genauso.</para>
    /// </summary>
    public static readonly TimeSpan DisplayThreshold = TimeSpan.FromMilliseconds(120);

    private readonly TextWriter _out;
    private readonly TextWriter _error;
    private readonly ToolOptions _options;
    private readonly bool _animate;
    private readonly Stopwatch _total = Stopwatch.StartNew();
    private readonly List<(Phase Phase, string Detail, TimeSpan Elapsed)> _timings = [];

    private Phase? _current;
    private string _currentDetail = "";
    private long _currentStartTicks;
    private bool _lineOnScreen;

    /// <param name="isTerminal"><c>null</c> = selbst ermitteln. Die Tests setzen den Wert, weil
    /// ein umgeleiteter Strom sonst nie den animierten Pfad nimmt und genau die Zusagen ungetestet
    /// blieben, die kaputtgehen koennen.</param>
    public TerminalOutput(TextWriter output, TextWriter error, ToolOptions options,
        bool? isTerminal = null)
    {
        _out = output;
        _error = error;
        _options = options;

        var terminal = isTerminal ?? !Console.IsErrorRedirected;
        _animate = options.Progress switch
        {
            ProgressMode.Never => false,
            ProgressMode.Always => true,
            // --verbose ersetzt die Live-Zeile durch die Tabelle; beides zugleich waere doppelt.
            _ => terminal && !options.Quiet && !options.Json && !options.Verbose,
        };
    }

    /// <summary>Die Optionen, unter denen dieses Kommando laeuft - damit ein Aufrufer nicht beides
    /// herumreichen muss.</summary>
    public ToolOptions Options => _options;

    /// <summary>Erfolgs-Meldungen wie <c>path: ok</c>. Gehen auf <b>stdout</b> und schweigen bei
    /// <c>--quiet</c>. Angeforderte Dumps laufen <b>nicht</b> hierueber - die sind Nutzlast, keine
    /// Plauderei, und duerfen von <c>--quiet</c> nicht verschluckt werden.</summary>
    public void Info(string message)
    {
        if (_options.Quiet) return;
        EraseLine();
        _out.WriteLine(message);
    }

    /// <summary>Schreibt Nutzlast auf stdout - Disassembly, IR-Dump, AST. Nie unterdrueckt.</summary>
    public void Payload(string text)
    {
        EraseLine();
        _out.Write(text);
    }

    /// <summary>Beginnt eine Phase: startet ihre Uhr und zeigt sie an.</summary>
    public void BeginPhase(Phase phase, string detail = "")
    {
        _current = phase;
        _currentDetail = detail;
        _currentStartTicks = _total.ElapsedTicks;
        DrawLine(phase, detail);
    }

    /// <summary>
    /// Ergaenzt den Detailtext der laufenden Phase - etwa einen Modulnamen, sobald er bekannt ist.
    /// Fuer die Tabelle wird angehaengt, damit "load" alle Module nennt und nicht nur das letzte.
    /// </summary>
    public void UpdateDetail(string detail)
    {
        if (_current is not { } phase) return;
        _currentDetail = _currentDetail.Length == 0 ? detail : $"{_currentDetail}, {detail}";
        DrawLine(phase, detail);
    }

    /// <summary>Schliesst die laufende Phase ab.</summary>
    /// <param name="elapsedOverride">Fuer Phasen, deren Dauer der Aufrufer selbst gemessen hat.
    /// Gebraucht wird das genau einmal: <c>Compilation.Resolve</c> laedt die importierten Module
    /// intern, die Grenze Load/Resolve ist von aussen also nicht beobachtbar. Statt die Bibliothek
    /// dafuer aufzubohren, misst der Modul-Lader sich selbst und die Resolve-Zeit wird um seine
    /// Dauer vermindert - genauer als eine Phasengrenze und ohne Eingriff in
    /// <c>Lyric.Resolver</c>.</param>
    public void EndPhase(TimeSpan? elapsedOverride = null)
    {
        if (_current is not { } phase) return;

        _timings.Add((phase, _currentDetail,
            elapsedOverride ?? TimeSpan.FromTicks(_total.ElapsedTicks - _currentStartTicks)));
        _current = null;
        _currentDetail = "";
    }

    /// <summary>Traegt eine anderswo gemessene Phase in die Tabelle ein, ohne sie zur laufenden zu
    /// machen.</summary>
    public void ReportPhase(Phase phase, string detail, TimeSpan elapsed) =>
        _timings.Add((phase, detail, elapsed));

    /// <summary>
    /// Rendert den Diagnose-Bestand - Klartext oder JSON, genau einmal. Loescht vorher die
    /// Fortschrittszeile.
    ///
    /// <para>Die Entscheidung Text-oder-JSON faellt <b>hier</b> und nirgends sonst. Traefe sie
    /// jedes Kommando selbst, gaebe es Pfade, auf denen <c>--json</c> still Klartext liefert -
    /// derselbe Fehler wie beim dreifach kopierten Compiler-Vorspann in M6.</para>
    /// </summary>
    public void Render(DiagnosticEngine diagnostics)
    {
        EraseLine();
        if (_options.Json) diagnostics.RenderJson(_error);
        else diagnostics.RenderText(_error);
        _error.Flush();
    }

    /// <summary>
    /// Raeumt die Zeile weg und druckt bei <c>--verbose</c> die Zeittabelle.
    ///
    /// <para><b>Muss laufen, bevor ein Lyric-Programm startet</b> - sonst landet dessen erste
    /// Ausgabe neben einer halben Fortschrittszeile. Deshalb auch <see cref="IDisposable"/>: ein
    /// <c>using</c>-Block macht das Vergessen schwer.</para>
    /// </summary>
    public void Finish()
    {
        EndPhase();
        EraseLine();

        if (!_options.Verbose || _timings.Count == 0) return;

        foreach (var (phase, detail, elapsed) in _timings)
            _error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {PhaseNames.Short(phase),-9}{Fit(detail),-36}{elapsed.TotalMilliseconds,8:F1} ms"));

        _error.WriteLine($"  {new string('-', 53)}");
        _error.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {"",-9}{"total",-36}{_total.Elapsed.TotalMilliseconds,8:F1} ms"));
        _error.Flush();
        _timings.Clear();
    }

    public void Dispose() => Finish();

    private void DrawLine(Phase phase, string detail)
    {
        // Die Schwelle wird bei jedem Zeichnen neu geprueft, nicht einmal am Anfang: ein Lauf
        // wird erst waehrend seiner spaeteren Phasen lang genug, um eine Anzeige zu verdienen.
        if (!_animate || _total.Elapsed < DisplayThreshold) return;

        var text = detail.Length == 0
            ? PhaseNames.Progressive(phase)
            : $"{PhaseNames.Progressive(phase),-10} {detail}";
        _error.Write($"\r{EraseToEndOfLine}  {text}");
        _error.Flush();
        _lineOnScreen = true;
    }

    private void EraseLine()
    {
        if (!_lineOnScreen) return;
        _error.Write($"\r{EraseToEndOfLine}");
        _error.Flush();
        _lineOnScreen = false;
    }

    private static string Fit(string detail) =>
        detail.Length <= 35 ? detail : detail[..32] + "...";
}
