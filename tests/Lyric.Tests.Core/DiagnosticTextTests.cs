using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Lyric.Tests.Core;

/// <summary>
/// What a diagnostic is allowed to say.
///
/// <para>A message points at the code the reader is looking at, never at a document. A citation ages
/// in two ways at once — the file gets renamed and the section gets renumbered — and both had
/// happened: five messages named <c>Sprache.md</c>, which has been <c>docs/Grammar.md</c> for a
/// while, and cited sections §10 and §11 of a document that has seven. Someone following either was
/// sent nowhere twice.</para>
///
/// <para>Checked over the SOURCE rather than over emitted messages, because there is no way to make a
/// compiler emit every diagnostic it can. A string literal is the thing being constrained, so a
/// string literal is what is inspected; comments and XML documentation are free to cite whatever they
/// like, and they do.</para>
/// </summary>
public sealed class DiagnosticTextTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    /// <summary>A double-quoted literal on one line. Verbatim and raw literals spanning lines are not
    /// matched, and no diagnostic message is written as one.</summary>
    private static readonly Regex StringLiteral = new("\"(?:[^\"\\\\\\n]|\\\\.)*\"", RegexOptions.Compiled);

    /// <summary>What must not stand inside one: a section sign, or the name of a document.</summary>
    private static readonly Regex Citation = new("§|\\.md\\b", RegexOptions.Compiled);

    public static TheoryData<string> ProductionSources()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(
            Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            var path = file.Replace('\\', '/');
            if (path.Contains("/obj/") || path.Contains("/bin/")) continue;
            data.Add(file);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ProductionSources))]
    public void No_string_literal_cites_a_document(string path)
    {
        var offenders = new List<string>();
        var lines = File.ReadAllLines(path);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart();

            // A '//' or '///' line is a comment in full. Citing a document there is the point of
            // having documents.
            if (line.StartsWith("//", StringComparison.Ordinal)) continue;

            foreach (Match literal in StringLiteral.Matches(lines[i]))
                if (Citation.IsMatch(literal.Value))
                    offenders.Add($"  line {i + 1}: {literal.Value}");
        }

        Assert.True(offenders.Count == 0,
            $"{Path.GetFileName(path)} has string literals citing a document:\n"
            + string.Join('\n', offenders)
            + "\n\nA diagnostic names what is wrong, not where to read about it.");
    }
}
