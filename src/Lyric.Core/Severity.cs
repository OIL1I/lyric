namespace Lyric.Core;

public enum Severity
{
    Error,
    Warning,
    Hint
}

public static class SeverityExtensions
{
    public static string ToDisplayString(this Severity severity)
    {
        return severity.ToString().ToLower();
    }
}
