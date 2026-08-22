using System.Text.Json;
using Lyric.Bytecode;
using Lyric.Compiler;
using Lyric.Core;
using Lyric.Lsp.Protocol;
using Lyric.Vm;
using Lyric.Vm.Debugging;

namespace Lyric.Dap;

/// <summary>
/// The Debug Adapter Protocol server: one session per process, the shape VS Code launches.
///
/// <para>The adapter owns the whole debug run: it COMPILES the program (unoptimized, with source
/// map and debug info — the debug shape), loads it, and drives the VM's
/// <see cref="DebugController"/>. Compiling in-process is a deliberate choice: a full check with
/// the standard library measures tens of milliseconds, and the alternative — attaching to
/// pre-built bytecode by default — debugs a file that may not be the one in the editor.</para>
///
/// <para>Requests are handled ON the read loop, one at a time; the stop events of the program
/// arrive from their own pump task. The framing (<see cref="LspConnection"/>) serializes writers,
/// so the two never interleave a message.</para>
///
/// <para>The sequence against the client: <c>initialize</c> is answered with the capabilities;
/// the <c>initialized</c> event goes out after the LAUNCH response, when a program exists for
/// breakpoints to bind against; <c>configurationDone</c> starts execution.</para>
/// </summary>
/// <summary>What a host of the server may pin. The binary passes nothing; the tests pin the
/// repository's stdlib, because the test host does not sit beside one.</summary>
public sealed record DapServerOptions
{
    /// <summary>Where the standard library lives; <c>null</c> means the compiler's default
    /// (<c>LYRIC_STDLIB</c> or the directory beside the binary).</summary>
    public string? StdlibRoot { get; init; }
}

public sealed class DapServer
{
    private readonly LspConnection _connection;
    private readonly DapServerOptions _options;
    private readonly Session? _attached;
    private int _sequence;

    private Session? _session;
    private bool _disconnect;

    /// <summary>The launching adapter: it compiles and starts the program itself. What
    /// <c>lyrdbg</c> is.</summary>
    public DapServer(Stream input, Stream output, DapServerOptions? options = null)
    {
        _connection = new LspConnection(input, output);
        _options = options ?? new DapServerOptions();
    }

    /// <summary>
    /// The ATTACHING adapter: a host that already runs a program serves an editor for it.
    ///
    /// <para>A game has no <c>main</c> to launch and, more to the point, the bug worth stopping at
    /// is rarely the one that happens at startup — it is the one in level three, twenty minutes
    /// in. A host builds one of these per <see cref="DebugController"/> and gives it a pair of
    /// streams (a socket it accepted, usually); the editor sends <c>attach</c> instead of
    /// <c>launch</c>, and everything after that is the same protocol.</para>
    ///
    /// <para>One server per controller, which answers the question a multi-session host would
    /// otherwise have to: WHICH program a <c>setBreakpoints</c> is about is decided by which
    /// connection it arrived on.</para>
    ///
    /// <para>The debuggee's output does not travel as output events here — the host owns the
    /// program's writers and has its own console. What ends the session never ends the program:
    /// see <see cref="DebugController.Detach"/>.</para>
    /// </summary>
    /// <param name="baseDirectory">What the module's source-map paths are relative to — the
    /// directory the host compiled the scripts from. Editor paths are mapped through it in both
    /// directions.</param>
    public DapServer(Stream input, Stream output, DebugController controller,
        string baseDirectory, DapServerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        _connection = new LspConnection(input, output);
        _options = options ?? new DapServerOptions();
        _attached = new Session
        {
            Controller = controller,
            BaseDirectory = Path.GetFullPath(baseDirectory),
            Arguments = [],
            Started = true,
        };
    }

    /// <summary>Everything one launched program carries: the controller, where its sources live,
    /// and the variable references handed out while it stands still.</summary>
    private sealed class Session
    {
        public required DebugController Controller { get; init; }
        public required string BaseDirectory { get; init; }
        public required string[] Arguments { get; init; }
        public bool Started;

