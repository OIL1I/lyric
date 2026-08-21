using System.Globalization;
using Lyric.Core;
using Lyric.Lsp;

namespace Lyric.Cli.LanguageServer;

/// <summary>
/// The process around <see cref="LspServer"/>.
///
/// <para>STDOUT BELONGS TO THE PROTOCOL. Nothing in this binary may write to it — one stray line
/// lands between two framed messages and desynchronises the stream for the rest of the session.
/// Everything addressed to a human goes to stderr, which the editor collects into its own log.
/// </para>
///
/// <para>The streams are taken as raw byte streams rather than through <see cref="Console.Out"/>:
/// a <see cref="TextWriter"/> would apply an encoding and, on Windows, a newline translation that
/// turns the <c>\r\n</c> of a header into <c>\r\r\n</c>.</para>
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        ConsoleStreams.UseUtf8WhenRedirected();

        var options = new LspServerOptions();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--version":
                    await Console.Error.WriteLineAsync($"lyrls {ToolchainVersion.Value}");
                    return ExitCodes.Success;

                case "--help" or "-h":
                    await Console.Error.WriteAsync(Usage);
                    return ExitCodes.Success;

                // The transport, named by the client rather than chosen by it. A language client
                // appends this for every server it launches as a plain executable, so accepting it
                // is not a courtesy — a server that rejects it never starts.
                //
                // It is a no-op because stdio is the only transport this binary has. Naming the one
                // it already speaks changes nothing.
                case "--stdio":
                    break;

                // The transports it does NOT have. Left as their own message rather than falling
                // into 'unknown argument': the caller asked for something specific and gets told
                // that specific thing is missing, instead of being pointed at a typo.
                case var pipe when pipe.StartsWith("--pipe=", StringComparison.Ordinal)
                                   || pipe.StartsWith("--socket=", StringComparison.Ordinal):
                    await Console.Error.WriteLineAsync(
                        $"lyrls: '{pipe.Split('=')[0]}' is not supported; this server speaks stdio only");
                    return ExitCodes.Usage;

                case "--stdlib" when i + 1 < args.Length:
                    options = options with { StdlibRoot = args[++i] };
                    break;

                case "--debounce" when i + 1 < args.Length:
                    if (!int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture,
                            out var milliseconds))
                    {
                        await Console.Error.WriteLineAsync(
                            $"lyrls: --debounce expects a number of milliseconds, got '{args[i + 1]}'");
                        return ExitCodes.Usage;
                    }
                    i++;
                    options = options with { Debounce = TimeSpan.FromMilliseconds(milliseconds) };
                    break;

                default:
                    await Console.Error.WriteLineAsync($"lyrls: unknown argument '{args[i]}'");
                    await Console.Error.WriteAsync(Usage);
                    return ExitCodes.Usage;
            }
        }

        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        using var server = new LspServer(input, output, options);

        // No cancellation token from a console signal: the protocol has its own way out ('shutdown'
        // then 'exit'), and an editor that dies simply closes the stream, which ends the read loop.
        return await server.RunAsync().ConfigureAwait(false);
    }

    private const string Usage =
        """
        usage: lyrls [options]

        Speaks the Language Server Protocol over stdin and stdout. It is started by an editor,
        not by hand.

        options:
          --stdio             use stdin and stdout; the only transport, and the default
          --stdlib <path>     where the standard library lives (default: beside this binary)
          --debounce <ms>     quiet time before a changed file is compiled (default: 50)
          --version           print the toolchain version
          --help, -h          print this text

        """;
}
