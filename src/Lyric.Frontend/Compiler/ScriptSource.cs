using Lyric.Core;

namespace Lyric.Compiler;

/// <summary>
/// Where the entry module's source comes from: disk or memory.
///
/// <para>The module name is derived from the file path, so a host compiling from memory has to
/// name the module itself.</para>
///
/// <para>One seam rather than a second path: the two origins differ in exactly one step, getting
/// the <see cref="FileId"/>. Everything after that is identical.</para>
/// </summary>
public sealed class ScriptSource
{
    private readonly string? _path;
    private readonly string? _text;

    private ScriptSource(string displayName, string? moduleName, string? path, string? text)
    {
        DisplayName = displayName;
        ModuleName = moduleName;
        _path = path;
        _text = text;
    }

    /// <summary>The name used in diagnostics and in the progress display.</summary>
    public string DisplayName { get; }

    /// <summary>
    /// What the module is CALLED, as opposed to how it is displayed.
    ///
    /// <para>The two are separate because a host loading two scripts would otherwise hold two
    /// modules both named <c>main</c>, and a call by name would find the wrong function.</para>
    ///
    /// <para><c>null</c> for a file: there the name comes from the path.</para>
    /// </summary>
    public string? ModuleName { get; }

    /// <summary>
    /// The directory a source map's paths are made relative to: the entry file's own directory, or
    /// the working directory for source held in memory.
    ///
    /// <para>It lives here because this is where the path lives. A file outside it — the standard
    /// library sits beside the toolchain — keeps its bare name rather than a chain of <c>..</c>
    /// segments describing the machine that built the module.</para>
    /// </summary>
    public string BaseDirectory => _path is null
        ? Directory.GetCurrentDirectory()
        : Path.GetDirectoryName(Path.GetFullPath(_path)) ?? Directory.GetCurrentDirectory();

    /// <summary>
    /// A file on disk.
    /// </summary>
    /// <param name="moduleName">What the module should be called. <c>null</c> leaves it to the
    /// resolver's default (<c>main</c>), which is what the CLI does. A host names it, because it
    /// needs the name later to call into the module.</param>
    public static ScriptSource FromDisk(string path, string? moduleName = null) =>
        new(Path.GetFileName(path), moduleName, path, null);

    /// <summary>
    /// Source held in memory, under a name chosen by the caller.
    ///
    /// <para>The name is required: two scripts without a path would collide silently under the
    /// same one, and only the host knows whether two are the same module.</para>
    /// </summary>
    public static ScriptSource FromText(string moduleName, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentNullException.ThrowIfNull(text);
        return new ScriptSource(moduleName, moduleName, null, text);
    }

    /// <summary>
    /// A file whose CONTENT comes from memory while its IDENTITY comes from the path.
    ///
    /// <para>The case of an editor: the file exists on disk, but the authoritative text is the
    /// unsaved buffer. <see cref="FromDisk"/> would read the stale bytes and
    /// <see cref="FromText"/> would lose the path, and with it the only thing that tells two
    /// buffers of the same name apart.</para>
    ///
    /// <para><paramref name="displayName"/> is the whole path rather than the file name: it is
    /// what a diagnostic is matched against to decide whether it belongs to this file, and two
    /// open files can share a name.</para>
    /// </summary>
    /// <param name="moduleName">As in <see cref="FromDisk"/>: <c>null</c> leaves the name to the
    /// module header, and to <c>main</c> when there is none.</param>
    public static ScriptSource FromBuffer(string displayName, string text, string? moduleName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(text);
        return new ScriptSource(displayName, moduleName, null, text);
    }

    /// <summary>Places the source in the <see cref="SourceManager"/>. <c>null</c> when the file
    /// could not be read; the diagnostic is then in <paramref name="diagnostics"/>.</summary>
    internal FileId? Open(SourceManager sources, DiagnosticEngine diagnostics)
    {
        if (_text is not null) return sources.AddVirtual(DisplayName, _text);

        try
        {
            return sources.AddFromDisk(_path!);
        }
        catch
        {
            diagnostics.Report(CliDiagnostics.FileUnreadable, Severity.Error, default,
                $"failed to read file: {_path}");
            return null;
        }
    }
}
