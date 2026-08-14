using System.Runtime.CompilerServices;
using System.Text;
using Lyric.Ir;

namespace Lyric.Tests.Ir;

/// <summary>
/// Golden tests for the <see cref="IrPrinter"/>. Every fixture is built as an IR object in
/// <see cref="Fixtures"/>, dumped and compared against the committed snapshot (golden/&lt;name&gt;.ir).
///
/// Snapshots are NOT maintained by hand: produce them once with LYRIC_UPDATE_SNAPSHOTS=1, read them
/// over, commit — the same mechanics as the parser goldens. From then on the comparison locks the dump
/// format.
/// </summary>
public class GoldenTests
{
    private static bool UpdateMode =>
        Environment.GetEnvironmentVariable("LYRIC_UPDATE_SNAPSHOTS") is "1" or "true";

    // [CallerFilePath] gives the path of this file at compile time, so the snapshots lie in the source tree
    // rather than in the bin/ output.
    private static string GoldenDir([CallerFilePath] string thisFile = "")
        => Path.Combine(Path.GetDirectoryName(thisFile)!, "golden");

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");

    private static void Check(string name)
    {
        var dir = GoldenDir();
        Directory.CreateDirectory(dir);
        var snapshotPath = Path.Combine(dir, name + ".ir");
        var actual = Normalize(IrPrinter.Dump(Fixtures.Build(name)));

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

    [Theory]
    [InlineData("single_block")]       // load + binop + ret, the base layout
    [InlineData("comparison")]         // the destination type bool differs from the operand type i64
    [InlineData("diamond")]            // condbr, br and store over 4 blocks
    [InlineData("void_store")]         // void: a bare ret plus a store without a dest
    [InlineData("convert")]            // a convert with From and To visible
    [InlineData("two_functions_call")] // CallContext: name and return type, a value call and a void call
    [InlineData("loop")]               // a back edge: br back to an earlier block
    public void Golden_ir_matches_snapshot(string name) => Check(name);
}
