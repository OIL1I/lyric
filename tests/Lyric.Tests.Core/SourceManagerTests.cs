using Lyric.Core;
using Xunit;

namespace Lyric.Tests.Core;

public class SourceManagerTests
{
    // ─── AddVirtual and AddFromDisk: registration ──────────────────────────

    [Fact]
    public void AddVirtual_returns_valid_FileId()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test", "hello");
        Assert.True(id.IsValid);
        Assert.Equal(1, id.Value);
    }

    [Fact]
    public void AddVirtual_assigns_sequential_ids()
    {
        var sm = new SourceManager();
        var a = sm.AddVirtual("a", "x");
        var b = sm.AddVirtual("b", "y");
        var c = sm.AddVirtual("c", "z");
        Assert.Equal(1, a.Value);
        Assert.Equal(2, b.Value);
        Assert.Equal(3, c.Value);
    }

    [Fact]
    public void AddVirtual_accepts_empty_text()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("empty", "");
        Assert.Equal("", sm.GetText(id));
        Assert.Equal(1, sm.LineCount(id));
    }

    [Fact]
    public void AddVirtual_null_displayName_throws()
    {
        var sm = new SourceManager();
        Assert.Throws<ArgumentNullException>(() => sm.AddVirtual(null!, "text"));
    }

    [Fact]
    public void AddVirtual_null_text_throws()
    {
        var sm = new SourceManager();
        Assert.Throws<ArgumentNullException>(() => sm.AddVirtual("name", null!));
    }

    [Fact]
    public void AddFromDisk_reads_text_from_file()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "from disk\nline 2", System.Text.Encoding.UTF8);
            var sm = new SourceManager();
            var id = sm.AddFromDisk(path);
            Assert.Equal("from disk\nline 2", sm.GetText(id));
            Assert.Equal(path, sm.GetPath(id));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AddFromDisk_strips_utf8_bom()
    {
        var path = Path.GetTempFileName();
        try
        {
            var bytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a', (byte)'b' };
            File.WriteAllBytes(path, bytes);
            var sm = new SourceManager();
            var id = sm.AddFromDisk(path);
            Assert.Equal("ab", sm.GetText(id));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AddFromDisk_missing_file_throws()
    {
        var fakePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".lyr");
        Assert.Throws<FileNotFoundException>(() => new SourceManager().AddFromDisk(fakePath));
    }

    [Fact]
    public void AddFromDisk_same_path_twice_gives_two_ids()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "x");
            var sm = new SourceManager();
            var a = sm.AddFromDisk(path);
            var b = sm.AddFromDisk(path);
            Assert.NotEqual(a, b);
        }
        finally { File.Delete(path); }
    }

    // ─── FileCount ─────────────────────────────────────────────────────────

    [Fact]
    public void FileCount_starts_at_zero()
    {
        Assert.Equal(0, new SourceManager().FileCount);
    }

    [Fact]
    public void FileCount_increments_per_add()
    {
        var sm = new SourceManager();
        sm.AddVirtual("a", "x");
        sm.AddVirtual("b", "y");
        Assert.Equal(2, sm.FileCount);
    }

    // ─── GetText / GetPath ─────────────────────────────────────────────────

    [Fact]
    public void GetText_returns_added_text()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("x", "hello world");
        Assert.Equal("hello world", sm.GetText(id));
    }

    [Fact]
    public void GetPath_returns_display_name()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("src/foo.lyr", "x");
        Assert.Equal("src/foo.lyr", sm.GetPath(id));
    }

    [Fact]
    public void GetText_with_None_throws()
    {
        var sm = new SourceManager();
        sm.AddVirtual("a", "x");
        Assert.Throws<ArgumentException>(() => sm.GetText(FileId.None));
    }

    [Fact]
    public void GetText_with_unknown_id_throws()
    {
        var sm = new SourceManager();
        sm.AddVirtual("a", "x");
        Assert.Throws<ArgumentException>(() => sm.GetText(new FileId(99)));
    }

    [Fact]
    public void GetText_with_last_valid_id_works()
    {
        // Regression: makes sure id.Value == FileCount is not wrongly rejected.
        var sm = new SourceManager();
        sm.AddVirtual("a", "x");
        sm.AddVirtual("b", "y");
        var last = sm.AddVirtual("c", "z");
        Assert.Equal(3, last.Value);
        Assert.Equal("z", sm.GetText(last));
    }

    // ─── LineCount ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("",          1)]   // an empty file is one empty line
    [InlineData("abc",       1)]
    [InlineData("a\n",       2)]
    [InlineData("a\nb",      2)]
    [InlineData("a\nb\n",    3)]   // an empty line after the last newline
    [InlineData("a\r\nb",    2)]
    public void LineCount_counts_correctly(string text, int expected)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("t", text);
        Assert.Equal(expected, sm.LineCount(id));
    }

    [Fact]
    public void LineCount_with_None_throws()
    {
        Assert.Throws<ArgumentException>(() => new SourceManager().LineCount(FileId.None));
    }

    // ─── Locate: a 1-based position from an offset ─────────────────────────

    [Theory]
    // Single line, no newline
    [InlineData("abc",      0, 1, 1)]
    [InlineData("abc",      1, 1, 2)]
    [InlineData("abc",      2, 1, 3)]
    [InlineData("abc",      3, 1, 4)]   // EOF
    // Two lines, LF
    [InlineData("a\nb",     0, 1, 1)]
    [InlineData("a\nb",     1, 1, 2)]   // the newline itself
    [InlineData("a\nb",     2, 2, 1)]
    [InlineData("a\nb",     3, 2, 2)]   // EOF
    // Leading newline
    [InlineData("\n",       0, 1, 1)]
    [InlineData("\n",       1, 2, 1)]   // EOF after a newline
    // CRLF
    [InlineData("a\r\nb",   0, 1, 1)]
    [InlineData("a\r\nb",   1, 1, 2)]   // the \r is part of line 1
    [InlineData("a\r\nb",   2, 1, 3)]   // \n
    [InlineData("a\r\nb",   3, 2, 1)]   // b
    [InlineData("a\r\nb",   4, 2, 2)]   // EOF
    // Three lines
    [InlineData("a\nb\nc",  0, 1, 1)]
    [InlineData("a\nb\nc",  2, 2, 1)]
    [InlineData("a\nb\nc",  4, 3, 1)]
    [InlineData("a\nb\nc",  5, 3, 2)]
    // Empty text — only offset 0 valid
    [InlineData("",         0, 1, 1)]
    public void Locate_returns_1based_line_and_column(
        string text, int offset, int expectedLine, int expectedCol)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("t", text);
        var pos = sm.Locate(id, offset);
        Assert.Equal(expectedLine, pos.Line);
        Assert.Equal(expectedCol, pos.Column);
    }

    [Fact]
    public void Locate_negative_offset_throws()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("t", "abc");
        Assert.Throws<ArgumentOutOfRangeException>(() => sm.Locate(id, -1));
    }

    [Fact]
    public void Locate_offset_past_length_throws()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("t", "abc");
        Assert.Throws<ArgumentOutOfRangeException>(() => sm.Locate(id, 4));
    }

    [Fact]
    public void Locate_offset_at_length_is_EOF_position()
    {
        // text.Length is explicitly allowed: it represents EOF.
        var sm = new SourceManager();
        var id = sm.AddVirtual("t", "abc");
        var pos = sm.Locate(id, 3);
        Assert.Equal(1, pos.Line);
        Assert.Equal(4, pos.Column);
    }

    [Fact]
    public void Locate_with_None_throws()
    {
        Assert.Throws<ArgumentException>(() => new SourceManager().Locate(FileId.None, 0));
    }

    // ─── LocateStart / LocateEnd via Span ──────────────────────────────────

    [Fact]
    public void LocateStart_uses_span_start()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("t", "a\nbc");
        var span = new Span(id, 2, 4);
        var pos = sm.LocateStart(span);
        Assert.Equal(2, pos.Line);
        Assert.Equal(1, pos.Column);
    }

    [Fact]
    public void LocateEnd_uses_span_end()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("t", "a\nbc");
        var span = new Span(id, 2, 4);
        var pos = sm.LocateEnd(span);
        Assert.Equal(2, pos.Line);
        Assert.Equal(3, pos.Column);
    }

    // ─── Slice ─────────────────────────────────────────────────────────────

    [Fact]
    public void Slice_extracts_substring()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("t", "hello world");
        var span = new Span(id, 6, 11);
        Assert.Equal("world", sm.Slice(span).ToString());
    }

    [Fact]
    public void Slice_empty_span_is_empty()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("t", "hello");
        var span = new Span(id, 2, 2);
        Assert.Equal(0, sm.Slice(span).Length);
    }

    [Fact]
    public void Slice_at_end_of_file_works()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("t", "abc");
        var span = new Span(id, 3, 3);   // EOF-Span
        Assert.Equal(0, sm.Slice(span).Length);
    }

    [Fact]
    public void Slice_with_unknown_file_throws()
    {
        var sm = new SourceManager();
        sm.AddVirtual("t", "abc");
        var bogus = new Span(new FileId(99), 0, 1);
        Assert.Throws<ArgumentException>(() => sm.Slice(bogus).ToString());
    }

    [Fact]
    public void Slice_past_end_throws()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("t", "abc");
        var span = new Span(id, 0, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => sm.Slice(span).ToString());
    }

    // ─── GetLineText ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("abc",          1, "abc")]                  // single line, no NL
    [InlineData("a\nb",         1, "a")]
    [InlineData("a\nb",         2, "b")]                    // the last line without a newline
    [InlineData("a\nb\n",       1, "a")]
    [InlineData("a\nb\n",       2, "b")]
    [InlineData("a\nb\n",       3, "")]                     // an empty line after a trailing newline
    [InlineData("a\r\nb",       1, "a")]                    // CRLF: the \r has to go
    [InlineData("a\r\nb",       2, "b")]
    [InlineData("\n",           1, "")]
    [InlineData("\n",           2, "")]
    [InlineData("",             1, "")]
    [InlineData("hello world",  1, "hello world")]
    [InlineData("a\n\nb",       2, "")]                     // an empty line in the middle
    public void GetLineText_returns_line_without_terminator(
        string text, int line, string expected)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("t", text);
        Assert.Equal(expected, sm.GetLineText(id, line));
    }

    [Fact]
    public void GetLineText_line_zero_throws()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("t", "abc");
        Assert.Throws<ArgumentOutOfRangeException>(() => sm.GetLineText(id, 0));
    }

    [Fact]
    public void GetLineText_line_past_end_throws()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("t", "a\nb");
        Assert.Throws<ArgumentOutOfRangeException>(() => sm.GetLineText(id, 3));
    }

    [Fact]
    public void GetLineText_with_None_throws()
    {
        Assert.Throws<ArgumentException>(() => new SourceManager().GetLineText(FileId.None, 1));
    }

    // ─── UTF-16: multi-byte characters and surrogate pairs ─────────────────

    [Fact]
    public void Locate_handles_multibyte_chars_as_single_code_unit()
    {
        // The characters are all BMP code points, one UTF-16 code unit each.
        var sm = new SourceManager();
        var id = sm.AddVirtual("t", "äöü");
        Assert.Equal(new LinePosition(1, 1), sm.Locate(id, 0));
        Assert.Equal(new LinePosition(1, 2), sm.Locate(id, 1));
        Assert.Equal(new LinePosition(1, 4), sm.Locate(id, 3));
    }

    [Fact]
    public void Locate_counts_surrogate_pairs_as_two_code_units()
    {
        // 🌍 (U+1F30D) lies outside the BMP and is 2 UTF-16 code units. Documents the deliberate behaviour
        // of the UTF-16 offset choice.
        var sm = new SourceManager();
        var id = sm.AddVirtual("t", "🌍x");
        Assert.Equal(3, sm.GetText(id).Length);
        Assert.Equal(new LinePosition(1, 3), sm.Locate(id, 2)); // x
    }
}
