using Lyric.Core;
using Lyric.Lexing;
using Lyric.Parsing;

namespace Lyric.Formatting;

/// <summary>
/// The whole pipeline of formatting one file: parse, gather the comments, build the document,
/// render.
///
/// <para>A file that does not parse is NOT formatted — the answer is <c>null</c> and the
/// diagnostics stand in the engine. A formatter that writes its guess over a file with a typo
/// in it destroys the one thing the user still had: their text.</para>
///
/// <para>The comments come from a lexer pass of their own, with a throwaway diagnostic engine:
/// the parse just reported every lexical finding, and a second copy of each would double the
/// output for one mistake. Doc comments are tokens in that stream and join the plain comments
/// under one positional mechanism — where a comment stands is decided by where it STANDS, for
/// all three forms alike.</para>
/// </summary>
public static class Formatter
{
    public static string? Format(SourceManager sources, FileId file, DiagnosticEngine diagnostics)
    {
        var module = new Parser(sources, file, diagnostics).ParseModule();
        if (diagnostics.HasErrors) return null;

        var comments = CollectComments(sources, file);
        return DocRenderer.Render(AstFormatter.Build(module, sources.GetText(file), comments));
    }

    private static IReadOnlyList<Trivia> CollectComments(SourceManager sources, FileId file)
    {
        var lexer = new Lexer(sources, file, new DiagnosticEngine(sources), collectTrivia: true);

        var docs = new List<Trivia>();
        Token token;
        do
        {
            token = lexer.Next();
            if (token.TokenKind == TokenKind.DocComment)
                docs.Add(new Trivia(TriviaKind.DocComment, token.Span));
        } while (token.TokenKind != TokenKind.Eof);

        // Both lists are already source-ordered; the merge keeps them that way.
        var merged = new List<Trivia>(lexer.CollectedTrivia.Count + docs.Count);
        merged.AddRange(lexer.CollectedTrivia);
        merged.AddRange(docs);
        merged.Sort((a, b) => a.Span.Start.CompareTo(b.Span.Start));
        return merged;
    }
}
