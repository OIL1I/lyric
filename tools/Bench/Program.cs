using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Embedding;
using Lyric.Vm;

namespace Lyric.Bench;

/// <summary>
/// Measures what one operation costs inside the interpreter: nanoseconds and allocated bytes.
///
/// <para>Each case is a complete Lyric program whose <c>main</c> runs a loop of a known operation
/// count. The scalar case has the same loop shape and no operation, so subtracting its per-op
/// figures removes the loop and the VM start from a case and leaves the operation alone. That is
/// the method behind the numbers in STATUS.md, made repeatable.</para>
///
/// <para>Runs in-process on purpose: a batch invocation would measure process start and JIT
/// warm-up, which a long-lived host pays once. Two warm-up runs precede the measurement so the
/// interpreter loop is tiered before the clock starts; the minimum over the repetitions is
/// reported, because noise only ever adds.</para>
///
/// <para><b>The adjusted columns of this first table became unreliable with format 3.6</b>, and
/// the reason is worth knowing rather than patching over. Subtracting a baseline CASE assumes
/// every case carries the same loop around its operation. Instruction selection broke that
/// assumption: the scalar baseline computes a nested expression, which does not fuse, while a
/// case with a plain accumulator now runs three instructions where the baseline runs nine. The
/// subtraction then reports a negative cost for a native call, which is not a small error but a
/// meaningless one. The interpreter table below does not have the problem — it differences two
/// iteration counts of the SAME program — and rebuilding the first table on that method is its
/// own piece of work.</para>
/// </summary>
internal static class Program
{
    private const int Repetitions = 9;

    /// <summary>More trials than the table above takes, because these figures are DIFFERENCES:
    /// two estimates, each of which has to have found its own quiet moment.</summary>
    private const int InterpreterTrials = 15;

    /// <param name="Baseline">The case whose per-op figures are subtracted, so the reported
    /// number names the operation rather than the loop around it.</param>
    /// <param name="RawNatives">Run through <c>VmHost.Load</c> with a raw
    /// <see cref="NativeRegistry"/> instead of the embedding layer — see
    /// <see cref="Prepare"/>.</param>
    private sealed record Case(string Name, int Ops, string? Baseline, string Source,
        bool RawNatives = false);

    private sealed record Result(double NsPerOp, double BytesPerOp);

    /// <param name="Template">A whole program whose loop bound is written <c>{N}</c>. It is
    /// compiled twice, at two iteration counts, and only the difference is reported.</param>
    private sealed record InterpreterCase(string Name, string Template);

