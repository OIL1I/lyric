using Lyric.Core;

namespace Lyric.Sema;

/// <summary>
/// The <c>until</c> field of <c>@Deprecated</c>: a version this declaration is kept until, and
/// a build that has reached it is an error.
///
/// <para><b>Why the compiler enforces it at all.</b> A deprecation is a promise with two halves —
/// "use something else" and "this goes away" — and only the first half was ever written down
/// where anyone could see it. The second lived in a release note, which is another way of saying
/// it lived in somebody's memory. A form kept past its promise is worse than one removed on time:
/// it teaches everyone that the dates mean nothing.</para>
///
/// <para><b>Why it stops the build rather than warning.</b> A warning about a removal that should
/// already have happened is a warning nobody acts on — the same reason the doc ratchet and the
/// corpus-silence invariant are errors. The failure lands on the maintainer preparing the release
/// whose version number just arrived, which is exactly who has to act.</para>
///
/// <para><b>Why a field and not a second attribute.</b> An <c>@Sunset</c> beside
/// <c>@Deprecated</c> would be a second mechanism for "this is going away", and the two would
/// eventually disagree about a declaration that carried one and not the other.</para>
/// </summary>
public static class DeprecationPromise
{
    /// <summary>
    /// Checks one promise and reports what is wrong with it.
    ///
    /// <para><c>until = "3.5"</c> means REMOVED IN 3.5: the build fails as soon as the tree
    /// claims 3.5, not one version later. "Kept until" names the release that does the removing,
    /// so the error arrives while that release is being prepared.</para>
    /// </summary>
    /// <param name="until">What the attribute carried; empty means no promise was made.</param>
    /// <param name="span">Where to report — the attribute, not the declaration under it.</param>
    public static void Check(string until, Span span, DiagnosticEngine de, string? toolchain = null)
    {
        if (until.Length == 0) return;

        if (Parse(until) is not { } promised)
        {
            de.Report("LYR-SEM0081", Severity.Error, span,
                $"'{until}' is not a version — 'until' names the release that removes this, "
                + "as \"3.5\" or \"3.5.1\"");
            return;
        }

        // The toolchain's own version, which the tree CLAIMS rather than the one it was built
        // from: the version bump lands on the release branch, and the promise has to come due
        // there, before the tag exists.
        var current = Parse(toolchain ?? ToolchainVersion.Value);
        if (current is not { } now || Compare(now, promised) < 0) return;

        de.Report("LYR-SEM0081", Severity.Error, span,
            $"this was kept until {until} and the toolchain is {toolchain ?? ToolchainVersion.Value} "
            + "— remove it, or move the promise out");
    }

    /// <summary>
    /// <c>major.minor[.patch]</c>, and nothing cleverer: no pre-release tags, no build metadata,
    /// no ranges. A promise is a point in time, and every version this project has ever had fits
    /// three numbers.
    /// </summary>
    private static (int Major, int Minor, int Patch)? Parse(string text)
    {
        var parts = text.Split('.');
        if (parts.Length is < 2 or > 3) return null;

        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
                return null;
            numbers[i] = value;
        }

        return (numbers[0], numbers[1], numbers[2]);
    }

    private static int Compare((int Major, int Minor, int Patch) a,
        (int Major, int Minor, int Patch) b)
    {
        if (a.Major != b.Major) return a.Major.CompareTo(b.Major);
        if (a.Minor != b.Minor) return a.Minor.CompareTo(b.Minor);
        return a.Patch.CompareTo(b.Patch);
    }
}
