using Lyric.Core;
using Lyric.Ir;

namespace Lyric.Tests.Ir;

/// <summary>
/// Non-golden tests of the <see cref="IrPrinter"/>: properties no snapshot covers — determinism, error
/// paths, standalone resolution.
/// </summary>
public class PrinterTests
{
    [Fact]
    public void Dump_is_deterministic()
    {
        var module = Fixtures.Build("two_functions_call");
        Assert.Equal(IrPrinter.Dump(module), IrPrinter.Dump(module));
    }

    [Fact]
    public void Block_without_terminator_throws()
    {
        var block = new IrBlock(new BlockId(0), new List<IrOp>()); // the terminator stays null
        var func = new IrFunction("main.broken", new IrScalarType(IrScalar.Void), 0,
            new List<IrLocal>(), new List<IrTemp>(), new List<IrBlock> { block });
        var module = new IrModule(new List<IrFunction> { func });

        Assert.Throws<InternalCompilationException>(() => IrPrinter.Dump(module));
    }

    [Fact]
    public void Standalone_function_dump_leaves_call_unresolved()
    {
        // main.main is f2 in the module; dumped standalone the context is missing, so the target stays the
        // raw index f0 and the destination type of the call is '?'.
        var main = Fixtures.Build("two_functions_call").Functions[2];
        var dump = IrPrinter.Dump(main);

        Assert.Contains("t1: ? = call f0(t0)", dump);
        Assert.Contains("call f1(t1)", dump);
    }
}
