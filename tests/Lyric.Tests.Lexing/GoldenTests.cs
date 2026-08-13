using System.Runtime.CompilerServices;
using System.Text;
using Lyric.Core;
using Lyric.Lexing;
using Xunit;

namespace Lyric.Tests.Lexing;

/// <summary>
/// Golden tests for the lexer: every fixture (golden/&lt;name&gt;.lyr) is tokenized and the token dump,
/// plus rendered diagnostics for negative cases, is compared against the committed snapshot
/// (golden/&lt;name&gt;.tokens).
///
/// Snapshots are NOT maintained by hand: produce them once with the environment variable
/// LYRIC_UPDATE_SNAPSHOTS=1, read them over, commit. From then on the comparison locks the token
/// stream.
/// </summary>
public class GoldenTests
{
    private static bool UpdateMode =>
        Environment.GetEnvironmentVariable("LYRIC_UPDATE_SNAPSHOTS") is "1" or "true";

    // [CallerFilePath] yields this file's path at compile time, so snapshots are read and
    // written in the source tree rather than in the bin/ output.
    private static string GoldenDir([CallerFilePath] string thisFile = "")
        => Path.Combine(Path.GetDirectoryName(thisFile)!, "golden");

    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");

    private static (DiagnosticEngine diag, string dump) LexAndDump(string displayName, string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual(displayName, source);
        var de = new DiagnosticEngine(sm);
        var lexer = new Lexer(sm, id, de);

        var tokens = new List<Token>();
        Token t;
        do
        {
            t = lexer.Next();
            tokens.Add(t);
        } while (t.TokenKind != TokenKind.Eof);

        var dump = Normalize(TokenDumper.Dump(tokens, sm));
        if (!dump.EndsWith('\n')) dump += "\n";

        if (de.Count == 0) return (de, dump);

        var sw = new StringWriter(new StringBuilder()) { NewLine = "\n" };
        de.RenderText(sw);
        return (de, dump + "\n=== diagnostics ===\n" + Normalize(sw.ToString()));
    }

    [Theory]
    // Positive cases: no diagnostics, only the token stream.
    [InlineData("operators")]
    [InlineData("literals")]
    [InlineData("fstring")]
    [InlineData("program")]
    // Negative cases: the snapshot contains the token stream AND the rendered diagnostics.
    [InlineData("unterminated")]
    [InlineData("bad_suffix")]
    public void Golden_fixture_matches_snapshot(string name)
    {
        var dir = GoldenDir();
        var inputPath = Path.Combine(dir, name + ".lyr");
        var snapshotPath = Path.Combine(dir, name + ".tokens");

        Assert.True(File.Exists(inputPath), $"missing fixture: {inputPath}");

        var source = File.ReadAllText(inputPath, Encoding.UTF8);
        var (_, actual) = LexAndDump(name + ".lyr", source);

        if (UpdateMode)
        {
            File.WriteAllText(snapshotPath, actual, new UTF8Encoding(false));
            return;
        }

        Assert.True(File.Exists(snapshotPath),
            $"missing snapshot: {snapshotPath}\n" +
            "Run once with LYRIC_UPDATE_SNAPSHOTS=1 to generate it, then review and commit.");

        var expected = Normalize(File.ReadAllText(snapshotPath, Encoding.UTF8));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Example_hello_tokenizes_without_errors()
    {
        // examples/hello.lyr tokenizes without errors.
        var path = Path.Combine(RepoRoot(), "examples", "hello.lyr");
        Assert.True(File.Exists(path), $"missing example: {path}");

        var (diag, _) = LexAndDump("hello.lyr", File.ReadAllText(path, Encoding.UTF8));
        Assert.False(diag.HasErrors);
    }
}
