namespace Lyric.Cli;

/// <summary>
/// Welche Werkzeuge benutzt dieser Lauf (ADR-019)?
///
/// <para>Gestaffelt und fuer jedes Werkzeug gleich: <c>--&lt;flag&gt; &lt;pfad&gt;</c> schlaegt
/// Umgebungsvariable schlaegt „mitgeliefert". Vorher gab es diese Staffelung nur fuer die Runtime;
/// die Verallgemeinerung kostet nichts und nimmt die formelle Sprachspezifikation vorweg, nach der
/// auch ein zweiter Compiler denkbar ist.</para>
/// </summary>
public sealed record ToolSelection(IReadOnlyDictionary<Tool, string?> Overrides)
{
    /// <summary>Der Pfad, unter dem dieses Werkzeug gestartet wird.</summary>
    public string PathOf(Tool tool) =>
        tool.Resolve(Overrides.TryGetValue(tool, out var path) ? path : null);

    /// <summary>Der Name fuer <c>--version</c>: „bundled" oder der gewaehlte Pfad.</summary>
    public string DisplayOf(Tool tool) =>
        tool.Display(Overrides.TryGetValue(tool, out var path) ? path : null);

    /// <summary>
    /// Liest die Werkzeug-Flags heraus und gibt den Rest unveraendert zurueck.
    ///
    /// <para>Alles, was hier nicht erkannt wird, wandert <b>unbesehen</b> an das Werkzeug weiter —
    /// deshalb versteht <c>lyric build</c> jede Option, die <c>lyrc build</c> versteht, ohne dass
    /// diese Datei sie kennen muesste. Nach <c>--</c> wird nichts mehr angefasst: dort beginnen die
    /// Argumente des Lyric-Programms.</para>
    /// </summary>
    public static (ToolSelection Selection, string[] Remaining, string? Error) Parse(string[] args)
    {
        var overrides = new Dictionary<Tool, string?>();
        var rest = new List<string>(args.Length);

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--") { rest.AddRange(args[i..]); break; }

            var tool = Tool.All.FirstOrDefault(t => t.Flag == args[i]);
            if (tool is null) { rest.Add(args[i]); continue; }

            if (i + 1 >= args.Length)
                return (Bundled, [], $"{tool.Flag}: missing path argument");

            overrides[tool] = args[++i];
        }

        return (new ToolSelection(overrides), rest.ToArray(), null);
    }

    /// <summary>Alles mitgeliefert — der Normalfall.</summary>
    public static ToolSelection Bundled => new(new Dictionary<Tool, string?>());
}
