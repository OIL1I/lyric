using System.Runtime.CompilerServices;
using Lyric.DocGen.Extraction;
using Lyric.DocGen.Model;

namespace Lyric.Tests.DocGen;

/// <summary>
/// How much of the public surface carries a doc comment.
///
/// <para>A RATCHET, not the gate the reference eventually needs: the target is every public item,
/// and the standard library is nowhere near it. A test asserting the target would be red on main,
/// so this one asserts the floor instead — documentation can be added but not removed, and the
/// floor moves up with it.</para>
///
/// <para>When it fails because the share ROSE, raise <see cref="Floor"/>. That is the intended
/// maintenance, and it is the only thing that keeps the number honest.</para>
/// </summary>
public class DocCoverageTests
{
    /// <summary>
    /// The documented items counted after the 2.0 cut (2026-08-20): 389 of 389 — the cut removed
    /// 41 deprecated (and documented) declarations, and the coverage reached ALL of the surface
    /// that remains. Lowered deliberately from 430 for exactly that removal.
    /// </summary>
    private const int Floor = 390;

    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static IEnumerable<DocItem> Flatten(IEnumerable<DocItem> items)
    {
        foreach (var item in items)
        {
            yield return item;
            foreach (var member in Flatten(item.Members)) yield return member;
        }
    }

    private static DocItem[] Surface() =>
        Flatten(StdlibExtractor
            .Extract(Path.Combine(RepoRoot(), "stdlib"), RepoRoot())
            .Modules.SelectMany(m => m.Items)).ToArray();

    [Fact]
    public void The_documented_part_of_the_public_surface_never_shrinks()
    {
        var surface = Surface();
        var documented = surface.Count(i => !string.IsNullOrWhiteSpace(i.Doc));

        Assert.True(documented >= Floor,
            $"documented items dropped from {Floor} to {documented} of {surface.Length}. " +
            "Doc comments are not to be removed; if this is intended, lower the floor deliberately.");

        Assert.False(documented > Floor,
            $"documented items rose from {Floor} to {documented} of {surface.Length}. " +
            $"Raise DocCoverageTests.Floor to {documented}.");
    }

    /// <summary>
    /// Which modules carry nothing at all.
    ///
    /// <para>The list a writer worked through, and it is now empty. It stays as an assertion rather
    /// than being deleted: a module added without a single line would show up here, which is the
    /// cheapest moment to notice.</para>
    /// </summary>
    [Fact]
    public void No_module_is_left_without_any_documentation()
    {
        var model = StdlibExtractor.Extract(Path.Combine(RepoRoot(), "stdlib"), RepoRoot());
        var bare = model.Modules
            .Where(m => Flatten(m.Items).All(i => string.IsNullOrWhiteSpace(i.Doc)))
            .Select(m => m.Path)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([], bare);
    }
}
