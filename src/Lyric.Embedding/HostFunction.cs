using System.Reflection;
using System.Text;
using Lyric.Bytecode;
using Lyric.Vm;

namespace Lyric.Embedding;

/// <summary>
/// A function registered by the host: the Lyric signature, the .NET delegate, and the bridge
/// between them.
///
/// <para>A declaration is generated for it, the same shape the standard library uses: a bodyless
/// <c>pub fn</c> states the signature, <see cref="NativeRegistry"/> supplies the delegate, and the
/// two are bound by name at load time. The generated file lives in memory rather than on disk.
/// </para>
/// </summary>
internal sealed class HostFunction
{
    private readonly Delegate _implementation;
    private readonly BytecodeType[] _parameters;
    private readonly Type[] _parameterTypes;
    private readonly BytecodeType _return;

    private HostFunction(string name, Delegate implementation, BytecodeType[] parameters,
        Type[] parameterTypes, BytecodeType returnType, string declaration)
    {
        Name = name;
        _implementation = implementation;
        _parameters = parameters;
        _parameterTypes = parameterTypes;
        _return = returnType;
        Declaration = declaration;
    }

    public string Name { get; }

    /// <summary>The line that ends up in the synthetic host module.</summary>
    public string Declaration { get; }

    public TypeTag[] ParameterTags => _parameters.Select(p => p.Tag).ToArray();

    public TypeTag ReturnTag => _return.Tag;

    public bool ReturnsValue => _return.Tag != TypeTag.Void;

    /// <summary>Derives the Lyric signature from the .NET delegate.</summary>
    /// <exception cref="ArgumentException">A parameter or return type cannot cross the boundary.
    /// The message names it.</exception>
    /// <summary>A method on a host type: the receiver is parameter 0, and the generated
    /// declaration sits in the class body rather than at module level.</summary>
    public static HostFunction Method(string owner, string name, Delegate implementation,
        bool mutates, IReadOnlyDictionary<Type, string> hostTypes)
    {
        var function = From(name, implementation, hostTypes, skipFirstParameter: true,
            mutates: mutates);

        // The receiver must be the registered type; otherwise the mismatch would only surface at
        // binding time.
        var receiver = implementation.Method.GetParameters().FirstOrDefault();
        if (receiver is null || !hostTypes.TryGetValue(receiver.ParameterType, out var actual)
            || !string.Equals(actual, owner, StringComparison.Ordinal))
            throw new ArgumentException(
                $"host method '{owner}.{name}': the first parameter must be the receiver of type "
                + $"'{owner}'");

        return function;
    }

    public static HostFunction From(string name, Delegate implementation,
        IReadOnlyDictionary<Type, string> hostTypes, bool skipFirstParameter = false,
        bool mutates = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(implementation);

        var method = implementation.Method;
        var parameters = method.GetParameters();

        // The receiver is first in the .NET delegate and absent from the Lyric declaration, where
        // it is 'this'. The type list still carries it: the registry binds against the lowered
        // signature, which has it as parameter 0.
        var declared = skipFirstParameter ? parameters.Skip(1).ToArray() : parameters;

        var tags = new BytecodeType[parameters.Length];
        var text = new StringBuilder("pub ")
            .Append(mutates ? "mut fn " : "fn ").Append(name).Append('(');

        for (var i = 0; i < parameters.Length; i++)
            tags[i] = TypeOf(parameters[i].ParameterType, name, parameters[i].Name ?? $"p{i}",
                hostTypes);

        for (var i = 0; i < declared.Length; i++)
        {
            if (i > 0) text.Append(", ");
            var tag = tags[skipFirstParameter ? i + 1 : i];
            text.Append(declared[i].Name ?? $"p{i}").Append(": ").Append(Marshal.Describe(tag));
        }

        var returnType = method.ReturnType == typeof(void)
            ? BytecodeType.Scalar(TypeTag.Void)
            : TypeOf(method.ReturnType, name, "the return value", hostTypes);

        // The ';,' is intentional: the member separator is written for block bodies, and a
        // bodyless method needs both tokens.
        text.Append("): ").Append(Marshal.Describe(returnType))
            .Append(skipFirstParameter ? ";," : ";");

        return new HostFunction(name, implementation, tags,
            parameters.Select(p => p.ParameterType).ToArray(), returnType, text.ToString());
    }

    /// <summary>The shape <see cref="NativeRegistry"/> expects.</summary>
    public Func<LyrValue[], LyrValue> Bridge => arguments =>
    {
        var boxed = new object?[arguments.Length];
        for (var i = 0; i < arguments.Length; i++)
            boxed[i] = Marshal.FromLyric(arguments[i], _parameters[i], _parameterTypes[i],
                $"argument {i + 1} of host function '{Name}'");

        // DynamicInvoke rather than a typed call: the signature is only known at runtime. A
        // compiled delegate would be an optimization behind the same surface.
        object? produced;
        try
        {
            produced = _implementation.DynamicInvoke(boxed);
        }
        catch (TargetInvocationException wrapped) when (wrapped.InnerException is { } cause)
        {
            // Unwrapped, so a host that throws in its own function gets its own exception type
            // back rather than TargetInvocationException.
            throw new HostFunctionException(Name, cause);
        }

        return _return.Tag == TypeTag.Void
            ? default
            : Marshal.ToLyric(produced, _return, $"the result of host function '{Name}'");
    };

    /// <summary>The full types. A host type is distinguished by its name.</summary>
    public BytecodeType[] ParameterTypes => _parameters;

    /// <inheritdoc cref="ParameterTypes"/>
    public BytecodeType ReturnType => _return;

    private static BytecodeType TypeOf(Type type, string function, string what,
        IReadOnlyDictionary<Type, string> hostTypes)
    {
        // A registered host type first: it is the one non-scalar that crosses the boundary, as an
        // opaque reference.
        if (hostTypes.TryGetValue(type, out var hostName))
            return new BytecodeType(TypeTag.Host, -1) { HostName = hostName };

        var tag = type switch
        {
            _ when type == typeof(long) => TypeTag.I64,
            _ when type == typeof(int) => TypeTag.I32,
            _ when type == typeof(short) => TypeTag.I16,
            _ when type == typeof(sbyte) => TypeTag.I8,
            _ when type == typeof(ulong) => TypeTag.U64,
            _ when type == typeof(uint) => TypeTag.U32,
            _ when type == typeof(ushort) => TypeTag.U16,
            _ when type == typeof(byte) => TypeTag.U8,
            _ when type == typeof(double) => TypeTag.F64,
            _ when type == typeof(float) => TypeTag.F32,
            _ when type == typeof(bool) => TypeTag.Bool,
            _ when type == typeof(char) => TypeTag.Char,
            _ when type == typeof(string) => TypeTag.String,
            _ => throw new ArgumentException(
                $"host function '{function}': {what} has type '{type.Name}', which cannot cross "
                + "the boundary — scalars, strings, and types registered with RegisterType<T>."),
        };

        return BytecodeType.Scalar(tag);
    }
}

/// <summary>
/// The host threw inside one of its own registered functions.
///
/// <para>Separate from <see cref="EmbeddingException"/> (the script does not compile) and
/// <see cref="ScriptPanicException"/> (the script panicked). The original exception hangs off
/// <see cref="Exception.InnerException"/>.</para>
/// </summary>
public sealed class HostFunctionException : Exception
{
    internal HostFunctionException(string function, Exception cause)
        : base($"the host function '{function}' threw {cause.GetType().Name}: {cause.Message}",
            cause) => Function = function;

    /// <summary>The name the function was registered under.</summary>
    public string Function { get; }
}
