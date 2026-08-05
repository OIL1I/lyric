namespace Lyric.Core;

/// <summary>Wann eine Fortschrittsanzeige erscheint.</summary>
public enum ProgressMode
{
    /// <summary>Nur, wenn stderr ein Terminal ist und der Lauf lange genug dauert.</summary>
    Auto,

    /// <summary>Nie. Fuer Faelle, in denen die Terminal-Erkennung danebenliegt.</summary>
    Never,

    /// <summary>Immer, auch ohne Terminal. Existiert fuer die Tests — ohne einen erzwingbaren
    /// Pfad liesse sich „der Fortschritt fasst stdout nicht an" nicht pruefen, und genau das ist
    /// die Zusage, die kaputtgehen kann.</summary>
    Always,
}

/// <summary>
/// Die Optionen, die <b>alle drei</b> Binaries verstehen muessen.
///
/// <para>Einmal geparst und einmal interpretiert (ADR-017 sinngemaess): waeren es drei Kopien,
/// gaebe <c>lyrc check --json</c> JSON und <c>lyric check --json</c> still nicht — derselbe Bug wie
/// beim dreifach kopierten Compiler-Vorspann in M6, nur eine Ebene tiefer. Deshalb in
/// <c>Lyric.Core</c>, dem einzigen gemeinsamen Vorfahr; <c>lyrvm</c> darf nichts
/// Compiler-seitiges referenzieren.</para>
///
/// <para><see cref="Parse"/> liefert die <b>Restargumente</b> mit, damit jedes Binary danach
/// seinen eigenen Parser darauf laufen lassen kann — dieselbe Form wie
/// <c>VmSelection.Parse</c>.</para>
/// </summary>
public sealed record ToolOptions
{
    /// <summary>Diagnosen als JSON statt als Klartext. Geht wie der Klartext auf <b>stderr</b>:
    /// stdout gehoert dem Programm bzw. dem angeforderten Dump (Runner-Vertrag §9.3).</summary>
    public bool Json { get; init; }

    /// <summary>Unterdrueckt Erfolgs-Meldungen (<c>path: ok</c>, <c>path: N bytes</c>) und den
    /// Fortschritt. Diagnosen bleiben — ein <c>--quiet</c>, das Fehler schluckt, waere
    /// gefaehrlich statt leise.</summary>
    public bool Quiet { get; init; }

    /// <summary>Zeitaufschluesselung je Phase statt der Live-Zeile. Funktioniert auch ohne
    /// Terminal und benutzt keine Escape-Sequenzen.</summary>
    public bool Verbose { get; init; }

    public ProgressMode Progress { get; init; } = ProgressMode.Auto;

    public static ToolOptions Default => new();

    /// <summary>
    /// Zieht die gemeinsamen Flags aus der Kommandozeile.
    ///
    /// <para>Alles ab dem ersten <c>--</c> bleibt unangetastet: dahinter stehen die Argumente des
    /// Lyric-Programms, und ein <c>--quiet</c> darin gehoert dem Programm, nicht uns.</para>
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

                // Keine Kurzform '-v': die ist in allen drei Binaries bereits '--version' und
                // steht so in Doku.md §23. rustc loest dasselbe mit '-V'/'-v' — das waere hier
                // eine Bruchaenderung an einer schon ausgelieferten Oberflaeche.
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
