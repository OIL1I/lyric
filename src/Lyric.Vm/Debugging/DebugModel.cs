namespace Lyric.Vm.Debugging;

/// <summary>Why the program stopped, or how it ended.</summary>
public enum StopReason
{
    /// <summary>Stopped before the first source line, because the launch asked for it.</summary>
    Entry,
    Breakpoint,
    Step,

    /// <summary>An explicit pause request.</summary>
    Pause,

    /// <summary>The program ran to its end. <see cref="StopEvent.ExitCode"/> carries
    /// <c>main</c>'s value masked with <c>0xFF</c>, the runner contract's exit code.</summary>
    Exited,

    /// <summary>A panic or a runtime error ended the program.
    /// <see cref="StopEvent.Description"/> carries the message and the backtrace.</summary>
    Terminated,
}

/// <summary>One event out of the debugged program, in order. <see cref="StopReason.Exited"/> and
/// <see cref="StopReason.Terminated"/> are final; everything before them leaves the program
/// paused and inspectable.</summary>
public sealed record StopEvent(StopReason Reason, string? Description = null, int? ExitCode = null);

/// <summary>
/// What became of one requested breakpoint. <see cref="Verified"/> is false when no executable
/// code maps to the line — a blank line, a declaration, a comment; <see cref="Line"/> is then
/// where the breakpoint actually sits, which may be the next mapped line below the request.
/// </summary>
public sealed record BreakpointBinding(int RequestedLine, bool Verified, int Line);

/// <summary>One frame of the paused program, innermost first.</summary>
/// <param name="File">The file as the source map names it: relative to the compile's base
/// directory, or a bare name for a file outside it. <c>null</c> when the module carries no
/// position for the frame.</param>
public sealed record DebugFrameInfo(int Index, string Function, string? File, int? Line);

/// <summary>
/// One value under a name. <see cref="Handle"/> is non-zero for a value with structure — an
/// object, an array, a variant with payload — and feeds <c>DebugController.Expand</c>; the
/// handles die at the next resume.
/// </summary>
public sealed record DebugVariable(string Name, string Value, string Type, int Handle);
