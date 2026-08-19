namespace Lyric.Core;

/// <summary>
/// A secondary remark under a diagnostic: another PLACE that belongs to the same finding
/// ("previous definition here"), or a remark with no place at all.
///
/// <para>Notes point at locations, never at literature — a message that cites a document ages in
/// two directions at once, and both have happened here before.</para>
/// </summary>
/// <param name="Location">Where the note points. <c>default</c> means the note has no place and
/// renders as text alone, the same convention <see cref="Diagnostic.Span"/> follows.</param>
public readonly record struct DiagnosticNote(Span Location, string Message)
{
    /// <summary>A note that is text alone.</summary>
    public DiagnosticNote(string message) : this(default, message) { }
}
