using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lyric.Lsp.Protocol;

/// <summary>
/// One message as it arrives, before anything is known about it.
///
/// <para>All four members are optional, because the three shapes JSON-RPC allows differ only in
/// which of them are present: a request has <see cref="Method"/> and <see cref="Id"/>, a
/// notification only <see cref="Method"/>, a response only <see cref="Id"/>. Deciding that from
/// one type rather than from three parse attempts keeps the dispatcher honest about a message
/// that is none of them.</para>
///
/// <para><see cref="Id"/> and <see cref="Params"/> stay <see cref="JsonElement"/>: the id may be a
/// number or a string and is echoed back UNCHANGED, and the parameters cannot be typed before the
/// method is known. Deserializing into <see cref="JsonElement"/> detaches it from the reader's
/// buffer, so both outlive the parse.</para>
/// </summary>
public sealed record JsonRpcMessage
{
    [JsonPropertyName("jsonrpc")] public string? JsonRpc { get; init; }
    [JsonPropertyName("id")] public JsonElement? Id { get; init; }
    [JsonPropertyName("method")] public string? Method { get; init; }
    [JsonPropertyName("params")] public JsonElement? Params { get; init; }

    /// <summary>Present on a failed RESPONSE. Read only to log it: the one request this server
    /// sends is the watch registration, and a client that refuses it should say so somewhere the
    /// user can find.</summary>
    [JsonPropertyName("error")] public JsonRpcError? Error { get; init; }

    /// <summary>A request expects an answer; a notification does not. The distinction is the
    /// presence of an id and nothing else.</summary>
    public bool IsRequest => Method is not null && Id is not null;

    public bool IsNotification => Method is not null && Id is null;

    /// <summary>An answer to a request this server sent: an id without a method.</summary>
    public bool IsResponse => Method is null && Id is not null;
}

/// <summary>
/// An answer that carries a result.
///
/// <para>Separate from <see cref="JsonRpcFailure"/> rather than one type with two nullable
/// members: a response carries a result OR an error, and <c>result</c> may legitimately be
/// <c>null</c> — <c>shutdown</c> is specified to answer exactly that. One type could not tell
/// "no result" from "the result is null" without a third flag.</para>
/// </summary>
public sealed record JsonRpcSuccess
{
    [JsonPropertyName("jsonrpc")] public string JsonRpc => "2.0";
    [JsonPropertyName("id")] public required JsonElement Id { get; init; }
    [JsonPropertyName("result")] public JsonElement? Result { get; init; }
}

/// <summary>An answer that carries an error.</summary>
public sealed record JsonRpcFailure
{
    [JsonPropertyName("jsonrpc")] public string JsonRpc => "2.0";

    /// <summary>Null only when the request was so malformed that no id could be read; the
    /// specification asks for a null id there rather than for silence.</summary>
    [JsonPropertyName("id")] public JsonElement? Id { get; init; }

    [JsonPropertyName("error")] public required JsonRpcError Error { get; init; }
}

public sealed record JsonRpcError
{
    [JsonPropertyName("code")] public required int Code { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }
}

/// <summary>
/// A request the SERVER issues. Exactly one exists: the watch registration after
/// <c>initialized</c>.
///
/// <para>The id is a plain integer because this side chooses it; the <see cref="JsonElement"/>
/// gymnastics of <see cref="JsonRpcMessage.Id"/> exist only for ids chosen by the other side.
/// </para>
/// </summary>
public sealed record JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")] public string JsonRpc => "2.0";
    [JsonPropertyName("id")] public required int Id { get; init; }
    [JsonPropertyName("method")] public required string Method { get; init; }
    [JsonPropertyName("params")] public JsonElement? Params { get; init; }
}

/// <summary>A message the server sends without being asked. In this server: diagnostics and log
/// messages.</summary>
public sealed record JsonRpcNotification
{
    [JsonPropertyName("jsonrpc")] public string JsonRpc => "2.0";
    [JsonPropertyName("method")] public required string Method { get; init; }
    [JsonPropertyName("params")] public JsonElement? Params { get; init; }
}

/// <summary>
/// The error codes this server issues.
///
/// <para>The first block is JSON-RPC's own, the second is reserved by the language server
/// protocol. They are separate ranges, so a client can tell a transport fault from a protocol
/// one.</para>
/// </summary>
public static class JsonRpcErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    /// <summary>A request arrived before <c>initialize</c>. The client is expected to wait rather
    /// than to retry.</summary>
    public const int ServerNotInitialized = -32002;

    /// <summary>The request was understood and the answer is "no, because": a rename whose target
    /// is the standard library, for instance. The message is shown to the user, which is the whole
    /// point of refusing with this code rather than with an empty result.</summary>
    public const int RequestFailed = -32803;
}
