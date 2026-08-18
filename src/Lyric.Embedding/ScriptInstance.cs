using Lyric.Bytecode;
using Lyric.Vm;

namespace Lyric.Embedding;

/// <summary>
/// A loaded, initialized script the host calls functions on.
///
/// <para>An instance holds the state. The module-constant initializer runs exactly once when it
/// is created, and what it leaves behind survives every call. Two instances of the same module
/// share nothing, so a VM can hold several scripts at once.</para>
/// </summary>
public sealed class ScriptInstance
{
    private readonly LangVm _vm;
    private readonly LoadedProgram _program;
    private readonly string _prefix;

    internal ScriptInstance(LangVm vm, ScriptModule module, LoadedProgram program)
    {
        _vm = vm;
        Module = module;
        _program = program;
        _prefix = module.Name + ".";
    }

    /// <summary>The module this instance came from.</summary>
    public ScriptModule Module { get; }

    /// <summary>
    /// Reads the source file again, compiles it and returns a new instance.
    ///
    /// <para>The old instance stays valid: if the compilation fails this call throws and the host
    /// keeps what it has. The module constants are recomputed, because a new instance is new
    /// state. Host objects survive, because they belong to the garbage collector rather than to
    /// the instance.</para>
    ///
    /// <para>The host swaps its own reference: <c>instance = instance.Reload();</c>.</para>
    /// </summary>
    /// <exception cref="ScriptException">The module did not come from disk, so there is nothing to
    /// re-read.</exception>
    /// <exception cref="EmbeddingException">The new version does not compile. This instance stays
    /// usable.</exception>
    public ScriptInstance Reload()
    {
        if (Module.Origin is not { } path)
            throw new ScriptException("LYR-EMB0008",
                $"'{Module.Name}' was compiled from memory — there is no file to reload", null);

        return _vm.Instantiate(_vm.CompileFile(path));
    }

    /// <summary>Does this script define a <c>pub</c> function of that name?</summary>
    public bool Defines(string function) => _program.IndexOfFunction(_prefix + function) >= 0;

    /// <summary>Calls a function of the script and returns its result.</summary>
    /// <param name="function">The unqualified name. The module name is prepended, so a call to
    /// <c>length</c> cannot reach <c>std.string.length</c>, which is linked into the same
    /// module.</param>
    /// <exception cref="ScriptException">No such function, wrong arity, or a value that does not
    /// cross the boundary.</exception>
    /// <exception cref="ScriptPanicException">The script panicked.</exception>
    public TResult Call<TResult>(string function, params object?[] arguments)
    {
        var (index, signature) = Resolve(function, arguments.Length);
        var marshalled = MarshalArguments(function, signature, arguments);

        var produced = Invoke(index, marshalled);
        return Marshal.FromLyric<TResult>(produced, signature.ReturnType,
            $"the result of '{function}'");
    }

    /// <summary>As <see cref="Call{TResult}"/>, for a function that returns nothing.
    ///
    /// <para>Separate because <c>Call&lt;void&gt;</c> cannot be written in C#.</para></summary>
    public void CallVoid(string function, params object?[] arguments)
    {
        var (index, signature) = Resolve(function, arguments.Length);
        Invoke(index, MarshalArguments(function, signature, arguments));
    }

    /// <summary>The attribute rows of this script's module. The same answer as
    /// <see cref="ScriptModule.Attributes"/>, reachable from the instance a host holds.</summary>
    public ModuleAttributes Attributes => Module.Attributes;

    /// <summary>
    /// Calls the function an attribute row names. The use IS the handle: it carries the function
    /// index, so nothing is looked up by name — the path a host takes after enumerating
    /// <c>Attributes.OnFunctions("System")</c> once.
    /// </summary>
    /// <exception cref="ScriptException">The use does not name a function, or the arity does not
    /// match.</exception>
    public TResult Call<TResult>(AttributeUse target, params object?[] arguments)
    {
        var (index, signature) = Resolve(target, arguments.Length);
        var produced = Invoke(index, MarshalArguments(target.TargetName, signature, arguments));
        return Marshal.FromLyric<TResult>(produced, signature.ReturnType,
            $"the result of '{target.TargetName}'");
    }

    /// <inheritdoc cref="Call{TResult}(AttributeUse, object?[])"/>
    public void CallVoid(AttributeUse target, params object?[] arguments)
    {
        var (index, signature) = Resolve(target, arguments.Length);
        Invoke(index, MarshalArguments(target.TargetName, signature, arguments));
    }

    private (int Index, BytecodeFunction Signature) Resolve(AttributeUse target, int argumentCount)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target.TargetKind != AttributeTargetKind.Function)
            throw new ScriptException("LYR-EMB0006",
                $"'@{target.Attribute}' sits on {(target.TargetKind == AttributeTargetKind.Module ? "the module" : $"type '{target.TargetName}'")} — there is no function to call", null);

        var signature = _program.Module.Functions[target.Target];
        if (signature.ParamCount != argumentCount)
            throw new ScriptException("LYR-EMB0007",
                $"'{target.TargetName}' takes {signature.ParamCount} argument(s), got {argumentCount}",
                null);

        return (target.Target, signature);
    }

    private (int Index, BytecodeFunction Signature) Resolve(string function, int argumentCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(function);

        var index = _program.IndexOfFunction(_prefix + function);
        if (index < 0)
            throw new ScriptException("LYR-EMB0006",
                $"'{Module.Name}' has no function '{function}'", null);

        var signature = _program.Module.Functions[index];
        if (signature.ParamCount != argumentCount)
            throw new ScriptException("LYR-EMB0007",
                $"'{function}' takes {signature.ParamCount} argument(s), got {argumentCount}",
                null);

        return (index, signature);
    }

    private static LyrValue[] MarshalArguments(string function, BytecodeFunction signature,
        object?[] arguments)
    {
        var values = new LyrValue[arguments.Length];
        for (var i = 0; i < arguments.Length; i++)
            values[i] = Marshal.ToLyric(arguments[i], signature.SlotTypes[i],
                $"argument {i + 1} of '{function}'");
        return values;
    }

    private LyrValue Invoke(int index, LyrValue[] arguments)
    {
        try
        {
            return _program.Invoke(index, arguments);
        }
        catch (LyricPanic panic)
        {
            throw new ScriptPanicException(panic.Code, panic.Message, panic);
        }
        catch (LyricRuntimeException runtime)
        {
            throw new ScriptException(runtime.Code, runtime.Message, runtime);
        }
    }
}
