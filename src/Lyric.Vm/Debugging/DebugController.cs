using System.Collections.Concurrent;
using Lyric.Bytecode;

namespace Lyric.Vm.Debugging;

/// <summary>
/// Drives one debugged run of a program: breakpoints, stepping, pause, and — while paused — the
/// stack, the variables and their expansion. The protocol adapter and the tests sit on this API;
/// neither knows the interpreter's internals.
///
/// <para>THREADING. The program runs on a dedicated thread this class starts. Every instruction
/// boundary passes through <see cref="OnInstruction"/> (via <c>DebugPolicy</c>); when a stop
/// condition holds, that thread parks itself inside the hook and stays parked until a resume
/// command releases it. The consumer drains <see cref="Events"/> and, between a stop event and
/// the next resume, inspects the parked frames — safe precisely because the only thread that
/// mutates them is standing still.</para>
///
/// <para>POSITIONS are line-granular, the source map's coordinate system. Files are named as the
/// map names them (relative to the compile's base directory); mapping editor paths onto that is
/// the adapter's job, because only the adapter knows where the compile ran.</para>
/// </summary>
public sealed class DebugController
{
    private enum Mode { Run, StepIn, StepOver, StepOut, Paused }

    private readonly LoadedProgram _program;
    private readonly BytecodeModule _module;
    private readonly BytecodeSourceMap? _map;

    /// <summary>Stop events in order, ending with Exited or Terminated. A blocking collection
    /// rather than an event: the adapter's read loop pulls, and a test can wait.</summary>
    private readonly BlockingCollection<StopEvent> _events = new();

    /// <summary>Parks the program thread while paused; a resume command releases it.</summary>
    private readonly SemaphoreSlim _resume = new(0, 1);

    private volatile Mode _mode;
    private volatile bool _pauseRequested;

    /// <summary>Breakpoint marks per function per instruction index. Replaced WHOLESALE on every
    /// set — the program thread reads whatever array it sees, complete either way, so no lock
    /// sits on the instruction path.</summary>
    private volatile bool[]?[]? _breakpoints;

    // The step anchor: where the last resume left from. Written by the consumer thread before
    // the semaphore release, read by the program thread after the wait — the semaphore is the
    // fence.
    private string? _anchorFile;
    private int _anchorLine;
    private int _anchorDepth;

    /// <summary>The instruction the program resumed AT, as function index and instruction index.
    /// A breakpoint there does not re-fire immediately, or Continue on a breakpoint would never
    /// leave it.</summary>
    private long _resumedAt = -1;

    // The parked state, valid from StopAt until the next resume.
    private Interpreter.Frame? _pausedFrame;
    private Stack<Interpreter.Frame>? _pausedStack;
    private readonly List<(BytecodeType Type, LyrValue Value)> _handles = new();

    /// <summary>Whether the program has stopped at least once — the first stop of a
    /// stop-on-entry launch reports <see cref="StopReason.Entry"/> instead of a step.</summary>
    private bool _everStopped;

    private DebugController(LoadedProgram program, bool stopOnEntry)
    {
        _program = program;
        _module = program.Module;
        _map = program.Module.SourceMap;

        // Stop-on-entry is a step-in from nowhere: the first instruction with a known line
        // differs from the (nonexistent) anchor and stops.
        _mode = stopOnEntry ? Mode.StepIn : Mode.Run;
        _anchorFile = null;
        _anchorLine = -1;
        _anchorDepth = int.MaxValue;
    }

    /// <summary>
    /// A controller over a loaded program, not yet running — breakpoints set now are in place
    /// before the first instruction, which is the protocol's configuration phase.
    ///
    /// <para>The global initializer already ran when the program was LOADED, so it runs outside
    /// the debugger — a breakpoint in a global's initializer expression does not hit. Recorded
    /// rather than hidden; moving the initializer under the hook means deferring it out of
    /// <c>LoadedProgram.Load</c>, a load-semantics change this milestone does not make.</para>
    /// </summary>
    public static DebugController Create(LoadedProgram program, bool stopOnEntry = false) =>
        new(program, stopOnEntry);

