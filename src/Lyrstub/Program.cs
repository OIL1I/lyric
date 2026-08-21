using Lyric.Core;
using Lyric.Vm;

namespace Lyric.Cli.Stub;

/// <summary>
/// <c>lyrstub</c> — the executable a packed program IS.
///
/// <para><c>lyrpack</c> appends a <c>.lyrbc</c> module and a <see cref="PackFooter"/> to a copy
/// of this binary; started, the copy finds the module inside its own file and executes it. The
/// packed program owns the command line completely — no option here, not even <c>--help</c>,
/// because every argument belongs to the program someone shipped, not to the wrapper it travels
/// in.</para>
///
/// <para>A packed program is standalone by definition, so it runs with every capability, exactly
/// as <c>lyrvm run</c> without <c>--grant</c> does. Narrowing what a packed program may do is a
/// packing-time decision nobody has asked for; the place for it is a footer field, not a runtime
/// flag an end user could edit away.</para>
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        ConsoleStreams.UseUtf8WhenRedirected();

        // The apphost's own path, which single-file publishing keeps meaningful when
        // AppContext.BaseDirectory points at an extraction directory instead.
        if (Environment.ProcessPath is not { } self)
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.StubEmpty,
                "cannot locate my own executable file", ExitCodes.Failure);

        byte[] payload;
        try
        {
            // Read share: the OS holds the running image write-locked, not read-locked.
            using var stream = File.OpenRead(self);
            switch (PackFooter.TryRead(stream, out var bounds))
            {
                case PackFooterState.Absent:
                    return CliDiagnostics.Fail(Console.Error, CliDiagnostics.StubEmpty,
                        $"this is the empty lyric stub {ToolchainVersion.Value}: it carries no "
                        + "program. 'lyric pack' embeds one.", ExitCodes.Usage);

                case PackFooterState.Damaged:
                    return CliDiagnostics.Fail(Console.Error, CliDiagnostics.PackDamaged,
                        "the packed program is damaged — a truncated copy looks like this. "
                        + "Re-copy or re-pack it.", ExitCodes.Failure);
            }

            if (bounds.Length > int.MaxValue)
                return CliDiagnostics.Fail(Console.Error, CliDiagnostics.PackDamaged,
                    "the packed program is damaged: the recorded size is not a module anyone "
                    + "built.", ExitCodes.Failure);

            payload = new byte[bounds.Length];
            stream.Seek(bounds.Offset, SeekOrigin.Begin);
            stream.ReadExactly(payload);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.FileUnreadable,
                $"cannot read my own executable file: {ex.Message}", ExitCodes.Failure);
        }

        var module = VmHost.Load(payload, Console.Error);
        if (module is null) return ExitCodes.Failure;

        return VmHost.Execute(module, args, Console.Out, Console.Error, Capability.All);
    }
}
