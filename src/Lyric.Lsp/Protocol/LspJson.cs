using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Lyric.Lsp.Protocol;

/// <summary>
/// The serializer contract for everything that crosses the wire.
///
/// <para>Source-generated rather than reflection-driven. The binaries of this toolchain are
/// published self-contained, and a reflection-based serializer is the one component that stops
/// working when the publish starts trimming: the metadata it needs exists only at runtime, so the
/// trimmer cannot see it and the failure arrives as a missing property rather than as a build
/// error.</para>
///
/// <para>No global <see cref="JsonIgnoreCondition.WhenWritingNull"/>. A response to
/// <c>shutdown</c> is specified to carry <c>"result": null</c>, and a rule that drops null members
/// everywhere would turn that answer into a response with no result at all — a different message,
/// and one some clients wait on forever.</para>
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(JsonRpcMessage))]
[JsonSerializable(typeof(JsonRpcSuccess))]
[JsonSerializable(typeof(JsonRpcFailure))]
[JsonSerializable(typeof(JsonRpcNotification))]
[JsonSerializable(typeof(JsonRpcRequest))]
[JsonSerializable(typeof(RegistrationParams))]
[JsonSerializable(typeof(DidChangeWatchedFilesParams))]
[JsonSerializable(typeof(InitializeResult))]
[JsonSerializable(typeof(PublishDiagnosticsParams))]
[JsonSerializable(typeof(LogMessageParams))]
[JsonSerializable(typeof(DidOpenTextDocumentParams))]
[JsonSerializable(typeof(DidChangeTextDocumentParams))]
[JsonSerializable(typeof(DidCloseTextDocumentParams))]
[JsonSerializable(typeof(TextDocumentPositionParams))]
[JsonSerializable(typeof(Hover))]
[JsonSerializable(typeof(Location))]
[JsonSerializable(typeof(LocationLink[]))]
[JsonSerializable(typeof(InitializeParams))]
[JsonSerializable(typeof(DocumentSymbolParams))]
[JsonSerializable(typeof(ReferenceParams))]
[JsonSerializable(typeof(CompletionParams))]
[JsonSerializable(typeof(IReadOnlyList<CompletionItem>))]
[JsonSerializable(typeof(List<Location>))]
[JsonSerializable(typeof(IReadOnlyList<DocumentSymbol>))]
[JsonSerializable(typeof(RenameParams))]
[JsonSerializable(typeof(PrepareRenameResult))]
[JsonSerializable(typeof(WorkspaceEdit))]
[JsonSerializable(typeof(WorkspaceSymbolParams))]
[JsonSerializable(typeof(IReadOnlyList<SymbolInformation>))]
public sealed partial class LspJson : JsonSerializerContext
{
    /// <summary>
    /// Reads the parameters of a message into the type the method expects.
    ///
    /// <para>Returns <c>null</c> when the member is absent or does not fit, which the caller turns
    /// into <see cref="JsonRpcErrorCodes.InvalidParams"/>. A throw would take the read loop down
    /// over one malformed message from one client.</para>
    /// </summary>
    public static T? ReadParams<T>(JsonElement? parameters, JsonTypeInfo<T> type) where T : class
    {
        if (parameters is not { } element) return null;
        try
        {
            return element.Deserialize(type);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Turns a payload into the detached element a response or notification carries.
    /// </summary>
    public static JsonElement ToElement<T>(T value, JsonTypeInfo<T> type) =>
        JsonSerializer.SerializeToElement(value, type);
}
