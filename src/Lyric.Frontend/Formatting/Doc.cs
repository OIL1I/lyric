namespace Lyric.Formatting;

/// <summary>
/// The document the formatter builds instead of a string: text with structured line-break
/// OPPORTUNITIES, decided later by <see cref="DocRenderer"/> against the line width.
///
/// <para>This is Wadler's algebra in the shape Prettier made standard. The formatter for a node
/// says "these parts belong together" (<see cref="Group"/>), "break here if the group breaks"
/// (<see cref="Line"/>/<see cref="SoftLine"/>), "always break here" (<see cref="HardLine"/>) —
/// and never measures a column itself. Without this layer every node formatter carries its own
/// width arithmetic, which is how a formatter grows fifty inconsistent special cases.</para>
///
/// <para>The three line forms differ only in their FLAT rendering: a <see cref="Line"/> is a
/// space, a <see cref="SoftLine"/> is nothing, a <see cref="HardLine"/> has no flat form at all —
/// it forces every group around it to break. Broken, all three are a newline at the current
/// indentation.</para>
/// </summary>
public abstract record Doc
{
    public sealed record Text(string Value) : Doc;

    public sealed record Concat(IReadOnlyList<Doc> Parts) : Doc;

    /// <summary>A space when flat, a newline when the enclosing group breaks.</summary>
    public sealed record Line : Doc;

    /// <summary>Nothing when flat, a newline when the enclosing group breaks.</summary>
    public sealed record SoftLine : Doc;

    /// <summary>Always a newline. A group containing one never fits, so it breaks.</summary>
    public sealed record HardLine : Doc;

    /// <summary>Rendered flat when its flat form fits into the remaining width, broken
    /// otherwise. Groups nest: an outer group may break while an inner one stays flat.</summary>
    public sealed record Group(Doc Content) : Doc;

    /// <summary>One indentation step deeper for every newline INSIDE the content. The text
    /// before the first newline stays where it is.</summary>
    public sealed record Indent(Doc Content) : Doc;

    // The builders. Static on the type so a formatter reads as prose: Doc.Group(...).

    public static readonly Doc Nil = new Text("");
    public static readonly Doc Space = new Text(" ");
    public static readonly Doc LineOrSpace = new Line();
    public static readonly Doc LineOrNothing = new SoftLine();
    public static readonly Doc NewLine = new HardLine();

    public static Doc From(string text) => new Text(text);

    public static Doc Of(params Doc[] parts) => new Concat(parts);

    public static Doc GroupOf(params Doc[] parts) => new Group(new Concat(parts));

    public static Doc IndentOf(params Doc[] parts) => new Indent(new Concat(parts));

    /// <summary>The parts with the separator between neighbours — the comma-list shape.</summary>
    public static Doc Join(Doc separator, IReadOnlyList<Doc> parts)
    {
        if (parts.Count == 0) return Nil;
        if (parts.Count == 1) return parts[0];

        var joined = new List<Doc>(parts.Count * 2 - 1);
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0) joined.Add(separator);
            joined.Add(parts[i]);
        }

        return new Concat(joined);
    }
}
