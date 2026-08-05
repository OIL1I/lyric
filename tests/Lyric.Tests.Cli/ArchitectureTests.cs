namespace Lyric.Tests.Cli;

/// <summary>
/// Der wichtigste Test dieses Projekts (ADR-017).
///
/// <para>Die Kernaussage der Binary-Trennung ist eine Abhaengigkeits-Aussage, keine
/// Datei-Aussage: <c>lyrvm</c> darf nichts Compiler-seitiges enthalten. Vor dem Schnitt war das
/// verletzt — <c>Lyric.Bytecode</c> referenzierte <c>Lyric.Ir</c>, das <c>Lyric.Sema</c>
/// referenzierte, und damit zog jede Runtime die gesamte Front-End-Kette mit.</para>
///
/// <para>Geprueft wird das <b>Ausgabeverzeichnis</b>, nicht die Metadaten: die ehrliche Frage
/// lautet „was liegt neben <c>lyrvm.exe</c>, wenn ich es ausliefere". Eine ungenutzte
/// Assembly-Referenz waere hier egal, eine mitkopierte DLL ist es nicht.</para>
///
/// <para>Ohne diesen Test wandert die Kante innerhalb eines Meilensteins zurueck, und es faellt
/// niemandem auf — der Split funktioniert ja weiterhin, er bedeutet nur nichts mehr.</para>
/// </summary>
public sealed class ArchitectureTests
{
    /// <summary>Alles, was zwischen Quelltext und <c>.lyrbc</c> steht. Eine Runtime braucht
    /// nichts davon: sie bekommt fertige Bytes.</summary>
    private static readonly string[] CompilerAssemblies =
    [
        "Lyric.Lexing.dll",
        "Lyric.AST.dll",
        "Lyric.Parsing.dll",
        "Lyric.Resolver.dll",
        "Lyric.Sema.dll",
        "Lyric.Ir.dll",
        "Lyric.Bytecode.Emit.dll",
        "Lyric.Compiler.dll",
    ];

    [Fact]
    public void Lyrvm_ships_nothing_from_the_compiler()
    {
        var shipped = ShippedAssemblies("Lyrvm");
        var offenders = CompilerAssemblies.Where(shipped.Contains).ToArray();

        Assert.True(offenders.Length == 0,
            "lyrvm must not ship compiler assemblies (ADR-017), but found: "
            + string.Join(", ", offenders)
            + ". A runtime that carries the compiler makes ADR-013 ('someone writes a second "
            + "runtime from the spec alone') an empty claim. Check the ProjectReferences of "
            + "Lyric.Bytecode and Lyric.Vm — that is where the edge crept back in.");
    }

    [Fact]
    public void Lyrvm_ships_exactly_the_three_libraries_it_needs()
    {
        var shipped = ShippedAssemblies("Lyrvm");

        // Positiv formuliert, damit der Test auch faellt, wenn jemand die Runtime durch eine
        // vierte Kante *erweitert*, die zufaellig nicht auf der Verbotsliste steht.
        Assert.Equal(
            ["Lyric.Bytecode.dll", "Lyric.Core.dll", "Lyric.Vm.dll", "lyrvm.dll"],
            shipped.Where(name => name.StartsWith("Lyric.", StringComparison.Ordinal)
                                  || name.StartsWith("lyr", StringComparison.Ordinal))
                .OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Lyrc_ships_no_runtime()
    {
        var shipped = ShippedAssemblies("Lyrc");

        // Die Gegenrichtung. Sie ist weniger dramatisch — ein Compiler mit Interpreter waere
        // bloss fett, nicht widerspruechlich — aber sie haelt die Rollen sauber: 'lyrc' fuehrt
        // nichts aus, also hat es auch nichts, womit es koennte.
        Assert.DoesNotContain("Lyric.Vm.dll", shipped);
    }

    [Fact]
    public void Lyric_driver_ships_both_sides()
    {
        // Die Gegenprobe zu den drei Tests oben: der Treiber *muss* beides haben, sonst pruefen
        // sie nur, dass die Projekte leer sind.
        var shipped = ShippedAssemblies("Lyric.Cli");

        Assert.Contains("Lyric.Vm.dll", shipped);
        Assert.Contains("Lyric.Compiler.dll", shipped);
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

    private static HashSet<string> ShippedAssemblies(string project) =>
        Directory.GetFiles(Toolchain.OutputDirectory(project), "*.dll")
            .Select(Path.GetFileName)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
}
