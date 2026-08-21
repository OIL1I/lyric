using Lyric.Core;

namespace Lyric.Cli.Repl;

/// <summary>
/// <c>lyrrepl</c> — the interactive prompt.
///
/// <para>The only tool that holds both libraries: it compiles and executes, and the session state
/// lives between the two.</para>
/// </summary>
public static class Program
{
    private const string Prompt = "lyr> ";

    public static int Main(string[] rawArgs)
    {
        ConsoleStreams.UseUtf8WhenRedirected();

        // A REAL console still gets its code page: the prompt prints what the user typed,
        // and on Windows that needs UTF-8 to survive. Redirected, the line above has already
        // decided the encoding — and setting the code page there would reach into a console
        // this process is merely attached to, which is how it used to change what tools
        // running beside it wrote.
        if (!Console.IsOutputRedirected) Console.OutputEncoding = System.Text.Encoding.UTF8;

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

            // EOF (Ctrl+D, or a pipe that ends) terminates the loop.
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

    /// <summary>The colon commands. No Lyric statement starts with <c>:</c>, so a command cannot
    /// collide with valid code.</summary>
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
                // What the session kept across entries.
                if (session.Declarations.Count == 0) Console.WriteLine("(nothing declared yet)");
                else foreach (var declaration in session.Declarations) Console.WriteLine(declaration);
                return false;

            default:
                Console.Error.WriteLine($"unknown command: {input} — try ':help'");
                return false;
        }
    }

    /// <summary>Where the standard library lives: <c>--stdlib</c>, then <c>LYRIC_STDLIB</c>,
    /// then next to the binary — the same precedence as <c>lyrc</c>.</summary>
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
