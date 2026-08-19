namespace Lyric.Core;

/// <summary>
/// The capability levels. One bit each; the values are part of the bytecode contract (section
/// id 1) and do not change.
/// </summary>
[Flags]
public enum Capability : ulong
{
    None = 0,

    /// <summary><c>std.io.file</c> — reading and writing the file system.</summary>
    FileAccess = 1UL << 0,

    /// <summary><c>std.io.net</c> — sockets. The module does not exist yet; the bit is fixed
    /// regardless, because a number that later means something else invalidates every older
    /// <c>.lyrbc</c>.</summary>
    NetworkAccess = 1UL << 1,

    /// <summary><c>std.os</c> — environment variables, processes, exit codes.</summary>
    OsAccess = 1UL << 2,

    /// <summary><c>std.dotnet</c> — host access through reflection.</summary>
    HostAccess = 1UL << 3,

    /// <summary>What the standalone mode grants: everything.</summary>
    All = FileAccess | NetworkAccess | OsAccess | HostAccess,
}

/// <summary>
/// Which standard library module requires which capability.
///
/// <para>Both sides need the table: the compiler writes what a module requires into the
/// Capabilities section, and the runtime checks that against what it grants.</para>
///
/// <para>The requirement travels in the module, and enforcement happens at load time along with
/// the rest of the validation, so a host loading foreign bytes is protected too.</para>
/// </summary>
public static class CapabilityTable
{
    private static readonly (string Module, Capability Needs)[] Gated =
    [
        ("std.io.file", Capability.FileAccess),
        ("std.io.net", Capability.NetworkAccess),
        ("std.os", Capability.OsAccess),
        // The same bit as std.os, deliberately: reading the clock is a question to the
        // environment, and a new bit would be a contract change for every older runtime.
        ("std.time", Capability.OsAccess),
        ("std.dotnet", Capability.HostAccess),
    ];

    /// <summary>What this module requires. <see cref="Capability.None"/> for everything that is
    /// always permitted.</summary>
    public static Capability Required(string moduleName)
    {
        foreach (var (module, needs) in Gated)
            if (module == moduleName)
                return needs;
        return Capability.None;
    }

    /// <summary>What an import of this name requires, submodules included: <c>std.os.env</c>
    /// inherits from <c>std.os</c>.</summary>
    public static Capability RequiredForImport(string moduleName)
    {
        var needed = Capability.None;
        foreach (var (module, needs) in Gated)
            if (moduleName == module || moduleName.StartsWith(module + ".", StringComparison.Ordinal))
                needed |= needs;
        return needed;
    }

    /// <summary>The name of a single level, for diagnostics. Several bits are joined with
    /// <c>+</c>.</summary>
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

    /// <summary>A command-line list (<c>file,os</c>) as bits. <c>null</c> when a name is unknown,
    /// so the caller reports rather than silently granting less.</summary>
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
