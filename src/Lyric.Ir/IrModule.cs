namespace Lyric.Ir;

public record struct IrLocal(LocalId Id, string Name, IrType Type);
public record struct IrTemp(TempId Id, IrType Type);

public class IrBlock(BlockId Id, List<IrOp> Insts)
{
    public BlockId Id { get; init; } = Id;
    public List<IrOp> Insts { get; init; } = Insts;
    public IrTerminator? Terminator { get; set; } = null;
}
public class IrFunction(string Name, IrType ReturnType, int ParamCount, List<IrLocal> Locals, List<IrTemp> Temps, List<IrBlock> Blocks)
{
    public string Name { get; init; } = Name;
    public IrType ReturnType { get; init; } = ReturnType;
    public int ParamCount { get; init; } = ParamCount;
    public List<IrLocal> Locals { get; init; } = Locals;
    public List<IrTemp> Temps { get; init; } = Temps;
    public List<IrBlock> Blocks { get; init; } = Blocks;
    public BlockId Entry { get; set; }
}

public class IrModule(List<IrFunction> Functions)
{
    public List<IrFunction> Functions { get; init; } = Functions;
}