    /// <summary>
    /// Starts the program on its own thread and returns immediately. From here the consumer
    /// drains <see cref="Events"/>; the run ends with <see cref="StopReason.Exited"/> or
    /// <see cref="StopReason.Terminated"/>, after which the collection completes.
    /// </summary>
    public void Start(IReadOnlyList<string> arguments)
    {
        if (_thread is not null) throw new InvalidOperationException("the program already runs");

        _thread = new Thread(() => RunProgram(arguments), 16 * 1024 * 1024)
        {
            Name = "lyric-debuggee",
            IsBackground = true,
        };
        _thread.Start();
    }

    private Thread? _thread;

    /// <summary>Create and start in one step, for a consumer without a configuration phase.</summary>
    public static DebugController Launch(LoadedProgram program, IReadOnlyList<string> arguments,
        bool stopOnEntry = false)
    {
        var controller = Create(program, stopOnEntry);
        controller.Start(arguments);
        return controller;
    }

    /// <summary>The stream of stop events. <c>Take()</c> blocks until the program stops or
    /// ends.</summary>
    public BlockingCollection<StopEvent> Events => _events;

    private void RunProgram(IReadOnlyList<string> arguments)
    {
        try
        {
            var result = _program.RunEntry(arguments, this);
            Publish(new StopEvent(StopReason.Exited,
                ExitCode: (int)(result.AsI64 & 0xFF)));
        }
        catch (LyricPanic panic)
        {
            var trace = panic.CallStack.Count > 0
                ? "\n  at " + string.Join("\n  at ", panic.CallStack)
                : "";
            Publish(new StopEvent(StopReason.Terminated,
                Description: $"panic: {panic.Message}{trace}", ExitCode: 101));
        }
        catch (LyricRuntimeException error)
        {
            Publish(new StopEvent(StopReason.Terminated,
                Description: error.Message, ExitCode: 1));
        }
        finally
        {
            _events.CompleteAdding();
        }
    }

    // ------------------------------------------------------------------ the instruction hook

    /// <summary>
    /// Called by the interpreter before every instruction, on the program thread. The order of
    /// checks is the cost order: while running free, one volatile read and one array probe.
    /// </summary>
    internal void OnInstruction(Stack<Interpreter.Frame> frames, Interpreter.Frame frame)
    {
        // Detached: the program belongs to its host again, and nothing here may stop it. First,
        // because a stop with nobody left to resume it is a hang, and the hook is still installed
        // for the rest of a call that was already running when the session ended.
        if (_detached) return;

        if (_pauseRequested)
        {
            StopAt(frames, frame, StopReason.Pause);
            return;
        }

        // Breakpoints fire in every mode — a step that lands on one reports the breakpoint,
        // which is what an editor highlights.
        var breaks = _breakpoints;
        if (breaks is not null && breaks[frame.Fn.Index] is { } marks && marks[frame.Ip])
        {
            var here = Key(frame);
            if (here != _resumedAt)
            {
                StopAt(frames, frame, StopReason.Breakpoint);
                return;
            }
        }

        // Past the first instruction the resume point is history; a loop coming back to it hits
        // its breakpoint again.
        _resumedAt = -1;

        switch (_mode)
        {
            case Mode.Run:
                return;

            case Mode.StepIn:
            {
                if (LeftAnchor(frame)) StopAt(frames, frame, StopReason.Step);
                return;
            }

            case Mode.StepOver:
            {
                // A deeper frame is the called function: run through it. Same depth: stop at the
                // next line. Shallower: the stepped line returned; stop as soon as a line is
                // known.
                if (frames.Count > _anchorDepth) return;
                if (frames.Count < _anchorDepth)
                {
                    if (Position(frame) is not null) StopAt(frames, frame, StopReason.Step);
                    return;
                }
                if (LeftAnchor(frame)) StopAt(frames, frame, StopReason.Step);
                return;
            }

            case Mode.StepOut:
            {
                if (frames.Count < _anchorDepth && Position(frame) is not null)
                    StopAt(frames, frame, StopReason.Step);
                return;
            }
        }
    }

