using System.Text;

namespace Lyric.Bytecode;

/// <summary>
/// The header data of a <c>.lyrbc</c> module in readable and machine-readable form; the basis of
/// <c>lyrvm info</c>.
///
/// <para>It answers whether a file is what it claims to be, which the disassembler does not; with
/// <c>--json</c> two readers can be diffed against each other.</para>
///
/// <para>Section byte sizes are not included: <see cref="BytecodeModule"/> discards them after
/// parsing.</para>
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
        if (module.Attributes.Count > 0)
            sb.AppendLine($"  attributes      {module.Attributes.Count}");
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

    /// <summary>Hand-written JSON, as in <see cref="Core.DiagnosticEngine.RenderJson"/>, so the
    /// output is byte-stable and two runtimes can diff it.</summary>
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
          .Append(",\"attributes\":").Append(module.Attributes.Count)
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

    /// <summary>The name of the entry function. <see cref="BytecodeModule.Start"/> indexes the
    /// shared space of imports first, then functions.</summary>
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
