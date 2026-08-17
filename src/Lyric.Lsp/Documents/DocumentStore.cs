using System.Collections.Concurrent;

namespace Lyric.Lsp.Documents;

/// <summary>
/// One open buffer. <see cref="Uri"/> is the string the client sent, kept UNCHANGED: diagnostics
/// are addressed with it, and a client that does not recognise the spelling it gets back drops
/// them silently.
/// </summary>
public sealed record OpenDocument
{
    public required string Uri { get; init; }
    public required string Path { get; init; }
    public required int Version { get; init; }
    public required string Text { get; init; }
}

/// <summary>
/// What the editor has open, which is not what is on disk.
///
/// <para>The authoritative text of an open file is the unsaved buffer. <see cref="Core.SourceManager"/>
/// cannot hold it — it is append-only by design, and a file that changes on every keystroke would
/// grow it without bound — so the overlay lives here and is handed to the compiler per run.</para>
///
/// <para>The key is the PATH, not the URI: two spellings of one URI are the same file (see
/// <see cref="DocumentUri"/>), and keying on the string the client happened to send would open the
/// same document twice.</para>
///
/// <para>Concurrent because the read loop writes it while analysis tasks read it.</para>
/// </summary>
public sealed class DocumentStore
{
    private readonly ConcurrentDictionary<string, OpenDocument> _documents =
        new(DocumentUri.PathComparer);

    /// <summary>
    /// Records an opened or changed buffer and returns it, or <c>null</c> when the URI names
    /// nothing this server can analyse.
    /// </summary>
    public OpenDocument? Set(string uri, int version, string text)
    {
        if (!DocumentUri.TryToFilePath(uri, out var path)) return null;

        var document = new OpenDocument
        {
            Uri = uri,
            Path = path,
            Version = version,
            Text = text,
        };

        _documents[path] = document;
        return document;
    }

    /// <summary>Forgets a buffer. Returns what was there, so the caller can clear the diagnostics
    /// that were published under its URI.</summary>
    public OpenDocument? Remove(string uri)
    {
        if (!DocumentUri.TryToFilePath(uri, out var path)) return null;
        return _documents.TryRemove(path, out var document) ? document : null;
    }

    /// <summary>The current state of a buffer, by the path form of its URI.</summary>
    public OpenDocument? ByPath(string path) =>
        _documents.TryGetValue(path, out var document) ? document : null;

    /// <summary>
    /// Is this still the newest version of that document?
    ///
    /// <para>The question an analysis asks before it publishes. A result computed from text the
    /// user has since edited does not merely arrive late: its offsets address a document that no
    /// longer exists, so the squiggles land on the wrong characters.</para>
    /// </summary>
    public bool IsCurrent(OpenDocument document) =>
        ByPath(document.Path) is { } current && current.Version == document.Version;

    public int Count => _documents.Count;

    /// <summary>Every open buffer. A snapshot: the collection may change while it is read.</summary>
    public IReadOnlyCollection<OpenDocument> All => _documents.Values.ToArray();

    /// <summary>
    /// The open buffers as the compiler wants them: absolute path to text.
    ///
    /// <para>What <see cref="CompilerOptions.SourceOverlay"/> takes, so a program is compiled
    /// against the unsaved text of everything it imports rather than against the last save.</para>
    /// </summary>
    public Dictionary<string, string> Overlay()
    {
        var overlay = new Dictionary<string, string>(DocumentUri.PathComparer);
        foreach (var document in _documents.Values)
            overlay[Path.GetFullPath(document.Path)] = document.Text;
        return overlay;
    }
}
