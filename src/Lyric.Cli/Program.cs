namespace Lyric.Cli;

public static class Program
{
    // Update this on each release tag.
    private const string Version = "0.0.1-dev";

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 0;
        }

        return args[0] switch
        {
            "--version" or "-v" => PrintVersion(),
            "--help" or "-h" => HelpAndOk(),
            _ => Unknown(args[0]),
        };
    }

    private static int PrintVersion()
    {
        Console.WriteLine($"lyric {Version}");
        return 0;
    }

    private static int HelpAndOk()
    {
        PrintHelp();
        return 0;
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"unknown command: {cmd}");
        Console.Error.WriteLine("try 'lyric --help'");
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("lyric — compiler and VM for the Lyric language");
        Console.WriteLine();
        Console.WriteLine("Usage: lyric <command> [args]");
        Console.WriteLine();
        Console.WriteLine("Commands (M0 stub — more coming):");
        Console.WriteLine("  --version, -v    Show version");
        Console.WriteLine("  --help, -h       Show this help");
    }
}
