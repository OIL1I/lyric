using System.Text.RegularExpressions;
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
    public void The_driver_carries_neither_compiler_nor_runtime_of_its_own()
    {
        // ADR-019, und die schaerfste Aussage dieser Datei: der Treiber COMPILIERT NICHTS und
        // FUEHRT NICHTS AUS. Er startet Werkzeuge — also liegen die Werkzeuge daneben, aber ihre
        // Bibliotheken sind nicht seine.
        //
        // Vor ADR-019 stand hier die Gegenprobe „der Treiber muss beides haben". Dass sie sich
        // umgedreht hat, IST die Entscheidung: vorher war lyric ein zweiter Compiler mit bequemerer
        // Oberflaeche, jetzt ist es ein Dispatcher.
        var shipped = LyricAssemblies("Lyric.Cli");

        Assert.Contains(Shared, shipped);        // Exit- und Diagnose-Codes
        Assert.Contains("lyric.dll", shipped);   // er selbst
        Assert.Contains("lyrc.dll", shipped);    // die Werkzeuge liegen daneben,
        Assert.Contains("lyrvm.dll", shipped);   // weil er sie dort sucht
        Assert.Contains("lyrrepl.dll", shipped); // seit ADR-021 auch die REPL
    }

    [Fact]
    public void Every_tool_the_driver_dispatches_to_lies_next_to_it()
    {
        // Der Treiber sucht seine Werkzeuge NEBEN der eigenen exe (Tool.Resolve). Fehlt eines,
        // meldet er das erst zur Laufzeit — und zwar dem Nutzer, nicht dem Entwickler. Die Liste
        // hier ist dieselbe wie Tool.All; waechst sie, faellt dieser Test, bis das Kopier-Target
        // in Lyric.Cli.csproj nachgezogen ist.
        var shipped = LyricAssemblies("Lyric.Cli");

        foreach (var tool in new[] { "lyrc.dll", "lyrvm.dll", "lyrrepl.dll" })
            Assert.Contains(tool, shipped);
    }

    [Fact]
    public void The_repl_is_the_one_tool_that_needs_both_sides()
    {
        // ADR-021. Die Ausnahme, und sie steht hier ausdruecklich statt als Luecke: eine REPL
        // uebersetzt UND fuehrt aus, und der Zustand muss dazwischen leben — 'lyric run' loest
        // das ueber zwei Subprozesse, was interaktiv nicht geht.
        //
        // Dass sie beide Bibliotheken hat, widerspricht ADR-017 nicht: die Kante trennt die
        // BIBLIOTHEKEN, sie verbietet nicht, beide zu benutzen. Dass man sie kombinieren kann,
        // ohne sie aufzuweichen, ist der Beweis, dass der Schnitt sauber liegt — und die
        // Trennung fuer lyrc und lyrvm gilt unveraendert weiter (die Tests darueber).
        var shipped = LyricAssemblies("Lyrrepl");

        Assert.Contains(Shared, shipped);
        Assert.Contains(Frontend, shipped);
        Assert.Contains(Runtime, shipped);
        Assert.Contains("lyrrepl.dll", shipped);
    }

    [Fact]
    public void The_driver_has_no_reference_of_its_own_to_frontend_or_runtime()
    {
        // Die Bibliotheken lyrfe/lyrrt liegen im Verzeichnis — aber weil die WERKZEUGE sie
        // brauchen, nicht der Treiber. Was ihn bindet, steht in seiner Projektdatei, und dort
        // darf genau eine Kante stehen.
        var project = File.ReadAllText(Path.Combine(Toolchain.RepositoryRoot, "src", "Lyric.Cli",
            "Lyric.Cli.csproj"));

        // ReplaceLineEndings() zuerst: die Projektdateien im Repo haben gemischte Zeilenenden, und
        // ein Split auf Environment.NewLine faende auf Windows in einer LF-Datei gar nichts — der
        // Test waere dann still gruen, weil die Liste leer ist statt richtig.
        var referenced = project.ReplaceLineEndings()
            .Split(Environment.NewLine, StringSplitOptions.TrimEntries)
            .Where(line => line.Contains("ProjectReference")
                           && !line.Contains("ReferenceOutputAssembly"))
            .ToArray();

        Assert.Single(referenced);
        Assert.Contains("Lyric.Core", referenced[0]);
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

    /// <summary>
    /// Was die README als Auslieferung abdruckt, liefert <c>build/publish.proj</c> auch.
    ///
    /// <para><b>Der Anlass ist ein Befund aus M10/E6.</b> <c>lyrembed.dll</c> stand in der README
    /// und in <c>Doku.md</c> §21 als das, was ein Host referenziert — und landete in keiner
    /// Auslieferung, weil kein Binary sie referenziert und sie selbst nicht in der Publish-Liste
    /// stand. Ein dokumentierter Lieferposten ausserhalb des Artefakts; dieselbe Luecke hatte M9
    /// vier Mal.</para>
    ///
    /// <para>Geprueft wird die Richtung, die den Fehler faengt: <b>von der README zur
    /// Publish-Liste</b>. Umgekehrt („was publish.proj liefert, nennt die README") waere ebenfalls
    /// wahr gewesen und haette nichts gemerkt.</para>
    /// </summary>
    [Fact]
    public void Everything_the_readme_ships_is_actually_published()
    {
        var readme = File.ReadAllText(Path.Combine(Toolchain.RepositoryRoot, "README.md"));

        // Der Block unter "### Shipping", zwischen der Ergebnis-Zusage und dem Satz danach.
        var block = Regex.Match(readme, @"What ends up there, and nothing else:\s*```(.*?)```",
            RegexOptions.Singleline);
        Assert.True(block.Success, "README no longer prints what a publish produces");

        var named = Regex.Matches(block.Groups[1].Value, @"\b([a-z.]+\.(?:dll|exe))\b")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.NotEmpty(named);

        var produced = PublishedAssemblies();
        foreach (var file in named)
            Assert.Contains(Path.GetFileNameWithoutExtension(file), produced,
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Die Assembly-Namen, die ein Publish erzeugt: die Projekte aus
    /// <c>publish.proj</c> plus alles, was sie transitiv referenzieren.</summary>
    private static IReadOnlyCollection<string> PublishedAssemblies()
    {
        var root = Toolchain.RepositoryRoot;
        var project = File.ReadAllText(Path.Combine(root, "build", "publish.proj"));

        var queue = new Queue<string>(Regex.Matches(project, @"<Binary Include=""([^""]+)""")
            .Select(m => Path.GetFullPath(Path.Combine(root, "build", m.Groups[1].Value))));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (queue.Count > 0)
        {
            var path = queue.Dequeue();
            if (!seen.Add(path) || !File.Exists(path)) continue;

            var text = File.ReadAllText(path);
            var assembly = Regex.Match(text, @"<AssemblyName>([^<]+)</AssemblyName>");
            names.Add(assembly.Success
                ? assembly.Groups[1].Value
                : Path.GetFileNameWithoutExtension(path));

            foreach (Match reference in Regex.Matches(text, @"<ProjectReference Include=""([^""]+)"""))
                queue.Enqueue(Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(path)!, reference.Groups[1].Value)));
        }

        return names;
    }
}