    /// <summary>Is the program at a known position that differs from the step anchor?</summary>
    private bool LeftAnchor(Interpreter.Frame frame)
    {
        if (Position(frame) is not { } at) return false;
        return at.Line != _anchorLine || !string.Equals(at.File, _anchorFile, StringComparison.Ordinal);
    }

    private SourcePosition? Position(Interpreter.Frame frame) =>
        _map?.Locate(frame.Fn.Index, frame.Fn.Instructions[frame.Ip].Offset);

    private static long Key(Interpreter.Frame frame) =>
        ((long)frame.Fn.Index << 32) | (uint)frame.Ip;

    /// <summary>Parks the program thread. Everything the consumer inspects afterwards is what
    /// this thread left behind, and it stands still until <see cref="Resume"/>.</summary>
    private void StopAt(Stack<Interpreter.Frame> frames, Interpreter.Frame frame, StopReason reason)
    {
        _pauseRequested = false;
        _pausedFrame = frame;
        _pausedStack = frames;
        _handles.Clear();
        _mode = Mode.Paused;

        // Entry is reported as such exactly once: the first stop of a stop-on-entry launch.
        var effective = reason == StopReason.Step && !_everStopped ? StopReason.Entry : reason;
        _everStopped = true;

        Publish(new StopEvent(effective));
        _resume.Wait();
    }

    // ------------------------------------------------------------------ commands

    /// <summary>Requests a pause; the program stops before its next instruction. A program deep
    /// inside a native call stops when it returns.</summary>
    public void Pause() => _pauseRequested = true;

    /// <summary>
    /// Gives the program back to its host: every breakpoint goes, a parked thread is released,
    /// and the event stream ends.
    ///
    /// <para>What a session needs when it ends WITHOUT the program ending — the shape an attached
    /// debugger has. An editor that closes, crashes or simply disconnects leaves a game whose
    /// thread may be standing at a breakpoint; without this it stands there for good, and the
    /// breakpoints nobody listens to any more park it again on the next frame.</para>
    ///
    /// <para>A controller is spent afterwards: it hooks nothing and reports nothing. Attaching
    /// again means a new one, which is also the honest model — the old session is over.</para>
    /// </summary>
    public void Detach()
    {
        if (_detached) return;
        _detached = true;

        lock (_breakpointFiles)
        {
            _breakpointFiles.Clear();
            _breakpoints = null;
        }

        _pauseRequested = false;

        // Order matters: the mode leaves Paused before the thread wakes, or the wakened thread
        // reads Paused and parks itself again on the next instruction.
        var wasPaused = _mode == Mode.Paused;
        _mode = Mode.Run;
        _pausedFrame = null;
        _pausedStack = null;
        _handles.Clear();
        if (wasPaused) _resume.Release();

        _events.CompleteAdding();
    }

    private volatile bool _detached;

    /// <summary>Publishes a stop event unless the session is over. After a detach the collection
    /// is complete, and an <c>Add</c> would throw ON THE DEBUGGEE'S THREAD — turning the end of a
    /// debug session into the end of the program it was debugging.</summary>
    private void Publish(StopEvent stop)
    {
        if (_detached) return;
        try
        {
            _events.Add(stop);
        }
        catch (InvalidOperationException)
        {
            // Detached between the check and the add. The session is over either way.
        }
    }

    public void Continue() => Resume(Mode.Run);
    public void StepIn() => Resume(Mode.StepIn);
    public void StepOver() => Resume(Mode.StepOver);
    public void StepOut() => Resume(Mode.StepOut);

    private void Resume(Mode mode)
    {
        if (_mode != Mode.Paused)
            throw new InvalidOperationException("the program is not paused");

        var frame = _pausedFrame!;
        var at = Position(frame);
        _anchorFile = at?.File;
        _anchorLine = at?.Line ?? -1;
        _anchorDepth = _pausedStack!.Count;
        _resumedAt = Key(frame);

        _pausedFrame = null;
        _pausedStack = null;
        _handles.Clear();

        _mode = mode;
        _resume.Release();
    }

