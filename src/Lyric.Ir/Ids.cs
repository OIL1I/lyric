namespace Lyric.Ir
{
    public readonly record struct BlockId(int Value)
    {
        public override string ToString()
        {
            return $"bb{Value}";
        }

    }

    public readonly record struct TempId(int Value)
    {
        public override string ToString()
        {
            return $"t{Value}";
        }
    }

    public readonly record struct LocalId(int Value)
    {
        public override string ToString()
        {
            return $"l{Value}";
        }
    }
    public readonly record struct FunctionId(int Value)
    {
        public override string ToString()
        {
            return $"f{Value}";
        }
    }
}