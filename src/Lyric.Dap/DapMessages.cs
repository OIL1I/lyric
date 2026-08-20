using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lyric.Dap;

/// <summary>
/// The three message shapes of the Debug Adapter Protocol, plus the bodies this adapter speaks.
///
/// <para>The same base protocol as LSP — a Content-Length header block, then JSON — but a
/// different message schema: a sequence number and a <c>type</c> discriminator instead of
/// JSON-RPC's envelope. Field names are the protocol's, fixed with attributes rather than a
/// naming policy, so the C# names stay idiomatic.</para>
/// </summary>
public sealed record DapMessage
{
    [JsonPropertyName("seq")] public int Seq { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }

    // Request half.
    [JsonPropertyName("command")] public string? Command { get; init; }
    [JsonPropertyName("arguments")] public JsonElement? Arguments { get; init; }

    // Event half.
    [JsonPropertyName("event")] public string? Event { get; init; }

    // Response half (read back by the tests, which sit on the same type).
    [JsonPropertyName("request_seq")] public int RequestSeq { get; init; }
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }

    [JsonPropertyName("body")] public JsonElement? Body { get; init; }
}

public sealed record DapResponse
{
    [JsonPropertyName("seq")] public required int Seq { get; init; }
    [JsonPropertyName("type")] public string Type => "response";
    [JsonPropertyName("request_seq")] public required int RequestSeq { get; init; }
    [JsonPropertyName("success")] public required bool Success { get; init; }
    [JsonPropertyName("command")] public required string Command { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("body")] public object? Body { get; init; }
}

public sealed record DapEvent
{
    [JsonPropertyName("seq")] public required int Seq { get; init; }
    [JsonPropertyName("type")] public string Type => "event";
    [JsonPropertyName("event")] public required string Event { get; init; }
    [JsonPropertyName("body")] public object? Body { get; init; }
}

// ------------------------------------------------------------------ bodies

/// <summary>What this adapter can do, answered to <c>initialize</c>. Absent capabilities are
/// false by the protocol's rule, so only the true ones are stated.</summary>
public sealed record Capabilities
{
    [JsonPropertyName("supportsConfigurationDoneRequest")]
    public bool SupportsConfigurationDoneRequest => true;

    /// <summary>Hover evaluation works because evaluate is a name lookup — exactly the shape a
    /// hover asks for.</summary>
    [JsonPropertyName("supportsEvaluateForHovers")]
    public bool SupportsEvaluateForHovers => true;
}

public sealed record StoppedBody(
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("threadId")] int ThreadId,
    [property: JsonPropertyName("allThreadsStopped")] bool AllThreadsStopped = true);

public sealed record ExitedBody([property: JsonPropertyName("exitCode")] int ExitCode);

public sealed record OutputBody(
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("output")] string Output);

public sealed record DapThread(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name);

public sealed record ThreadsBody([property: JsonPropertyName("threads")] IReadOnlyList<DapThread> Threads);

public sealed record DapSource(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string? Path);

public sealed record DapStackFrame(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("source")] DapSource? Source,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("column")] int Column);

public sealed record StackTraceBody(
    [property: JsonPropertyName("stackFrames")] IReadOnlyList<DapStackFrame> StackFrames,
    [property: JsonPropertyName("totalFrames")] int TotalFrames);

public sealed record DapScope(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("variablesReference")] int VariablesReference,
    [property: JsonPropertyName("expensive")] bool Expensive = false);

public sealed record ScopesBody([property: JsonPropertyName("scopes")] IReadOnlyList<DapScope> Scopes);

public sealed record DapVariable(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("variablesReference")] int VariablesReference);

public sealed record VariablesBody(
    [property: JsonPropertyName("variables")] IReadOnlyList<DapVariable> Variables);

public sealed record DapBreakpoint(
    [property: JsonPropertyName("verified")] bool Verified,
    [property: JsonPropertyName("line")] int Line);

public sealed record SetBreakpointsBody(
    [property: JsonPropertyName("breakpoints")] IReadOnlyList<DapBreakpoint> Breakpoints);

public sealed record ContinueBody(
    [property: JsonPropertyName("allThreadsContinued")] bool AllThreadsContinued = true);

public sealed record EvaluateBody(
    [property: JsonPropertyName("result")] string Result,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("variablesReference")] int VariablesReference);

/// <summary>The serializer settings of the wire: no nulls (a DAP consumer treats an absent field
/// and a false one alike), no indentation.</summary>
public static class DapJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
