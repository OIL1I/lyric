using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Lyric.Vm;
using Lyric.Vm.Debugging;

namespace Lyric.Tests.Vm;

/// <summary>
/// The debugger, at the VM level — no protocol anywhere. A test compiles a program the way a
/// debug session does (source map on, debug info on, optimizations OFF), drives the controller,
/// and reads stack, locals and globals while the program stands still.
///
/// <para>Every wait carries a timeout: a debugger bug here is a deadlock, and a deadlock must
/// fail the test rather than hang the suite.</para>
/// </summary>
public class DebuggerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    // ------------------------------------------------------------------ helpers

    private static LoadedProgram Load(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        var binding = comp.Resolve();
        var types = Semantics.Analyze(comp, binding, de);

        var writer = new StringWriter();
        de.RenderText(writer);
        Assert.False(de.HasErrors, "source did not compile:\n" + writer);

        // The debug shape: unoptimized (an inlined callee has no frame), with the source map and
        // the debug info the adapter relies on.
        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true, optimize: false);
        Assert.NotNull(ir);

        var bytes = BytecodeWriter.Write(ir!,
            new SourceMapContext(sm, Directory.GetCurrentDirectory()));
        var module = BytecodeReader.ReadOrThrow(bytes);
        return LoadedProgram.Load(module,
            NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null));
    }

    private static StopEvent Next(DebugController controller)
    {
        Assert.True(controller.Events.TryTake(out var stop, Timeout),
            "the debugger produced no event before the timeout");
        return stop!;
    }

    private static StopEvent ExpectStop(DebugController controller, StopReason reason)
    {
        var stop = Next(controller);
        Assert.Equal(reason, stop.Reason);
        return stop;
    }

    private static string? ValueOf(IReadOnlyList<DebugVariable> variables, string name) =>
        variables.FirstOrDefault(v => v.Name == name)?.Value;

    // ------------------------------------------------------------------ breakpoints

    private const string Countdown = """
        fn tick(n: int): int {
            let next = n - 1;
            return next;
        }

        fn main(): int {
            var n = 3;
            while (n > 0) {
                n = tick(n);
            }
            return n;
        }
        """;

    [Fact]
    public void A_breakpoint_stops_before_the_line_runs()
    {
        var controller = DebugController.Create(Load(Countdown));
        var bindings = controller.SetBreakpoints("test.lyr", [2]); // let next = n - 1;
        Assert.True(Assert.Single(bindings).Verified);

        controller.Start([]);
        ExpectStop(controller, StopReason.Breakpoint);

        var frames = controller.StackTrace();
        Assert.Equal("main.tick", frames[0].Function);
        Assert.Equal("test.lyr", frames[0].File);
        Assert.Equal(2, frames[0].Line);

        // BEFORE the line runs: the parameter is bound, the local is not yet.
        var locals = controller.Locals(0);
        Assert.Equal("3", ValueOf(locals, "n"));

        controller.Continue();
    }

    [Fact]
    public void A_breakpoint_in_a_loop_hits_every_pass()
    {
        var controller = DebugController.Create(Load(Countdown));
        controller.SetBreakpoints("test.lyr", [9]); // n = tick(n);
        controller.Start([]);

        for (var expected = 3; expected > 0; expected--)
        {
            ExpectStop(controller, StopReason.Breakpoint);
            Assert.Equal(expected.ToString(), ValueOf(controller.Locals(0), "n"));
            controller.Continue();
        }

        Assert.Equal(0, ExpectStop(controller, StopReason.Exited).ExitCode);
    }

    [Fact]
    public void A_line_without_code_slides_to_the_next_mapped_line()
    {
        var controller = DebugController.Create(Load("""
            fn main(): int {

                let x = 1;
                return x;
            }
            """));

        // Line 2 is blank; the breakpoint lands on line 3 and says so.
        var binding = Assert.Single(controller.SetBreakpoints("test.lyr", [2]));
        Assert.True(binding.Verified);
        Assert.Equal(2, binding.RequestedLine);
        Assert.Equal(3, binding.Line);

        controller.Start([]);
        ExpectStop(controller, StopReason.Breakpoint);
        Assert.Equal(3, controller.StackTrace()[0].Line);
        controller.Continue();
        ExpectStop(controller, StopReason.Exited);
    }

    [Fact]
    public void A_line_below_all_code_stays_unverified()
    {
        var controller = DebugController.Create(Load("fn main(): int { return 0; }"));
        var binding = Assert.Single(controller.SetBreakpoints("test.lyr", [99]));
        Assert.False(binding.Verified);

        controller.Start([]);
        ExpectStop(controller, StopReason.Exited);
    }

    // ------------------------------------------------------------------ stepping

    private const string Steps = """
        fn double(x: int): int {
            let d = x * 2;
            return d;
        }

        fn main(): int {
            let a = double(5);
            let b = a + 1;
            return b;
        }
        """;

    [Fact]
    public void Step_over_runs_the_call_and_stops_on_the_next_line()
    {
        var controller = DebugController.Create(Load(Steps), stopOnEntry: true);
        controller.Start([]);
        ExpectStop(controller, StopReason.Entry);
        Assert.Equal(7, controller.StackTrace()[0].Line); // let a = double(5);

        controller.StepOver();
        ExpectStop(controller, StopReason.Step);

        var frames = controller.StackTrace();
        Assert.Equal("main.main", frames[0].Function);
        Assert.Equal(8, frames[0].Line); // let b = a + 1;
        Assert.Equal("10", ValueOf(controller.Locals(0), "a")); // the call RAN
        controller.Continue();
        ExpectStop(controller, StopReason.Exited);
    }

    [Fact]
    public void Step_in_enters_the_callee()
    {
        var controller = DebugController.Create(Load(Steps), stopOnEntry: true);
        controller.Start([]);
        ExpectStop(controller, StopReason.Entry);

        controller.StepIn();
        ExpectStop(controller, StopReason.Step);

        var frames = controller.StackTrace();
        Assert.Equal("main.double", frames[0].Function);
        Assert.Equal(2, frames[0].Line);
        Assert.Equal("main.main", frames[1].Function);
        Assert.Equal(7, frames[1].Line); // the call site
        Assert.Equal("5", ValueOf(controller.Locals(0), "x"));
        controller.Continue();
        ExpectStop(controller, StopReason.Exited);
    }

    [Fact]
    public void Step_out_returns_to_the_caller()
    {
        var controller = DebugController.Create(Load(Steps), stopOnEntry: true);
        controller.Start([]);
        ExpectStop(controller, StopReason.Entry);

        controller.StepIn();
        ExpectStop(controller, StopReason.Step);
        Assert.Equal("main.double", controller.StackTrace()[0].Function);

        controller.StepOut();
        ExpectStop(controller, StopReason.Step);
        Assert.Equal("main.main", controller.StackTrace()[0].Function);
        Assert.Single(controller.StackTrace());
        controller.Continue();
        ExpectStop(controller, StopReason.Exited);
    }

    [Fact]
    public void A_breakpoint_wins_over_a_step()
    {
        // Step over a call whose body carries a breakpoint: the breakpoint reports, not the step.
        var controller = DebugController.Create(Load(Steps), stopOnEntry: true);
        controller.SetBreakpoints("test.lyr", [2]);
        controller.Start([]);
        ExpectStop(controller, StopReason.Entry);

        controller.StepOver();
        ExpectStop(controller, StopReason.Breakpoint);
        Assert.Equal("main.double", controller.StackTrace()[0].Function);
        controller.Continue();
        ExpectStop(controller, StopReason.Exited);
    }

    [Fact]
    public void Pause_stops_the_program_at_the_next_instruction()
    {
        var controller = DebugController.Create(Load(Steps), stopOnEntry: true);
        controller.Start([]);
        ExpectStop(controller, StopReason.Entry);

        // The request lands while paused; the resume immediately honours it.
        controller.Pause();
        controller.Continue();
        ExpectStop(controller, StopReason.Pause);

        controller.Continue();
        ExpectStop(controller, StopReason.Exited);
    }

    // ------------------------------------------------------------------ variables

    [Fact]
    public void Values_of_every_shape_render()
    {
        var controller = DebugController.Create(Load("""
            struct Vec2 { x: float, y: float }

            class Box { label: string = "crate", count: int = 2 }

            enum Shape { Dot, Circle(float) }

            fn main(): int {
                let i = -7;
                let f = 1.5;
                let ok = true;
                let c = 'A';
                let s = "hi\nthere";
                let v = Vec2 { x = 1.0, y = 2.0 };
                let box = Box {};
                let shape = Shape.Circle(3.5);
                let xs = [10, 20, 30];
                let maybe: ?int = null;
                let some: ?int = 42;
                return i;
            }
            """));
        controller.SetBreakpoints("test.lyr", [19]); // return i;
        controller.Start([]);
        ExpectStop(controller, StopReason.Breakpoint);

        var locals = controller.Locals(0);
        Assert.Equal("-7", ValueOf(locals, "i"));
        Assert.Equal("1.5", ValueOf(locals, "f"));
        Assert.Equal("true", ValueOf(locals, "ok"));
        Assert.Equal("'A'", ValueOf(locals, "c"));
        Assert.Equal("\"hi\\nthere\"", ValueOf(locals, "s"));
        Assert.Equal("null", ValueOf(locals, "maybe"));
        Assert.Equal("42", ValueOf(locals, "some"));
        Assert.Equal("int[3]", ValueOf(locals, "xs"));

        // A struct expands into its named fields.
        var v = locals.First(l => l.Name == "v");
        Assert.Equal("Vec2", v.Value);
        var fields = controller.Expand(v.Handle);
        Assert.Equal("1", ValueOf(fields, "x"));
        Assert.Equal("2", ValueOf(fields, "y"));

        // A class instance the same way.
        var box = locals.First(l => l.Name == "box");
        var boxFields = controller.Expand(box.Handle);
        Assert.Equal("\"crate\"", ValueOf(boxFields, "label"));
        Assert.Equal("2", ValueOf(boxFields, "count"));

        // An enum shows its variant and expands into the payload.
        var shape = locals.First(l => l.Name == "shape");
        Assert.Equal("Shape.Circle", shape.Value);
        var payload = controller.Expand(shape.Handle);
        Assert.Equal("3.5", payload[0].Value);

        // Array elements by index.
        var xs = locals.First(l => l.Name == "xs");
        var elements = controller.Expand(xs.Handle);
        Assert.Equal(["10", "20", "30"], elements.Select(e => e.Value));

        controller.Continue();
        ExpectStop(controller, StopReason.Exited);
    }

    [Fact]
    public void Globals_are_a_scope_of_their_own()
    {
        var controller = DebugController.Create(Load("""
            let limit = 100;

            fn main(): int { return limit; }
            """));
        controller.SetBreakpoints("test.lyr", [3]);
        controller.Start([]);
        ExpectStop(controller, StopReason.Breakpoint);

        Assert.Equal("100", ValueOf(controller.Globals(), "limit"));
        controller.Continue();
        ExpectStop(controller, StopReason.Exited);
    }

    [Fact]
    public void Evaluate_walks_a_dotted_path()
    {
        var controller = DebugController.Create(Load("""
            struct Inner { value: int }
            struct Outer { inner: Inner }

            fn main(): int {
                let o = Outer { inner = Inner { value = 9 } };
                return o.inner.value;
            }
            """));
        controller.SetBreakpoints("test.lyr", [6]);
        controller.Start([]);
        ExpectStop(controller, StopReason.Breakpoint);

        Assert.Equal("9", controller.Evaluate(0, "o.inner.value")?.Value);
        Assert.Null(controller.Evaluate(0, "o.nothing"));
        Assert.Null(controller.Evaluate(0, "missing"));

        controller.Continue();
        ExpectStop(controller, StopReason.Exited);
    }

    // ------------------------------------------------------------------ ends

    [Fact]
    public void The_exit_code_is_mains_value_masked()
    {
        var controller = DebugController.Create(Load("fn main(): int { return 300; }"));
        controller.Start([]);
        Assert.Equal(300 & 0xFF, ExpectStop(controller, StopReason.Exited).ExitCode);
    }

    [Fact]
    public void A_panic_terminates_with_message_and_backtrace()
    {
        var controller = DebugController.Create(Load("""
            fn boom(n: int): int {
                return 10 / n;
            }

            fn main(): int {
                return boom(0);
            }
            """));
        controller.Start([]);

        var stop = ExpectStop(controller, StopReason.Terminated);
        Assert.Equal(101, stop.ExitCode);
        Assert.Contains("division by zero", stop.Description);
        Assert.Contains("main.boom", stop.Description);
    }

    [Fact]
    public void Without_stop_on_entry_and_breakpoints_the_program_just_runs()
    {
        var controller = DebugController.Create(Load("fn main(): int { return 5; }"));
        controller.Start([]);
        Assert.Equal(5, ExpectStop(controller, StopReason.Exited).ExitCode);
    }

    // ------------------------------------------------------------------ a program without an entry point

    /// <summary>
    /// The shape an embedded script has: no `main`, and a host that calls one function per frame
    /// from its own loop. Before 2.7 the debugger was reachable only through RunEntry, so the
    /// whole machinery existed for a shape a game does not have.
    /// </summary>
    private const string Frames = """
        pub fn update(n: int): int {
            let doubled = n * 2;
            return doubled + 1;
        }

        pub fn other(): int {
            return 7;
        }
        """;

    [Fact]
    public async Task A_breakpoint_hits_inside_an_invoked_function()
    {
        var program = Load(Frames);
        var controller = DebugController.Create(program);
        Assert.True(Assert.Single(controller.SetBreakpoints("test.lyr", [2])).Verified);

        // The host's own thread makes the call; the commands come from this one.
        var index = program.IndexOfFunction("main.update");
        var call = Task.Run(() => program.Invoke(index, controller, LyrValue.FromI64(20)).AsI64);

        ExpectStop(controller, StopReason.Breakpoint);
        var frames = controller.StackTrace();
        Assert.Equal("main.update", frames[0].Function);
        Assert.Equal(2, frames[0].Line);
        Assert.Equal("20", ValueOf(controller.Locals(0), "n"));

        controller.Continue();
        Assert.Equal(41, await call.WaitAsync(Timeout));
    }

    [Fact]
    public async Task Stepping_works_from_an_invoked_function()
    {
        var program = Load(Frames);
        var controller = DebugController.Create(program);
        controller.SetBreakpoints("test.lyr", [2]);

        var index = program.IndexOfFunction("main.update");
        var call = Task.Run(() => program.Invoke(index, controller, LyrValue.FromI64(1)).AsI64);

        ExpectStop(controller, StopReason.Breakpoint);
        controller.StepOver();

        var stop = ExpectStop(controller, StopReason.Step);
        Assert.Equal(3, controller.StackTrace()[0].Line);
        // The line before it ran, so its binding now has a value.
        Assert.Equal("2", ValueOf(controller.Locals(0), "doubled"));

        controller.Continue();
        Assert.Equal(3, await call.WaitAsync(Timeout));
    }

    [Fact]
    public async Task One_controller_serves_call_after_call()
    {
        // The per-frame model: the controller is attached once and every call it is passed to
        // stops. Nothing about it is per-run.
        var program = Load(Frames);
        var controller = DebugController.Create(program);
        controller.SetBreakpoints("test.lyr", [2]);
        var index = program.IndexOfFunction("main.update");

        for (var n = 1; n <= 3; n++)
        {
            var argument = n;
            var call = Task.Run(() =>
                program.Invoke(index, controller, LyrValue.FromI64(argument)).AsI64);

            ExpectStop(controller, StopReason.Breakpoint);
            Assert.Equal(argument.ToString(), ValueOf(controller.Locals(0), "n"));

            controller.Continue();
            Assert.Equal(argument * 2 + 1, await call.WaitAsync(Timeout));
        }
    }

    [Fact]
    public void A_call_without_a_breakpoint_runs_through()
    {
        // The controller is attached and the other function carries no breakpoint: the call
        // returns without ever stopping, which is what a host needs for the frames in between.
        var program = Load(Frames);
        var controller = DebugController.Create(program);
        controller.SetBreakpoints("test.lyr", [2]);

        Assert.Equal(7,
            program.Invoke(program.IndexOfFunction("main.other"), controller).AsI64);
    }

    [Fact]
    public void The_event_stream_stays_open_between_calls()
    {
        // No Exited event: nothing ended, the host simply stopped calling. A stream that
        // completed after one frame would end a session the game is still in.
        var program = Load(Frames);
        var controller = DebugController.Create(program);

        program.Invoke(program.IndexOfFunction("main.other"), controller);

        Assert.False(controller.Events.IsCompleted);
        Assert.Empty(controller.Events);
    }
}
