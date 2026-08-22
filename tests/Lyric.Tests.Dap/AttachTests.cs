using System.Runtime.CompilerServices;
using System.Text.Json;
using Lyric.Bytecode;
using Lyric.Compiler;
using Lyric.Core;
using Lyric.Vm;
using Lyric.Vm.Debugging;

namespace Lyric.Tests.Dap;

/// <summary>
/// The adapter serving a program someone else is running.
///
/// <para>A game has no <c>main</c> to launch, and the bug worth stopping at is rarely the one at
/// startup — it is the one in level three. These tests take the host's side: they compile and load
/// the program themselves, build the controller, call into it the way a frame loop does, and let
/// the editor attach to what is already running.</para>
///
/// <para>The end of a session is the sharp part. An editor that disconnects while the program
/// stands at a breakpoint has to give the thread back, or the game is parked for good — and the
/// breakpoints nobody reads any more would park it again on the next frame.</para>
/// </summary>
public class AttachTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private readonly string _directory;

    public AttachTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "lyric-attach-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* a straggler on Windows; the temp dir cleaner gets it */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>A script with no entry point — what an embedded program looks like. The host calls
    /// <c>update</c> once per frame.</summary>
    private const string Script = """
        pub fn update(n: int): int {
            let doubled = n * 2;
            return doubled + 1;
        }
        """;

    /// <summary>The host's half: compile in the debug shape, load, and keep the program.</summary>
    private LoadedProgram Host(string source = Script, string name = "game.lyr")
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, source);

        var result = SourceCompiler.Compile(path, new CompilerOptions
        {
            StdlibRoot = Path.Combine(RepoRoot(), "stdlib"),
            Optimize = false,
        });
        Assert.NotNull(result.Bytes);

        return LoadedProgram.Load(BytecodeReader.ReadOrThrow(result.Bytes!),
            NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null));
    }

    /// <summary>initialize, attach, breakpoints, configurationDone — the sequence a client sends,
    /// with <c>attach</c> where <c>launch</c> would be.</summary>
    private static async Task AttachAsync(DapTestClient client, string file, int[] lines)
    {
        await client.RequestAsync("initialize", new { adapterID = "lyric" });

        var attached = await client.RequestAsync("attach", new { });
        Assert.True(attached.Success);

        client.TakeEvent("initialized");

        await client.RequestAsync("setBreakpoints", new
        {
            source = new { path = file },
            breakpoints = lines.Select(l => new { line = l }).ToArray(),
        });
        await client.RequestAsync("configurationDone");
    }

    [Fact]
    public async Task An_editor_attaches_to_a_program_a_host_is_already_running()
    {
        var program = Host();
        var controller = DebugController.Create(program);
        var index = program.IndexOfFunction("main.update");
        await using var client = new DapTestClient(controller, _directory);

        await AttachAsync(client, Path.Combine(_directory, "game.lyr"), [2]);

        // The frame loop, on the host's own thread — the caller parks at the breakpoint.
        var frame = Task.Run(() => program.Invoke(index, controller, LyrValue.FromI64(20)).AsI64);

        var stopped = client.TakeEvent("stopped");
        Assert.Equal("breakpoint", stopped.Body!.Value.GetProperty("reason").GetString());

        var stack = await client.RequestAsync("stackTrace", new { threadId = 1 });
        var top = stack.Body!.Value.GetProperty("stackFrames")[0];
        Assert.Equal("main.update", top.GetProperty("name").GetString());
        Assert.Equal(2, top.GetProperty("line").GetInt32());

        await client.RequestAsync("continue", new { threadId = 1 });
        Assert.Equal(41, await frame.WaitAsync(Timeout));
    }

    [Fact]
    public async Task The_breakpoint_holds_for_the_next_frame_too()
    {
        var program = Host();
        var controller = DebugController.Create(program);
        var index = program.IndexOfFunction("main.update");
        await using var client = new DapTestClient(controller, _directory);

        await AttachAsync(client, Path.Combine(_directory, "game.lyr"), [2]);

        for (var n = 1; n <= 3; n++)
        {
            var argument = n;
            var frame = Task.Run(() =>
                program.Invoke(index, controller, LyrValue.FromI64(argument)).AsI64);

            client.TakeEvent("stopped");
            await client.RequestAsync("continue", new { threadId = 1 });
            Assert.Equal(argument * 2 + 1, await frame.WaitAsync(Timeout));
        }
    }

    [Fact]
    public async Task Disconnecting_while_parked_gives_the_thread_back()
    {
        // The question an attached session has and a launching one never does: the editor is gone
        // and the game is standing at a breakpoint. Without a detach it stands there for good.
        var program = Host();
        var controller = DebugController.Create(program);
        var index = program.IndexOfFunction("main.update");
        var client = new DapTestClient(controller, _directory);

        await AttachAsync(client, Path.Combine(_directory, "game.lyr"), [2]);

        var frame = Task.Run(() => program.Invoke(index, controller, LyrValue.FromI64(5)).AsI64);
        client.TakeEvent("stopped");

        await client.RequestAsync("disconnect");

        Assert.Equal(11, await frame.WaitAsync(Timeout));
    }

    [Fact]
    public async Task A_detached_session_leaves_no_breakpoints_behind()
    {
        // The other half: a breakpoint nobody listens to would park the next frame with no editor
        // to resume it.
        var program = Host();
        var controller = DebugController.Create(program);
        var index = program.IndexOfFunction("main.update");
        var client = new DapTestClient(controller, _directory);

        await AttachAsync(client, Path.Combine(_directory, "game.lyr"), [2]);
        await client.RequestAsync("disconnect");

        var frame = Task.Run(() => program.Invoke(index, controller, LyrValue.FromI64(10)).AsI64);
        Assert.Equal(21, await frame.WaitAsync(Timeout));
    }

    [Fact]
    public async Task An_attaching_adapter_refuses_to_launch()
    {
        var program = Host();
        await using var client = new DapTestClient(DebugController.Create(program), _directory);

        await client.RequestAsync("initialize", new { adapterID = "lyric" });
        var launched = await client.RequestAsync("launch",
            new { program = Path.Combine(_directory, "game.lyr") });

        Assert.False(launched.Success);
        Assert.Contains("already running", launched.Message);
    }

    [Fact]
    public async Task A_launching_adapter_refuses_to_attach()
    {
        await using var client = new DapTestClient(Path.Combine(RepoRoot(), "stdlib"));

        await client.RequestAsync("initialize", new { adapterID = "lyric" });
        var attached = await client.RequestAsync("attach", new { });

        Assert.False(attached.Success);
        Assert.Contains("starts the program itself", attached.Message);
    }
}
