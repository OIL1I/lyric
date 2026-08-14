namespace Lyric.Bytecode;

/// <summary>A source position: the file as it was named at compile time, and a 1-based line.</summary>
public readonly record struct SourcePosition(string File, int Line)
{
    public override string ToString() => $"{File}:{Line}";
}

/// <summary>One row of a function's map: from this byte offset onwards, the code comes from this
/// file and line. It holds until the next row.</summary>
public readonly record struct BytecodeSourceRow(int Offset, int FileIndex, int Line);

/// <summary>
/// The SourceMap section (id 6), read.
///
/// <para>Absent from a stripped module, which stays valid — the only consequence is that a panic
/// names a function instead of a line.</para>
/// </summary>
public sealed class BytecodeSourceMap
{
    /// <summary>File names in the order the section lists them; a row's <c>FileIndex</c> points in
    /// here.</summary>
    public required IReadOnlyList<string> Files { get; init; }

    /// <summary>One row list per function of the module, in the same order. A function without
    /// position information has an empty list rather than no entry.</summary>
    public required IReadOnlyList<IReadOnlyList<BytecodeSourceRow>> Functions { get; init; }

    /// <summary>
    /// Where the code at <paramref name="offset"/> in function <paramref name="function"/> comes
    /// from, or <c>null</c> when the map says nothing about it.
    ///
    /// <para>A row holds from its own offset up to the next one, so this is the last row at or
    /// before the offset — found by bisection, because the rows ascend.</para>
    /// </summary>
    public SourcePosition? Locate(int function, int offset)
    {
        if (function < 0 || function >= Functions.Count) return null;

        var rows = Functions[function];

        // Before the first row nothing is known. That is not the same as "the first row applies":
        // a function may begin with code no source line produced.
        if (rows.Count == 0 || offset < rows[0].Offset) return null;

        var low = 0;
        var high = rows.Count - 1;
        while (low < high)
        {
            var middle = low + ((high - low + 1) / 2);
            if (rows[middle].Offset <= offset) low = middle;
            else high = middle - 1;
        }

        var row = rows[low];
        return new SourcePosition(Files[row.FileIndex], row.Line);
    }
}
