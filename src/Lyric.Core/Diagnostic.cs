namespace Lyric.Core;

/// <param name="Notes">Secondary remarks, in telling order. <c>null</c> and empty mean the same
/// thing; <c>null</c> keeps the common case allocation-free.</param>
public readonly record struct Diagnostic(
    string Code,
    Severity Severity,
    Span Span,
    string Message,
    IReadOnlyList<DiagnosticNote>? Notes = null
    );
