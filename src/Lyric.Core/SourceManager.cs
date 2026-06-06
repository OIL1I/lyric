using System.Text;

namespace Lyric.Core;

public sealed class SourceManager
{
    private record class FileEntry
    {
        public required string Path { get; init; }
        public required string Text { get; init; }
        public required int[] LineStarts { get; init; }
    }
    
    private readonly List<FileEntry> _files = new();
    
    public int FileCount => _files.Count;
    
    public int LineCount(FileId id)
    {
        if (!id.IsValid) throw new ArgumentException($"invalid file id:{id.Value}", nameof(id));
        if (id.Value > _files.Count) throw new ArgumentException($"no file with id:{id.Value}", nameof(id));
        return _files[id.Value - 1].LineStarts.Length;
    }
    
    public FileId AddFromDisk(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        return AddVirtual(path, text);
    }

    public FileId AddVirtual(string displayName, string text)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(text);
        
        List<int> lineStarts = new();
        lineStarts.Add(0);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') lineStarts.Add(i + 1);
        }
        _files.Add(new FileEntry { Path = displayName, Text = text, LineStarts = lineStarts.ToArray() });
        return new FileId(_files.Count);
    }

    public string GetText(FileId id)
    {
        if (!id.IsValid) throw new ArgumentException($"invalid file id:{id.Value}", nameof(id));
        if (id.Value > _files.Count) throw new ArgumentException($"no file with id:{id.Value}", nameof(id));
        return _files[id.Value - 1].Text;
    }

    public string GetPath(FileId id)
    {
        if (!id.IsValid) throw new ArgumentException("invalid file id", nameof(id));
        if (id.Value > _files.Count) throw new ArgumentException($"no file with id:{id.Value}", nameof(id));
        return _files[id.Value - 1].Path;
    }

    public LinePosition Locate(FileId id, int offset)
    {
        if (!id.IsValid) throw new ArgumentException("invalid file id", nameof(id));
        if (id.Value > _files.Count) throw new ArgumentException($"no file with id:{id.Value}", nameof(id));
        var fileEntry = _files[id.Value - 1];
        if (offset < 0 || offset > fileEntry.Text.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        int line = 0;
        do
        {
            var currentLineStart = fileEntry.LineStarts[line];
            if (currentLineStart > offset) break;
            line++;
        } while (line < fileEntry.LineStarts.Length);
        return new LinePosition(line, offset - fileEntry.LineStarts[line - 1] + 1);
    }

    public LinePosition LocateStart(Span span)
    {
        return Locate(span.File, span.Start);
    }
    
    public LinePosition LocateEnd(Span span)
    {
        return Locate(span.File, span.End);
    }

    public string GetLineText(FileId id, int line)
    {
        if (!id.IsValid) throw new ArgumentException("invalid file id", nameof(id));
        if (id.Value > _files.Count) throw new ArgumentException($"no file with id:{id.Value}", nameof(id));
        var fileEntry = _files[id.Value - 1];
        if (line < 1 || line > fileEntry.LineStarts.Length) throw new ArgumentOutOfRangeException(nameof(line));
        
        var start = fileEntry.LineStarts[line - 1];
        var end = (line == LineCount(id)) ? fileEntry.Text.Length : fileEntry.LineStarts[line];
        
        if (end > start && fileEntry.Text[end-1] == '\n') end--;
        if (end > start && fileEntry.Text[end - 1] == '\r') end--;
        var length = end - start;
        return fileEntry.Text.Substring(start, length);
    }

    public ReadOnlySpan<char> Slice(Span span)
    {
        var id = span.File;
        if (!id.IsValid) throw new ArgumentException("invalid file id", nameof(id));
        if (id.Value > _files.Count) throw new ArgumentException($"no file with id:{id.Value}", nameof(id));
        
        if (span.Start < 0 || span.Start > _files[id.Value - 1].Text.Length) throw new ArgumentOutOfRangeException(nameof(span.Start));
        if (span.End < 0 || span.End > _files[id.Value - 1].Text.Length) throw new ArgumentOutOfRangeException(nameof(span.End));
        return _files[id.Value - 1].Text.AsSpan(span.Start, span.End - span.Start);
    }
}