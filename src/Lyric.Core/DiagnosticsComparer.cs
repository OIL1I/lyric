namespace Lyric.Core;

public class DiagnosticsComparer: IComparer<Diagnostic>
{
    public int Compare(Diagnostic x, Diagnostic y)
    {
        var fileIdComparison = x.Span.File.Value.CompareTo(y.Span.File.Value);
        if (fileIdComparison != 0) return fileIdComparison;
        var spanStartComparison = x.Span.Start.CompareTo(y.Span.Start);
        if (spanStartComparison != 0) return spanStartComparison;
        var spanEndComparison = x.Span.End.CompareTo(y.Span.End);
        if (spanEndComparison != 0) return spanEndComparison;
        return string.Compare(x.Code, y.Code, StringComparison.Ordinal);
    }
}