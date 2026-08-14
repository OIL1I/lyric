using System.Text.Json;
using System.Text.Json.Serialization;
using Lyric.DocGen.Extraction;
using Lyric.DocGen.Model;

namespace Lyric.DocGen;

/// <summary>
/// The generator's entry point.
///
/// <para>Today it extracts the standard library and writes the model as JSON. The JSON is what the
/// golden tests compare, and it is what the renderer will consume once it exists.</para>
/// </summary>
public static class Program
{
    /// <summary>
    /// Indented and with a trailing newline, so a snapshot diff shows lines rather than one string.
    /// Unicode is not escaped: the signatures carry '…' and the docs carry ordinary prose.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static int Main(string[] args)
    {
        if (args.Length is 0 or > 2 || args[0] is "-h" or "--help")
        {
            Console.Error.WriteLine("usage: docgen <repo-root> [<output.json>]");
            return 2;
        }

        var repoRoot = Path.GetFullPath(args[0]);
        var stdlibRoot = Path.Combine(repoRoot, "stdlib");

        if (!Directory.Exists(stdlibRoot))
        {
            Console.Error.WriteLine($"docgen: no stdlib directory under {repoRoot}");
            return 1;
        }

        DocModel model;
        try
        {
            model = StdlibExtractor.Extract(stdlibRoot, repoRoot);
        }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine($"docgen: {e.Message}");
            return 1;
        }

        var json = Serialize(model);
        if (args.Length == 2)
            File.WriteAllText(args[1], json);
        else
            Console.Out.Write(json);

        return 0;
    }

    /// <summary>The model as JSON, with '\n' line endings on every platform so the output does not
    /// depend on where it was produced.</summary>
    public static string Serialize(DocModel model) =>
        JsonSerializer.Serialize(model, Json).ReplaceLineEndings("\n") + "\n";
}
