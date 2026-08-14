using Lyric.Bytecode.Encoding;
using Lyric.Core;

namespace Lyric.Bytecode;

/// <summary>What a source map needs beyond the module: the text the spans point into, and the
/// directory paths are made relative to.</summary>
/// <param name="Sources">The manager the spans' <see cref="FileId"/>s belong to.</param>
/// <param name="BaseDirectory">Usually the directory of the file being compiled. Everything below
/// it keeps its relative path; everything else falls back to its bare file name.</param>
public sealed record SourceMapContext(SourceManager Sources, string BaseDirectory);

/// <summary>
/// Collects source positions while the code of each function is emitted, and writes them as the
/// SourceMap section (id 6).
///
/// <para>A row is kept only where the position CHANGES. A loop body is dozens of instructions across
/// a handful of lines, and one row per instruction would make this the largest section in the
/// file.</para>
///
/// <para>The offsets are byte offsets into the function's code, the same coordinate
/// <c>blockOffsets</c> uses. An instruction index would be shorter here and would presuppose that a
/// runtime decodes into an array before it runs — see <c>docs/Bytecode.md</c> §SourceMap.</para>
/// </summary>
internal sealed class SourceMapBuilder(SourceMapContext context)
{
    private readonly record struct Row(int Offset, int FileIndex, int Line);

    /// <summary>One row list per function, in the order the functions are written.</summary>
    private readonly List<List<Row>> _functions = new();

    private List<Row>? _current;

    /// <summary>File ids in first-use order, and their display names beside them. The index into
    /// these lists is what a row carries.</summary>
    private readonly List<FileId> _files = new();
    private readonly List<string> _names = new();

    public void BeginFunction()
    {
        _current = new List<Row>();
        _functions.Add(_current);
    }

    /// <summary>Records where the instruction beginning at <paramref name="offset"/> comes from.
    /// Called before the instruction is emitted, so the offset covers the slot loads that belong to
    /// it as well.</summary>
    public void At(int offset, Span span)
    {
        // A synthetic node carries no file. It inherits whatever row precedes it, which is the
        // nearest real position rather than a wrong one.
        if (!span.File.IsValid) return;

        var fileIndex = FileIndexOf(span.File);
        var line = context.Sources.LocateStart(span).Line;

        if (_current!.Count > 0)
        {
            var last = _current[^1];

            // Same position: the row would say nothing new.
            if (last.FileIndex == fileIndex && last.Line == line) return;

            // Nothing was emitted since the last row, so no byte belongs to this position. The
            // offsets have to ascend strictly, and a zero delta is invalid past the first row.
            if (last.Offset == offset) return;
        }

        _current.Add(new Row(offset, fileIndex, line));
    }

    /// <summary>The file names have to be in the pool BEFORE the Strings section is serialized,
    /// which happens long before this section is written.</summary>
    /// <param name="intern">The pool's intern function. Passed rather than the pool itself: this
    /// needs a way to turn a string into an index and nothing else.</param>
    public void InternNames(Func<string, int> intern)
    {
        foreach (var name in _names) intern(name);
    }

    /// <summary>True when not a single position was recorded. The section is then left out
    /// entirely rather than written empty.</summary>
    public bool IsEmpty => _functions.TrueForAll(rows => rows.Count == 0);

    public void WritePayload(ByteWriter s, Func<string, int> intern)
    {
        s.ULeb(_names.Count);
        foreach (var name in _names) s.ULeb(intern(name));

        s.ULeb(_functions.Count);
        foreach (var rows in _functions)
        {
            s.ULeb(rows.Count);

            // The first row carries its offset outright, every later one the difference to the one
            // before. Deltas are shorter as LEB128 and make the ascent checkable without state.
            var previous = 0;
            foreach (var row in rows)
            {
                s.ULeb(row.Offset - previous);
                s.ULeb(row.FileIndex);
                s.ULeb(row.Line);
                previous = row.Offset;
            }
        }
    }

    private int FileIndexOf(FileId file)
    {
        var index = _files.IndexOf(file);
        if (index >= 0) return index;

        _files.Add(file);
        _names.Add(DisplayName(context.Sources.GetPath(file), context.BaseDirectory));
        return _files.Count - 1;
    }

    /// <summary>
    /// The name a panic prints, and it must not depend on the machine that built the module:
    /// <c>docs/Bytecode.md</c> §1 forbids absolute paths, and one would carry the builder's home
    /// directory into every shipped file.
    /// </summary>
    private static string DisplayName(string path, string baseDirectory)
    {
        string relative;
        try
        {
            relative = Path.GetRelativePath(Path.GetFullPath(baseDirectory), Path.GetFullPath(path));
        }
        catch (ArgumentException)
        {
            // A display name that is not a path at all — the REPL and the tests hand in names like
            // "<repl>". Those are already relative in the only sense that matters.
            return path;
        }

        // Anything outside the base directory answers with '..' segments. The standard library sits
        // beside the toolchain rather than beside the program, so its relative path would describe
        // the layout of the building machine. Such a file keeps its bare name.
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return Path.GetFileName(path);

        // One spelling on every platform, for the same reason the goldens are stored with LF.
        return relative.Replace('\\', '/');
    }
}
