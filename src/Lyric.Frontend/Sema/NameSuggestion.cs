using Lyric.Core;

namespace Lyric.Sema;

/// <summary>
/// The "did you mean" candidate for a name that resolved to nothing.
///
/// <para>One suggestion or none: a wrong guess is worse than no guess, so a tie between two
/// equally close names answers nothing, and the edit budget shrinks with the name — a four-letter
/// word one typo away is plausible, two typos away it is a different word.</para>
/// </summary>
internal static class NameSuggestion
{
    /// <summary>The suggestion as a ready note, or <c>null</c> for none.</summary>
    public static DiagnosticNote? Note(string written, IEnumerable<string> candidates) =>
        For(written, candidates) is { } best ? new DiagnosticNote($"did you mean '{best}'?") : null;

    public static string? For(string written, IEnumerable<string> candidates)
    {
        var budget = written.Length <= 4 ? 1 : 2;
        string? best = null;
        var bestDistance = budget + 1;
        var tie = false;

        foreach (var candidate in candidates)
        {
            if (candidate == written || candidate == "_") continue;
            if (Math.Abs(candidate.Length - written.Length) > budget) continue;

            var distance = Bounded(written, candidate, budget);
            if (distance > budget) continue;

            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
                tie = false;
            }
            // The same name reachable through two scopes is one candidate, not a tie.
            else if (distance == bestDistance && candidate != best)
            {
                tie = true;
            }
        }

        return tie ? null : best;
    }

    /// <summary>Levenshtein distance, capped: anything beyond <paramref name="budget"/> comes
    /// back as <c>budget + 1</c> — the caller only asks "close enough or not".</summary>
    private static int Bounded(string a, string b, int budget)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowMinimum = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), substitution);
                rowMinimum = Math.Min(rowMinimum, current[j]);
            }
            if (rowMinimum > budget) return budget + 1;
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
