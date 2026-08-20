using Lyric.Core;
using Lyric.Dap;

namespace Lyric.Cli.Debugger;

/// <summary>
/// <c>lyrdbg</c> — the debug adapter. An editor launches it and speaks the Debug Adapter
/// Protocol over stdio; there is no interactive mode.
///
/// <para>Stdout carries the protocol and NOTHING else — the debuggee's output travels inside it
/// as events, which is why the adapter compiles and runs the program in-process instead of
/// spawning a runtime whose stdout it would have to capture anyway.</para>
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Contains("--version") || args.Contains("-v"))
        {
            Console.Out.WriteLine($"lyrdbg {ToolchainVersion.Value}");
            return ExitCodes.Success;
        }

        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.Out.WriteLine("lyrdbg — the Lyric debug adapter (Debug Adapter Protocol over stdio)");
            Console.Out.WriteLine();
            Console.Out.WriteLine("An editor launches this binary; it is not used by hand.");
            Console.Out.WriteLine("The launch request takes: program (.lyr or .lyrbc), args,");
            Console.Out.WriteLine("stopOnEntry, noDebug. A .lyr compiles in-process, unoptimized,");
            Console.Out.WriteLine("with source map and debug info.");
            return ExitCodes.Success;
        }

        try
        {
            var server = new DapServer(
                Console.OpenStandardInput(), Console.OpenStandardOutput());
            server.RunAsync().GetAwaiter().GetResult();
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            // The protocol stream is gone or broken; stderr is all that is left to say why.
            Console.Error.WriteLine($"lyrdbg: {ex.Message}");
            return ExitCodes.Failure;
        }
    }
}
