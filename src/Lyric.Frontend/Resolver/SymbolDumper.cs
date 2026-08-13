using System.Text;

namespace Lyric.Resolver;

/// <summary>
/// A deterministic dump of a compilation's resolved symbol structure for golden snapshots,
/// alongside <c>AstDumper</c>. Shows the members per module in declaration order, including
/// visibility and import resolution. Built-ins are omitted.
/// </summary>
public static class SymbolDumper
{
    public static string Dump(Compilation compilation)
    {
        var sb = new StringBuilder();
        foreach (var module in compilation.Modules)
        {
            sb.Append("Module ").Append(module.FullName).Append('\n');
            foreach (var s in module.Members.Symbols) Write(s, 1, sb);
        }
        return sb.ToString();
    }

    private static void Write(Symbol s, int indent, StringBuilder sb)
    {
        switch (s)
        {
            case TypeSymbol t:
                Line(sb, indent, $"{t.Kind} {t.Name}{Vis(t.Visibility)}");
                foreach (var m in t.Members.Symbols) Write(m, indent + 1, sb);
                break;
            case FunctionSymbol f:
                Line(sb, indent, $"Function {f.Name}{Vis(f.Visibility)}{(f.IsMut ? " mut" : "")}");
                break;
            case FieldSymbol:
                Line(sb, indent, $"Field {s.Name}");
                break;
            case EnumVariantSymbol:
                Line(sb, indent, $"Variant {s.Name}");
                break;
            case GlobalSymbol g:
                Line(sb, indent, $"Global {g.Name}{Vis(g.Visibility)}");
                break;
            case ImportBindingSymbol ib:
                Line(sb, indent, $"Import {ib.Name} -> {Describe(ib.Target)}");
                break;
            case ExternalSymbol ex:
                Line(sb, indent, $"External {ex.Name} (from {string.Join('.', ex.SourcePath)})");
                break;
            case ErrorSymbol:
                Line(sb, indent, $"Error {s.Name}");
                break;
        }
    }

    private static string Describe(Symbol s) => s switch
    {
        ModuleSymbol m => $"module {m.FullName}",
        TypeSymbol t => $"{t.Kind} {t.Name}",
        FunctionSymbol f => $"fn {f.Name}",
        GlobalSymbol g => $"global {g.Name}",
        _ => s.Name
    };

    private static string Vis(Visibility v) => v == Visibility.Public ? " [pub]" : "";

    private static void Line(StringBuilder sb, int indent, string text) =>
        sb.Append(' ', indent * 2).Append(text).Append('\n');
}
