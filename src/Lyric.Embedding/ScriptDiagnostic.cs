using System.Globalization;
using Lyric.Core;

namespace Lyric.Embedding;

/// <summary>
/// What a compilation reported, with its place already resolved.
///
/// <para>A compiler <see cref="Diagnostic"/> carries a <see cref="Span"/>, which is a file INDEX
/// plus offsets — meaningless without the <c>SourceManager</c> that handed the index out, and that
/// manager belongs to one compilation and is gone by the time a host catches the exception. A host
/// was therefore left with a code and a message: <c>unknown identifier 'x'</c>, and in a project of
/// thirteen files the question "in which one?" every time.</para>
///
/// <para>Resolved here instead, at the throw site, where the manager is still alive. The host gets
/// a path, a line and a column — enough to point an editor at it, which is the same treatment a
/// runtime panic's backtrace already gets.</para>
/// </summary>
/// <param name="File">The path of the file, or <c>null</c> for a diagnostic with no place —
/// one about the compilation as a whole rather than about a position in it.</param>
/// <param name="Line">1-based, or <c>0</c> when there is no place.</param>
/// <param name="Column">1-based, or <c>0</c> when there is no place.</param>
/// <param name="Notes">Other places belonging to the same finding, in telling order.</param>
public readonly record struct ScriptDiagnostic(
    string Code,
    Severity Severity,
    string Message,
    string? File,
    int Line,
    int Column,
    IReadOnlyList<ScriptNote> Notes)
{
    /// <summary>The one-line form of the command line: <c>file:line:col: error[CODE]: message</c>,
    /// and the code and message alone when there is no place. A host that only wants to log
    /// something has it here rather than assembling it from the parts.</summary>
    public override string ToString() =>
        File is null
            ? $"{Severity.ToDisplayString()}[{Code}]: {Message}"
            : string.Create(CultureInfo.InvariantCulture,
                $"{File}:{Line}:{Column}: {Severity.ToDisplayString()}[{Code}]: {Message}");

    internal static ScriptDiagnostic From(Diagnostic diagnostic, SourceManager sources)
    {
        var notes = diagnostic.Notes is { Count: > 0 } list
            ? list.Select(note => ScriptNote.From(note, sources)).ToArray()
            : [];

        if (!diagnostic.Span.File.IsValid)
            return new ScriptDiagnostic(diagnostic.Code, diagnostic.Severity, diagnostic.Message,
                null, 0, 0, notes);

        var at = sources.LocateStart(diagnostic.Span);
        return new ScriptDiagnostic(diagnostic.Code, diagnostic.Severity, diagnostic.Message,
            sources.GetPath(diagnostic.Span.File), at.Line, at.Column, notes);
    }
}

/// <summary>A secondary remark under a diagnostic — another place that belongs to the same
/// finding, or a remark with no place at all.</summary>
/// <inheritdoc cref="ScriptDiagnostic" path="/param[@name='File']"/>
public readonly record struct ScriptNote(string Message, string? File, int Line, int Column)
{
    public override string ToString() =>
        File is null
            ? Message
            : string.Create(CultureInfo.InvariantCulture, $"{File}:{Line}:{Column}: {Message}");

    internal static ScriptNote From(DiagnosticNote note, SourceManager sources)
    {
        if (!note.Location.File.IsValid) return new ScriptNote(note.Message, null, 0, 0);

        var at = sources.LocateStart(note.Location);
        return new ScriptNote(note.Message, sources.GetPath(note.Location.File), at.Line, at.Column);
    }
}
