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

/// <summary>A group in the navigation. The order of the pages is the order they are shown in.</summary>
public sealed record SiteSection(string Title, SitePage[] Pages);

/// <param name="Version">The path segment this site is written under, for example <c>v1.0.0</c>.</param>
public sealed record SiteContent(string Version, SiteSection[] Sections)
{
    public IEnumerable<SitePage> Pages => Sections.SelectMany(s => s.Pages);
}