    public static int Main(string[] args)
    {
        var filter = args.Length > 0 ? args[0] : null;
        var stdlib = Path.Combine(RepoRoot(), "stdlib");

        Console.WriteLine($"config: {Configuration()}, ops per case: 100000, " +
                          $"repetitions: {Repetitions}, figures are minima");
        Console.WriteLine();
        Console.WriteLine("| case | ns/op | B/op | ns/op adj. | B/op adj. |");
        Console.WriteLine("|---|---:|---:|---:|---:|");

        // Round-robin over several cycles rather than case after case: the interpreter loop is
        // ONE shared method, and tiered compilation keeps improving it while the harness runs.
        // Measured sequentially, the first case pays the cold tiers for all of them — the scalar
        // baseline came out slower than the loop that did the same work plus a call. The minimum
        // per case across cycles sees every case at the final tier at least once.
        var cases = Cases().ToList();
        var vm = new LangVm(new HostOptions
        {
            StdlibRoot = stdlib,
            NativeRoots = new Dictionary<string, string>
            {
                ["bench"] = Path.Combine(RepoRoot(), "tools", "Bench", "sdk"),
            },
        });
        var runners = cases.ToDictionary(c => c.Name, c => Prepare(vm, c), StringComparer.Ordinal);

        foreach (var c in cases) Require(runners[c.Name](), c.Name);

        var results = cases.ToDictionary(c => c.Name,
            _ => new Result(double.MaxValue, double.MaxValue), StringComparer.Ordinal);

        for (var cycle = 0; cycle < 3; cycle++)
            foreach (var c in cases)
            {
                var measured = Measure(runners[c.Name], c);
                results[c.Name] = new Result(
                    Math.Min(results[c.Name].NsPerOp, measured.NsPerOp),
                    Math.Min(results[c.Name].BytesPerOp, measured.BytesPerOp));
            }

        foreach (var c in cases)
        {
            if (filter is not null && !c.Name.Contains(filter, StringComparison.Ordinal))
                continue;

            var result = results[c.Name];
            var baseline = c.Baseline is null ? new Result(0, 0) : results[c.Baseline];
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"| {c.Name} | {result.NsPerOp:F1} | {result.BytesPerOp:F1} " +
                $"| {result.NsPerOp - baseline.NsPerOp:F1} " +
                $"| {result.BytesPerOp - baseline.BytesPerOp:F1} |"));
        }

        Interpreter(stdlib, filter);
        return 0;
    }

    /// <summary>
    /// What ONE instruction costs — the figure M32 is about.
    ///
    /// <para>Separate from the table above, and measured differently, because the question is a
    /// different one. Up there a case is compared against a loop of the same shape to isolate an
    /// operation. Here the loop IS the subject: what is asked is how much of a program's time is
    /// the dispatch itself.</para>
    ///
    /// <para>Every case runs at TWO iteration counts and only the difference is reported. That
    /// removes the prologue, the VM start and everything else that happens once, without needing
    /// a baseline case to subtract — and it removes the argument about whether the baseline had
    /// the same shape.</para>
    ///
    /// <para>The instruction count per iteration is MEASURED, not counted by hand from a
    /// disassembly and not written into the case: an <see cref="ExecutionBudget"/> reports what
    /// it charged, so the difference between the two runs divided by the difference in
    /// iterations is exactly the instructions one iteration executes. Which matters most when the
    /// emitter starts fusing instructions — the number that is supposed to fall is then the
    /// number the harness reads, not one a person maintains beside it.</para>
    ///
    /// <para>The counting run is its own run: charging a budget puts the loop on a different
    /// policy, so a figure taken while counting would measure the counting.</para>
    /// </summary>
    private static void Interpreter(string stdlib, string? filter)
    {
        const int Low = 1_000_000;
        const int High = 2_000_000;

        var vm = new LangVm(new HostOptions { StdlibRoot = stdlib });
        var cases = InterpreterCases()
            .Where(c => filter is null || c.Name.Contains(filter, StringComparison.Ordinal))
            .ToList();
        if (cases.Count == 0) return;

        Console.WriteLine();
        Console.WriteLine($"interpreter: {High - Low} iterations differenced, "
                          + $"{InterpreterTrials} interleaved trials, minima");
        Console.WriteLine();
        Console.WriteLine("| case | instr/iter | ns/iter | ns/instr |");
        Console.WriteLine("|---|---:|---:|---:|");

        foreach (var c in cases)
        {
            var low = Loaded(vm, c, Low);
            var high = Loaded(vm, c, High);

            var instructions = (Charged(high, c.Name) - Charged(low, c.Name)) / (double)(High - Low);
            var nanos = PairedDifference(low, high, c.Name) / (High - Low);

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"| {c.Name} | {instructions:F1} | {nanos:F1} | {nanos / instructions:F2} |"));
        }
    }

    /// <summary>
    /// The time the extra iterations cost: the minimum of each run, differenced.
    ///
    /// <para><b>Minimum, not median of differences.</b> Interpreter noise is one-sided — a trial
    /// can be interrupted, never accelerated — so the minimum over enough trials is the estimate
    /// of the undisturbed time, and the difference of two such estimates is the undisturbed
    /// difference. Taking the median of paired differences instead, which sounds more careful,
    /// gives every disturbance in the SUBTRAHEND a vote in the answer: a slow low run makes the
    /// difference too small, and nothing in a median removes that.</para>
    ///
    /// <para>The two runs still alternate rather than running in blocks. Not for the statistics
    /// but for the tier: the interpreter loop is one shared method that tiered compilation keeps
    /// improving while the harness runs, so measuring one program to completion first would
    /// measure it colder than the other.</para>
    /// </summary>
    private static double PairedDifference(LoadedProgram low, LoadedProgram high, string name)
    {
        Require(low.RunEntry([]).AsI64, name);   // warm the tier before the clock matters
        Require(high.RunEntry([]).AsI64, name);

        var bestLow = long.MaxValue;
        var bestHigh = long.MaxValue;
        for (var i = 0; i < InterpreterTrials; i++)
        {
            bestLow = Math.Min(bestLow, Timed(low, name));
            bestHigh = Math.Min(bestHigh, Timed(high, name));
        }

        return (bestHigh - bestLow) * (1_000_000_000.0 / Stopwatch.Frequency);
    }

    private static long Timed(LoadedProgram program, string name)
    {
        var before = Stopwatch.GetTimestamp();
        var exit = program.RunEntry([]).AsI64;
        var ticks = Stopwatch.GetTimestamp() - before;
        Require(exit, name);
        return ticks;
    }

    /// <summary>Compiles one interpreter case at one iteration count. The raw path, as for the
    /// native cases above: the embedding layer has nothing to do with what is measured here.
    /// </summary>
    private static LoadedProgram Loaded(LangVm vm, InterpreterCase c, int iterations)
    {
        var source = c.Template.Replace("{N}", iterations.ToString(CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
        var module = vm.Compile(source, "bench");
        var loaded = VmHost.Load(module.Bytes, Console.Error)
                     ?? throw new InvalidOperationException($"case '{c.Name}' did not load");
        return LoadedProgram.Load(loaded, RawRegistry(), Capability.None);
    }

    /// <summary>How many instructions one run executes. The limit is a ceiling nothing here
    /// approaches — exhausting it would be a panic, which is the right failure for a case that
    /// grew a runaway loop.</summary>
    private static long Charged(LoadedProgram program, string name)
    {
        var budget = new ExecutionBudget(long.MaxValue / 2);
        Require(program.RunEntry([], budget).AsI64, name);
        return budget.Consumed;
    }

    /// <summary>Turns a case into a run delegate returning the exit code.</summary>
    /// <remarks>Two paths on purpose. Ordinary cases go through <see cref="LangVm.Run"/>. Cases
    /// with RAW NATIVES bypass the embedding layer's delegate marshalling — it boxes every
    /// argument and DynamicInvokes, which would bury the interpreter's own transport cost this
    /// harness wants to see. The raw path is <c>VmHost.Load</c> plus a registry of
    /// <c>Func&lt;LyrValue[], LyrValue&gt;</c> implementations: exactly the path a game engine
    /// host takes.</remarks>
    private static Func<long> Prepare(LangVm vm, Case c)
    {
        ScriptModule module;
        try
        {
            module = vm.Compile(c.Source, "bench");
        }
        catch (EmbeddingException ex)
        {
            // Name the case and the diagnostics: a harness that dies with a bare count sends
            // whoever broke a snippet into the debugger instead of to the line.
            Console.Error.WriteLine($"case '{c.Name}' did not compile:");
            foreach (var diagnostic in ex.Diagnostics)
                Console.Error.WriteLine($"  {diagnostic.Code}: {diagnostic.Message}");
            throw;
        }

        if (!c.RawNatives) return () => vm.Run(module);

        var loaded = VmHost.Load(module.Bytes, Console.Error)
                     ?? throw new InvalidOperationException($"case '{c.Name}' did not load");
        var program = LoadedProgram.Load(loaded, RawRegistry(), Capability.None);
        return () => program.RunEntry([]).AsI64;
    }

    /// <summary>Sinks for the boundary probes: enough side effect that nothing can be elided,
    /// no work that would pollute the figure.</summary>
    private static double _sink;

    private static NativeRegistry RawRegistry()
    {
        var registry = NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null);

        registry.Register("bench.api.step", [TypeTag.F64], TypeTag.F64,
            arguments => LyrValue.FromF64(arguments[0].AsF64 * 0.9999 + 1.5));

        registry.Register("bench.api.push4",
            [TypeTag.F64, TypeTag.F64, TypeTag.F64, TypeTag.F64], TypeTag.Void,
            arguments =>
            {
                _sink = arguments[0].AsF64 + arguments[1].AsF64
                        + arguments[2].AsF64 - arguments[3].AsF64;
                return default;
            });

        // 'pull2(v: Vec2)' arrives flattened: the host registers against the wire signature,
        // two floats, exactly as it would have written two scalar parameters itself.
        registry.Register("bench.api.pull2", [TypeTag.F64, TypeTag.F64], TypeTag.F64,
            arguments => LyrValue.FromF64(arguments[0].AsF64 * 0.9999 + arguments[1].AsF64));

        // 'origin(): Vec2' — the result buffer is the trailing argument; one value per field.
        registry.RegisterStructReturning("bench.api.origin", [], [TypeTag.F64, TypeTag.F64],
            (arguments, result) =>
            {
                result[0] = LyrValue.FromF64(0.25);
                result[1] = LyrValue.FromF64(1.5);
            });

        return registry;
    }

    private static Result Measure(Func<long> run, Case c)
    {
        var bestTicks = long.MaxValue;
        var bestBytes = long.MaxValue;
        for (var i = 0; i < Repetitions; i++)
        {
            var bytesBefore = GC.GetAllocatedBytesForCurrentThread();
            var ticksBefore = Stopwatch.GetTimestamp();
            var exit = run();
            var ticks = Stopwatch.GetTimestamp() - ticksBefore;
            var bytes = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;

            Require(exit, c.Name);
            bestTicks = Math.Min(bestTicks, ticks);
            bestBytes = Math.Min(bestBytes, bytes);
        }

        var nanos = bestTicks * (1_000_000_000.0 / Stopwatch.Frequency);
        return new Result(nanos / c.Ops, (double)bestBytes / c.Ops);
    }

    private static void Require(long exit, string name)
    {
        if (exit != 0)
            throw new InvalidOperationException($"case '{name}' exited with {exit}");
    }

    private static string Configuration()
    {
#if DEBUG
        // The verifier runs in Debug and dominates nothing that matters here; the figures are
        // only comparable in Release.
        return "DEBUG — numbers are not meaningful";
#else
        return "Release";
#endif
    }

    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    // ------------------------------------------------------------------ the cases
    //
    // Loop bodies read what they compute, so nothing is removable; operands depend on the
    // accumulator, so nothing is a foldable constant. All loops run 100 000 operations.

    private static IEnumerable<Case> Cases()
    {
        // The loop and the VM start, nothing else. Everything scalar-shaped subtracts this.
        yield return new Case("scalar", 100_000, null, """
            fn main(): int {
                var acc = 0.0;
                var i = 0;
                while (i < 100000) {
                    acc = acc * 0.9999 + 1.5;
                    i = i + 1;
                }
                return if (acc > 0.0) 0 else 1;
            }
            """);

        // A direct call to a one-line function: the frame cost.
        yield return new Case("call", 100_000, "scalar", """
            fn step(a: float): float { return a * 0.9999 + 1.5; }

            fn main(): int {
                var acc = 0.0;
                var i = 0;
                while (i < 100000) {
                    acc = step(acc);
                    i = i + 1;
                }
                return if (acc > 0.0) 0 else 1;
            }
            """);

        // A struct built and read in the same iteration: the allocation cost alone.
        yield return new Case("construct", 100_000, "scalar", """
            struct Vec2 { x: float, y: float }

            fn main(): int {
                var acc = 0.0;
                var i = 0;
                while (i < 100000) {
                    let v = Vec2 { x = acc, y = 1.5 };
                    acc = v.x * 0.9999 + v.y;
                    i = i + 1;
                }
                return if (acc > 0.0) 0 else 1;
            }
            """);

        // Construction plus method call: the Vec2.add shape from STATUS.md, and the value the
        // scalar-replacement slice has to bring to zero.
        yield return new Case("construct_call", 100_000, "scalar", """
            struct Vec2 {
                x: float,
                y: float,
                fn add(other: Vec2): Vec2 {
                    return Vec2 { x = this.x + other.x, y = this.y + other.y };
                }
            }

            fn main(): int {
                var a = Vec2 { x = 0.0, y = 0.0 };
                let b = Vec2 { x = 0.5, y = 0.25 };
                var i = 0;
                while (i < 100000) {
                    a = a.add(b);
                    i = i + 1;
                }
                return if (a.x > 0.0) 0 else 1;
            }
            """);

        // The same shape through the operator: must cost what the written call costs.
        yield return new Case("operator", 100_000, "scalar", """
            import std.core { Add };

            struct Vec2 :: [Add<Vec2>] {
                x: float,
                y: float,
                fn add(other: Vec2): Vec2 {
                    return Vec2 { x = this.x + other.x, y = this.y + other.y };
                }
            }

            fn main(): int {
                var a = Vec2 { x = 0.0, y = 0.0 };
                let b = Vec2 { x = 0.5, y = 0.25 };
                var i = 0;
                while (i < 100000) {
                    a = a + b;
                    i = i + 1;
                }
                return if (a.x > 0.0) 0 else 1;
            }
            """);

        // The integer loop the two for-in cases are measured against.
        yield return new Case("while_range", 100_000, null, """
            fn main(): int {
                var sum = 0;
                var i = 0;
                while (i < 100000) {
                    sum = sum + i;
                    i = i + 1;
                }
                return if (sum > 0) 0 else 1;
            }
            """);

        // The RangeIterator route: one direct next() call per element today.
        yield return new Case("forin_range", 100_000, "while_range", """
            fn main(): int {
                var sum = 0;
                for (i in 0..100000) {
                    sum = sum + i;
                }
                return if (sum > 0) 0 else 1;
            }
            """);

        // The ArrayIterator route; 100 passes over 1000 elements.
        yield return new Case("forin_array", 100_000, "while_range", """
            fn main(): int {
                let xs = [1] * 1000;
                var sum = 0;
                var pass = 0;
                while (pass < 100) {
                    for (x in xs) {
                        sum = sum + x;
                    }
                    pass = pass + 1;
                }
                return if (sum > 0) 0 else 1;
            }
            """);

        // --------------------------------------------------------------- the boundary probes
        //
        // Erato's finding, reproduced in Lyric's own harness: the crossing is cheap, its
        // allocation scatter is not. Every native call builds a LyrValue[arity]; these two cases
        // put a number on it, per arity.

        // One scalar in, one out — the smallest round trip.
        yield return new Case("native_ret", 100_000, "scalar", """
            import bench.api { step };

            fn main(): int {
                var acc = 0.0;
                var i = 0;
                while (i < 100000) {
                    acc = step(acc);
                    i = i + 1;
                }
                return if (acc > 0.0) 0 else 1;
            }
            """, RawNatives: true);

        // Four scalars in, nothing out — the setPosition shape.
        yield return new Case("native_void4", 100_000, "scalar", """
            import bench.api { push4 };

            fn main(): int {
                var acc = 0.0;
                var i = 0;
                while (i < 100000) {
                    push4(acc, 0.5, 1.5, 2.5);
                    acc = acc * 0.9999 + 1.5;
                    i = i + 1;
                }
                return if (acc > 0.0) 0 else 1;
            }
            """, RawNatives: true);

        // A struct crossing the boundary: built fresh each pass, flattened at the call. The
        // gate is 0 B — the scalarizer dissolves the construction because flattening removed
        // the escape.
        yield return new Case("native_vec2_arg", 100_000, "scalar", """
            import bench.api { Vec2, pull2 };

            fn main(): int {
                var acc = 0.0;
                var i = 0;
                while (i < 100000) {
                    let v = Vec2 { x = acc, y = 1.5 };
                    acc = pull2(v);
                    i = i + 1;
                }
                return if (acc > 0.0) 0 else 1;
            }
            """, RawNatives: true);

        // A struct coming BACK over the boundary: the positionOf shape. The gate is 0 B — the
        // buffer exists once per program, and the copy-out dissolves when the value never
        // escapes.
        yield return new Case("native_vec2_ret", 100_000, "scalar", """
            import bench.api { origin };

            fn main(): int {
                var acc = 0.0;
                var i = 0;
                while (i < 100000) {
                    let p = origin();
                    acc = acc * 0.9999 + p.x + p.y;
                    i = i + 1;
                }
                return if (acc > 0.0) 0 else 1;
            }
            """, RawNatives: true);

        // The callvirt route: iter() answers with the interface, so every next() dispatches
        // dynamically. The setup (1000 adds) is inside the measurement and constant across
        // toolchain versions; the figure is a gate, not an absolute.
        yield return new Case("set_iter", 100_000, "while_range", """
            import std.collections { Set };

            fn main(): int {
                var s = Set<int>.empty();
                var k = 0;
                while (k < 1000) {
                    s.add(k);
                    k = k + 1;
                }

                var sum = 0;
                var pass = 0;
                while (pass < 100) {
                    for (v in s.iter()) {
                        sum = sum + v;
                    }
                    pass = pass + 1;
                }
                return if (sum > 0) 0 else 1;
            }
            """);
    }

    /// <summary>
    /// Five shapes that do as little as possible, so what they measure is the dispatch.
    ///
    /// <para>The names are Erato's, from the <c>bench/interp</c> that produced the finding this
    /// milestone answers — the numbers are meant to be comparable to that report, which is the
    /// only outside measurement of this interpreter that exists.</para>
    ///
    /// <para>Each one differs from <c>loopOnly</c> by ONE operation in the body, so the columns
    /// answer the question the finding raised: whether an instruction's price depends on what it
    /// does.</para>
    /// </summary>
    private static IEnumerable<InterpreterCase> InterpreterCases()
    {
        // Nothing but the loop: the counter, the comparison, the jump. Five of its nine
        // instructions are bookkeeping, which is what slices 3 and 4 are aimed at.
        yield return new InterpreterCase("loopOnly", """
            fn main(): int {
                var i = 0;
                while (i < {N}) {
                    i = i + 1;
                }
                return if (i > 0) 0 else 1;
            }
            """);

        yield return new InterpreterCase("intAdd", """
            fn main(): int {
                var i = 0;
                var acc = 0;
                while (i < {N}) {
                    acc = acc + 3;
                    i = i + 1;
                }
                return if (acc > 0) 0 else 1;
            }
            """);

        yield return new InterpreterCase("floatAdd", """
            fn main(): int {
                var i = 0;
                var acc = 0.0;
                while (i < {N}) {
                    acc = acc + 1.5;
                    i = i + 1;
                }
                return if (acc > 0.0) 0 else 1;
            }
            """);

        // Bit work: the cheapest thing an ALU does, against the float above.
        yield return new InterpreterCase("maskOnly", """
            fn main(): int {
                var i = 0;
                var acc = 1;
                while (i < {N}) {
                    acc = (acc + 1) & 1023;
                    i = i + 1;
                }
                return if (acc >= 0) 0 else 1;
            }
            """);

        // What a crossing into the host costs, measured the same way as everything else here, so
        // it can be compared against a count of dispatches. 'sqrt' is a native whose own work is
        // a single machine instruction, which leaves the boundary.
        yield return new InterpreterCase("nativeSqrt", """
            import std.math { sqrt };

            fn main(): int {
                var acc = 0.0;
                var i = 0;
                while (i < {N}) {
                    acc = acc + sqrt(2.0);
                    i = i + 1;
                }
                return if (acc > 0.0) 0 else 1;
            }
            """);

        // Not a shape but a CALL, and the one Erato measured as its most expensive line: two
        // random numbers cost it more than five boundary crossings. What it measures here is a
        // standard-library function written in Lyric — a xorshift round is six shift-and-xor
        // steps, and every one of them is instructions.
        yield return new InterpreterCase("randomFloat", """
            import std.random { Random };

            fn main(): int {
                var r = Random.seeded(1234);
                var acc = 0.0;
                var i = 0;
                while (i < {N}) {
                    acc = acc + r.nextFloat();
                    i = i + 1;
                }
                return if (acc > 0.0) 0 else 1;
            }
            """);

        // The one case that touches memory, and the only one carrying a bounds check.
        yield return new InterpreterCase("arrayRead", """
            fn main(): int {
                let xs = [1] * 256;
                var i = 0;
                var acc = 0;
                while (i < {N}) {
                    acc = acc + xs[i & 255];
                    i = i + 1;
                }
                return if (acc > 0) 0 else 1;
            }
            """);
    }
}
