namespace Lyric.Tests.Cli;

/// <summary>
/// Der wichtigste Test dieses Projekts (ADR-017).
///
/// <para>Die Kernaussage der Binary-Trennung ist eine Abhaengigkeits-Aussage, keine
/// Datei-Aussage: <c>lyrvm</c> darf nichts Compiler-seitiges enthalten und <c>lyrc</c> nichts
/// Runtime-seitiges. Vor dem Schnitt war die erste Richtung verletzt — <c>Lyric.Bytecode</c>
/// referenzierte <c>Lyric.Ir</c>, das <c>Lyric.Sema</c> referenzierte, und damit zog jede Runtime
/// die gesamte Front-End-Kette mit.</para>
///
/// <para>Geprueft wird das <b>Ausgabeverzeichnis</b>, nicht die Metadaten: die ehrliche Frage
/// lautet „was liegt neben <c>lyrvm.exe</c>, wenn ich es ausliefere". Eine ungenutzte
/// Assembly-Referenz waere hier egal, eine mitkopierte DLL ist es nicht.</para>
///
/// <para><b>Seit der Assembly-Konsolidierung</b> gibt es genau drei Bibliotheken, und die
/// Aussage wird dadurch schaerfer statt schwaecher: nicht mehr „diese acht Dateien duerfen nicht
/// dabei sein", sondern „es sind genau diese, und keine andere". Die Verbotsliste musste bei
/// jedem neuen Projekt gepflegt werden — was niemand tat.</para>
/// </summary>
public sealed class ArchitectureTests
{
    /// <summary>Alles zwischen Quelltext und <c>.lyrbc</c>: Lexer, Parser, Resolver, Sema, IR,
    /// Bytecode-Writer, Pipeline. Eine Runtime braucht nichts davon — sie bekommt fertige
    /// Bytes.</summary>
    private const string Frontend = "lyrfe.dll";

    /// <summary>Der Interpreter. Ein Compiler fuehrt nichts aus.</summary>
    private const string Runtime = "lyrrt.dll";

    /// <summary>Diagnostik, Quelltextverwaltung und die Leseseite des Formats — der gemeinsame
    /// Vertrag, den beide Seiten brauchen.</summary>
    private const string Shared = "lyrcore.dll";

    [Fact]
    public void Lyrvm_ships_exactly_the_shared_contract_and_the_interpreter()
    {
        // Positiv formuliert, damit der Test auch faellt, wenn jemand die Runtime durch eine
        // dritte Kante *erweitert*, die auf keiner Verbotsliste steht.
        AssertShips("Lyrvm", Shared, Runtime, "lyrvm.dll");
    }

    [Fact]
    public void Lyrvm_ships_nothing_from_the_compiler()
    {
        Assert.DoesNotContain(Frontend, LyricAssemblies("Lyrvm"));
    }

    [Fact]
    public void Lyrc_ships_exactly_the_shared_contract_and_the_frontend()
    {
        AssertShips("Lyrc", Shared, Frontend, "lyrc.dll");
    }

    [Fact]
    public void Lyrc_ships_no_runtime()
    {
        // Die Gegenrichtung. Sie ist weniger dramatisch — ein Compiler mit Interpreter waere
        // bloss fett, nicht widerspruechlich — aber sie haelt die Rollen sauber: 'lyrc' fuehrt
        // nichts aus, also hat es auch nichts, womit es koennte.
        //
        // Sie ist auch der Grund, warum die Leseseite des Formats in lyrcore liegt und nicht bei
        // der VM: der Bytecode-Writer braucht dieselben Op-Codes und Typ-Tags. Laege das Lesen
        // bei der Runtime, zoege jeder Compiler-Build den Interpreter mit und dieser Test fiele.
        Assert.DoesNotContain(Runtime, LyricAssemblies("Lyrc"));
    }

    [Fact]
    public void Lyric_driver_ships_both_sides()
    {
        // Die Gegenprobe zu den Tests oben: der Treiber *muss* beides haben, sonst pruefen sie
        // nur, dass die Projekte leer sind.
        AssertShips("Lyric.Cli", Shared, Frontend, Runtime, "lyric.dll");
    }

    [Fact]
    public void Compiling_binaries_carry_the_stdlib_and_the_runtime_does_not()
    {
        // Die Stdlib ist Quelltext (M6) und wird beim Compilieren gebraucht, nicht beim
        // Ausfuehren: ein .lyrbc traegt seine Importe symbolisch, die Runtime bindet sie ueber
        // NativeRegistry. Liegt stdlib/ neben lyrvm, ist entweder die Content-Regel falsch
        // verdrahtet oder die Runtime tut mehr, als sie soll.
        Assert.True(Directory.Exists(Path.Combine(Toolchain.OutputDirectory("Lyrc"), "stdlib")));
        Assert.True(Directory.Exists(Path.Combine(Toolchain.OutputDirectory("Lyric.Cli"), "stdlib")));
        Assert.False(Directory.Exists(Path.Combine(Toolchain.OutputDirectory("Lyrvm"), "stdlib")));
    }

    /// <summary>Beide Seiten werden sortiert: welche Reihenfolge das Dateisystem liefert, ist
    /// keine Aussage ueber die Architektur, und eine Erwartung in Ordinal-Sortierung waere nur
    /// ein Raetsel fuer den naechsten Leser.</summary>
    private static void AssertShips(string project, params string[] expected) =>
        Assert.Equal(
            expected.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            LyricAssemblies(project));

    /// <summary>
    /// Die ausgelieferten Lyric-Assemblies eines Binaries, alphabetisch.
    ///
    /// <para>Erfasst wird alles, was <c>lyr</c> heisst — <b>auch Unbekanntes</b>. Beim Umbau auf
    /// drei Assemblies lagen in <c>bin/</c> noch die DLLs der alten Projektnamen aus einem
    /// frueheren Build; ein Vergleich gegen eine Verbotsliste haette sie uebersehen, ein
    /// Gleichheitsvergleich faellt darueber. Das ist erwuenscht: was neben der exe liegt, wird
    /// ausgeliefert, egal wer es dort abgelegt hat.</para>
    /// </summary>
    private static string[] LyricAssemblies(string project) =>
        Directory.GetFiles(Toolchain.OutputDirectory(project), "lyr*.dll")
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
}