    // ------------------------------------------------------------------ breakpoints

    /// <summary>
    /// Replaces the breakpoints of ONE file with the given lines and answers what became of each
    /// request. Lines of other files stay.
    ///
    /// <para>A line maps to every source-map row that names it — a line entered twice (a loop
    /// head, a lambda on the declaration line) breaks at each entry. A line without a row slides
    /// down to the next mapped line of the same file; below the last mapped line the request
    /// stays unverified.</para>
    /// </summary>
    public IReadOnlyList<BreakpointBinding> SetBreakpoints(string file, IReadOnlyList<int> lines)
    {
        lock (_breakpointFiles)
        {
            _breakpointFiles[file] = lines.ToArray();
            return Rebuild(file);
        }
    }

    /// <summary>The requested lines per file; the marks array is derived from this.</summary>
    private readonly Dictionary<string, int[]> _breakpointFiles = new(StringComparer.Ordinal);

    private IReadOnlyList<BreakpointBinding> Rebuild(string reportFile)
    {
        var functions = _program.PreparedFunctions;
        var marks = new bool[]?[functions.Length];
        var bindings = new List<BreakpointBinding>();

        foreach (var (file, lines) in _breakpointFiles)
        {
            foreach (var requested in lines)
            {
                var boundLine = BindLine(file, requested);
                var verified = false;

                if (boundLine is { } line)
                {
                    for (var f = 0; f < functions.Length; f++)
                        verified |= Mark(marks, functions[f], f, file, line);
                }

                if (file == reportFile)
                    bindings.Add(new BreakpointBinding(requested, verified,
                        verified ? boundLine!.Value : requested));
            }
        }

        _breakpoints = marks;
        return bindings;
    }

    /// <summary>The line a request lands on: the requested line when the map knows it, else the
    /// nearest mapped line below it, else nothing.</summary>
    private int? BindLine(string file, int requested)
    {
        if (_map is null) return null;

        int? best = null;
        foreach (var rows in _map.Functions)
            foreach (var row in rows)
            {
                if (!string.Equals(_map.Files[row.FileIndex], file, StringComparison.Ordinal))
                    continue;
                if (row.Line < requested) continue;
                if (best is null || row.Line < best) best = row.Line;
                if (best == requested) return requested;
            }

        return best;
    }

    /// <summary>Marks the first instruction of every region of this function that starts at the
    /// line. A row's offset is an instruction boundary by the format's contract.</summary>
    private bool Mark(bool[]?[] marks, Interpreter.Prepared function, int index, string file,
        int line)
    {
        if (_map is null || index >= _map.Functions.Count) return false;

        var any = false;
        foreach (var row in _map.Functions[index])
        {
            if (row.Line != line) continue;
            if (!string.Equals(_map.Files[row.FileIndex], file, StringComparison.Ordinal)) continue;

            var at = IndexOfOffset(function, row.Offset);
            if (at < 0) continue;

            (marks[index] ??= new bool[function.Instructions.Length])[at] = true;
            any = true;
        }

        return any;
    }

