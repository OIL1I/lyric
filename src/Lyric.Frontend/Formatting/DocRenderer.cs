using System.Text;

namespace Lyric.Formatting;

/// <summary>
/// Renders a <see cref="Doc"/> to text: the one place that measures columns.
///
/// <para>Wadler's algorithm, iteratively. A work stack holds (indent, flat?, doc); on a
/// <see cref="Doc.Group"/> the renderer asks whether the group's FLAT form fits into what
/// remains of the line — a bounded lookahead that stops at the first newline or at the width,
/// so it is linear in practice. A <see cref="Doc.HardLine"/> can never fit, which is exactly
/// how it forces its enclosing groups to break.</para>
///
/// <para>Output is LF-only with no trailing whitespace: indentation is written lazily, when the
/// first text of the line arrives, so an empty line is empty rather than four invisible
/// spaces — the goldens diff cleanly and the .gitattributes contract holds.</para>
/// </summary>
public static class DocRenderer
{
    /// <summary>The soft limit of CONTRIBUTING.md. A single unbreakable text may still exceed
    /// it — the renderer never truncates and never breaks inside a token.</summary>
    public const int DefaultWidth = 100;

    public const int IndentSize = 4;

    public static string Render(Doc doc, int width = DefaultWidth)
    {
        var output = new StringBuilder();
        var column = 0;
        var pendingIndent = 0;

        var work = new Stack<(int Indent, bool Flat, Doc Doc)>();
        work.Push((0, false, doc));

        while (work.Count > 0)
        {
            var (indent, flat, current) = work.Pop();
            switch (current)
            {
                case Doc.Text(var value):
                    if (value.Length == 0) break;
                    if (pendingIndent > 0)
                    {
                        output.Append(' ', pendingIndent);
                        column = pendingIndent;
                        pendingIndent = 0;
                    }

                    output.Append(value);
                    column += value.Length;
                    break;

                case Doc.Concat(var parts):
                    for (var i = parts.Count - 1; i >= 0; i--)
                        work.Push((indent, flat, parts[i]));
                    break;

                case Doc.Line when flat:
                    work.Push((indent, true, Doc.Space));
                    break;

                case Doc.SoftLine when flat:
                    break;

                case Doc.Line or Doc.SoftLine or Doc.HardLine:
                    // A HardLine inside a flat group cannot happen: Fits refused the group.
                    output.Append('\n');
                    column = 0;
                    pendingIndent = indent;
                    break;

                case Doc.Group(var content):
                    var available = width - (pendingIndent > 0 ? pendingIndent : column);
                    work.Push((indent, flat || Fits(content, available, work), content));
                    break;

                case Doc.Indent(var content):
                    work.Push((indent + IndentSize, flat, content));
                    break;
            }
        }

        return output.ToString();
    }

    /// <summary>
    /// Would <paramref name="doc"/>, rendered FLAT, fit into <paramref name="available"/>
    /// columns?
    ///
    /// <para>The rest of the surrounding work is measured too, up to the next possible line
    /// break: a group whose own text fits but whose closing neighbour (`)`, `;`, `,`) would
    /// cross the width must break — the classic off-by-a-paren of naive implementations.</para>
    /// </summary>
    private static bool Fits(Doc doc, int available,
        Stack<(int Indent, bool Flat, Doc Doc)> continuation)
    {
        var work = new Stack<(bool Flat, Doc Doc)>();
        work.Push((true, doc));

        // The continuation is enumerated lazily on demand — usually the group itself decides
        // long before the surrounding parts matter.
        using var rest = continuation.GetEnumerator();

        while (available >= 0)
        {
            if (work.Count == 0)
            {
                if (!rest.MoveNext()) return true;
                var (_, restFlat, restDoc) = rest.Current;
                work.Push((restFlat, restDoc));
                continue;
            }

            var (flat, current) = work.Pop();
            switch (current)
            {
                case Doc.Text(var value):
                    available -= value.Length;
                    break;

                case Doc.Concat(var parts):
                    for (var i = parts.Count - 1; i >= 0; i--)
                        work.Push((flat, parts[i]));
                    break;

                case Doc.Line when flat:
                    available -= 1;
                    break;

                case Doc.SoftLine when flat:
                    break;

                case Doc.Line or Doc.SoftLine:
                    // A break in NON-flat surroundings ends the line: everything behind it is
                    // the next line's problem, so what came before fits.
                    return true;

                case Doc.HardLine:
                    // Inside the measured group (flat) a hard line can never fit; outside it
                    // ends the line like any break.
                    return !flat;

                case Doc.Group(var content):
                    // Measured flat: the question is whether ONE line holds everything.
                    work.Push((flat, content));
                    break;

                case Doc.Indent(var content):
                    work.Push((flat, content));
                    break;
            }
        }

        return false;
    }
}
