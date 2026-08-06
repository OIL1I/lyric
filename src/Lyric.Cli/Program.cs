using Lyric.Core;

namespace Lyric.Cli;

/// <summary>
/// <c>lyric</c> — der Einstiegspunkt der Werkzeug-Suite (ADR-019).
///
/// <para><b>Dieses Programm compiliert nichts und fuehrt nichts aus.</b> Es waehlt Werkzeuge,
/// uebersetzt bequeme Kommandos in technische und reicht durch, was zurueckkommt: <c>lyric run
/// app.lyr</c> ist <c>lyrc build</c> gefolgt von <c>lyrvm run</c>. Genau deshalb hat es keine
/// Referenz auf <c>lyrfe</c> oder <c>lyrrt</c>, und ein Architektur-Test haelt das fest.</para>
///
/// <para><b>Warum das die Revision von ADR-017 ist.</b> Dort lief die mitgelieferte Runtime
/// in-process, begruendet mit einem gesparten Prozessstart von „~50–70 ms". Gemessen am
/// 2026-08-06: 283 ms in-process gegen 290 ms ueber den Subprozess — der Unterschied liegt im
/// Rauschen. Bezahlt wurden dafuer zwei Ausfuehrungspfade, die gegeneinander getestet werden
/// mussten, und vier Kommandos, die es zweimal gab.</para>
///
/// <para>Die Debug-Dumps (<c>tokenize</c>, <c>parse</c>, <c>lower</c>) gibt es hier bewusst
/// <b>nicht</b>: sie sind Compiler-Interna und wohnen in <c>lyrc</c>. Dieses Programm nimmt
/// Vorgaben an, wo ein Werkzeug eine Entscheidung verlangt — das ist sein ganzer Zweck.</para>
/// </summary>
public static class Program
{
    public static int Main(string[] rawArgs)
    {
        var (selection, args, error) = ToolSelection.Parse(rawArgs);
        if (error is not null)
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.MissingArgument,
                error, ExitCodes.Usage);

        if (args.Length == 0) { PrintHelp(); return ExitCodes.Success; }

