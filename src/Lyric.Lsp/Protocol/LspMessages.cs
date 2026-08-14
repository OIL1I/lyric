using System.Text.Json.Serialization;

namespace Lyric.Lsp.Protocol;

// The subset of the protocol this server speaks. Every member the server neither reads nor writes
// is left out rather than carried as an unused field: an absent member is a question the server
// does not answer, and a present one that is always null is a promise it silently breaks.
//
// Names are mapped to camelCase by the serializer options; only members whose wire name is not the
// camelCase form of their C# name carry an explicit attribute.

/// <summary>A position in a document: both components 0-based, the character counted in UTF-16
/// code units.</summary>
/// <remarks>
/// The unit matters and is negotiated: the server announces <c>utf-16</c> in
/// <see cref="ServerCapabilities.PositionEncoding"/>, which is what <see cref="Core.Span"/> already
/// counts in. Announcing anything else would mean re-encoding every offset.
/// </remarks>
public sealed record Position
{
    public required int Line { get; init; }
    public required int Character { get; init; }
}

/// <summary>A half-open range, like <see cref="Core.Span"/>: <c>end</c> is the first position no
/// longer covered.</summary>
public sealed record Range
{
    public required Position Start { get; init; }
    public required Position End { get; init; }
}

/// <summary>The four levels an editor renders. Lyric knows three of them; there is no
/// <c>Information</c> in <see cref="Core.Severity"/>.</summary>
public enum LspSeverity
{
    Error = 1,
    Warning = 2,
    Information = 3,
    Hint = 4,
}

public sealed record LspDiagnostic
{
    public required Range Range { get; init; }
    public required LspSeverity Severity { get; init; }

    /// <summary>The stable identifier, <c>LYR-SEM0064</c> and its kind. A string rather than a
    /// number, because the diagnostic codes of this compiler are strings.</summary>
    public required string Code { get; init; }

    /// <summary>Who is speaking. Editors show it beside the message when several servers report on
    /// one file.</summary>
    public string Source => "lyric";

    public required string Message { get; init; }
}

public sealed record PublishDiagnosticsParams
{
    public required string Uri { get; init; }

    /// <summary>The document version these diagnostics were computed from. A client uses it to
    /// discard an answer that its own newer edit has already invalidated — the second half of the
    /// guard <see cref="Analysis.AnalysisService"/> keeps on this side.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Version { get; init; }

    public required IReadOnlyList<LspDiagnostic> Diagnostics { get; init; }
}

public enum TextDocumentSyncKind
{
    None = 0,

    /// <summary>Every change carries the whole document. What this server asks for; see
    /// <see cref="Documents.DocumentStore"/>.</summary>
    Full = 1,

    Incremental = 2,
}

public sealed record TextDocumentSyncOptions
{
    /// <summary>Whether <c>didOpen</c> and <c>didClose</c> are sent at all. Required here: without
    /// <c>didClose</c> the diagnostics of a closed file would stay in the editor forever.</summary>
    public bool OpenClose => true;

    public required TextDocumentSyncKind Change { get; init; }
}

public sealed record ServerCapabilities
{
    public required TextDocumentSyncOptions TextDocumentSync { get; init; }
    public required string PositionEncoding { get; init; }
}

public sealed record ServerInfo
{
    public required string Name { get; init; }
    public required string Version { get; init; }
}

public sealed record InitializeResult
{
    public required ServerCapabilities Capabilities { get; init; }
    public required ServerInfo ServerInfo { get; init; }
}

public sealed record TextDocumentItem
{
    public required string Uri { get; init; }
    public required int Version { get; init; }
    public required string Text { get; init; }
}

public sealed record VersionedTextDocumentIdentifier
{
    public required string Uri { get; init; }
    public required int Version { get; init; }
}

public sealed record TextDocumentIdentifier
{
    public required string Uri { get; init; }
}

public sealed record DidOpenTextDocumentParams
{
    public required TextDocumentItem TextDocument { get; init; }
}

/// <summary>One change. Under <see cref="TextDocumentSyncKind.Full"/> the range members are absent
/// and <see cref="Text"/> is the entire document.</summary>
public sealed record TextDocumentContentChangeEvent
{
    public required string Text { get; init; }
}

public sealed record DidChangeTextDocumentParams
{
    public required VersionedTextDocumentIdentifier TextDocument { get; init; }
    public required IReadOnlyList<TextDocumentContentChangeEvent> ContentChanges { get; init; }
}

public sealed record DidCloseTextDocumentParams
{
    public required TextDocumentIdentifier TextDocument { get; init; }
}

public enum MessageType
{
    Error = 1,
    Warning = 2,
    Info = 3,
    Log = 4,
}

public sealed record LogMessageParams
{
    public required MessageType Type { get; init; }
    public required string Message { get; init; }
}

/// <summary>The method names this server reacts to, so a typo is a compile error rather than a
/// message that is silently never dispatched.</summary>
public static class LspMethods
{
    public const string Initialize = "initialize";
    public const string Initialized = "initialized";
    public const string Shutdown = "shutdown";
    public const string Exit = "exit";
    public const string CancelRequest = "$/cancelRequest";

    public const string DidOpen = "textDocument/didOpen";
    public const string DidChange = "textDocument/didChange";
    public const string DidClose = "textDocument/didClose";
    public const string DidSave = "textDocument/didSave";

    public const string PublishDiagnostics = "textDocument/publishDiagnostics";
    public const string LogMessage = "window/logMessage";
}
