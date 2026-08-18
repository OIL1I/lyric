using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Lyric.Embedding;

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
/// </summary>
internal static class Program
{
    private const int Repetitions = 9;

    /// <param name="Baseline">The case whose per-op figures are subtracted, so the reported
    /// number names the operation rather than the loop around it.</param>
    private sealed record Case(string Name, int Ops, string? Baseline, string Source);

    private sealed record Result(double NsPerOp, double BytesPerOp);

    public static int Main(string[] args)
    {
        var filter = args.Length > 0 ? args[0] : null;
        var stdlib = Path.Combine(RepoRoot(), "stdlib");

        Console.WriteLine($"config: {Configuration()}, ops per case: 100000, " +
                          $"repetitions: {Repetitions}, figures are minima");
        Console.WriteLine();
        Console.WriteLine("| case | ns/op | B/op | ns/op adj. | B/op adj. |");
        Console.WriteLine("|---|---:|---:|---:|---:|");

        // One warm pass over everything first: the interpreter loop is one shared method, and
        // without this the first case pays its tiering for all of them — measured as a negative
        // call cost before this pass existed.
        var cases = Cases().ToList();
        foreach (var c in cases)
        {
            var vm = new LangVm(new HostOptions { StdlibRoot = stdlib });
            Require(vm.Run(vm.Compile(c.Source, "bench")), c.Name);
        }

        var results = new Dictionary<string, Result>(StringComparer.Ordinal);
        foreach (var c in cases)
        {
            var result = Measure(c, stdlib);
            results[c.Name] = result;

            if (filter is not null && !c.Name.Contains(filter, StringComparison.Ordinal))
                continue;

            var baseline = c.Baseline is null ? new Result(0, 0) : results[c.Baseline];
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"| {c.Name} | {result.NsPerOp:F1} | {result.BytesPerOp:F1} " +
                $"| {result.NsPerOp - baseline.NsPerOp:F1} " +
                $"| {result.BytesPerOp - baseline.BytesPerOp:F1} |"));
        }

        return 0;
    }

    private static Result Measure(Case c, string stdlib)
    {
        var vm = new LangVm(new HostOptions { StdlibRoot = stdlib });
        var module = vm.Compile(c.Source, "bench");

        Require(vm.Run(module), c.Name);
        Require(vm.Run(module), c.Name);

        var bestTicks = long.MaxValue;
        var bestBytes = long.MaxValue;
        for (var i = 0; i < Repetitions; i++)
        {
            var bytesBefore = GC.GetAllocatedBytesForCurrentThread();
            var ticksBefore = Stopwatch.GetTimestamp();
            var exit = vm.Run(module);
            var ticks = Stopwatch.GetTimestamp() - ticksBefore;
            var bytes = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;

            Require(exit, c.Name);
            bestTicks = Math.Min(bestTicks, ticks);
            bestBytes = Math.Min(bestBytes, bytes);
        }

        var nanos = bestTicks * (1_000_000_000.0 / Stopwatch.Frequency);
        return new Result(nanos / c.Ops, (double)bestBytes / c.Ops);
    }

    private static void Require(int exit, string name)
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

        // The callvirt route: iter() answers with the interface, so every next() dispatches
        // dynamically. The setup (1000 adds) is inside the measurement and constant across
        // toolchain versions; the figure is a gate, not an absolute.
        yield return new Case("set_iter", 100_000, "while_range", """
            import std.collections { emptySet };

            fn main(): int {
                var s = emptySet<int>();
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
}
