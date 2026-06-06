namespace Lyric.Core;

public readonly record struct Diagnostic(
    string Code,
    Severity Severity,
    Span Span,
    string Message
    );