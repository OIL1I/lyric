using Lyric.Core;
using Lyric.Lsp.Analysis;
using Lyric.Lsp.Documents;
using Lyric.Lsp.Protocol;

namespace Lyric.Tests.Lsp;

/// <summary>
/// The compiler diagnostic as the protocol wants it: four severities, and the notes as related
/// information. Tested against the mapper directly — the harness tests prove delivery, this one
/// proves the shape.
/// </summary>
public sealed class DiagnosticMappingTests
{
    [Fact]
    public void Every_severity_has_a_protocol_severity()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("map.lyr", "abcdef");
        var de = new DiagnosticEngine(sm);
        de.Report("E", Severity.Error,   new Span(id, 0, 1), "");
        de.Report("W", Severity.Warning, new Span(id, 1, 2), "");
        de.Report("I", Severity.Info,    new Span(id, 2, 3), "");
        de.Report("H", Severity.Hint,    new Span(id, 3, 4), "");

        var mapped = DiagnosticMapper.ForFile(sm, de.SortedSnapshot(), id);

        Assert.Equal(
            [LspSeverity.Error, LspSeverity.Warning, LspSeverity.Information, LspSeverity.Hint],
            mapped.Select(d => d.Severity));
    }

    [Fact]
    public void A_note_with_a_place_becomes_related_information_there()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("map.lyr", "let x = 1;\nlet x = 2;");
        var de = new DiagnosticEngine(sm);
        de.Report("D", Severity.Error, new Span(id, 15, 16), "'x' is already defined",
            new DiagnosticNote(new Span(id, 4, 5), "previous definition"));

        var mapped = Assert.Single(DiagnosticMapper.ForFile(sm, de.SortedSnapshot(), id));
        var related = Assert.Single(mapped.RelatedInformation!);

        Assert.Equal("previous definition", related.Message);
        Assert.Equal(DocumentUri.FromFilePath("map.lyr"), related.Location.Uri);
        Assert.Equal(0, related.Location.Range.Start.Line);
        Assert.Equal(4, related.Location.Range.Start.Character);
    }

    [Fact]
    public void A_note_without_a_place_is_anchored_at_the_diagnostic()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("map.lyr", "abc");
        var de = new DiagnosticEngine(sm);
        de.Report("X", Severity.Error, new Span(id, 1, 2), "msg",
            new DiagnosticNote("try an annotation"));

        var mapped = Assert.Single(DiagnosticMapper.ForFile(sm, de.SortedSnapshot(), id));
        var related = Assert.Single(mapped.RelatedInformation!);

        Assert.Equal(mapped.Range, related.Location.Range);
    }

    [Fact]
    public void Unused_and_unreachable_codes_carry_the_unnecessary_tag()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("map.lyr", "abcdef");
        var de = new DiagnosticEngine(sm);
        // SEM0074 lost its Deprecated tag with 2.0: it is a plain error now, and an error with
        // strikethrough would read as "still works, just frowned upon".
        de.Report("LYR-SEM0071", Severity.Warning, new Span(id, 0, 1), "unused");
        de.Report("LYR-SEM0073", Severity.Warning, new Span(id, 1, 2), "unreachable");
        de.Report("LYR-SEM0076", Severity.Warning, new Span(id, 2, 3), "deprecated form");
        de.Report("LYR-SEM0074", Severity.Error,   new Span(id, 3, 4), "static extension");

        var mapped = DiagnosticMapper.ForFile(sm, de.SortedSnapshot(), id);

        Assert.Equal([LspDiagnosticTag.Unnecessary], mapped[0].Tags);
        Assert.Equal([LspDiagnosticTag.Unnecessary], mapped[1].Tags);
        Assert.Equal([LspDiagnosticTag.Deprecated], mapped[2].Tags);
        Assert.Null(mapped[3].Tags);
    }

    [Fact]
    public void A_note_free_diagnostic_carries_no_related_information()
    {
        // null rather than empty: the serializer omits the key, and the wire stays what it was
        // before notes existed.
        var sm = new SourceManager();
        var id = sm.AddVirtual("map.lyr", "abc");
        var de = new DiagnosticEngine(sm);
        de.Report("X", Severity.Error, new Span(id, 0, 1), "msg");

        var mapped = Assert.Single(DiagnosticMapper.ForFile(sm, de.SortedSnapshot(), id));
        Assert.Null(mapped.RelatedInformation);
    }
}
