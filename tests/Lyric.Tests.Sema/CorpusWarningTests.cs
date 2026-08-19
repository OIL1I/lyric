using System.Runtime.CompilerServices;
using Lyric.Compiler;
using Lyric.Core;
using Xunit;

namespace Lyric.Tests.Sema;

/// <summary>
/// The repository holds itself to its own diagnostics: every real Lyric file — the standard
/// library, the examples, the project templates — checks in complete silence. The formatter's
/// corpus rule, applied to warnings: a new warning that fires on the corpus either found real
/// dirt (clean it in the same commit) or is wrong (fix it before it ships).
/// </summary>
public class CorpusWarningTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();
        foreach (var root in new[] { "stdlib", "examples", "templates" })
        foreach (var file in Directory.GetFiles(Path.Combine(RepoRoot(), root), "*.lyr",
                     SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(RepoRoot(), file);

            // The corpus is what the repository TRACKS; build output is not in it — the same
            // rule the formatter's corpus follows, for the same reason.
            var segments = relative.Split(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(s => s is "bin" or "obj" or "out")) continue;

            data.Add(relative);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void The_repository_checks_in_silence(string relativePath)
    {
        var options = new CompilerOptions { StdlibRoot = Path.Combine(RepoRoot(), "stdlib") };

        // A standard library file is not an entry file — as one it may not even declare its
        // natives. It is checked the way it is ever compiled: loaded through the std root, by a
        // probe that imports it.
        var result = relativePath.StartsWith("stdlib", StringComparison.Ordinal)
            ? SourceCompiler.Check(ScriptSource.FromBuffer(
                    Path.Combine(RepoRoot(), "corpus_probe.lyr"),
                    $"import {ModulePathOf(relativePath)};\n"),
                options)
            : SourceCompiler.Check(Path.Combine(RepoRoot(), relativePath), options);

        Assert.True(result.Diagnostics.Count == 0,
            $"{relativePath} does not check in silence:\n" + string.Join("\n",
                result.Diagnostics.Diagnostics.Select(d =>
                    $"{d.Severity.ToDisplayString()}[{d.Code}]: {d.Message}")));
    }

    /// <summary>stdlib/std/io/file.lyr → std.io.file</summary>
    private static string ModulePathOf(string relativePath)
    {
        var segments = Path.ChangeExtension(relativePath, null)!
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Join('.', segments.Skip(1)); // drop the 'stdlib' root segment
    }
}
