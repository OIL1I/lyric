using Lyric.Core;
using Lyric.Ir;

namespace Lyric.Tests.Ir;

/// <summary>
/// Nicht-Golden-Tests des <see cref="IrPrinter"/>: Eigenschaften, die kein Snapshot
/// abdeckt (Determinismus, Fehlerpfade, Standalone-Auflösung).
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
        var block = new IrBlock(new BlockId(0), new List<IrOp>()); // Terminator bleibt null
        var func = new IrFunction("main.broken", new IrScalarType(IrScalar.Void), 0,
            new List<IrLocal>(), new List<IrTemp>(), new List<IrBlock> { block });
        var module = new IrModule(new List<IrFunction> { func });

        Assert.Throws<InternalCompilationException>(() => IrPrinter.Dump(module));
    }

    [Fact]
    public void Standalone_function_dump_leaves_call_unresolved()
    {
        // main.main ist f2 im Modul; standalone gedumpt fehlt der Kontext → Ziel bleibt der
        // rohe Index f0 und der Dest-Typ des Calls ist '?'.
        var main = Fixtures.Build("two_functions_call").Functions[2];
        var dump = IrPrinter.Dump(main);

        Assert.Contains("t1: ? = call f0(t0)", dump);
        Assert.Contains("call f1(t1)", dump);
    }
}
