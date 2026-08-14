using System.Runtime.CompilerServices;

namespace Lyric.Tests.DocGen;

/// <summary>
/// The repository root, resolved from this file's compile-time path rather than from the working
/// directory, which differs between a run from the IDE and one from the CI.
/// </summary>
internal static class TestPaths
{
    private static string TestDir([CallerFilePath] string thisFile = "")
        => Path.GetDirectoryName(thisFile)!;

    public static string RepoRoot(params string[] parts) =>
        Path.GetFullPath(Path.Combine([TestDir(), "..", "..", .. parts]));
}