    private static int IndexOfOffset(Interpreter.Prepared function, int offset)
    {
        // Binary search over the decoded instructions, which are in offset order.
        var instructions = function.Instructions;
        var low = 0;
        var high = instructions.Length - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (instructions[middle].Offset == offset) return middle;
            if (instructions[middle].Offset < offset) low = middle + 1;
            else high = middle - 1;
        }
        return -1;
    }

    // ------------------------------------------------------------------ inspection (paused only)

    /// <summary>The call stack, innermost first. Valid only while paused.</summary>
    public IReadOnlyList<DebugFrameInfo> StackTrace()
    {
        var frame = PausedOrThrow();
        var result = new List<DebugFrameInfo>();

        // The innermost frame stands BEFORE its instruction; every caller stands just past its
        // call, so the call site is at Ip - 1 — the same arithmetic the panic backtrace uses.
        var at = Position(frame);
        result.Add(new DebugFrameInfo(0, frame.Fn.Source.Name, at?.File, at?.Line));

        foreach (var caller in _pausedStack!)
        {
            var index = caller.Ip - 1;
            SourcePosition? position = index >= 0 && index < caller.Fn.Instructions.Length
                ? _map?.Locate(caller.Fn.Index, caller.Fn.Instructions[index].Offset)
                : null;
            result.Add(new DebugFrameInfo(result.Count, caller.Fn.Source.Name,
                position?.File, position?.Line));
        }

        return result;
    }

    /// <summary>The named locals of one frame of <see cref="StackTrace"/>. Slots the compiler
    /// created are not in the answer — the DebugInfo section marks them with the empty string.
    /// </summary>
    public IReadOnlyList<DebugVariable> Locals(int frameIndex)
    {
        var frame = FrameAt(frameIndex);
        var names = _module.SlotNames is { } all && frame.Fn.Index < all.Count
            ? all[frame.Fn.Index]
            : [];

        var result = new List<DebugVariable>();
        for (var slot = 0; slot < frame.Slots.Length && slot < names.Count; slot++)
        {
            if (names[slot].Length == 0) continue;
            result.Add(Variable(names[slot], frame.Fn.Source.SlotTypes[slot], frame.Slots[slot]));
        }
        return result;
    }

    /// <summary>The module's global slots, named where the DebugInfo section names them.</summary>
    public IReadOnlyList<DebugVariable> Globals()
    {
        PausedOrThrow();
        var slots = _program.GlobalSlots;
        var names = _module.GlobalNames;

        var result = new List<DebugVariable>();
        for (var i = 0; i < slots.Length && i < names.Count; i++)
        {
            if (names[i].Length == 0) continue;
            result.Add(Variable(names[i], _module.Globals[i], slots[i]));
        }
        return result;
    }

    /// <summary>The children of an expandable variable: the fields of an object, the elements of
    /// an array, the payload of a variant.</summary>
    public IReadOnlyList<DebugVariable> Expand(int handle)
    {
        PausedOrThrow();
        if (handle <= 0 || handle > _handles.Count) return [];

        var (type, value) = _handles[handle - 1];
        return ValueRenderer.Children(_module, type, value)
            .Select(child => Variable(child.Name, child.Type, child.Value))
            .ToList();
    }

    /// <summary>
    /// A dotted path against one frame: a local (or, failing that, a global), then fields. The
    /// whole evaluate story of this debugger — names, not expressions: an expression evaluator
    /// would be a compile against a frame's context, a milestone of its own, and half of one
    /// would answer wrongly.
    /// </summary>
    public DebugVariable? Evaluate(int frameIndex, string expression)
    {
        var segments = expression.Split('.');
        if (segments.Length == 0) return null;

        var root = Locals(frameIndex).LastOrDefault(v => v.Name == segments[0])
                   ?? Globals().LastOrDefault(v => v.Name == segments[0]);
        if (root is null) return null;

        var current = root;
        foreach (var segment in segments.Skip(1))
        {
            if (current.Handle == 0) return null;
            current = Expand(current.Handle).FirstOrDefault(v => v.Name == segment);
            if (current is null) return null;
        }

        return current;
    }

    private DebugVariable Variable(string name, BytecodeType type, LyrValue value)
    {
        var rendered = ValueRenderer.Render(_module, type, value);
        var handle = 0;
        if (rendered.Expandable)
        {
            _handles.Add((type, value));
            handle = _handles.Count;
        }
        return new DebugVariable(name, rendered.Value, rendered.Type, handle);
    }

    private Interpreter.Frame PausedOrThrow()
    {
        if (_mode != Mode.Paused || _pausedFrame is null)
            throw new InvalidOperationException("the program is not paused");
        return _pausedFrame;
    }

    private Interpreter.Frame FrameAt(int index)
    {
        var frame = PausedOrThrow();
        if (index == 0) return frame;

        var i = 1;
        foreach (var caller in _pausedStack!)
        {
            if (i == index) return caller;
            i++;
        }
        throw new ArgumentOutOfRangeException(nameof(index),
            $"frame {index} of {i} frames");
    }
}
