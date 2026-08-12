namespace Lyric.Cli;

/// <summary>
/// Which tools this run uses.
///
/// <para>The same precedence for every tool: <c>--&lt;flag&gt; &lt;path&gt;</c> beats the
/// environment variable, which beats the bundled executable.</para>
/// </summary>
public sealed record ToolSelection(IReadOnlyDictionary<Tool, string?> Overrides)
{
    /// <summary>The path this tool is started from.</summary>
    public string PathOf(Tool tool) =>
        tool.Resolve(Overrides.TryGetValue(tool, out var path) ? path : null);

    /// <summary>The name for <c>--version</c>: "bundled", or the selected path.</summary>
    public string DisplayOf(Tool tool) =>
        tool.Display(Overrides.TryGetValue(tool, out var path) ? path : null);

    /// <summary>
    /// Takes the tool flags out and returns the rest unchanged.
    ///
    /// <para>Anything not recognised here is passed on to the tool untouched. Nothing after
    /// <c>--</c> is inspected; that is where the Lyric program's arguments start.</para>
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

    /// <summary>Every tool bundled.</summary>
    public static ToolSelection Bundled => new(new Dictionary<Tool, string?>());
}
