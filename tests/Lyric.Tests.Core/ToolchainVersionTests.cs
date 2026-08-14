using System.Reflection;
using Lyric.Core;

namespace Lyric.Tests.Core;

/// <summary>
/// The toolchain version stands at two places: as a C# constant in <see cref="ToolchainVersion.Value"/>,
/// where <c>lyric --version</c> reads it, and as <c>&lt;Version&gt;</c> in
/// <c>Directory.Build.props</c>, where the file properties of the binaries need it.
///
/// <para>One of the two has to be the source, and MSBuild cannot read a C# constant. Rather than
/// arguing the duplication away, it is guarded: when the numbers drift apart this test fails rather than
/// the user, to whom <c>lyric --version</c> says something other than the file properties of the exe.
/// </para>
///
/// <para>Exactly this fault happened once already, one level deeper: the Start section was indexed
/// differently by the writer than by the reader, and 1300 tests did not notice, because both readings
/// coincided by accident in the test programs.</para>
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

        // The SDK appends '+<commit-sha>' in source-link builds. What matters is the part before it: the
        // number a human typed.
        var version = stamped!.Split('+')[0];

        Assert.Equal(ToolchainVersion.Value, version);
    }
}
