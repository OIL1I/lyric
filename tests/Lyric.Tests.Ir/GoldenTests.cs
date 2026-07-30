using System.Runtime.CompilerServices;
using System.Text;
using Lyric.Ir;

namespace Lyric.Tests.Ir;

/// <summary>
/// Golden-Tests für den <see cref="IrPrinter"/>. Jede Fixture wird in <see cref="Fixtures"/>
/// als IR-Objekt gebaut, gedumpt und gegen den committeten Snapshot (golden/&lt;name&gt;.ir)
/// verglichen.
///
/// Snapshots werden NICHT von Hand gepflegt: einmal mit LYRIC_UPDATE_SNAPSHOTS=1 erzeugen,
/// drüberlesen, committen (gleiche Mechanik wie die Parser-Goldens). Danach lockt der
/// Vergleich das Dump-Format fest.
/// </summary>
public class GoldenTests
{
    private static bool UpdateMode =>
        Environment.GetEnvironmentVariable("LYRIC_UPDATE_SNAPSHOTS") is "1" or "true";

    // [CallerFilePath] liefert den Pfad dieser Datei zur Compile-Zeit → Snapshots liegen
    // im Source-Baum, nicht im bin/-Output.
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
    [InlineData("single_block")]       // load + binop + ret, Grundlayout
    [InlineData("comparison")]         // Dest-Typ bool ≠ Operandentyp i64
    [InlineData("diamond")]            // condbr / br / store über 4 Blöcke
    [InlineData("void_store")]         // void: nacktes ret + dest-loses store
    [InlineData("convert")]            // convert mit From/To sichtbar
    [InlineData("two_functions_call")] // CallContext: Name + Rückgabetyp, value- + void-Call
    [InlineData("loop")]               // Back-Edge: br zurück auf einen früheren Block
    public void Golden_ir_matches_snapshot(string name) => Check(name);
}
