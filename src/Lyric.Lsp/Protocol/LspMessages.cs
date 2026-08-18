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

    /// <summary>Announced only because it is implemented. A capability a server declares and then
    /// answers with nothing is worse than one it never claimed: the editor shows an empty tooltip
    /// instead of leaving the gesture to another provider.</summary>
    public required bool HoverProvider { get; init; }

    public required bool DefinitionProvider { get; init; }

    public required bool DocumentSymbolProvider { get; init; }

    public required bool ReferencesProvider { get; init; }

    public required CompletionOptions CompletionProvider { get; init; }

    public required RenameOptions RenameProvider { get; init; }

    public required bool WorkspaceSymbolProvider { get; init; }
}

/// <summary>Announcing <c>prepareProvider</c> makes the editor ask before opening its rename box,
/// so a refusal arrives before the user types a new name — the better of the two moments.</summary>
public sealed record RenameOptions
{
    public required bool PrepareProvider { get; init; }
}

public sealed record RenameParams
{
    public required TextDocumentIdentifier TextDocument { get; init; }
    public required Position Position { get; init; }
    public required string NewName { get; init; }
}

/// <summary>The answer to <c>prepareRename</c>: what to select and what to prefill.</summary>
public sealed record PrepareRenameResult
{
    public required Range Range { get; init; }
    public required string Placeholder { get; init; }
}

public sealed record TextEdit
{
    public required Range Range { get; init; }
    public required string NewText { get; init; }
}

/// <summary>
/// Edits over possibly many files, keyed by URI. The plain <c>changes</c> shape rather than
/// <c>documentChanges</c>: nothing here needs versioned edits or file operations, and the simple
/// shape is the one every client accepts.
/// </summary>
public sealed record WorkspaceEdit
{
    public required Dictionary<string, List<TextEdit>> Changes { get; init; }
}

public sealed record WorkspaceSymbolParams
{
    public required string Query { get; init; }
}

/// <summary>
/// The FLAT symbol shape. Deprecated members are omitted; the container name is all it can say
/// about nesting, and that is enough for a list a user filters by name.
/// </summary>
public sealed record SymbolInformation
{
    public required string Name { get; init; }
    public required SymbolKind Kind { get; init; }
    public required Location Location { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContainerName { get; init; }
}

/// <summary>What makes the editor ask for completions without being told to.</summary>
public sealed record CompletionOptions
{
    /// <summary>Typing one of these opens the list. Only <c>.</c>: everything else this server can
    /// answer is reached by typing a name, which the client asks about on its own.</summary>
    public required IReadOnlyList<string> TriggerCharacters { get; init; }
}

/// <summary>
/// The icon an editor puts beside a completion. A closed enum of the protocol, as
/// <see cref="SymbolKind"/> is, and mapped with the same judgement.
/// </summary>
public enum CompletionItemKind
{
    Method = 2,
    Function = 3,
    Field = 5,
    Variable = 6,
    Class = 7,
    Interface = 8,
    Module = 9,
    Enum = 13,
    Constant = 21,
    Struct = 22,
    EnumMember = 20,
}

public sealed record CompletionItem
{
    public required string Label { get; init; }

    public required CompletionItemKind Kind { get; init; }

    /// <summary>The one-line hint beside the label. The declaring type or module, so two members of
    /// the same name are told apart before the documentation is opened.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; init; }

    /// <summary>What was written above the declaration, if anything.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MarkupContent? Documentation { get; init; }
}

public sealed record CompletionParams
{
    public required TextDocumentIdentifier TextDocument { get; init; }
    public required Position Position { get; init; }
}

/// <summary>Whether the declaration itself counts as one of the places a name occurs. The client
/// decides; the two questions are different and an editor asks both.</summary>
public sealed record ReferenceContext
{
    public required bool IncludeDeclaration { get; init; }
}

public sealed record ReferenceParams
{
    public required TextDocumentIdentifier TextDocument { get; init; }
    public required Position Position { get; init; }
    public required ReferenceContext Context { get; init; }
}

/// <summary>
/// What kind of thing a symbol is, for the icon an editor puts beside it.
///
/// <para>A closed enum of the protocol, designed for other languages. Not every Lyric declaration
/// has an entry that fits; where none does, <see cref="Analysis.DocumentSymbolProvider"/> names the
/// one it chose and why.</para>
/// </summary>
public enum SymbolKind
{
    Namespace = 3,
    Class = 5,
    Method = 6,
    Field = 8,
    Enum = 10,
    Interface = 11,
    Function = 12,
    Constant = 14,
    EnumMember = 22,
    Struct = 23,
}

/// <summary>
/// One entry of a document's outline, with the entries declared inside it.
///
/// <para><see cref="SelectionRange"/> must be enclosed by <see cref="Range"/>: the first is what an
/// editor reveals, the second what it puts the cursor on. The two come from a declaration's span and
/// its name span, and that containment is a property the AST already guarantees.</para>
/// </summary>
public sealed record DocumentSymbol
{
    public required string Name { get; init; }

    public required SymbolKind Kind { get; init; }

    /// <summary>Everything the declaration covers, its body included.</summary>
    public required Range Range { get; init; }

    /// <summary>The name inside it.</summary>
    public required Range SelectionRange { get; init; }

