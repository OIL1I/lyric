namespace Lyric.Core;

/// <summary>When a progress display appears.</summary>
public enum ProgressMode
{
    /// <summary>Only when stderr is a terminal and the run takes long enough.</summary>
    Auto,

    /// <summary>Never, for cases where terminal detection is wrong.</summary>
    Never,

    /// <summary>Always, terminal or not. Makes the animated path testable.</summary>
    Always,
}

/// <summary>
/// The options every binary of the suite understands, parsed and interpreted in one place.
///
/// <para><see cref="Parse"/> also returns the remaining arguments, so each binary can run its own
/// parser over them.</para>
/// </summary>
public sealed record ToolOptions
{
    /// <summary>Diagnostics as JSON instead of plain text. Goes to stderr like the plain text;
    /// stdout belongs to the program or to the requested dump.</summary>
    public bool Json { get; init; }

    /// <summary>Suppresses success messages and the progress display. Diagnostics remain.
    /// </summary>
    public bool Quiet { get; init; }

    /// <summary>A per-phase timing breakdown instead of the live line. Works without a terminal
    /// and uses no escape sequences.</summary>
    public bool Verbose { get; init; }

    public ProgressMode Progress { get; init; } = ProgressMode.Auto;

    public static ToolOptions Default => new();

    /// <summary>
    /// Takes the shared flags out of the command line.
    ///
    /// <para>Everything from the first <c>--</c> onwards is left untouched; a <c>--quiet</c> there
    /// belongs to the Lyric program.</para>
    /// </summary>
    public static (ToolOptions Options, string[] Remaining, string? Error) Parse(string[] args)
    {
        var options = Default;
        var remaining = new List<string>(args.Length);

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--") { remaining.AddRange(args[i..]); break; }

            switch (args[i])
            {
                case "--json":
                    options = options with { Json = true };
                    break;

                // No '-v' short form: it is already '--version' in every binary.
                case "--verbose":
                    options = options with { Verbose = true };
                    break;

                case "--quiet" or "-q":
                    options = options with { Quiet = true };
                    break;

                case "--progress":
                    if (i + 1 >= args.Length)
                        return (options, [], "--progress: missing mode (auto, never or always)");
                    var mode = args[++i];
                    if (!Enum.TryParse<ProgressMode>(mode, ignoreCase: true, out var parsed))
                        return (options, [], $"--progress: unknown mode '{mode}' "
                                             + "(expected auto, never or always)");
                    options = options with { Progress = parsed };
                    break;

                default:
                    remaining.Add(args[i]);
                    break;
            }
        }

        return (options, remaining.ToArray(), null);
    }
}
