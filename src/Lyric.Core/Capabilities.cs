namespace Lyric.Core;

/// <summary>
/// Die Capability-Stufen aus ADR-007. Ein Bit pro Stufe; die Werte sind
/// <b>Bytecode-Vertrag</b> (Bytecode.md §Capabilities, Sektion Id 1) und dürfen sich nicht mehr
/// ändern.
/// </summary>
[Flags]
public enum Capability : ulong
{
    None = 0,

    /// <summary><c>std.io.file</c> — Lesen und Schreiben im Dateisystem.</summary>
    FileAccess = 1UL << 0,

    /// <summary><c>std.io.net</c> — Sockets. Das Modul selbst ist aus M8 gestrichen (v1.X), das
    /// Bit steht trotzdem fest: eine Nummer, die später etwas anderes bedeutet, macht jedes
    /// ältere <c>.lyrbc</c> falsch.</summary>
    NetworkAccess = 1UL << 1,

    /// <summary><c>std.os</c> — Umgebungsvariablen, Prozesse, Exit-Codes.</summary>
    OsAccess = 1UL << 2,

    /// <summary><c>std.dotnet</c> — Zugriff auf den Host über Reflection.</summary>
    HostAccess = 1UL << 3,

    /// <summary>Was der Standalone-Modus gewährt (Doku §20.2): alles. Wer <c>lyric run</c> tippt,
    /// führt sein eigenes Programm aus — die Trust-Boundary liegt dort nicht zwischen Host und
    /// Skript, sondern gar nicht.</summary>
    All = FileAccess | NetworkAccess | OsAccess | HostAccess,
}

/// <summary>
/// Welches Stdlib-Modul welche Capability verlangt (Doku §20.1).
///
/// <para><b>Die Tabelle steht hier und nicht im Compiler</b>, weil beide Seiten sie brauchen und
/// aus verschiedenen Gründen: der Compiler schreibt in die Capabilities-Sektion, <b>was ein Modul
/// verlangt</b>, und die Runtime prüft beim Laden gegen das, <b>was sie gewährt</b>. Zwei Kopien
/// wären zwei Wahrheiten darüber, was <c>std.os</c> kostet.</para>
///
/// <para><b>Warum die Runtime die eigentliche Grenze ist.</b> ADR-007 nennt die Resolve-Zeit, und
/// dort gibt es die frühe, freundliche Meldung. Aber ein <c>.lyrbc</c> kann von woanders kommen —
/// ein Compiler-Check schützt einen Host nicht, der fremden Bytecode lädt. Deshalb steht der
/// Bedarf <b>im Modul</b> (ADR-013: alles Nötige steht im Format), und die Durchsetzung passiert
/// beim Laden, zusammen mit der übrigen Validierung.</para>
/// </summary>
public static class CapabilityTable
{
    private static readonly (string Module, Capability Needs)[] Gated =
    [
        ("std.io.file", Capability.FileAccess),
        ("std.io.net", Capability.NetworkAccess),
        ("std.os", Capability.OsAccess),
        ("std.dotnet", Capability.HostAccess),
    ];

    /// <summary>Was dieses Modul verlangt. <see cref="Capability.None"/> für alles, was immer
    /// erlaubt ist — <c>std.core</c>, <c>std.string</c>, <c>std.collections</c> und die übrigen
    /// aus der ersten Zeile von Doku §20.1.</summary>
    public static Capability Required(string moduleName)
    {
        foreach (var (module, needs) in Gated)
            if (module == moduleName)
                return needs;
        return Capability.None;
    }

    /// <summary>Was ein Import dieses Namens verlangt — inklusive Untermodulen. <c>std.os.env</c>
    /// erbt von <c>std.os</c>, weil sonst jedes neue Untermodul eine stille Lücke wäre.</summary>
    public static Capability RequiredForImport(string moduleName)
    {
        var needed = Capability.None;
        foreach (var (module, needs) in Gated)
            if (moduleName == module || moduleName.StartsWith(module + ".", StringComparison.Ordinal))
                needed |= needs;
        return needed;
    }

    /// <summary>Der Name einer einzelnen Stufe, wie ihn Doku §20.1 schreibt — für Diagnosen.
    /// Mehrere Bits werden mit <c>+</c> verbunden.</summary>
    public static string Describe(Capability capability)
    {
        if (capability == Capability.None) return "none";

        var parts = new List<string>();
        if (capability.HasFlag(Capability.FileAccess)) parts.Add("fileAccess");
        if (capability.HasFlag(Capability.NetworkAccess)) parts.Add("networkAccess");
        if (capability.HasFlag(Capability.OsAccess)) parts.Add("osAccess");
        if (capability.HasFlag(Capability.HostAccess)) parts.Add("hostAccess");
        return string.Join(" + ", parts);
    }

    /// <summary>Eine Liste aus der Kommandozeile (<c>file,os</c>) in Bits. <c>null</c>, wenn ein
    /// Name unbekannt ist — dann meldet der Aufrufer, statt still weniger zu gewähren.</summary>
    public static Capability? Parse(string list)
    {
        var granted = Capability.None;
        foreach (var raw in list.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            Capability? one = raw.Trim() switch
            {
                "file" or "fileAccess" => Capability.FileAccess,
                "net" or "network" or "networkAccess" => Capability.NetworkAccess,
                "os" or "osAccess" => Capability.OsAccess,
                "host" or "hostAccess" => Capability.HostAccess,
                "all" => Capability.All,
                "none" => Capability.None,
                _ => null,
            };
            if (one is null) return null;
            granted |= one.Value;
        }
        return granted;
    }
}
