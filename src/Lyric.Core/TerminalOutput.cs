using System.Diagnostics;
using System.Globalization;

namespace Lyric.Core;

/// <summary>
/// The single writer to stderr while a command runs.
///
/// <para>Diagnostic output goes through here too: it clears the progress line before writing, so
/// an error never lands in the middle of it.</para>
///
/// <para>Progress never goes to stdout, which carries the program's output or a requested dump
/// and has to stay machine-readable.</para>
///
/// <para>The display is plain ASCII.</para>
/// </summary>
public sealed class TerminalOutput : IDisposable
{
    /// <summary>Clears from the cursor to the end of the line (ANSI EL).</summary>
    private const string EraseToEndOfLine = "\u001b[K";

    /// <summary>How long a run may take before anything is displayed, so a fast run shows
    /// nothing rather than flickering.</summary>
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

    /// <param name="isTerminal"><c>null</c> detects it. Tests set it, because a redirected stream
    /// would never take the animated path.</param>
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
            // --verbose replaces the live line with the table.
            _ => terminal && !options.Quiet && !options.Json && !options.Verbose,
        };
    }

    /// <summary>The options this command runs under.</summary>
    public ToolOptions Options => _options;

    /// <summary>Success messages such as <c>path: ok</c>. They go to stdout and are silent under
    /// <c>--quiet</c>. Requested dumps do not go through here; they are payload.</summary>
    public void Info(string message)
    {
        if (_options.Quiet) return;
        EraseLine();
        _out.WriteLine(message);
    }

    /// <summary>Writes payload to stdout: disassembly, IR dump, AST. Never suppressed.</summary>
    public void Payload(string text)
    {
        EraseLine();
        _out.Write(text);
    }

    /// <summary>Begins a phase: starts its clock and displays it.</summary>
    public void BeginPhase(Phase phase, string detail = "")
    {
        _current = phase;
        _currentDetail = detail;
        _currentStartTicks = _total.ElapsedTicks;
        DrawLine(phase, detail);
    }

    /// <summary>Adds detail to the running phase, such as a module name. For the table the text
    /// is appended, so "load" names every module rather than only the last.</summary>
    public void UpdateDetail(string detail)
    {
        if (_current is not { } phase) return;
        _currentDetail = _currentDetail.Length == 0 ? detail : $"{_currentDetail}, {detail}";
        DrawLine(phase, detail);
    }

    /// <summary>Ends the running phase.</summary>
    /// <param name="elapsedOverride">For a phase the caller timed itself. <c>Compilation.Resolve</c>
    /// loads the imported modules internally, so the load/resolve boundary is not observable from
    /// outside; the module loader times itself and the resolve time is reduced by it.</param>
    public void EndPhase(TimeSpan? elapsedOverride = null)
    {
        if (_current is not { } phase) return;

        _timings.Add((phase, _currentDetail,
            elapsedOverride ?? TimeSpan.FromTicks(_total.ElapsedTicks - _currentStartTicks)));
        _current = null;
        _currentDetail = "";
    }

    /// <summary>Records a phase measured elsewhere without making it the running one.</summary>
    public void ReportPhase(Phase phase, string detail, TimeSpan elapsed) =>
        _timings.Add((phase, detail, elapsed));

    /// <summary>
    /// Renders the collected diagnostics once, as text or JSON, clearing the progress line first.
    ///
    /// <para>The text-or-JSON decision is made here and nowhere else.</para>
    /// </summary>
    public void Render(DiagnosticEngine diagnostics)
    {
        EraseLine();
        if (_options.Json) diagnostics.RenderJson(_error);
        else diagnostics.RenderText(_error);
        _error.Flush();
    }

    /// <summary>
    /// Clears the line and, under <c>--verbose</c>, prints the timing table.
    ///
    /// <para>It has to run before a Lyric program starts, or the program's first output lands
    /// beside half a progress line. Hence <see cref="IDisposable"/>.</para>
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
        // The threshold is re-checked on every draw: a run only becomes long enough during its
        // later phases.
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