        return args[0] switch
        {
            "--version" or "-v" => Version(selection),
            "--help" or "-h" => Help(),
            "run" => Run(args, selection),
            "build" or "check" => Forward(Tool.Compiler, selection, args),
            "disasm" => Forward(Tool.Runtime, selection, args),
            _ => CliDiagnostics.Fail(Console.Error, CliDiagnostics.UnknownCommand,
                $"unknown command: {args[0]} — try 'lyric --help'", ExitCodes.Usage),
        };
    }

    /// <summary>
    /// Uebersetzen und ausfuehren. Bei einem fertigen <c>.lyrbc</c> entfaellt der erste Schritt.
    ///
    /// <para>Das Zwischenergebnis ist eine <b>temporaere Datei</b>, die danach verschwindet. Ein
    /// Cache waere beim zweiten Lauf schneller, braeuchte aber ein Verzeichnis, eine
    /// Invalidierungsregel (die auch Stdlib-Aenderungen erfassen muss) und ein <c>clean</c> — ein
    /// eigener Mechanismus mit einer eigenen klassischen Fehlerquelle. Er laesst sich spaeter
    /// darueberlegen, ohne hier etwas zu aendern.</para>
    /// </summary>
    private static int Run(string[] args, ToolSelection selection)
    {
        var separator = Array.IndexOf(args, "--");
        var positional = separator < 0 ? args : args[..separator];
        var programArguments = separator < 0 ? [] : args[(separator + 1)..];

        if (positional.Length < 2)
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.MissingArgument,
                "run: missing file argument", ExitCodes.Usage);

        var path = positional[1];
        var passThrough = positional[2..];

        // Das Werkzeug VOR dem ersten Aufruf pruefen. Sonst meldete sich eine falsch konfigurierte
        // Runtime erst nach einem vollstaendigen Compile-Lauf, und die Meldung kaeme aus dem
        // Prozess-Start statt aus der Konfiguration.
        if (Missing(selection, Tool.Runtime) is { } runtimeError) return runtimeError;

        if (path.EndsWith(".lyrbc", StringComparison.OrdinalIgnoreCase))
            return Execute(selection, path, passThrough, programArguments);

        if (Missing(selection, Tool.Compiler) is { } compilerError) return compilerError;

        // Der Name traegt den der Quelle, damit ein Backtrace aus der Runtime lesbar bleibt.
        var module = Path.Combine(Path.GetTempPath(),
            $"{Path.GetFileNameWithoutExtension(path)}-{Guid.NewGuid():N}.lyrbc");

        try
        {
            // '--quiet' ist die Vorgabe, die diesen Einstiegspunkt ausmacht: wer 'run' tippt,
            // will sein Programm sehen und nicht die Groesse eines Zwischenartefakts, das gleich
            // wieder verschwindet. Steht es in passThrough schon drin, schadet es nicht — und wer
            // die Meldung will, ruft 'lyric build' oder gleich 'lyrc'.
            var built = Tool.Run(selection.PathOf(Tool.Compiler),
                ["build", path, "-o", module, "--quiet", .. passThrough], Console.Error);
            if (built != ExitCodes.Success) return built;

            return Execute(selection, module, [], programArguments);
        }
        finally
        {
            // Auch wenn das Programm gepanickt hat: die Datei gehoert diesem Lauf und keiner
            // spaeteren Sitzung.
            try { File.Delete(module); } catch (IOException) { /* nicht schlimmer machen */ }
        }
    }

    private static int Execute(ToolSelection selection, string module, string[] options,
        string[] programArguments)
    {
        string[] tail = programArguments.Length == 0 ? [] : ["--", .. programArguments];
        return Tool.Run(selection.PathOf(Tool.Runtime),
            ["run", module, .. options, .. tail], Console.Error);
    }

    /// <summary>Ein Kommando, das ein Werkzeug schon kann — unveraendert weitergereicht. Was
    /// <c>lyrc build</c> an Optionen versteht, versteht <c>lyric build</c> damit auch, ohne dass
    /// diese Datei sie kennen muss.</summary>
    private static int Forward(Tool tool, ToolSelection selection, string[] args)
    {
        if (Missing(selection, tool) is { } error) return error;
        return Tool.Run(selection.PathOf(tool), args, Console.Error);
    }

    private static int? Missing(ToolSelection selection, Tool tool)
    {
        var path = selection.PathOf(tool);
        return File.Exists(path)
            ? null
            : CliDiagnostics.Fail(Console.Error, CliDiagnostics.VmNotFound,
                $"{tool.Name} not found: {path} (set {tool.EnvironmentVariable} or pass {tool.Flag})",
                ExitCodes.Failure);
    }

    private static int Version(ToolSelection selection)
    {
        Console.Out.WriteLine($"lyric {ToolchainVersion.Value}");
        foreach (var tool in Tool.All)
            Console.Out.WriteLine($"  {tool.Name,-6} {selection.DisplayOf(tool)}");
        return ExitCodes.Success;
    }

    private static int Help() { PrintHelp(); return ExitCodes.Success; }

    private static void PrintHelp()
    {
        Console.Out.WriteLine("""
            lyric - the Lyric tool suite

            Usage: lyric <command> <file> [options] [-- <program args>]

            Commands:
              run <file>               Compile and execute (.lyr or .lyrbc)
              build <file> [-o <out>]  Compile .lyr to .lyrbc
              check <file>             Resolve and type-check only
              disasm <file.lyrbc>      Print a readable disassembly

            Options:
              --compiler <path>        Compiler to use; defaults to $LYRIC_COMPILER,
                                       then the bundled lyrc
              --vm <path>              Runtime to use; defaults to $LYRIC_VM,
                                       then the bundled lyrvm
              --version, -v            Show versions and the selected tools
              --help, -h               Show this help

            Every other option is passed straight to the tool that runs the command.
            For compiler internals (tokenize, parse, lower) call 'lyrc' directly;
            to inspect a module (verify, info) call 'lyrvm'.
            """);
    }
}
