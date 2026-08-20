using System.Runtime.CompilerServices;
using System.Text.Json;
using Lyric.Dap;

namespace Lyric.Tests.Dap;

/// <summary>
/// The adapter end to end, in-process: a real program on disk, the full protocol sequence, and
/// the JSON shapes a client actually reads.
/// </summary>
public class DapServerTests : IDisposable
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private readonly string _directory;

    public DapServerTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "lyric-dap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* a straggler on Windows; the temp dir cleaner gets it */ }
    }

    private string WriteProgram(string source, string name = "prog.lyr")
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, source);
        return path;
    }

    private DapTestClient Client() => new(Path.Combine(RepoRoot(), "stdlib"));

    private const string Counting = """
        import std.io.console { println };

        fn tick(n: int): int {
            let next = n - 1;
            return next;
        }

        fn main(): int {
            var n = 2;
            while (n > 0) {
                n = tick(n);
            }
            println("done");
            return n;
        }
        """;

    /// <summary>The full protocol handshake up to a running, configured program.</summary>
    private async Task<string> LaunchAsync(DapTestClient client, string source,
        int[]? breakpointLines = null, bool stopOnEntry = false)
    {
        var program = WriteProgram(source);

        Assert.True((await client.RequestAsync("initialize", new { adapterID = "lyric" })).Success);
        var launch = await client.RequestAsync("launch", new { program, stopOnEntry });
        Assert.True(launch.Success, launch.Message ?? "launch failed without a message");
        client.TakeEvent("initialized");

        if (breakpointLines is not null)
        {
            var response = await client.RequestAsync("setBreakpoints", new
            {
                source = new { path = program },
                breakpoints = breakpointLines.Select(line => new { line }).ToArray(),
            });
            Assert.True(response.Success);
            foreach (var breakpoint in response.Body!.Value.GetProperty("breakpoints").EnumerateArray())
                Assert.True(breakpoint.GetProperty("verified").GetBoolean());
        }

        Assert.True((await client.RequestAsync("configurationDone")).Success);
        return program;
    }

    [Fact]
    public async Task A_program_runs_to_its_end_and_reports_output_and_exit()
    {
        await using var client = Client();
        await LaunchAsync(client, Counting);

        var output = client.TakeEvent("output").Body!.Value;
        Assert.Equal("stdout", output.GetProperty("category").GetString());
        Assert.Equal("done\n", output.GetProperty("output").GetString());

        Assert.Equal(0, client.TakeEvent("exited").Body!.Value.GetProperty("exitCode").GetInt32());
        client.TakeEvent("terminated");
    }

    [Fact]
    public async Task A_breakpoint_stops_and_the_stack_names_file_and_line()
    {
        await using var client = Client();
        var program = await LaunchAsync(client, Counting, breakpointLines: [4]);

        var stopped = client.TakeEvent("stopped").Body!.Value;
        Assert.Equal("breakpoint", stopped.GetProperty("reason").GetString());
        Assert.Equal(1, stopped.GetProperty("threadId").GetInt32());

        var threads = (await client.RequestAsync("threads")).Body!.Value;
        var thread = Assert.Single(threads.GetProperty("threads").EnumerateArray().ToList());
        Assert.Equal("main", thread.GetProperty("name").GetString());

        var stack = (await client.RequestAsync("stackTrace", new { threadId = 1 })).Body!.Value;
        var frames = stack.GetProperty("stackFrames").EnumerateArray().ToList();
        Assert.Equal(2, frames.Count); // tick under main
        Assert.Equal("main.tick", frames[0].GetProperty("name").GetString());
        Assert.Equal(4, frames[0].GetProperty("line").GetInt32());
        Assert.Equal(program, frames[0].GetProperty("source").GetProperty("path").GetString());
        Assert.Equal("main.main", frames[1].GetProperty("name").GetString());

        await client.RequestAsync("continue", new { threadId = 1 });
    }

    [Fact]
    public async Task Scopes_variables_and_expansion_carry_the_debug_names()
    {
        await using var client = Client();
        await LaunchAsync(client, """
            import std.io.console { println };

            struct Vec2 { x: float, y: float }

            let scale = 3;

            fn main(): int {
                let v = Vec2 { x = 1.5, y = 2.5 };
                let xs = [7, 8];
                println("at");
                return scale;
            }
            """, breakpointLines: [10]); // println("at");

        client.TakeEvent("stopped");

        var scopes = (await client.RequestAsync("scopes", new { frameId = 0 })).Body!.Value
            .GetProperty("scopes").EnumerateArray().ToList();
        Assert.Equal(["Locals", "Globals"], scopes.Select(s => s.GetProperty("name").GetString()));

        var locals = (await client.RequestAsync("variables", new
        {
            variablesReference = scopes[0].GetProperty("variablesReference").GetInt32(),
        })).Body!.Value.GetProperty("variables").EnumerateArray()
            .ToDictionary(v => v.GetProperty("name").GetString()!, v => v);

        Assert.Equal("Vec2", locals["v"].GetProperty("value").GetString());
        Assert.Equal("int[2]", locals["xs"].GetProperty("value").GetString());

        // Expanding the struct answers its fields by name.
        var fields = (await client.RequestAsync("variables", new
        {
            variablesReference = locals["v"].GetProperty("variablesReference").GetInt32(),
        })).Body!.Value.GetProperty("variables").EnumerateArray()
            .ToDictionary(v => v.GetProperty("name").GetString()!,
                v => v.GetProperty("value").GetString());
        Assert.Equal("1.5", fields["x"]);
        Assert.Equal("2.5", fields["y"]);

        var globals = (await client.RequestAsync("variables", new
        {
            variablesReference = scopes[1].GetProperty("variablesReference").GetInt32(),
        })).Body!.Value.GetProperty("variables").EnumerateArray().ToList();
        Assert.Contains(globals, g => g.GetProperty("name").GetString() == "scale"
                                      && g.GetProperty("value").GetString() == "3");

        // Evaluate is the same lookup, dotted.
        var evaluate = (await client.RequestAsync("evaluate",
            new { expression = "v.y", frameId = 0 })).Body!.Value;
        Assert.Equal("2.5", evaluate.GetProperty("result").GetString());

        var unknown = await client.RequestAsync("evaluate",
            new { expression = "nothing", frameId = 0 });
        Assert.False(unknown.Success);

        await client.RequestAsync("continue", new { threadId = 1 });
    }

    [Fact]
    public async Task Stepping_moves_by_lines_and_frames()
    {
        await using var client = Client();
        await LaunchAsync(client, Counting, stopOnEntry: true);

        Assert.Equal("entry",
            client.TakeEvent("stopped").Body!.Value.GetProperty("reason").GetString());

        // Entry stands on 'var n = 2;' (line 9); a step lands on the while head (line 10).
        await client.RequestAsync("next", new { threadId = 1 });
        Assert.Equal("step",
            client.TakeEvent("stopped").Body!.Value.GetProperty("reason").GetString());
        var stack = (await client.RequestAsync("stackTrace", new { threadId = 1 })).Body!.Value;
        Assert.Equal(10, stack.GetProperty("stackFrames")[0].GetProperty("line").GetInt32());

        // Step over the loop head, then INTO the call on line 11.
        await client.RequestAsync("next", new { threadId = 1 });
        client.TakeEvent("stopped");
        await client.RequestAsync("stepIn", new { threadId = 1 });
        client.TakeEvent("stopped");
        stack = (await client.RequestAsync("stackTrace", new { threadId = 1 })).Body!.Value;
        Assert.Equal("main.tick",
            stack.GetProperty("stackFrames")[0].GetProperty("name").GetString());

        // And back out.
        await client.RequestAsync("stepOut", new { threadId = 1 });
        client.TakeEvent("stopped");
        stack = (await client.RequestAsync("stackTrace", new { threadId = 1 })).Body!.Value;
        Assert.Equal("main.main",
            stack.GetProperty("stackFrames")[0].GetProperty("name").GetString());

        await client.RequestAsync("continue", new { threadId = 1 });
        client.TakeEvent("exited");
    }

    [Fact]
    public async Task A_panic_reaches_the_client_as_stderr_and_exit_101()
    {
        await using var client = Client();
        await LaunchAsync(client, """
            fn main(): int {
                let zero = 0;
                return 1 / zero;
            }
            """);

        var output = client.TakeEvent("output").Body!.Value;
        Assert.Equal("stderr", output.GetProperty("category").GetString());
        Assert.Contains("division by zero", output.GetProperty("output").GetString());

        Assert.Equal(101,
            client.TakeEvent("exited").Body!.Value.GetProperty("exitCode").GetInt32());
        client.TakeEvent("terminated");
    }

    [Fact]
    public async Task A_program_that_does_not_compile_fails_the_launch_with_the_diagnostic()
    {
        await using var client = Client();
        var program = WriteProgram("fn main(): int { return undeclared; }");

        Assert.True((await client.RequestAsync("initialize", new { adapterID = "lyric" })).Success);
        var launch = await client.RequestAsync("launch", new { program });
        Assert.False(launch.Success);
        Assert.Contains("LYR-", launch.Message);
    }

    [Fact]
    public async Task Requests_before_a_launch_fail_instead_of_crashing()
    {
        await using var client = Client();
        Assert.True((await client.RequestAsync("initialize", new { adapterID = "lyric" })).Success);

        var response = await client.RequestAsync("stackTrace", new { threadId = 1 });
        Assert.False(response.Success);
        Assert.Contains("no program", response.Message);
    }

    [Fact]
    public async Task An_unknown_request_is_answered_not_dropped()
    {
        await using var client = Client();
        var response = await client.RequestAsync("gotoTargets", new { });
        Assert.False(response.Success);
        Assert.Contains("unsupported", response.Message);
    }
}
