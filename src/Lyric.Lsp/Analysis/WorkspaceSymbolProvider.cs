using Lyric.Core;
using Lyric.Lsp.Documents;
using Lyric.Lsp.Protocol;

namespace Lyric.Lsp.Analysis;

/// <summary>
/// The declarations of everything analysed, filtered by a query — what an editor's
/// go-to-symbol-in-workspace dialog asks for.
///
/// <para>Built on the same walk the outline uses, flattened: the flat
/// <see cref="SymbolInformation"/> shape is what this request answers with, and a second walk with
/// its own kind mapping would drift from the outline's the first time a declaration form is
/// added. The container name is the parent entry's name, which is all the flat shape can say
/// about nesting.</para>
///
/// <para>Matching is a case-insensitive substring. The protocol tells clients to expect generous
/// answers and filter themselves — fuzzy scoring is the CLIENT's feature, and an empty query
/// means "everything", which the editor then narrows keystroke by keystroke.</para>
/// </summary>
public static class WorkspaceSymbolProvider
{
    /// <summary>More than anyone scrolls through; the query is how the list gets shorter.</summary>
    private const int Limit = 512;

    public static IReadOnlyList<SymbolInformation> Find(
        IReadOnlyList<AnalysisSnapshot> compilations, string query)
    {
        var results = new List<SymbolInformation>();

        // Two compilations can hold the same file — a project and a lone script importing into
        // it. One entry per place, whichever compilation got there first.
        var seen = new HashSet<(string Path, int Line, int Character, string Name)>();

        foreach (var snapshot in compilations)
        {
            foreach (var module in snapshot.Model.Compilation.Modules)
            {
                // The standard library and native SDKs are not the user's workspace. Their
                // declarations are reachable through the jump; a symbol search that lists every
                // stdlib function buries the project under it.
                if (snapshot.Model.Compilation.IsNative(module)) continue;

                var ast = snapshot.Model.Compilation.AstOf(module);
                if (!ast.Span.File.IsValid) continue;

                var path = snapshot.Sources.GetPath(ast.Span.File);
                var uri = DocumentUri.FromFilePath(path);

                Flatten(snapshot, DocumentSymbolProvider.Of(snapshot.Sources, ast),
                    container: null, uri, path, query, seen, results);

                if (results.Count >= Limit) return results;
            }
        }

        return results;
    }

    private static void Flatten(
        AnalysisSnapshot snapshot, IReadOnlyList<DocumentSymbol> symbols, string? container,
        string uri, string path, string query, HashSet<(string, int, int, string)> seen,
        List<SymbolInformation> results)
    {
        foreach (var symbol in symbols)
        {
            if (results.Count >= Limit) return;

            if (Matches(symbol.Name, query)
                && seen.Add((path, symbol.SelectionRange.Start.Line,
                    symbol.SelectionRange.Start.Character, symbol.Name)))
            {
                results.Add(new SymbolInformation
                {
                    Name = symbol.Name,
                    Kind = symbol.Kind,
                    Location = new Location { Uri = uri, Range = symbol.SelectionRange },
                    ContainerName = container,
                });
            }

            if (symbol.Children is { } children)
                Flatten(snapshot, children, symbol.Name, uri, path, query, seen, results);
        }
    }

    private static bool Matches(string name, string query) =>
        query.Length == 0 || name.Contains(query, StringComparison.OrdinalIgnoreCase);
}
