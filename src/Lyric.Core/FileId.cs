namespace Lyric.Core;

/// <summary>
/// Opaque identifier for a source file managed by <see cref="SourceManager"/>.
/// </summary>
/// <remarks>
/// File IDs are assigned by the SourceManager when a file is registered.
/// They are stable for the lifetime of the SourceManager instance.
/// FileId(0) is reserved as "no file" / invalid.
/// </remarks>
public readonly record struct FileId(int Value)
{
    public static readonly FileId None = new(0);

    public bool IsValid => Value > 0;

    public override string ToString() => $"FileId({Value})";
}
