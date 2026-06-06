using System.IO;
using Lyric.Core;
using Xunit;

namespace Lyric.Tests.Core;

public class DiagnosticEngineTests
{
    private static StringWriter NewWriter()
    {
        // Plattform-unabhängig: WriteLine emittiert "\n", nicht Environment.NewLine.
        return new StringWriter { NewLine = "\n" };
    }

    // ─── Konstruktor / Sammler ─────────────────────────────────────────────

    [Fact]
    public void Constructor_null_sources_throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DiagnosticEngine(null!));
    }

    [Fact]
    public void Empty_engine_has_zero_counts()
    {
        var de = new DiagnosticEngine(new SourceManager());
        Assert.Equal(0, de.Count);
        Assert.Equal(0, de.ErrorCount);
        Assert.False(de.HasErrors);
        Assert.Empty(de.Diagnostics);
    }

    [Fact]
    public void Report_adds_to_diagnostics()
    {
        var de = new DiagnosticEngine(new SourceManager());
        de.Report("LYR-LEX0001", Severity.Error, default, "msg");
        Assert.Equal(1, de.Count);
    }

    [Fact]
    public void Report_preserves_insertion_order()
    {
        var de = new DiagnosticEngine(new SourceManager());
        de.Report("A", Severity.Error, default, "first");
        de.Report("B", Severity.Error, default, "second");
        Assert.Equal("first", de.Diagnostics[0].Message);
        Assert.Equal("second", de.Diagnostics[1].Message);
    }

    [Fact]
    public void ErrorCount_counts_only_errors()
    {
        var de = new DiagnosticEngine(new SourceManager());
        de.Report("1", Severity.Error, default, "");
        de.Report("2", Severity.Warning, default, "");
        de.Report("3", Severity.Error, default, "");
        de.Report("4", Severity.Hint, default, "");
        Assert.Equal(4, de.Count);
        Assert.Equal(2, de.ErrorCount);
        Assert.True(de.HasErrors);
    }

    [Fact]
    public void HasErrors_false_when_only_warnings_and_hints()
    {
        var de = new DiagnosticEngine(new SourceManager());
        de.Report("W", Severity.Warning, default, "");
        de.Report("H", Severity.Hint, default, "");
        Assert.False(de.HasErrors);
        Assert.Equal(0, de.ErrorCount);
    }

    [Fact]
    public void Report_null_code_throws()
    {
        var de = new DiagnosticEngine(new SourceManager());
        Assert.Throws<ArgumentNullException>(
            () => de.Report(null!, Severity.Error, default, "msg"));
    }

    [Fact]
    public void Report_null_message_throws()
    {
        var de = new DiagnosticEngine(new SourceManager());
        Assert.Throws<ArgumentNullException>(
            () => de.Report("CODE", Severity.Error, default, null!));
    }

    // ─── Sortierung ────────────────────────────────────────────────────────

    [Fact]
    public void SortedSnapshot_orders_by_file_first()
    {
        var sm = new SourceManager();
        var a = sm.AddVirtual("a.lyr", "abc");
        var b = sm.AddVirtual("b.lyr", "abc");
        var de = new DiagnosticEngine(sm);
        de.Report("X", Severity.Error, new Span(b, 0, 1), "b-first-inserted");
        de.Report("X", Severity.Error, new Span(a, 0, 1), "a-second-inserted");
        var sorted = de.SortedSnapshot();
        Assert.Equal("a-second-inserted", sorted[0].Message);
        Assert.Equal("b-first-inserted",  sorted[1].Message);
    }

    [Fact]
    public void SortedSnapshot_orders_by_start_within_file()
    {
        var sm = new SourceManager();
        var f = sm.AddVirtual("f.lyr", "abcdef");
        var de = new DiagnosticEngine(sm);
        de.Report("X", Severity.Error, new Span(f, 3, 4), "later");
        de.Report("X", Severity.Error, new Span(f, 0, 1), "earlier");
        var sorted = de.SortedSnapshot();
        Assert.Equal("earlier", sorted[0].Message);
    }

    [Fact]
    public void SortedSnapshot_orders_by_end_when_start_equal()
    {
        var sm = new SourceManager();
        var f = sm.AddVirtual("f.lyr", "abcdef");
        var de = new DiagnosticEngine(sm);
        de.Report("X", Severity.Error, new Span(f, 0, 3), "longer");
        de.Report("X", Severity.Error, new Span(f, 0, 1), "shorter");
        var sorted = de.SortedSnapshot();
        Assert.Equal("shorter", sorted[0].Message);
    }

    [Fact]
    public void SortedSnapshot_orders_by_code_when_spans_equal()
    {
        var sm = new SourceManager();
        var f = sm.AddVirtual("f.lyr", "abc");
        var de = new DiagnosticEngine(sm);
        de.Report("LYR-B", Severity.Error, new Span(f, 0, 1), "");
        de.Report("LYR-A", Severity.Error, new Span(f, 0, 1), "");
        var sorted = de.SortedSnapshot();
        Assert.Equal("LYR-A", sorted[0].Code);
        Assert.Equal("LYR-B", sorted[1].Code);
    }

    [Fact]
    public void SortedSnapshot_puts_None_file_first()
    {
        var sm = new SourceManager();
        var f = sm.AddVirtual("f.lyr", "abc");
        var de = new DiagnosticEngine(sm);
        de.Report("X", Severity.Error, new Span(f, 0, 1), "has-file");
        de.Report("Y", Severity.Error, default,           "no-file");
        var sorted = de.SortedSnapshot();
        Assert.Equal("no-file", sorted[0].Message);
    }

    [Fact]
    public void SortedSnapshot_is_a_copy_not_a_view()
    {
        // Mutation am Original wirkt sich nicht auf einen früheren Snapshot aus.
        var de = new DiagnosticEngine(new SourceManager());
        de.Report("A", Severity.Error, default, "first");
        var snap = de.SortedSnapshot();
        de.Report("B", Severity.Error, default, "second");
        Assert.Single(snap);
    }

    // ─── Text-Renderer ─────────────────────────────────────────────────────

    [Fact]
    public void RenderText_empty_engine_writes_nothing()
    {
        var de = new DiagnosticEngine(new SourceManager());
        var sw = NewWriter();
        de.RenderText(sw);
        Assert.Equal("", sw.ToString());
    }

    [Fact]
    public void RenderText_no_file_diagnostic_ends_with_blank_line()
    {
        // Erwartung: auch No-File-Diagnostics enden mit Leerzeile, konsistent zu File-Diagnostics.
        // Aktueller Code macht das NICHT — der Test treibt den Fix.
        var de = new DiagnosticEngine(new SourceManager());
        de.Report("LYR-CLI0001", Severity.Error, default, "no input file");
        var sw = NewWriter();
        de.RenderText(sw);
        Assert.Equal("error[LYR-CLI0001]: no input file\n\n", sw.ToString());
    }

    [Fact]
    public void RenderText_two_no_file_diagnostics_separated()
    {
        // Erwartung: Leerzeile zwischen zwei No-File-Diagnostics.
        var de = new DiagnosticEngine(new SourceManager());
        de.Report("A", Severity.Error, default, "first");
        de.Report("B", Severity.Error, default, "second");
        var sw = NewWriter();
        de.RenderText(sw);
        Assert.Equal("error[A]: first\n\nerror[B]: second\n\n", sw.ToString());
    }

    [Fact]
    public void RenderText_single_char_span()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", "let x = #;");
        var de = new DiagnosticEngine(sm);
        de.Report("LYR-LEX0001", Severity.Error, new Span(id, 8, 9), "unexpected character '#'");
        var sw = NewWriter();
        de.RenderText(sw);
        var expected =
            "test.lyr:1:9: error[LYR-LEX0001]: unexpected character '#'\n" +
            "let x = #;\n" +
            "        ^\n\n";
        Assert.Equal(expected, sw.ToString());
    }

    [Fact]
    public void RenderText_multi_char_span_underlines_full_length()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", "let foo = 1;");
        var de = new DiagnosticEngine(sm);
        de.Report("LYR-SEM0001", Severity.Warning, new Span(id, 4, 7), "unused");
        var sw = NewWriter();
        de.RenderText(sw);
        var expected =
            "test.lyr:1:5: warning[LYR-SEM0001]: unused\n" +
            "let foo = 1;\n" +
            "    ^^^\n\n";
        Assert.Equal(expected, sw.ToString());
    }

    [Fact]
    public void RenderText_zero_length_span_emits_single_caret()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", "abc");
        var de = new DiagnosticEngine(sm);
        de.Report("X", Severity.Error, new Span(id, 3, 3), "EOF reached");
        var sw = NewWriter();
        de.RenderText(sw);
        var expected =
            "test.lyr:1:4: error[X]: EOF reached\n" +
            "abc\n" +
            "   ^\n\n";
        Assert.Equal(expected, sw.ToString());
    }

    [Fact]
    public void RenderText_multi_line_span_emits_single_caret()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", "a\nb\nc");
        var de = new DiagnosticEngine(sm);
        de.Report("X", Severity.Error, new Span(id, 0, 4), "crosses lines");
        var sw = NewWriter();
        de.RenderText(sw);
        var expected =
            "test.lyr:1:1: error[X]: crosses lines\n" +
            "a\n" +
            "^\n\n";
        Assert.Equal(expected, sw.ToString());
    }

    [Fact]
    public void RenderText_renders_all_three_severities()
    {
        var de = new DiagnosticEngine(new SourceManager());
        de.Report("E", Severity.Error,   default, "err");
        de.Report("W", Severity.Warning, default, "warn");
        de.Report("H", Severity.Hint,    default, "hint");
        var sw = NewWriter();
        de.RenderText(sw);
        var s = sw.ToString();
        Assert.Contains("error[E]: err",     s);
        Assert.Contains("warning[W]: warn",  s);
        Assert.Contains("hint[H]: hint",     s);
    }

    [Fact]
    public void RenderText_renders_sorted()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("t.lyr", "abcdef");
        var de = new DiagnosticEngine(sm);
        de.Report("Z", Severity.Error, new Span(id, 4, 5), "later");
        de.Report("A", Severity.Error, new Span(id, 0, 1), "earlier");
        var sw = NewWriter();
        de.RenderText(sw);
        var s = sw.ToString();
        Assert.True(s.IndexOf("earlier") < s.IndexOf("later"));
    }

    // ─── JSON-Renderer ─────────────────────────────────────────────────────

    [Fact]
    public void RenderJson_empty_engine_writes_tight_array()
    {
        // Erwartung: kein Whitespace innerhalb des JSON. Aktueller Code hat ein
        // " " nach "diagnostics": — Test treibt den Fix.
        var de = new DiagnosticEngine(new SourceManager());
        var sw = NewWriter();
        de.RenderJson(sw);
        Assert.Equal("{\"diagnostics\":[]}", sw.ToString());
    }

    [Fact]
    public void RenderJson_no_file_diagnostic_omits_file_block()
    {
        var de = new DiagnosticEngine(new SourceManager());
        de.Report("LYR-CLI0001", Severity.Error, default, "no input");
        var sw = NewWriter();
        de.RenderJson(sw);
        var expected =
            "{\"diagnostics\":[" +
            "{\"code\":\"LYR-CLI0001\",\"severity\":\"error\"," +
            "\"message\":\"no input\"}" +
            "]}";
        Assert.Equal(expected, sw.ToString());
    }

    [Fact]
    public void RenderJson_file_diagnostic_includes_position_block()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", "abc");
        var de = new DiagnosticEngine(sm);
        de.Report("X", Severity.Error, new Span(id, 1, 2), "msg");
        var sw = NewWriter();
        de.RenderJson(sw);
        var expected =
            "{\"diagnostics\":[" +
            "{\"code\":\"X\",\"severity\":\"error\"," +
            "\"file\":\"test.lyr\"," +
            "\"start\":{\"line\":1,\"column\":2,\"offset\":1}," +
            "\"end\":{\"line\":1,\"column\":3,\"offset\":2}," +
            "\"message\":\"msg\"}" +
            "]}";
        Assert.Equal(expected, sw.ToString());
    }

    [Fact]
    public void RenderJson_escapes_double_quotes_in_message()
    {
        var de = new DiagnosticEngine(new SourceManager());
        de.Report("X", Severity.Error, default, "say \"hi\"");
        var sw = NewWriter();
        de.RenderJson(sw);
        Assert.Contains("\"message\":\"say \\\"hi\\\"\"", sw.ToString());
    }

    [Fact]
    public void RenderJson_escapes_backslash_in_file_path()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("c:\\foo\\bar.lyr", "abc");
        var de = new DiagnosticEngine(sm);
        de.Report("X", Severity.Error, new Span(id, 0, 1), "msg");
        var sw = NewWriter();
        de.RenderJson(sw);
        Assert.Contains("\"file\":\"c:\\\\foo\\\\bar.lyr\"", sw.ToString());
    }

    [Fact]
    public void RenderJson_escapes_newline_in_message()
    {
        var de = new DiagnosticEngine(new SourceManager());
        de.Report("X", Severity.Error, default, "line1\nline2");
        var sw = NewWriter();
        de.RenderJson(sw);
        Assert.Contains("\"message\":\"line1\\nline2\"", sw.ToString());
    }

    [Fact]
    public void RenderJson_escapes_control_chars_as_unicode()
    {
        var de = new DiagnosticEngine(new SourceManager());
        de.Report("X", Severity.Error, default, "x\u0001y");
        var sw = NewWriter();
        de.RenderJson(sw);
        Assert.Contains("\"message\":\"x\\u0001y\"", sw.ToString());
    }

    [Fact]
    public void RenderJson_passes_non_ascii_through()
    {
        var de = new DiagnosticEngine(new SourceManager());
        de.Report("X", Severity.Error, default, "ä日🌍");
        var sw = NewWriter();
        de.RenderJson(sw);
        Assert.Contains("\"message\":\"ä日🌍\"", sw.ToString());
    }

    [Fact]
    public void RenderJson_separates_multiple_diagnostics_with_comma()
    {
        var de = new DiagnosticEngine(new SourceManager());
        de.Report("A", Severity.Error, default, "first");
        de.Report("B", Severity.Error, default, "second");
        var sw = NewWriter();
        de.RenderJson(sw);
        Assert.Equal(
            "{\"diagnostics\":[" +
            "{\"code\":\"A\",\"severity\":\"error\",\"message\":\"first\"}," +
            "{\"code\":\"B\",\"severity\":\"error\",\"message\":\"second\"}" +
            "]}",
            sw.ToString());
    }

    [Fact]
    public void RenderJson_all_three_severities_as_strings()
    {
        var de = new DiagnosticEngine(new SourceManager());
        de.Report("E", Severity.Error,   default, "");
        de.Report("W", Severity.Warning, default, "");
        de.Report("H", Severity.Hint,    default, "");
        var sw = NewWriter();
        de.RenderJson(sw);
        var s = sw.ToString();
        Assert.Contains("\"severity\":\"error\"",   s);
        Assert.Contains("\"severity\":\"warning\"", s);
        Assert.Contains("\"severity\":\"hint\"",    s);
    }

    [Fact]
    public void RenderJson_renders_sorted()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("t.lyr", "abc");
        var de = new DiagnosticEngine(sm);
        de.Report("Z", Severity.Error, new Span(id, 2, 3), "later");
        de.Report("A", Severity.Error, new Span(id, 0, 1), "earlier");
        var sw = NewWriter();
        de.RenderJson(sw);
        var s = sw.ToString();
        Assert.True(s.IndexOf("earlier") < s.IndexOf("later"));
    }

    [Fact]
    public void RenderJson_null_output_throws()
    {
        var de = new DiagnosticEngine(new SourceManager());
        Assert.Throws<ArgumentNullException>(() => de.RenderJson(null!));
    }

    [Fact]
    public void RenderText_null_output_throws()
    {
        var de = new DiagnosticEngine(new SourceManager());
        Assert.Throws<ArgumentNullException>(() => de.RenderText(null!));
    }
}