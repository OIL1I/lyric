using Lyric.AST;
using Lyric.Core;

namespace Lyric.Ir
{
    public enum IrBinKind
    {
        Add, Sub, Mul, Div, Rem,
        Shl, Shr, BitAnd, BitOr, BitXor,
        Lt, Le, Gt, Ge, Eq, Ne
    }

    public static class IrBinKindExtensions
    {
        public static IrBinKind FromAst(BinaryOp op) => op switch
        {
            BinaryOp.Add => IrBinKind.Add,
            BinaryOp.Sub => IrBinKind.Sub,
            BinaryOp.Mul => IrBinKind.Mul,
            BinaryOp.Div => IrBinKind.Div,
            BinaryOp.Rem => IrBinKind.Rem,
            BinaryOp.Shl => IrBinKind.Shl,
            BinaryOp.Shr => IrBinKind.Shr,
            BinaryOp.BitAnd => IrBinKind.BitAnd,
            BinaryOp.BitOr => IrBinKind.BitOr,
            BinaryOp.BitXor => IrBinKind.BitXor,
            BinaryOp.Lt => IrBinKind.Lt,
            BinaryOp.Le => IrBinKind.Le,
            BinaryOp.Gt => IrBinKind.Gt,
            BinaryOp.Ge => IrBinKind.Ge,
            BinaryOp.Eq => IrBinKind.Eq,
            BinaryOp.Ne => IrBinKind.Ne,
            _ => throw new InternalCompilationException("unreachable: control-flow op in BinOp")
        };

        public static bool IsComparison(this IrBinKind kind) => kind switch
        {
            IrBinKind.Lt => true,
            IrBinKind.Le => true,
            IrBinKind.Gt => true,
            IrBinKind.Ge => true,
            IrBinKind.Eq => true,
            IrBinKind.Ne => true,
            _ => false
        };
    }

    public enum IrUnKind
    {
        Neg, Not, BitNot
    }

    public static class IrUnKindExtensions
    {
        public static IrUnKind FromAst(UnaryOp op) => op switch
        {
            UnaryOp.Neg => IrUnKind.Neg,
            UnaryOp.Not => IrUnKind.Not,
            UnaryOp.BitNot => IrUnKind.BitNot,
            _ => throw new InternalCompilationException("unreachable: inc/dec is not a UnOp")
        };
    }
}