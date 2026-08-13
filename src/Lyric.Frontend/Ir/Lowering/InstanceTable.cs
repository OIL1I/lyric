using Lyric.AST;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Ir.Lowering;

/// <summary>
/// The MONOMORPHIZED INSTANCES of generic functions.
///
/// <para>One <see cref="IrFunction"/> per concrete type argument tuple: <c>id&lt;int&gt;</c> and
/// <c>id&lt;string&gt;</c> are two functions, not one with a type parameter. The IR therefore stays
/// FULLY MONOMORPHIC — the verifier, the bytecode format and the VM learn nothing about generics, they
/// only see more functions.</para>
///
/// <para>MONOMORPHIC RATHER THAN GENERIC AT RUNTIME. C# reifies generics and needs a JIT that produces
/// code per instantiation; Java erases them and pays with boxing at every boundary. Both assume the
/// runtime knows types, and a Lyric value carries no type tag. Rust and C++ do the same for the same
/// reason.</para>
///
/// <para>The price is code duplication per instantiation. It is visible and bounded: one instance per
/// type tuple actually used, not per possible one.</para>
/// </summary>
internal sealed class InstanceTable
{
    /// <summary>A requested instance that has not been lowered yet.</summary>
    private readonly record struct Pending(
        FunctionDecl Decl, string Name, FunctionId Id, TypeSymbol? Receiver,
        IReadOnlyDictionary<GenericParamSymbol, LyrType> Substitution,
        GenericInstance? Owner = null);

    private readonly List<Pending> _pending = new();

    /// <summary>What has already been requested, so two calls of <c>id(7)</c> get the same instance
    /// rather than producing two identical functions.</summary>
    private readonly Dictionary<string, FunctionId> _byKey = new(StringComparer.Ordinal);

    /// <summary>How far the lowering has come. The table is drained SEVERAL times — an instance can
    /// request a lambda, a lambda an instance — and without this mark everything would arise anew on
    /// every pass.</summary>
    private int _lowered;

    private readonly FunctionIds _ids;

    public InstanceTable(FunctionIds ids) => _ids = ids;

    public bool IsEmpty => _pending.Count == 0;

    /// <summary>
    /// Requests an instance and returns its id: a new one the first time, the same one afterwards.
    ///
    /// <para>The id is settled immediately although the body is not lowered yet. That is what makes
    /// recursion possible: <c>fn depth&lt;T&gt;(n: int): int { return depth&lt;T&gt;(n - 1); }</c>
    /// requests itself and finds its own id already there.</para>
    /// </summary>
    public FunctionId Request(FunctionSymbol symbol, FunctionDecl decl, string baseName,
        TypeSymbol? receiver, IReadOnlyList<LyrType> typeArguments, TypeTable typeTable,
        Core.Span span)
    {
        if (symbol.Generics.Length != typeArguments.Count)
            throw new UnsupportedConstructException(
                $"call to '{baseName}' supplies {typeArguments.Count} type argument(s), "
                + $"but it declares {symbol.Generics.Length}", span);

        // The name IS the key: it contains the type arguments, is therefore unique, and a human can read
        // off a disassembly which instance is in front of them.
        var name = $"{baseName}<{string.Join(", ", typeArguments.Select(TypeFacts.Display))}>";
        if (_byKey.TryGetValue(name, out var existing)) return existing;

        // A type parameter still open means the inference did not get through at the call site, and then
        // there is no instance that could be built.
        for (var i = 0; i < typeArguments.Count; i++)
            if (typeArguments[i] is TypeParamType or Sema.ErrorType)
                throw new UnsupportedConstructException(
                    $"call to '{baseName}': type argument {i} is not concrete "
                    + $"('{TypeFacts.Display(typeArguments[i])}')", span);

        var substitution = new Dictionary<GenericParamSymbol, LyrType>(
            ReferenceEqualityComparer.Instance);
        for (var i = 0; i < symbol.Generics.Length; i++)
            substitution[symbol.Generics[i]] = typeArguments[i];

        var id = _ids.Next();
        _byKey[name] = id;
        _pending.Add(new Pending(decl, name, id, receiver, substitution));
        return id;
    }

    /// <summary>
    /// Requests a METHOD OF A TYPE INSTANCE: <c>Box&lt;int&gt;.get</c>.
    ///
    /// <para>The substitution comes from the type rather than from the call — <c>get()</c> has no type
    /// parameters of its own, its <c>T</c> is that of <c>Box</c>. Hence a separate request rather than
    /// the same one as for generic functions.</para>
    /// </summary>
    public FunctionId RequestMethod(FunctionSymbol method, FunctionDecl decl,
        GenericInstance owner, Core.Span span)
    {
        var ownerName =
            $"{owner.Definition.Name}<{string.Join(", ", owner.Arguments.Select(TypeFacts.Display))}>";
        var name = $"{ownerName}.{method.Name}";
        if (_byKey.TryGetValue(name, out var existing)) return existing;

        var substitution = new Dictionary<GenericParamSymbol, LyrType>(
            ReferenceEqualityComparer.Instance);
        for (var i = 0; i < owner.Definition.Generics.Length && i < owner.Arguments.Length; i++)
            substitution[owner.Definition.Generics[i]] = owner.Arguments[i];

        var id = _ids.Next();
        _byKey[name] = id;
        // A STATIC method gets no 'this'. 'Owner' stays set all the same: its 'T' is that of the type,
        // even when no receiver brings it along.
        _pending.Add(new Pending(decl, name, id, method.IsStatic ? null : owner.Definition,
            substitution, owner));
        return id;
    }

    /// <summary>
    /// Lowers all requested instances AS A WORKLIST, because an instance can request further ones while
    /// being lowered: <c>id&lt;T&gt;</c> calls <c>wrap&lt;T&gt;</c>, and only here is it settled which
    /// <c>T</c> was meant.
    /// </summary>
    public List<(FunctionId Id, IrFunction Function)> LowerAll(TypeResult types,
        IReadOnlyDictionary<FunctionSymbol, FunctionId> functions, ImportTable imports,
        TypeTable typeTable, GlobalTable globals, LambdaTable lambdas)
    {
        var lowered = new List<(FunctionId, IrFunction)>();

        for (; _lowered < _pending.Count; _lowered++)
        {
            var p = _pending[_lowered];
            lowered.Add((p.Id, new FunctionLowerer(p.Decl, p.Name, types, functions, imports, typeTable,
                p.Substitution, globals, lambdas, this, p.Receiver, p.Owner).Run()));
        }

        return lowered;
    }
}
