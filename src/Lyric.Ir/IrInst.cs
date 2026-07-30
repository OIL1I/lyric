using Lyric.Core;

namespace Lyric.Ir;

//Ir Consts
public abstract record IrConstValue;
public sealed record IntConst(ulong Value) : IrConstValue;
public sealed record FloatConst(double Value) : IrConstValue;
public sealed record BoolConst(bool Value) : IrConstValue;
public sealed record CharConst(int CodePoint) : IrConstValue;
public sealed record StringConst(string Value) : IrConstValue;

public abstract record IrInst(Span Span);
public abstract record IrOp(Span Span) : IrInst(Span);
public abstract record IrTerminator(Span Span) : IrInst(Span);

//Ir Op
public sealed record Const(TempId Dest, IrType Type, IrConstValue Value, Span Span) : IrOp(Span);
public sealed record BinOp(TempId Dest, IrBinKind Kind, IrType Type, TempId Lhs, TempId Rhs, Span Span) : IrOp(Span);
public sealed record UnOp(TempId Dest, IrUnKind Kind, IrType Type, TempId Operand, Span Span) : IrOp(Span);
public sealed record Convert(TempId Dest, IrType From, IrType To, TempId Operand, Span Span) : IrOp(Span);
public sealed record LoadLocal(TempId Dest, LocalId Local, IrType Type, Span Span) : IrOp(Span);
public sealed record StoreLocal(LocalId Local, TempId Value, Span Span) : IrOp(Span);
public sealed record Call(TempId? Dest, FunctionId Target, TempId[] Args, Span Span) : IrOp(Span); // Dest == null -> gwd. void

//Ir Terminator
public sealed record Return(TempId? Value, Span Span) : IrTerminator(Span); //Value == null -> void-return
public sealed record Branch(BlockId Target, Span Span) : IrTerminator(Span);
public sealed record CondBranch(TempId Cond, BlockId IfTrue, BlockId IfFalse, Span Span) : IrTerminator(Span);
public sealed record Unreachable(Span Span) : IrTerminator(Span);