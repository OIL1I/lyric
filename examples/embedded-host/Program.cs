using Lyric.Core;
using Lyric.Embedding;

// Ein Host, der ein Lyric-Skript laedt und entscheidet, was es darf.
//
// Das ist der Use-Case, fuer den Lyric ueberhaupt eine VM hat (ADR-001): eine Engine, ein Editor,
// ein Build-Werkzeug laedt fremden Code und will ihn NICHT alles machen lassen.

var stdlib = Path.Combine(AppContext.BaseDirectory, "stdlib");

// 1. Eine gesandboxte VM. Ohne Argumente bekaeme sie gar keine Capability; hier darf das Skript
//    auf die Konsole schreiben (das kostet keine) — mehr nicht.
var sandboxed = new LangVm(new HostOptions
{
    StdlibRoot = stdlib,
    Output = Console.Out,
});

Console.WriteLine("== gesandboxt ==");
var script = sandboxed.Compile("""
    import std.io.console { println };

    fn main(): int {
        println("hallo vom Skript");
        return 0;
    }
    """, "gruss");

Console.WriteLine($"exit = {sandboxed.Run(script)}");

// 2. Dasselbe Skript, das eine Datei anfassen will — die Capability fehlt.
Console.WriteLine();
Console.WriteLine("== was die Sandbox verhindert ==");
try
{
    sandboxed.Run(sandboxed.Compile("""
        import std.io.file { exists };
        fn main(): int { return if (exists("geheim.txt")) 1 else 0; }
        """, "schnueffler"));

    Console.WriteLine("FEHLER: das haette nicht laufen duerfen");
    return 1;
}
catch (ScriptException denied)
{
    Console.WriteLine($"abgelehnt: {denied.Code} — {denied.Message}");
}

// 3. Eine zweite VM mit mehr Rechten. Beide leben im selben Prozess und teilen nichts.
Console.WriteLine();
Console.WriteLine("== mit fileAccess ==");
var trusted = new LangVm(new HostOptions
{
    StdlibRoot = stdlib,
    Capabilities = Capability.FileAccess,
    Output = Console.Out,
});

Console.WriteLine($"exit = {trusted.Run(trusted.Compile("""
    import std.io.file { exists };
    import std.io.console { println };

    fn main(): int {
        let da = exists("geheim.txt");
        println(f"gibt es geheim.txt? {da}");
        return 0;
    }
    """, "leser"))}");

// 4. Ein Uebersetzungsfehler kommt als Daten zurueck, nicht als Text.
Console.WriteLine();
Console.WriteLine("== ein kaputtes Skript ==");
try
{
    sandboxed.Compile("fn main(): int { return unbekannt; }", "kaputt");
}
catch (EmbeddingException broken)
{
    foreach (var d in broken.Diagnostics)
        Console.WriteLine($"  {d.Code}: {d.Message}");
}

return 0;
