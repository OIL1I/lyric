using Lyric.Bytecode;
using Lyric.Core;

namespace Lyric.Vm;

/// <summary>
/// A loaded, bound and initialized module, ready to be called more than once.
///
/// <para><see cref="Interpreter.Run"/> executes a program and is then finished. This form keeps
/// its globals: the initializer runs once, and what it leaves behind survives every call.</para>
///
/// <para>An instance is the state. Two <see cref="LoadedProgram"/>s of the same module share
/// nothing.</para>
/// </summary>
public sealed class LoadedProgram
{
    private readonly BytecodeModule _module;
    private readonly Interpreter.Prepared[] _prepared;
    private readonly DispatchTable _dispatch;
    private readonly NativeRegistry.BoundNative[] _natives;
    private readonly LyrValue[] _globals;

    private LoadedProgram(BytecodeModule module, Interpreter.Prepared[] prepared,
        DispatchTable dispatch, NativeRegistry.BoundNative[] natives, LyrValue[] globals)
    {
        _module = module;
        _prepared = prepared;
        _dispatch = dispatch;
        _natives = natives;
        _globals = globals;
    }

    /// <summary>The module this instance came from, for name and signature lookups.</summary>
    public BytecodeModule Module => _module;

    /// <summary>Loads, binds, initializes.</summary>
    /// <exception cref="LyricRuntimeException">A missing capability, or an import that cannot be
    /// bound.</exception>
    public static LoadedProgram Load(BytecodeModule module, NativeRegistry? natives = null,
        Capability granted = Capability.All)
    {
        // First of all: a module requiring more than this VM grants never starts. The requirement
        // is recorded in the module, so a host loading foreign bytes checks the same thing.
        var missing = module.Capabilities & ~(ulong)granted;
        if (missing != 0)
            throw new LyricRuntimeException(VmDiagnostics.CapabilityDenied,
                $"module requires capability '{CapabilityTable.Describe((Capability)missing)}', "
                + "which this runtime does not grant");

        var prepared = new Interpreter.Prepared[module.Functions.Count];
        for (var i = 0; i < prepared.Length; i++)
            prepared[i] = Interpreter.Prepared.From(module.Functions[i],
                module.Handlers.Where(h => h.Function == i).ToArray());

        // Bound at load time: a missing native rejects the module before an instruction runs.
        var bound = (natives ?? new NativeRegistry()).Bind(module);
        var dispatch = DispatchTable.Build(module);

        // A string slot starts as the empty string rather than an empty reference, the same rule
        // as for object fields.
        var globals = new LyrValue[module.Globals.Count];
        for (var i = 0; i < globals.Length; i++)
            if (module.Globals[i].Tag == TypeTag.String) globals[i] = LyrValue.FromString(string.Empty);

        var program = new LoadedProgram(module, prepared, dispatch, bound, globals);

        // The initializer runs before everything else and exactly once. It is void; what counts
        // are the slots it leaves behind.
        if (module.GlobalInit is { } init && init >= module.Imports.Count)
            program.Execute(init - module.Imports.Count);

        return program;
    }

    /// <summary>Does this module have an entry point?</summary>
    public bool HasEntryPoint => _module.Start is not null;

    /// <summary>Runs <c>main</c> and returns its value.</summary>
    /// <exception cref="LyricRuntimeException">No entry point.</exception>
    public LyrValue RunEntry(IReadOnlyList<string> arguments)
    {
        if (_module.Start is not { } start)
            throw new LyricRuntimeException(VmDiagnostics.NoEntryPoint,
                "module has no start section — it is a library, not a program");

        // Start indexes the shared space (imports first, then functions); '_prepared' holds only
        // the defined functions. An entry point inside the import range cannot be executed.
        var entry = start - _module.Imports.Count;
        if (entry < 0)
            throw new LyricRuntimeException(VmDiagnostics.NoEntryPoint,
                $"start index {start} points into the import table — an entry point must be a "
                + "function defined in this module");

        // Two entry-point forms; which one is present is read from the signature in the function
        // table. The loader has already checked that a parameter is a 'string[]'.
        LyrValue[] entryArgs = _module.Functions[entry].ParamCount == 0
            ? []
            : [Interpreter.ArgumentArray(arguments)];

        return Execute(entry, entryArgs);
    }

    /// <summary>
    /// Finds a defined function by its fully qualified name (<c>&lt;module&gt;.&lt;name&gt;</c>),
    /// or <c>-1</c>.
    ///
    /// <para>Fully qualified because the function table also holds everything pulled in from the
    /// standard library, where a bare <c>length</c> would be ambiguous.</para>
    /// </summary>
    public int IndexOfFunction(string qualifiedName)
    {
        for (var i = 0; i < _module.Functions.Count; i++)
            if (string.Equals(_module.Functions[i].Name, qualifiedName, StringComparison.Ordinal))
                return i;
        return -1;
    }

    /// <summary>Runs the function at <paramref name="index"/>. The arguments go into the
    /// parameter slots; the caller checks arity and types against the function table.</summary>
    public LyrValue Invoke(int index, params LyrValue[] arguments) => Execute(index, arguments);

    private LyrValue Execute(int index, LyrValue[]? arguments = null) =>
        Interpreter.Execute(_prepared, index, _module.Strings, _module.Types, _dispatch,
            _natives, _globals, arguments);
}
