using Lyric.DocGen.Rendering;

namespace Lyric.DocGen.Site;

/// <summary>
/// One page of the site, as content rather than as a file.
///
/// <para><see cref="Html"/> is the body only; the surrounding shell comes from
/// <see cref="Template"/>. Keeping the two apart is what makes the content testable without a
/// temporary directory.</para>
/// </summary>
/// <param name="SitePath">Relative to the version root, ending in a slash.</param>
public sealed record SitePage(string SitePath, string Title, string Html, Heading[] Headings);

/// <summary>
/// The two halves of the site. A visitor either LEARNS (the guide, a reading path) or LOOKS
/// SOMETHING UP (the reference and the standard library); the welcome page is where the choice is
/// made, and the sidebar only ever shows the half the reader is in.
/// </summary>
public enum SiteArea { Guide, Documentation }

/// <summary>A group in the navigation. The order of the pages is the order they are shown in.</summary>
public sealed record SiteSection(string Title, SiteArea Area, SitePage[] Pages);

/// <summary>The changelog entry the welcome page shows: the heading of the entry and its body as
/// HTML. For a release it is that release's entry; for a nightly, the newest one.</summary>
public sealed record RecentChanges(string Title, string Html);

/// <param name="Version">The path segment this site is written under, for example <c>v1.0.0</c>.</param>
public sealed record SiteContent(string Version, SiteSection[] Sections, RecentChanges Changes)
{
    public IEnumerable<SitePage> Pages => Sections.SelectMany(s => s.Pages);

    /// <summary>Where an area is entered from the welcome page and the header: its first page.
    /// <c>null</c> for an area without pages, which the real site never has but a reduced one in a
    /// test may.</summary>
    public SitePage? EntryOf(SiteArea area) =>
        Sections.FirstOrDefault(s => s.Area == area && s.Pages.Length > 0)?.Pages[0];
}
