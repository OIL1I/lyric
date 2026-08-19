using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Lyric.AST;
using Lyric.Core;
using Lyric.Formatting;
using Lyric.Lexing;
using Lyric.Parsing;

namespace Lyric.Tests.Formatting;

/// <summary>
/// The formatter against every real Lyric file in the repository — the standard library, the
/// examples and the project templates. No goldens here, invariants: the file formats, the
/// result is stable under a second pass, the reparse yields the same tree, and every comment
/// of the input is still in the output. The goldens of the other files pin taste; these pin
/// CORRECTNESS, on the corpus that exists rather than the cases someone thought of.
/// </summary>
public class CorpusTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();
        foreach (var root in new[] { "stdlib", "examples", "templates" })
        foreach (var file in Directory.GetFiles(Path.Combine(RepoRoot(), root), "*.lyr",
                     SearchOption.AllDirectories))
            data.Add(Path.GetRelativePath(RepoRoot(), file));
        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Formatting_preserves_the_program(string relativePath)
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), relativePath));

        var formatted = Format(source, relativePath);

        // Stable: the second pass changes nothing, or every save hook fights the last one.
        Assert.Equal(formatted, Format(formatted, relativePath + " (reformatted)"));

        // Meaning-preserving: the reparse builds the same tree, spans aside.
        Assert.Equal(ShapeOf(source), ShapeOf(formatted));

        // Nothing said in a comment is lost.
        foreach (var comment in CommentsOf(source))
            Assert.Contains(comment, formatted);
    }

    private static string Format(string source, string name)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual(name, source);
        var de = new DiagnosticEngine(sm);
        var formatted = Formatter.Format(sm, id, de);

        Assert.True(formatted is not null, $"{name} did not parse — corpus files must");
        return formatted!;
    }

    /// <summary>The AST dump with the spans stripped: what the program IS, not where it
    /// stood.</summary>
    private static string ShapeOf(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("<shape>", source);
        var de = new DiagnosticEngine(sm);
        var module = new Parser(sm, id, de).ParseModule();
        Assert.False(de.HasErrors);

        return Regex.Replace(AstDumper.Dump(module, sm), @" \[\d+\.\.\d+\)", "");
    }

    /// <summary>Every comment of the file, one text per entry, CRLF-normalized and
    /// trailing-trimmed the way the formatter prints them.</summary>
    private static IEnumerable<string> CommentsOf(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("<comments>", source);
        var lexer = new Lexer(sm, id, new DiagnosticEngine(sm), collectTrivia: true);

        var comments = new List<string>();
        Token token;
        do
        {
            token = lexer.Next();
            if (token.TokenKind == TokenKind.DocComment)
                comments.Add(Slice(source, token.Span));
        } while (token.TokenKind != TokenKind.Eof);

        comments.AddRange(lexer.CollectedTrivia.Select(t => Slice(source, t.Span)));
        return comments;
    }

    private static string Slice(string source, Span span) =>
        source.Substring(span.Start, span.End - span.Start).Replace("\r\n", "\n").TrimEnd('\r');
}
