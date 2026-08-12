using Lyric.Bytecode;
using Lyric.Core;

namespace Lyric.Vm;

/// <summary>
/// The whole path from <c>.lyrbc</c> bytes to a process exit code: load, validate, execute, render
/// panics and runtime errors.
///
/// <para>It lives in the runtime rather than in a command-line project, so the exit-code rule of
/// the runner contract exists once.</para>
/// </summary>
public static class VmHost
{
    /// <summary>
    /// Executes an already loaded module and returns the process exit code.
    ///
    /// <para><paramref name="output"/> carries program output only and
    /// <paramref name="error"/> diagnostics and backtraces only; the runner contract forbids
    /// mixing them.</para>
    /// </summary>
    public static int Execute(BytecodeModule module, TextWriter output, TextWriter error) =>
        Execute(module, [], output, error);

    /// <param name="arguments">The program arguments of the runner contract: everything after
    /// the first <c>--</c>.</param>
    /// <param name="granted">Which capabilities this execution receives. Standalone grants
    /// everything; an embedded host sets it narrower.</param>
    public static int Execute(BytecodeModule module, IReadOnlyList<string> arguments,
        TextWriter output, TextWriter error, Capability granted = Capability.All)
    {
        try
        {
            var natives = NativeRegistry.CreateDefault(output, error);

            // The exit code is 0..255, so the lowest byte is taken.
            return (int)(Interpreter.Run(module, arguments, natives, granted).AsI64 & 0xFF);
        }
        catch (LyricPanic panic)
        {
            // A panic prints a backtrace and ends the VM. It is not catchable.
            error.WriteLine($"panic [{panic.Code}]: {panic.Message}");
            foreach (var frame in panic.CallStack) error.WriteLine($"    in {frame}");
            return ExitCodes.Panic;
        }
        catch (LyricRuntimeException ex)
        {
            var engine = new DiagnosticEngine(new SourceManager());
            engine.Report(ex.Code, Severity.Error, default, ex.Message);
            engine.RenderText(error);
            return ExitCodes.Failure;
        }
    }

    /// <summary>
    /// Loads bytes and executes them. A <c>null</c> load result means validation rejected the
    /// module; the diagnostics are already on <paramref name="error"/>.
    /// </summary>
    public static int Execute(byte[] bytes, TextWriter output, TextWriter error)
    {
        var module = Load(bytes, error);
        return module is null ? ExitCodes.Failure : Execute(module, output, error);
    }

    /// <summary>
    /// Reads and validates bytes completely without executing them; the basis of
    /// <c>lyrvm verify</c> and <c>lyrvm disasm</c>. Renders its own diagnostics and returns
    /// <c>null</c> when the module is rejected.
    /// </summary>
    public static BytecodeModule? Load(byte[] bytes, TextWriter error)
    {
        var engine = new DiagnosticEngine(new SourceManager());
        var module = BytecodeReader.Read(bytes, engine);
        engine.RenderText(error);
        return module;
    }

    /// <summary>
    /// Everything this runtime checks at load time without executing an instruction: format
    /// validation and import binding.
    ///
    /// <para>Import binding is included because a module importing an unknown native is invalid
    /// before it starts, and this is the question a conformance check asks.</para>
    /// </summary>
    public static int Verify(byte[] bytes, TextWriter output, TextWriter error)
    {
        var module = Load(bytes, error);
        if (module is null) return ExitCodes.Failure;

        try
        {
            NativeRegistry.CreateDefault(output, error).Bind(module);
        }
        catch (LyricRuntimeException ex)
        {
            var engine = new DiagnosticEngine(new SourceManager());
            engine.Report(ex.Code, Severity.Error, default, ex.Message);
            engine.RenderText(error);
            return ExitCodes.Failure;
        }

        return ExitCodes.Success;
    }
}
