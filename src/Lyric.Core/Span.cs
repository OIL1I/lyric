namespace Lyric.Core;

/// <summary>
/// A half-open byte range within a source file: [Start, End).
/// </summary>
/// <remarks>
/// Offsets are 0-based byte indices into the file's source text.
/// Use <see cref="SourceManager.Locate(Span)"/> to convert to line/column.
/// </remarks>
public readonly record struct Span(FileId File, int Start, int End)
{
    public int Length => End - Start;

    public bool IsEmpty => Start == End;

    public bool Contains(int offset) => offset >= Start && offset < End;

    /// <summary>
    /// Returns the smallest span that covers both this span and <paramref name="other"/>.
    /// Both spans must reference the same file.
    /// </summary>
    public Span Union(Span other)
    {
        if (other.File != File)
        {
            throw new ArgumentException(
                $"cannot union spans from different files: {File} vs {other.File}",
                nameof(other));
        }
        return new Span(File, Math.Min(Start, other.Start), Math.Max(End, other.End));
    }

    public override string ToString() => $"{File}[{Start}..{End})";
}
