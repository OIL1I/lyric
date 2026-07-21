using Lyric.Core;
using Lyric.Lexing;

namespace Lyric.AST
{
    public enum BinaryOp
    {
        Add, Sub, Mul, Div, Rem,
        Shl, Shr, BitAnd, BitXor, BitOr,
        Lt, Le, Gt, Ge, Eq, Ne,
        LogicalAnd, LogicalOr,
        Coalesce
    }

    public enum UnaryOp
    {
        Not, Neg, BitNot, PreInc, PreDec
    }

    public enum PostfixOp
    {
        Inc, Dec, ForceUnwrap
    }

    public static class Operators
    {
        public static BinaryOp MapBinary(TokenKind op) => op switch
        {
            TokenKind.Plus => BinaryOp.Add,
            TokenKind.Minus => BinaryOp.Sub,
            TokenKind.Star => BinaryOp.Mul,
            TokenKind.Slash => BinaryOp.Div,
            TokenKind.Percent => BinaryOp.Rem,
            TokenKind.Shl => BinaryOp.Shl,
            TokenKind.Shr => BinaryOp.Shr,
            TokenKind.Amp => BinaryOp.BitAnd,
            TokenKind.Caret => BinaryOp.BitXor,
            TokenKind.Pipe => BinaryOp.BitOr,
            TokenKind.Less => BinaryOp.Lt,
            TokenKind.LessEqual => BinaryOp.Le,
            TokenKind.Greater => BinaryOp.Gt,
            TokenKind.GreaterEqual => BinaryOp.Ge,
            TokenKind.EqualEqual => BinaryOp.Eq,
            TokenKind.ExclamationEqual => BinaryOp.Ne,
            TokenKind.AmpAmp => BinaryOp.LogicalAnd,
            TokenKind.PipePipe => BinaryOp.LogicalOr,
            TokenKind.QuestionQuestion => BinaryOp.Coalesce,
            _ => throw new InternalCompilationException($"unreachable: unexpected {op.ToString()}")

        };

        public static bool TryMapAssign(TokenKind op, out BinaryOp? binOp)
        {
            switch (op)
            {
                case TokenKind.PlusEqual: binOp = BinaryOp.Add; return true;
                case TokenKind.MinusEqual: binOp = BinaryOp.Sub; return true;
                case TokenKind.StarEqual: binOp = BinaryOp.Mul; return true;
                case TokenKind.SlashEqual: binOp = BinaryOp.Div; return true;
                case TokenKind.PercentEqual: binOp = BinaryOp.Rem; return true;
                case TokenKind.ShlEqual: binOp = BinaryOp.Shl; return true;
                case TokenKind.ShrEqual: binOp = BinaryOp.Shr; return true;
                case TokenKind.AmpEqual: binOp = BinaryOp.BitAnd; return true;
                case TokenKind.CaretEqual: binOp = BinaryOp.BitXor; return true;
                case TokenKind.PipeEqual: binOp = BinaryOp.BitOr; return true;
                case TokenKind.AmpAmpEqual: binOp = BinaryOp.LogicalAnd; return true;
                case TokenKind.PipePipeEqual: binOp = BinaryOp.LogicalOr; return true;
                case TokenKind.QuestionQuestionEqual: binOp = BinaryOp.Coalesce; return true;
                case TokenKind.Equal: binOp = null; return true;
                default: binOp = null; return false;
            }
        }

        public static UnaryOp MapPrefix(TokenKind op) => op switch
        {
            TokenKind.Exclamation => UnaryOp.Not,
            TokenKind.Minus => UnaryOp.Neg,
            TokenKind.Tilde => UnaryOp.BitNot,
            TokenKind.Inc => UnaryOp.PreInc,
            TokenKind.Dec => UnaryOp.PreDec,
            _ => throw new InternalCompilationException($"unreachable: unexpected {op.ToString()}")
        };

        public static PostfixOp MapPostfix(TokenKind op) => op switch
        {
            TokenKind.Inc => PostfixOp.Inc,
            TokenKind.Dec => PostfixOp.Dec,
            TokenKind.Exclamation => PostfixOp.ForceUnwrap,
            _ => throw new InternalCompilationException($"unreachable: unexpected {op.ToString()}")
        };
    }
}
