using System.Runtime.CompilerServices;
using System.Text;
using Lyric.Core;
using Lyric.Lexing;
using Xunit;

namespace Lyric.Tests.Lexing;

/// <summary>
/// Golden-Tests für den Lexer: jede Fixture (golden/&lt;name&gt;.lyr) wird getokenized,
/// der Token-Dump (+ gerenderte Diagnostics bei Negativ-Fällen) gegen den committeten
/// Snapshot (golden/&lt;name&gt;.tokens) verglichen.
///
/// Snapshots werden NICHT von Hand gepflegt: einmal mit Env-Var LYRIC_UPDATE_SNAPSHOTS=1
/// erzeugen, drüberlesen, committen. Danach lockt der Vergleich den Token-Stream fest.
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
    // Positiv — keine Diagnostics, nur Token-Stream.
    [InlineData("operators")]
    [InlineData("literals")]
    [InlineData("fstring")]
    [InlineData("program")]
    // Negativ — Snapshot enthält Token-Stream UND gerenderte Diagnostics.
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
        // M1-Exit-Kriterium (ROADMAP §M1): examples/hello.lyr tokenisiert fehlerfrei.
        var path = Path.Combine(RepoRoot(), "examples", "hello.lyr");
        Assert.True(File.Exists(path), $"missing example: {path}");

        var (diag, _) = LexAndDump("hello.lyr", File.ReadAllText(path, Encoding.UTF8));
        Assert.False(diag.HasErrors);
    }
}
