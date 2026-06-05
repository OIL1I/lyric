using Lyric.Core;
using Xunit;

namespace Lyric.Tests.Core;

public class SpanTests
{
    private static readonly FileId File1 = new(1);
    private static readonly FileId File2 = new(2);

    [Fact]
    public void Span_StoresOffsetsAndFile()
    {
        var span = new Span(File1, 10, 20);

        Assert.Equal(File1, span.File);
        Assert.Equal(10, span.Start);
        Assert.Equal(20, span.End);
        Assert.Equal(10, span.Length);
    }

    [Fact]
    public void Span_EmptyDetection()
    {
        var empty = new Span(File1, 5, 5);
        var nonempty = new Span(File1, 5, 6);

        Assert.True(empty.IsEmpty);
        Assert.False(nonempty.IsEmpty);
    }

    [Theory]
    [InlineData(9, false)]   // before start
    [InlineData(10, true)]   // at start (inclusive)
    [InlineData(15, true)]   // middle
    [InlineData(19, true)]   // last valid
    [InlineData(20, false)]  // at end (exclusive)
    [InlineData(25, false)]  // after end
    public void Span_ContainsRespectsHalfOpenRange(int offset, bool expected)
    {
        var span = new Span(File1, 10, 20);
        Assert.Equal(expected, span.Contains(offset));
    }

    [Fact]
    public void Span_UnionCombinesAdjacentOrOverlapping()
    {
        var a = new Span(File1, 5, 10);
        var b = new Span(File1, 8, 15);

        var union = a.Union(b);

        Assert.Equal(File1, union.File);
        Assert.Equal(5, union.Start);
        Assert.Equal(15, union.End);
    }

    [Fact]
    public void Span_UnionAcrossFilesThrows()
    {
        var a = new Span(File1, 0, 10);
        var b = new Span(File2, 0, 10);

        Assert.Throws<ArgumentException>(() => a.Union(b));
    }
}

public class FileIdTests
{
    [Fact]
    public void FileId_NoneIsInvalid()
    {
        Assert.False(FileId.None.IsValid);
    }

    [Fact]
    public void FileId_PositiveValueIsValid()
    {
        Assert.True(new FileId(1).IsValid);
        Assert.True(new FileId(42).IsValid);
    }

    [Fact]
    public void FileId_EqualityIsByValue()
    {
        Assert.Equal(new FileId(7), new FileId(7));
        Assert.NotEqual(new FileId(7), new FileId(8));
    }
}
