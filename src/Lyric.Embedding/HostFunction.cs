using System.Reflection;
using System.Text;
using Lyric.Bytecode;
using Lyric.Vm;

namespace Lyric.Embedding;

/// <summary>
/// Eine vom Host registrierte Funktion (M10/E3): die Lyric-Signatur, der .NET-Delegat, und die
/// Bruecke dazwischen.
///
/// <para><b>Warum eine Deklaration entsteht.</b> Der Compiler kennt nur, was deklariert ist —
/// <c>Doku.md</c> §21 zeigt ein Skript, das <c>playSound("hit")</c> ohne Import ruft, und das ist
/// so nicht baubar (§2.2: ein unaufloesbares Modul ist ein Fehler). Die Stdlib loest dasselbe
/// Problem seit M6: eine bodylose <c>pub fn</c> in einer <c>.lyr</c>-Datei sagt die Signatur, die
/// <see cref="NativeRegistry"/> liefert den Delegaten, gebunden <b>beim Laden</b> ueber den Namen.
/// <c>RegisterFunction</c> geht denselben Weg — die Datei liegt nur im Speicher statt auf der
/// Platte. Ein zweiter Mechanismus daneben waere Rule 2.</para>
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

    /// <summary>Die Zeile, die im synthetischen Host-Modul landet.</summary>
    public string Declaration { get; }

    public TypeTag[] ParameterTags => _parameters.Select(p => p.Tag).ToArray();

    public TypeTag ReturnTag => _return.Tag;

    public bool ReturnsValue => _return.Tag != TypeTag.Void;

    /// <summary>
    /// Leitet die Lyric-Signatur aus dem .NET-Delegaten ab.
    /// </summary>
    /// <exception cref="ArgumentException">Ein Parameter- oder Rueckgabetyp kann die Grenze nicht
    /// ueberqueren. Die Meldung nennt ihn — E3 traegt dieselben Typen wie E2, also Skalare und
    /// Strings.</exception>
    public static HostFunction From(string name, Delegate implementation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(implementation);

        var method = implementation.Method;
        var parameters = method.GetParameters();

        var tags = new BytecodeType[parameters.Length];
        var text = new StringBuilder("pub fn ").Append(name).Append('(');

        for (var i = 0; i < parameters.Length; i++)
        {
            tags[i] = TypeOf(parameters[i].ParameterType, name, parameters[i].Name ?? $"p{i}");
            if (i > 0) text.Append(", ");
            text.Append(parameters[i].Name ?? $"p{i}").Append(": ")
                .Append(Marshal.Describe(tags[i]));
        }

        var returnType = method.ReturnType == typeof(void)
            ? BytecodeType.Scalar(TypeTag.Void)
            : TypeOf(method.ReturnType, name, "the return value");

        text.Append("): ").Append(Marshal.Describe(returnType)).Append(';');

        return new HostFunction(name, implementation, tags,
            parameters.Select(p => p.ParameterType).ToArray(), returnType, text.ToString());
    }

    /// <summary>Die Form, die die <see cref="NativeRegistry"/> erwartet.</summary>
    public Func<LyrValue[], LyrValue> Bridge => arguments =>
    {
        var boxed = new object?[arguments.Length];
        for (var i = 0; i < arguments.Length; i++)
            boxed[i] = Marshal.FromLyric(arguments[i], _parameters[i], _parameterTypes[i],
                $"argument {i + 1} of host function '{Name}'");

        // DynamicInvoke und nicht ein getypter Aufruf: die Signatur steht erst zur Laufzeit fest,
        // und ein Ausdrucksbaum je Registrierung waere Aufwand fuer einen Pfad, der ohnehin ueber
        // die Prozessgrenze eines Skripts geht. Wird es je gemessen und zu teuer, ist ein
        // kompilierter Delegat eine reine Optimierung hinter derselben Oberflaeche.
        object? produced;
        try
        {
            produced = _implementation.DynamicInvoke(boxed);
        }
        catch (TargetInvocationException wrapped) when (wrapped.InnerException is { } cause)
        {
            // Die Ausnahme des HOSTS, nicht die Huelle der Reflection. Ein Host, der in seiner
            // eigenen Funktion wirft, will seinen Typ zurueck — nicht TargetInvocationException.
            throw new HostFunctionException(Name, cause);
        }

        return _return.Tag == TypeTag.Void
            ? default
            : Marshal.ToLyric(produced, _return, $"the result of host function '{Name}'");
    };

    private static BytecodeType TypeOf(Type type, string function, string what)
    {
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
                + "the boundary — E3 carries scalars and strings, the same as E2. Host objects "
                + "come in E4."),
        };

        return BytecodeType.Scalar(tag);
    }
}

/// <summary>
/// Der Host hat in seiner eigenen registrierten Funktion geworfen.
///
/// <para>Eigene Klasse, damit ein Host die drei Faelle auseinanderhalten kann: sein Skript ist
/// kaputt (<see cref="EmbeddingException"/>), sein Skript hat einen Bug
/// (<see cref="ScriptPanicException"/>), oder <b>sein eigener Code</b> ist gescheitert. Die
/// urspruengliche Ausnahme haengt als <see cref="Exception.InnerException"/> daran.</para>
/// </summary>
public sealed class HostFunctionException : Exception
{
    internal HostFunctionException(string function, Exception cause)
        : base($"the host function '{function}' threw {cause.GetType().Name}: {cause.Message}",
            cause) => Function = function;

    /// <summary>Der Name, unter dem die Funktion registriert wurde.</summary>
    public string Function { get; }
}
