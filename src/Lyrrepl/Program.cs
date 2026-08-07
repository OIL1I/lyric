using Lyric.Core;

namespace Lyric.Cli.Repl;

/// <summary>
/// <c>lyrrepl</c> — die interaktive Schleife (ADR-021).
///
/// <para>Das vierte Werkzeug neben <c>lyrc</c>, <c>lyrvm</c> und dem Treiber, und das erste mit
/// <b>beiden</b> Bibliotheken: eine REPL übersetzt und führt aus, und der Zustand muss dazwischen
/// leben. <c>lyric run</c> löst das über zwei Subprozesse — hier geht das nicht.</para>
/// </summary>
public static class Program
{
    private const string Prompt = "lyr> ";

    public static int Main(string[] rawArgs)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (rawArgs.Contains("--version") || rawArgs.Contains("-v"))
        {
            Console.WriteLine($"lyrrepl {ToolchainVersion.Value}");
            return ExitCodes.Success;
        }

        if (rawArgs.Contains("--help") || rawArgs.Contains("-h"))
        {
            PrintHelp();
            return ExitCodes.Success;
        }

        var session = new Session(StdlibRoot(rawArgs));

        Console.WriteLine($"Lyric {ToolchainVersion.Value} — :help for commands, :quit to leave");

        while (true)
        {
            Console.Write(Prompt);
            var line = Console.ReadLine();

            // EOF (Ctrl+D, oder eine Pipe, die endet) ist kein Fehler, sondern das Ende.
            if (line is null) return ExitCodes.Success;

            var input = line.Trim();
            if (input.Length == 0) continue;

            if (input.StartsWith(':'))
            {
                if (Command(input, session, out var exit)) return exit;
                continue;
            }

            session.Execute(input, Console.Out, Console.Error);
        }
    }

    /// <summary>Die Doppelpunkt-Kommandos. Sie beginnen mit <c>:</c>, weil das in Lyric kein
    /// Anfang eines Statements ist — ein Kommando kann damit nie mit gültigem Code kollidieren.
    /// </summary>
    private static bool Command(string input, Session session, out int exit)
    {
        exit = ExitCodes.Success;

        switch (input)
        {
            case ":quit" or ":q" or ":exit":
                return true;

            case ":help" or ":h":
                PrintHelp();
                return false;

            case ":reset":
                session.Reset();
                Console.WriteLine("forgot all declarations");
                return false;

            case ":list" or ":l":
                // Was die Sitzung ueber die Eingaben hinweg behalten hat. Ohne das raet man,
                // welche Deklarationen noch gelten — besonders nach einem Fehlschlag.
                if (session.Declarations.Count == 0) Console.WriteLine("(nothing declared yet)");
                else foreach (var declaration in session.Declarations) Console.WriteLine(declaration);
                return false;

            default:
                Console.Error.WriteLine($"unknown command: {input} — try ':help'");
                return false;
        }
    }

    /// <summary>Wo die Stdlib liegt: <c>--stdlib</c>, sonst <c>LYRIC_STDLIB</c>, sonst neben dem
    /// Binary. Dieselbe Staffelung wie bei <c>lyrc</c> — eine zweite waere eine zweite Wahrheit
    /// darüber, wo die Bibliothek wohnt.</summary>
    private static string? StdlibRoot(string[] args)
    {
        var index = Array.IndexOf(args, "--stdlib");
        if (index >= 0 && index + 1 < args.Length) return args[index + 1];

        var fromEnvironment = Environment.GetEnvironmentVariable("LYRIC_STDLIB");
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment;

        var beside = Path.Combine(AppContext.BaseDirectory, "stdlib");
        return Directory.Exists(beside) ? beside : null;
    }

    private static void PrintHelp() => Console.WriteLine(
        """
        lyrrepl — the interactive Lyric prompt

        Type an expression to see its value, a statement to run it, or a declaration
        (fn, class, struct, enum, let, import) to keep it for later entries.

          :help, :h     this text
          :list, :l     the declarations this session remembers
          :reset        forget them
          :quit, :q     leave

        Declarations accumulate; statements run once. A failed entry changes nothing.

        Options:
          --stdlib <dir>   where the standard library lives
          --version, -v    print the version
        """);
}
