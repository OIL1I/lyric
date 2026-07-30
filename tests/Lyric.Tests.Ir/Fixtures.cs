using Lyric.Core;
using Lyric.Ir;

namespace Lyric.Tests.Ir;

/// <summary>
/// Handgebautes IR für die Printer-Tests. P2 hat noch kein Lowering, also werden die
/// IR-Objekte hier direkt konstruiert. Spans sind irrelevant (der Printer druckt sie
/// nicht) und durchweg <c>default</c>. Temps sind der Vollständigkeit halber typkorrekt
/// befüllt, auch wenn der Printer sie (noch) nicht konsumiert — so bleiben die Fixtures
/// gültiges IR für den P3-Verifier.
/// </summary>
internal static class Fixtures
{
    private static readonly IrType I32 = new IrScalarType(IrScalar.I32);
    private static readonly IrType I64 = new IrScalarType(IrScalar.I64);
    private static readonly IrType Bool = new IrScalarType(IrScalar.Bool);
    private static readonly IrType Void = new IrScalarType(IrScalar.Void);
    private static readonly Span Sp = default;

    private static TempId T(int n) => new(n);
    private static LocalId L(int n) => new(n);
    private static BlockId B(int n) => new(n);
    private static FunctionId F(int n) => new(n);

    private static IrFunction Fn(string name, IrType ret, int paramCount,
        List<IrLocal> locals, List<IrTemp> temps, List<IrBlock> blocks)
        => new(name, ret, paramCount, locals, temps, blocks) { Entry = B(0) };

    private static IrBlock Block(int id, List<IrOp> insts, IrTerminator term)
        => new(B(id), insts) { Terminator = term };

    public static IrModule Build(string name) => name switch
    {
        "single_block" => SingleBlock(),
        "comparison" => Comparison(),
        "diamond" => Diamond(),
        "void_store" => VoidStore(),
        "convert" => ConvertWiden(),
        "two_functions_call" => TwoFunctionsCall(),
        _ => throw new ArgumentException($"unknown fixture: {name}")
    };

    // fn main.add(a, b) -> i64  { return a + b; }
    private static IrModule SingleBlock()
    {
        var locals = new List<IrLocal> { new(L(0), "a", I64), new(L(1), "b", I64) };
        var temps = new List<IrTemp> { new(T(0), I64), new(T(1), I64), new(T(2), I64) };
        var bb0 = Block(0,
            new List<IrOp>
            {
                new LoadLocal(T(0), L(0), I64, Sp),
                new LoadLocal(T(1), L(1), I64, Sp),
                new BinOp(T(2), IrBinKind.Add, I64, T(0), T(1), Sp),
            },
            new Return(T(2), Sp));
        return new IrModule(new List<IrFunction> { Fn("main.add", I64, 2, locals, temps, new List<IrBlock> { bb0 }) });
    }

    // fn main.isNeg(x) -> bool  { return x < 0; }   — Dest-Typ bool, Operanden i64
    private static IrModule Comparison()
    {
        var locals = new List<IrLocal> { new(L(0), "x", I64) };
        var temps = new List<IrTemp> { new(T(0), I64), new(T(1), I64), new(T(2), Bool) };
        var bb0 = Block(0,
            new List<IrOp>
            {
                new LoadLocal(T(0), L(0), I64, Sp),
                new Const(T(1), I64, new IntConst(0), Sp),
                new BinOp(T(2), IrBinKind.Lt, Bool, T(0), T(1), Sp),
            },
            new Return(T(2), Sp));
        return new IrModule(new List<IrFunction> { Fn("main.isNeg", Bool, 1, locals, temps, new List<IrBlock> { bb0 }) });
    }

