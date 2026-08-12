using Lyric.Bytecode;

namespace Lyric.Vm;

/// <summary>
/// The vtables, built once at load time from the Impls section.
///
/// <para>A <c>callvirt</c> knows the interface and the slot statically and the concrete type
/// dynamically, carried by the receiver. The lookup is therefore
/// (concrete type, interface) → function list, and this class makes it an array access.</para>
///
/// <para>A dense matrix over (type index × interface index): the loader has already validated
/// that every <c>mkiface</c> has an Impls row and every slot is in range, so the call path
/// neither checks nor hashes.</para>
/// </summary>
internal sealed class DispatchTable
{
    /// <summary>Row = concrete type, column = interface, cell = function index per slot.
    /// <c>null</c> means the type does not implement the interface; the loader rules out a
    /// <c>callvirt</c> ever landing there.</summary>
    private readonly int[]?[,] _rows;

    /// <summary>Row = interface, column = slot, cell = argument count including the receiver.
    ///
    /// <para>The arity lives here because the interpreter must take the receiver off the stack
    /// before it knows the target function — it needs the receiver's concrete type to find it.
    /// Every implementation of a slot shares its signature, so one number suffices.</para>
    /// </summary>
    private readonly int[]?[] _arity;

    private DispatchTable(int[]?[,] rows, int[]?[] arity)
    {
        _rows = rows;
        _arity = arity;
    }

    public static DispatchTable Build(BytecodeModule module)
    {
        var size = module.Types.Count;
        var rows = new int[]?[size, size];
        var arity = new int[]?[size];

        foreach (var impl in module.Impls)
        {
            var methods = new int[impl.Methods.Count];
            for (var i = 0; i < methods.Length; i++) methods[i] = impl.Methods[i];
            rows[impl.Type, impl.Interface] = methods;

            // The first row of an interface fixes the signatures; every further row matches.
            if (arity[impl.Interface] is not null) continue;

            var counts = new int[impl.Methods.Count];
            for (var i = 0; i < counts.Length; i++)
            {
                var target = impl.Methods[i];
                counts[i] = target < module.Imports.Count
                    ? module.Imports[target].ParamTypes.Count
                    : module.Functions[target - module.Imports.Count].ParamCount;
            }

            arity[impl.Interface] = counts;
        }

        return new DispatchTable(rows, arity);
    }

    /// <summary>How many values the call takes off the stack, receiver included.</summary>
    public int ArityOf(int interfaceType, int slot)
    {
        if (interfaceType >= 0 && interfaceType < _arity.Length
            && _arity[interfaceType] is { } counts && slot >= 0 && slot < counts.Length)
            return counts[slot];

        throw new LyricRuntimeException(VmDiagnostics.NoImplementation,
            $"interface {interfaceType} has no implementation at all, so slot {slot} has no "
            + "signature — the module was not validated at load time");
    }

    /// <summary>
    /// The function index in the shared space (imports first, then functions).
    ///
    /// <para>Throws only when load-time validation was bypassed, which the message says.</para>
    /// </summary>
    public int Resolve(int concreteType, int interfaceType, int slot)
    {
        if (concreteType >= 0 && concreteType < _rows.GetLength(0)
            && interfaceType >= 0 && interfaceType < _rows.GetLength(1)
            && _rows[concreteType, interfaceType] is { } methods
            && slot >= 0 && slot < methods.Length)
            return methods[slot];

        throw new LyricRuntimeException(VmDiagnostics.NoImplementation,
            $"no implementation for slot {slot} of interface {interfaceType} on type "
            + $"{concreteType} — the module was not validated at load time");
    }
}
