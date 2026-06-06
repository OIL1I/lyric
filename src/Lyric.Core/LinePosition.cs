namespace Lyric.Core;

public readonly record struct LinePosition(int Line, int Column)
{
    public override string ToString() => $"{Line}:{Column}";
}