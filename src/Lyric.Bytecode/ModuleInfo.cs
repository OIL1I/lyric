using System.Text;

namespace Lyric.Bytecode;

/// <summary>
/// Die Kopfdaten eines <c>.lyrbc</c>-Moduls in lesbarer und in maschinenlesbarer Form —
/// der Unterbau von <c>lyrvm info</c>.
///
/// <para>Warum das neben dem Disassembler existiert: der beantwortet „was tut dieser Code", nicht
/// „ist diese Datei, was sie zu sein behauptet". Fuer ADR-013 ist die zweite Frage die wichtigere,
/// weil <c>.lyrbc</c> ein Auslieferungsartefakt mit Spec ist — und weil sie das ist, was ein
/// <b>zweiter</b> Implementierer zuerst stellt: „stimmt mein Reader mit deinem darueber ueberein,
/// wie viele Eintraege in welcher Tabelle stehen?". <c>--json</c> macht daraus einen Diff.
/// Jedes ernsthafte Binaerformat hat so ein Werkzeug: <c>objdump -h</c>, <c>wasm-objdump -h</c>,
/// <c>ildasm /headers</c>.</para>
///
/// <para><b>Grenze</b>: Sektions-Byte-Groessen fehlen. <see cref="BytecodeModule"/> behaelt sie
/// nicht — der Reader verwirft sie nach dem Parsen. Sie nachzuruesten hiesse, das Modell um
/// Herkunftsdaten zu erweitern; das ist eine eigene Entscheidung und kein Nebenprodukt dieses
/// Kommandos.</para>
/// </summary>
public static class ModuleInfo
{
    public static string Text(BytecodeModule module, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{path}");
        sb.AppendLine($"  format          {module.VersionMajor}.{module.VersionMinor}");
        sb.AppendLine($"  capabilities    0x{module.Capabilities:x16}");
        sb.AppendLine($"  entry           {EntryName(module)}");
        sb.AppendLine($"  strings         {module.Strings.Count}");
        sb.AppendLine($"  types           {module.Types.Count} "
                      + $"({module.Types.Count(t => t.IsEnum)} enum)");
        sb.AppendLine($"  imports         {module.Imports.Count}");
        sb.AppendLine($"  functions       {module.Functions.Count}");
        sb.AppendLine($"  code            {TotalCodeBytes(module)} bytes");

        if (module.Imports.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  imports:");
            foreach (var import in module.Imports)
                sb.AppendLine($"    {import.Name}");
        }

        sb.AppendLine();
        sb.AppendLine("  functions:");
        sb.AppendLine($"    {"name",-34}{"params",7}{"slots",7}{"stack",7}{"blocks",8}{"bytes",8}");
        foreach (var fn in module.Functions)
            sb.AppendLine($"    {Fit(fn.Name, 34),-34}{fn.ParamCount,7}{fn.SlotTypes.Count,7}"
                          + $"{fn.MaxStack,7}{fn.BlockOffsets.Count,8}{fn.Code.Length,8}");

        return sb.ToString();
    }

    /// <summary>
    /// Handgeschriebenes JSON, wie <see cref="Core.DiagnosticEngine.RenderJson"/> auch — dieselbe
    /// Begruendung: eine Serializer-Abhaengigkeit fuer ein Dutzend Felder waere unverhaeltnismaessig,
    /// und die Ausgabe soll byte-stabil sein, damit zwei Runtimes sie diffen koennen.
    /// </summary>
    public static string Json(BytecodeModule module, string path)
    {
        var sb = new StringBuilder();
        sb.Append("{\"path\":").Append(Quote(path));
        sb.Append(",\"format\":{\"major\":").Append(module.VersionMajor)
          .Append(",\"minor\":").Append(module.VersionMinor).Append('}');
        sb.Append(",\"capabilities\":").Append(module.Capabilities);
        sb.Append(",\"entry\":").Append(module.Start is null ? "null" : Quote(EntryName(module)));
        sb.Append(",\"counts\":{\"strings\":").Append(module.Strings.Count)
          .Append(",\"types\":").Append(module.Types.Count)
          .Append(",\"imports\":").Append(module.Imports.Count)
          .Append(",\"functions\":").Append(module.Functions.Count)
          .Append(",\"codeBytes\":").Append(TotalCodeBytes(module)).Append('}');

        sb.Append(",\"imports\":[");
        for (var i = 0; i < module.Imports.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Quote(module.Imports[i].Name));
        }
        sb.Append(']');

        sb.Append(",\"functions\":[");
        for (var i = 0; i < module.Functions.Count; i++)
        {
            var fn = module.Functions[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"name\":").Append(Quote(fn.Name))
              .Append(",\"params\":").Append(fn.ParamCount)
              .Append(",\"slots\":").Append(fn.SlotTypes.Count)
              .Append(",\"maxStack\":").Append(fn.MaxStack)
              .Append(",\"blocks\":").Append(fn.BlockOffsets.Count)
              .Append(",\"codeBytes\":").Append(fn.Code.Length).Append('}');
        }
        sb.Append("]}");

        return sb.ToString();
    }

    /// <summary>Der Name der Einstiegsfunktion. <see cref="BytecodeModule.Start"/> indiziert den
    /// gemeinsamen Raum aus erst Imports, dann Funktionen — ein Einstieg im Import-Bereich waere
    /// ein kaputtes Modul, das der Reader aber schon abgelehnt haette.</summary>
    private static string EntryName(BytecodeModule module)
    {
        if (module.Start is not { } start) return "(library — no start section)";
        var index = start - module.Imports.Count;
        return index >= 0 && index < module.Functions.Count
            ? module.Functions[index].Name
            : $"(invalid index {start})";
    }

    private static int TotalCodeBytes(BytecodeModule module) =>
        module.Functions.Sum(fn => fn.Code.Length);

    private static string Fit(string text, int width) =>
        text.Length <= width ? text : text[..(width - 3)] + "...";

    private static string Quote(string text)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in text)
        {
            if (c == '"') sb.Append("\\\"");
            else if (c == '\\') sb.Append("\\\\");
            else if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }
}
