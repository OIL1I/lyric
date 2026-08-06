using System.Reflection;
using Lyric.Core;

namespace Lyric.Tests.Core;

/// <summary>
/// Die Toolchain-Version steht an zwei Stellen: als C#-Konstante in
/// <see cref="ToolchainVersion.Value"/> (dort liest sie <c>lyric --version</c>) und als
/// <c>&lt;Version&gt;</c> in <c>Directory.Build.props</c> (dort brauchen sie die
/// Datei-Eigenschaften der Binaries).
///
/// <para>Eine der beiden muss die Quelle sein, und MSBuild kann keine C#-Konstante lesen. Statt
/// die Doppelung wegzudiskutieren, wird sie bewacht: laufen die Zahlen auseinander, faellt dieser
/// Test — nicht erst der Nutzer, dem <c>lyric --version</c> etwas anderes sagt als die
/// Datei-Eigenschaften der exe.</para>
///
/// <para>Genau dieser Fehler ist in diesem Projekt schon einmal passiert, eine Ebene tiefer: die
/// Start-Sektion wurde vom Writer anders indiziert als vom Reader, und 1300 Tests haben es nicht
/// gemerkt, weil beide Lesarten in den Testprogrammen zufaellig zusammenfielen.</para>
/// </summary>
public class ToolchainVersionTests
{
    [Fact]
    public void The_csharp_constant_matches_the_version_msbuild_stamped_into_the_assembly()
    {
        var stamped = typeof(ToolchainVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        Assert.NotNull(stamped);

        // Das SDK haengt bei Source-Link-Builds '+<commit-sha>' an. Uns interessiert der Teil
        // davor — die Zahl, die ein Mensch getippt hat.
        var version = stamped!.Split('+')[0];

        Assert.Equal(ToolchainVersion.Value, version);
    }
}