    /// <summary>Omitted rather than sent empty: a client renders an expander for a present-but-empty
    /// array, and a struct with no members would look like one whose members failed to load.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<DocumentSymbol>? Children { get; init; }
}

public sealed record DocumentSymbolParams
{
    public required TextDocumentIdentifier TextDocument { get; init; }
}

/// <summary>A place in a file. The answer to "where is this declared".</summary>
public sealed record Location
{
    public required string Uri { get; init; }
    public required Range Range { get; init; }
}

/// <summary>
/// The richer answer to the same question: what the jump reveals and what it selects, kept apart.
///
/// <para>A client that announces <c>linkSupport</c> gets this instead of a
/// <see cref="Location"/>. The gain is <see cref="TargetRange"/>: a peek widget shows the whole
/// declaration and puts the cursor on the name, where a plain location has to choose one of the
/// two.</para>
/// </summary>
public sealed record LocationLink
{
    /// <summary>The span the cursor came from. Left out — a client uses it to widen its own
    /// highlight, and the compiler's node under the cursor is not always the whole name.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Range? OriginSelectionRange { get; init; }

    public required string TargetUri { get; init; }

    /// <summary>Everything the declaration covers, its body included.</summary>
    public required Range TargetRange { get; init; }

    /// <summary>The name inside it. Must be enclosed by <see cref="TargetRange"/>.</summary>
    public required Range TargetSelectionRange { get; init; }
}

/// <summary>What the client announces it can handle. Only the parts this server acts on.</summary>
public sealed record ClientCapabilities
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TextDocumentClientCapabilities? TextDocument { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkspaceClientCapabilities? Workspace { get; init; }
}

public sealed record WorkspaceClientCapabilities
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DidChangeWatchedFilesClientCapabilities? DidChangeWatchedFiles { get; init; }
}

public sealed record DidChangeWatchedFilesClientCapabilities
{
    /// <summary>Whether the client accepts a watch registration at all. There is no static form:
    /// file watching exists only as a dynamic registration, so a client without this never learns
    /// that a file changed behind the editor.</summary>
    public bool DynamicRegistration { get; init; }
}

public sealed record TextDocumentClientCapabilities
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DefinitionClientCapabilities? Definition { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DocumentSymbolClientCapabilities? DocumentSymbol { get; init; }
}

public sealed record DocumentSymbolClientCapabilities
{
    /// <summary>Whether the client reads the nested form. Absent means no, and this server then
    /// answers with nothing rather than with the flat form — see
    /// <see cref="Analysis.DocumentSymbolProvider"/>.</summary>
    public bool HierarchicalDocumentSymbolSupport { get; init; }
}

public sealed record DefinitionClientCapabilities
{
    /// <summary>Whether the client understands <see cref="LocationLink"/>. Absent means no: a
    /// client that never says so is one that would receive an object it cannot read.</summary>
    public bool LinkSupport { get; init; }
}

public sealed record InitializeParams
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ClientCapabilities? Capabilities { get; init; }
}

public sealed record TextDocumentPositionParams
{
    public required TextDocumentIdentifier TextDocument { get; init; }
    public required Position Position { get; init; }
}

/// <summary>Text with a declared format. Markdown, so a signature can be a fenced code block and
/// the client highlights it with the same grammar it uses for the file.</summary>
public sealed record MarkupContent
{
    public string Kind => "markdown";
    public required string Value { get; init; }
}

public sealed record Hover
{
    public required MarkupContent Contents { get; init; }

    /// <summary>What the answer is about. The editor underlines it, which is the only feedback the
    /// user gets that the cursor hit what they meant.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Range? Range { get; init; }
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

/// <summary>
/// One registration of the single kind this server makes. The protocol allows arbitrary
/// register options per method; typing them as the watched-files options rather than as a free
/// element keeps the one use checkable, and a second registration kind is a second member here.
/// </summary>
public sealed record Registration
{
    /// <summary>Chosen by the server; only needed again to unregister, which this server never
    /// does — the watches live exactly as long as the session.</summary>
    public required string Id { get; init; }

    public required string Method { get; init; }

    public required DidChangeWatchedFilesRegistrationOptions RegisterOptions { get; init; }
}

public sealed record RegistrationParams
{
    public required IReadOnlyList<Registration> Registrations { get; init; }
}

public sealed record DidChangeWatchedFilesRegistrationOptions
{
    public required IReadOnlyList<LspFileSystemWatcher> Watchers { get; init; }
}

/// <summary>The protocol calls this <c>FileSystemWatcher</c>; the prefix only keeps it apart from
/// <see cref="System.IO.FileSystemWatcher"/>, the same way <see cref="LspDiagnostic"/> does.
/// </summary>
public sealed record LspFileSystemWatcher
{
    /// <summary>Relative patterns need a base URI; the plain string form watches across every
    /// workspace folder, which is exactly what a server that discovers projects by path wants.
    /// </summary>
    public required string GlobPattern { get; init; }
}

public enum FileChangeType
{
    Created = 1,
    Changed = 2,
    Deleted = 3,
}

public sealed record FileEvent
{
    public required string Uri { get; init; }
    public required FileChangeType Type { get; init; }
}

public sealed record DidChangeWatchedFilesParams
{
    public required IReadOnlyList<FileEvent> Changes { get; init; }
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
    public const string Hover = "textDocument/hover";
    public const string Definition = "textDocument/definition";
    public const string DocumentSymbol = "textDocument/documentSymbol";
    public const string References = "textDocument/references";
    public const string Completion = "textDocument/completion";

    public const string PrepareRename = "textDocument/prepareRename";
    public const string Rename = "textDocument/rename";
    public const string WorkspaceSymbol = "workspace/symbol";

    public const string DidChangeWatchedFiles = "workspace/didChangeWatchedFiles";

    public const string PublishDiagnostics = "textDocument/publishDiagnostics";
    public const string LogMessage = "window/logMessage";
    public const string RegisterCapability = "client/registerCapability";
}