        /// <summary>Variable references handed to the client, each resolving to a variable list.
        /// Cleared on every resume: the protocol invalidates references when the program
        /// runs.</summary>
        public readonly List<Func<IReadOnlyList<DebugVariable>>> References = new();
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!_disconnect)
        {
            var payload = await _connection.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (payload is null) break;

            DapMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<DapMessage>(payload, DapJson.Options);
            }
            catch (JsonException)
            {
                continue; // not JSON; nothing to answer, because no seq is known
            }

            if (message is not { Type: "request", Command: not null }) continue;

            await HandleAsync(message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(DapMessage request, CancellationToken cancellationToken)
    {
        try
        {
            switch (request.Command)
            {
                case "initialize":
                    await RespondAsync(request, new Capabilities(), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case "launch" when _attached is not null:
                    await FailAsync(request,
                        "this adapter serves a program that is already running — send 'attach'",
                        cancellationToken).ConfigureAwait(false);
                    break;

                case "launch":
                    await LaunchAsync(request, cancellationToken).ConfigureAwait(false);
                    break;

                case "attach" when _attached is null:
                    await FailAsync(request,
                        "this adapter starts the program itself — send 'launch'",
                        cancellationToken).ConfigureAwait(false);
                    break;

                case "attach":
                    await AttachAsync(request, cancellationToken).ConfigureAwait(false);
                    break;

                case "setBreakpoints":
                    await SetBreakpointsAsync(request, cancellationToken).ConfigureAwait(false);
                    break;

                // Answered although this adapter offers no exception filters: a client sends the
                // request as part of its configuration sequence whether or not there is anything
                // to set, and an error on a request the protocol calls optional stops that
                // sequence — leaving a program that never starts. There is nothing to report, so
                // the response carries no body.
                case "setExceptionBreakpoints":
                    await RespondAsync(request, null, cancellationToken).ConfigureAwait(false);
                    break;

                case "configurationDone":
                {
                    // An attached session is marked started from the outset: the program is
                    // running, and starting it a second time is not a thing that exists.
                    var session = SessionOrThrow();
                    if (!session.Started)
                    {
                        session.Started = true;
                        session.Controller.Start(session.Arguments);
                    }
                    await RespondAsync(request, null, cancellationToken).ConfigureAwait(false);
                    break;
                }

                case "threads":
                    await RespondAsync(request,
                        new ThreadsBody([new DapThread(1, "main")]), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case "stackTrace":
                    await StackTraceAsync(request, cancellationToken).ConfigureAwait(false);
                    break;

                case "scopes":
                    await ScopesAsync(request, cancellationToken).ConfigureAwait(false);
                    break;

                case "variables":
                    await VariablesAsync(request, cancellationToken).ConfigureAwait(false);
                    break;

                case "evaluate":
                    await EvaluateAsync(request, cancellationToken).ConfigureAwait(false);
                    break;

                case "continue":
                    Resume(c => c.Continue());
                    await RespondAsync(request, new ContinueBody(), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case "next":
                    Resume(c => c.StepOver());
                    await RespondAsync(request, null, cancellationToken).ConfigureAwait(false);
                    break;

                case "stepIn":
                    Resume(c => c.StepIn());
                    await RespondAsync(request, null, cancellationToken).ConfigureAwait(false);
                    break;

                case "stepOut":
                    Resume(c => c.StepOut());
                    await RespondAsync(request, null, cancellationToken).ConfigureAwait(false);
                    break;

                case "pause":
                    SessionOrThrow().Controller.Pause();
                    await RespondAsync(request, null, cancellationToken).ConfigureAwait(false);
                    break;

                case "disconnect" or "terminate":
                    // Attached, the program is not ours to end: a session that stops has to give
                    // the thread back, or a game parked at a breakpoint stands there for good and
                    // the breakpoints nobody reads park it again next frame. Launched, the process
                    // is the session and ending it is the whole answer.
                    _attached?.Controller.Detach();
                    _disconnect = true;
                    await RespondAsync(request, null, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    await FailAsync(request, $"unsupported request '{request.Command}'",
                        cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
                                       or KeyNotFoundException or JsonException)
        {
            await FailAsync(request, ex.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ attach

    /// <summary>
    /// Binds the session to the controller this server was built with. Nothing is compiled,
    /// loaded or started — the program is already running, which is the point.
    ///
    /// <para>The sequence afterwards is the launch one: the response, then <c>initialized</c>, so
    /// the client sends its breakpoints and closes with <c>configurationDone</c>. A breakpoint set
    /// here binds against a program that is running RIGHT NOW and takes effect at the next
    /// instruction that reaches the line — there is no start to wait for.</para>
    /// </summary>
    private async Task AttachAsync(DapMessage request, CancellationToken cancellationToken)
    {
        _session = _attached;
        _ = Task.Run(() => PumpEventsAsync(_attached!.Controller), CancellationToken.None);

        await RespondAsync(request, null, cancellationToken).ConfigureAwait(false);
        await EmitAsync("initialized", null, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ launch

    private async Task LaunchAsync(DapMessage request, CancellationToken cancellationToken)
    {
        var arguments = request.Arguments ?? default;
        var program = Argument<string>(arguments, "program");
        if (program is null)
        {
            await FailAsync(request, "launch needs a 'program' path", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var stopOnEntry = Argument<bool?>(arguments, "stopOnEntry") ?? false;
        var noDebug = Argument<bool?>(arguments, "noDebug") ?? false;
        var programArguments = Argument<string[]>(arguments, "args") ?? [];

        program = Path.GetFullPath(program);
        var (module, baseDirectory, error) = LoadProgramBytes(program);
        if (module is null)
        {
            await FailAsync(request, error ?? "the program did not load", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        LoadedProgram loaded;
        try
        {
            // The program's stdout and stderr become output events: the adapter's own stdout IS
            // the protocol stream, and a single stray print would desynchronise it. The debuggee
            // reads no stdin for the same reason.
            var natives = NativeRegistry.CreateDefault(
                new EventTextWriter(line => PostOutput("stdout", line)),
                new EventTextWriter(line => PostOutput("stderr", line)),
                TextReader.Null);
            loaded = LoadedProgram.Load(module, natives);
        }
        catch (LyricRuntimeException ex)
        {
            await FailAsync(request, ex.Message, cancellationToken).ConfigureAwait(false);
            return;
        }

        var session = new Session
        {
            Controller = DebugController.Create(loaded, stopOnEntry && !noDebug),
            BaseDirectory = baseDirectory,
            Arguments = programArguments,
        };
        _session = session;

        _ = Task.Run(() => PumpEventsAsync(session.Controller), CancellationToken.None);

        await RespondAsync(request, null, cancellationToken).ConfigureAwait(false);

        // Now — and not earlier — breakpoints have a program to bind against. The client answers
        // with its setBreakpoints requests and closes with configurationDone.
        await EmitAsync("initialized", null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A <c>.lyr</c> compiles in the debug shape; a <c>.lyrbc</c> loads as it is. The
    /// base directory is what the module's source-map paths are relative to.</summary>
    private (BytecodeModule? Module, string BaseDirectory, string? Error)
        LoadProgramBytes(string program)
    {
        var baseDirectory = Path.GetDirectoryName(program) ?? Directory.GetCurrentDirectory();

        if (Path.GetExtension(program).Equals(".lyrbc", StringComparison.OrdinalIgnoreCase))
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(program);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (null, baseDirectory, $"failed to read {program}");
            }

            var diagnostics = new DiagnosticEngine(new SourceManager());
            var read = BytecodeReader.Read(bytes, diagnostics);
            return read is null
                ? (null, baseDirectory, RenderedDiagnostics(diagnostics))
                : (read, baseDirectory, null);
        }

        // The project file's layout counts here exactly as it does for 'lyrc build': without it
        // a program in a project would not find its own modules.
        var project = ProjectFile.Discover(baseDirectory);
        var result = SourceCompiler.Compile(program, new CompilerOptions
        {
            StdlibRoot = _options.StdlibRoot,
            SourceRoot = project?.SourceRoot,
            NativeRoots = project?.NativeRoots,
            Optimize = false,
        });

        return result.Bytes is null
            ? (null, baseDirectory, RenderedDiagnostics(result.Diagnostics))
            : (BytecodeReader.ReadOrThrow(result.Bytes), baseDirectory, null);
    }

    private static string RenderedDiagnostics(DiagnosticEngine diagnostics)
    {
        var writer = new StringWriter();
        diagnostics.RenderText(writer);
        return writer.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------ events out

    private async Task PumpEventsAsync(DebugController controller)
    {
        foreach (var stop in controller.Events.GetConsumingEnumerable())
        {
            switch (stop.Reason)
            {
                case StopReason.Exited:
                    await EmitAsync("exited", new ExitedBody(stop.ExitCode ?? 0),
                        CancellationToken.None).ConfigureAwait(false);
                    await EmitAsync("terminated", null, CancellationToken.None)
                        .ConfigureAwait(false);
                    break;

                case StopReason.Terminated:
                    if (stop.Description is not null) PostOutput("stderr", stop.Description);
                    await EmitAsync("exited", new ExitedBody(stop.ExitCode ?? 1),
                        CancellationToken.None).ConfigureAwait(false);
                    await EmitAsync("terminated", null, CancellationToken.None)
                        .ConfigureAwait(false);
                    break;

                default:
                    await EmitAsync("stopped", new StoppedBody(Reason(stop.Reason), 1),
                        CancellationToken.None).ConfigureAwait(false);
                    break;
            }
        }
    }

    private static string Reason(StopReason reason) => reason switch
    {
        StopReason.Entry => "entry",
        StopReason.Breakpoint => "breakpoint",
        StopReason.Pause => "pause",
        _ => "step",
    };

    private void PostOutput(string category, string line) =>
        _ = EmitAsync("output", new OutputBody(category, line + "\n"), CancellationToken.None);

    // ------------------------------------------------------------------ breakpoints

    private async Task SetBreakpointsAsync(DapMessage request, CancellationToken cancellationToken)
    {
        var session = SessionOrThrow();
        var arguments = request.Arguments!.Value;

        var path = arguments.GetProperty("source").TryGetProperty("path", out var p)
            ? p.GetString()
            : null;
        if (path is null)
        {
            await FailAsync(request, "setBreakpoints needs a source path", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var lines = new List<int>();
        if (arguments.TryGetProperty("breakpoints", out var requested))
            foreach (var breakpoint in requested.EnumerateArray())
                lines.Add(breakpoint.GetProperty("line").GetInt32());

        var bindings = session.Controller.SetBreakpoints(
            ToMapFile(session.BaseDirectory, path), lines);

        await RespondAsync(request, new SetBreakpointsBody(
                bindings.Select(b => new DapBreakpoint(b.Verified, b.Line)).ToList()),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>An editor path into the coordinate the source map uses: relative to the base
    /// directory with forward slashes, or the bare file name for a file outside it — the same
    /// derivation the compiler made when it wrote the map.</summary>
    private static string ToMapFile(string baseDirectory, string path)
    {
        string relative;
        try
        {
            relative = Path.GetRelativePath(baseDirectory, Path.GetFullPath(path));
        }
        catch (ArgumentException)
        {
            return path;
        }

        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return Path.GetFileName(path);

        return relative.Replace('\\', '/');
    }

    /// <summary>The reverse direction, for stack frames: a map path back under the base
    /// directory. A bare name that exists nowhere stays a name without a path.</summary>
    private static DapSource? Source(string baseDirectory, string? mapFile)
    {
        if (mapFile is null) return null;

        var candidate = Path.GetFullPath(Path.Combine(baseDirectory, mapFile));
        return File.Exists(candidate)
            ? new DapSource(Path.GetFileName(mapFile), candidate)
            : new DapSource(mapFile, null);
    }

    // ------------------------------------------------------------------ inspection

    private async Task StackTraceAsync(DapMessage request, CancellationToken cancellationToken)
    {
        var session = SessionOrThrow();
        var frames = session.Controller.StackTrace()
            .Select(f => new DapStackFrame(f.Index, f.Function,
                Source(session.BaseDirectory, f.File), f.Line ?? 0, f.Line is null ? 0 : 1))
            .ToList();

        await RespondAsync(request, new StackTraceBody(frames, frames.Count), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ScopesAsync(DapMessage request, CancellationToken cancellationToken)
    {
        var session = SessionOrThrow();
        var frame = request.Arguments!.Value.GetProperty("frameId").GetInt32();

        var scopes = new List<DapScope>
        {
            new("Locals", Reference(session, () => session.Controller.Locals(frame))),
        };
        if (session.Controller.Globals().Count > 0)
            scopes.Add(new DapScope("Globals", Reference(session, session.Controller.Globals)));

        await RespondAsync(request, new ScopesBody(scopes), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task VariablesAsync(DapMessage request, CancellationToken cancellationToken)
    {
        var session = SessionOrThrow();
        var reference = request.Arguments!.Value.GetProperty("variablesReference").GetInt32();

        if (reference <= 0 || reference > session.References.Count)
        {
            await RespondAsync(request, new VariablesBody([]), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var variables = session.References[reference - 1]()
            .Select(v => new DapVariable(v.Name, v.Value, v.Type,
                v.Handle == 0 ? 0 : Reference(session, () => session.Controller.Expand(v.Handle))))
            .ToList();

        await RespondAsync(request, new VariablesBody(variables), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task EvaluateAsync(DapMessage request, CancellationToken cancellationToken)
    {
        var session = SessionOrThrow();
        var arguments = request.Arguments!.Value;
        var expression = arguments.GetProperty("expression").GetString() ?? "";
        var frame = arguments.TryGetProperty("frameId", out var f) ? f.GetInt32() : 0;

        var found = session.Controller.Evaluate(frame, expression);
        if (found is null)
        {
            await FailAsync(request, $"'{expression}' is not a known name here",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var reference = found.Handle == 0
            ? 0
            : Reference(session, () => session.Controller.Expand(found.Handle));
        await RespondAsync(request, new EvaluateBody(found.Value, found.Type, reference),
            cancellationToken).ConfigureAwait(false);
    }

    private static int Reference(Session session, Func<IReadOnlyList<DebugVariable>> resolve)
    {
        session.References.Add(resolve);
        return session.References.Count;
    }

    private void Resume(Action<DebugController> action)
    {
        var session = SessionOrThrow();
        session.References.Clear();
        action(session.Controller);
    }

    private Session SessionOrThrow() =>
        _session ?? throw new InvalidOperationException("no program was launched");

    // ------------------------------------------------------------------ wire

    private static T? Argument<T>(JsonElement? arguments, string name)
    {
        if (arguments is not { } element || element.ValueKind != JsonValueKind.Object)
            return default;
        return element.TryGetProperty(name, out var value)
            ? value.Deserialize<T>(DapJson.Options)
            : default;
    }

    private Task RespondAsync(DapMessage request, object? body, CancellationToken cancellationToken) =>
        WriteAsync(new DapResponse
        {
            Seq = Interlocked.Increment(ref _sequence),
            RequestSeq = request.Seq,
            Success = true,
            Command = request.Command!,
            Body = body,
        }, cancellationToken);

    private Task FailAsync(DapMessage request, string message, CancellationToken cancellationToken) =>
        WriteAsync(new DapResponse
        {
            Seq = Interlocked.Increment(ref _sequence),
            RequestSeq = request.Seq,
            Success = false,
            Command = request.Command!,
            Message = message,
        }, cancellationToken);

    private Task EmitAsync(string name, object? body, CancellationToken cancellationToken) =>
        WriteAsync(new DapEvent
        {
            Seq = Interlocked.Increment(ref _sequence),
            Event = name,
            Body = body,
        }, cancellationToken);

    private Task WriteAsync(object message, CancellationToken cancellationToken) =>
        _connection.WriteAsync(
            JsonSerializer.SerializeToUtf8Bytes(message, DapJson.Options), cancellationToken);
}