    // fn main.clamp(x) -> i64  { r = x < 0 ? 0 : x; return r; }  — condbr/br/store, 4 Blöcke
    private static IrModule Diamond()
    {
        var locals = new List<IrLocal> { new(L(0), "x", I64), new(L(1), "r", I64) };
        var temps = new List<IrTemp> { new(T(0), I64), new(T(1), I64), new(T(2), Bool), new(T(3), I64) };
        var bb0 = Block(0,
            new List<IrOp>
            {
                new LoadLocal(T(0), L(0), I64, Sp),
                new Const(T(1), I64, new IntConst(0), Sp),
                new BinOp(T(2), IrBinKind.Lt, Bool, T(0), T(1), Sp),
            },
            new CondBranch(T(2), B(1), B(2), Sp));
        var bb1 = Block(1, new List<IrOp> { new StoreLocal(L(1), T(1), Sp) }, new Branch(B(3), Sp));
        var bb2 = Block(2, new List<IrOp> { new StoreLocal(L(1), T(0), Sp) }, new Branch(B(3), Sp));
        var bb3 = Block(3, new List<IrOp> { new LoadLocal(T(3), L(1), I64, Sp) }, new Return(T(3), Sp));
        return new IrModule(new List<IrFunction>
        {
            Fn("main.clamp", I64, 1, locals, temps, new List<IrBlock> { bb0, bb1, bb2, bb3 })
        });
    }

    // fn main.reset() -> void  { count = 0; }  — nacktes ret + dest-loses store
    private static IrModule VoidStore()
    {
        var locals = new List<IrLocal> { new(L(0), "count", I64) };
        var temps = new List<IrTemp> { new(T(0), I64) };
        var bb0 = Block(0,
            new List<IrOp>
            {
                new Const(T(0), I64, new IntConst(0), Sp),
                new StoreLocal(L(0), T(0), Sp),
            },
            new Return(null, Sp));
        return new IrModule(new List<IrFunction> { Fn("main.reset", Void, 0, locals, temps, new List<IrBlock> { bb0 }) });
    }

    // fn main.widen(x: i32) -> i64  { return x as i64; }  — convert mit From/To
    private static IrModule ConvertWiden()
    {
        var locals = new List<IrLocal> { new(L(0), "x", I32) };
        var temps = new List<IrTemp> { new(T(0), I32), new(T(1), I64) };
        var bb0 = Block(0,
            new List<IrOp>
            {
                new LoadLocal(T(0), L(0), I32, Sp),
                new Lyric.Ir.Convert(T(1), I32, I64, T(0), Sp), // qualifiziert: Convert kollidiert mit System.Convert
            },
            new Return(T(1), Sp));
        return new IrModule(new List<IrFunction> { Fn("main.widen", I64, 1, locals, temps, new List<IrBlock> { bb0 }) });
    }

    // Drei Funktionen: double (f0), log/void (f1), main (f2 ruft f0 + f1).
    private static IrModule TwoFunctionsCall()
    {
        // f0: main.double(n) -> i64  { return n * 2; }
        var dLocals = new List<IrLocal> { new(L(0), "n", I64) };
        var dTemps = new List<IrTemp> { new(T(0), I64), new(T(1), I64), new(T(2), I64) };
        var dbb0 = Block(0,
            new List<IrOp>
            {
                new LoadLocal(T(0), L(0), I64, Sp),
                new Const(T(1), I64, new IntConst(2), Sp),
                new BinOp(T(2), IrBinKind.Mul, I64, T(0), T(1), Sp),
            },
            new Return(T(2), Sp));
        var dbl = Fn("main.double", I64, 1, dLocals, dTemps, new List<IrBlock> { dbb0 });

        // f1: main.log(msg) -> void  { }
        var lLocals = new List<IrLocal> { new(L(0), "msg", I64) };
        var lbb0 = Block(0, new List<IrOp>(), new Return(null, Sp));
        var log = Fn("main.log", Void, 1, lLocals, new List<IrTemp>(), new List<IrBlock> { lbb0 });

        // f2: main.main() -> i64  { t0 = 21; t1 = double(t0); log(t1); return t1; }
        var mTemps = new List<IrTemp> { new(T(0), I64), new(T(1), I64) };
        var mbb0 = Block(0,
            new List<IrOp>
            {
                new Const(T(0), I64, new IntConst(21), Sp),
                new Call(T(1), F(0), new[] { T(0) }, Sp), // t1 = call main.double(t0)
                new Call(null, F(1), new[] { T(1) }, Sp), // call main.log(t1)  (void)
            },
            new Return(T(1), Sp));
        var main = Fn("main.main", I64, 0, new List<IrLocal>(), mTemps, new List<IrBlock> { mbb0 });

        return new IrModule(new List<IrFunction> { dbl, log, main });
    }
}
