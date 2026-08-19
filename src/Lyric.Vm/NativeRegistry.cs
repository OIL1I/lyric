using System.Globalization;
using Lyric.Bytecode;

namespace Lyric.Vm;

/// <summary>
/// The native implementations behind the standard library's import declarations.
///
/// <para>Binding is symbolic, by name, at load time: the module names
/// <c>std.io.console.println</c> together with its signature and the registry supplies the
/// delegate. A missing name or a mismatched signature rejects the module rather than failing at
/// the call.</para>
///
/// <para>The host-facing <c>RegisterFunction</c> uses the same seam.</para>
///
/// <para><b>The argument array is a loan.</b> The runtime pools the <c>LyrValue[]</c> it passes
/// to an implementation and reuses it after the call returns. Read the arguments during the
/// call, freely and more than once — but an implementation that stores the ARRAY itself for
/// later reads someone else's arguments. Copy the values out instead; every implementation in
/// this file already does.</para>
/// </summary>
public sealed class NativeRegistry
{
    private readonly Dictionary<string, Native> _natives = new(StringComparer.Ordinal);

    private sealed record Native(
        TypeTag[] ParamTypes, TypeTag ReturnType, Func<LyrValue[], LyrValue> Implementation,
        TypeTag? ReturnElement = null,
        BytecodeType[]? FullParamTypes = null, BytecodeType? FullReturnType = null,
        TypeTag[]? StructResult = null, TypeTag?[]? ParamElements = null);

    public void Register(string name, TypeTag[] paramTypes, TypeTag returnType,
        Func<LyrValue[], LyrValue> implementation) =>
        _natives[name] = new Native(paramTypes, returnType, implementation);

    /// <summary>A native returning a <c>T[]</c>.
    ///
    /// <para>An array has no layout: it is a homogeneous sequence whose element type is named in
    /// the import, so the host needs to know nothing about a module's field order. Objects stay
    /// outside.</para>
    ///
    /// <para><paramref name="element"/> is checked while binding; without it <c>string[]</c> and
    /// <c>char[]</c> would be indistinguishable.</para></summary>
    public void RegisterArrayReturning(string name, TypeTag[] paramTypes, TypeTag element,
        Func<LyrValue[], LyrValue> implementation) =>
        _natives[name] = new Native(paramTypes, TypeTag.Array, implementation, element);

    /// <summary>A native returning a <c>?T</c>, used where failure is an ordinary state of the
    /// world: a file that does not exist, an environment variable that is not set.
    ///
    /// <para>As with arrays the inner tag is checked while binding; <c>?string</c> and <c>?int</c>
    /// both carry <c>TypeTag.Optional</c>.</para></summary>
    public void RegisterOptionalReturning(string name, TypeTag[] paramTypes, TypeTag inner,
        Func<LyrValue[], LyrValue> implementation) =>
        _natives[name] = new Native(paramTypes, TypeTag.Optional, implementation, inner);

    /// <summary>A native with array PARAMETERS (v1.14) — an array crossing the boundary INTO the
    /// host, the direction <c>readBytes</c> never needed. The implementation reads the argument
    /// as <c>(LyrValue[])args[i].AsObject</c>, under the loan contract of the class.
    ///
    /// <para><paramref name="paramElements"/> runs parallel to <paramref name="paramTypes"/>:
    /// the element tag for an array parameter, <c>null</c> for a scalar one. Checked while
    /// binding, as for an array return — without it <c>string[]</c> and <c>char[]</c> would be
    /// indistinguishable. <paramref name="returnElement"/> carries the element or inner tag when
    /// the return type itself is an array or an optional.</para></summary>
    public void RegisterWithArrayParams(string name, TypeTag[] paramTypes,
        TypeTag?[] paramElements, TypeTag returnType, Func<LyrValue[], LyrValue> implementation,
        TypeTag? returnElement = null) =>
        _natives[name] = new Native(paramTypes, returnType, implementation, returnElement,
            ParamElements: paramElements);

    /// <summary>
    /// A native whose signature contains host types.
    ///
    /// <para>It needs the full types rather than the tags: two host types both carry
    /// <see cref="TypeTag.Host"/>, and their name is all the module and the runtime know about
    /// them.</para>
    /// </summary>
    public void RegisterWithTypes(string name, BytecodeType[] paramTypes, BytecodeType returnType,
        Func<LyrValue[], LyrValue> implementation) =>
        _natives[name] = new Native(
            paramTypes.Select(p => p.Tag).ToArray(), returnType.Tag, implementation,
            ReturnElement: null, FullParamTypes: paramTypes, FullReturnType: returnType);

    /// <summary>
    /// A native declared in Lyric to RETURN a struct: <c>pub fn positionOf(e: int): Vec2;</c>.
    ///
    /// <para>On the wire the struct comes back through a trailing out-parameter — a buffer the
    /// runtime owns and passes as the LAST argument. The implementation receives the ordinary
    /// arguments plus that buffer's slots and fills ONE VALUE PER FIELD in field order. The same
    /// writing pattern as an array parameter, one entity wide; the script side sees an ordinary
    /// value.</para>
    ///
    /// <para><paramref name="resultFields"/> names the field tags the implementation writes.
    /// Binding checks them against the struct layout the module declares, so a host that
    /// disagrees with the SDK fails at load, not in frame 40 000.</para>
    ///
    /// <para>The loan contract of the class applies to BOTH arrays: read arguments and write the
    /// result during the call, keep neither. And a native that re-enters the VM does so before
    /// writing its result, or the inner call may read a half-written buffer.</para>
    /// </summary>
    public void RegisterStructReturning(string name, TypeTag[] paramTypes, TypeTag[] resultFields,
        Action<LyrValue[], LyrValue[]> implementation) =>
        _natives[name] = new Native(
            [.. paramTypes, TypeTag.Struct], TypeTag.Void,
            arguments =>
            {
                implementation(arguments, (LyrValue[])arguments[^1].AsObject);
                return default;
            },
            StructResult: resultFields);

    /// <summary>A bound import: the implementation plus what the call site needs. Arity and
    /// return kind live here so the interpreter looks nothing up in the hot path.</summary>
    public sealed record BoundNative(int Arity, bool ReturnsValue,
        Func<LyrValue[], LyrValue> Implementation);

    /// <summary>Binds every import of a module. Throws as soon as one is missing or differs from
    /// what the module declares.</summary>
    public BoundNative[] Bind(BytecodeModule module)
    {
        var bound = new BoundNative[module.Imports.Count];

        for (var i = 0; i < module.Imports.Count; i++)
        {
            var import = module.Imports[i];
            if (!_natives.TryGetValue(import.Name, out var native))
                throw new LyricRuntimeException(VmDiagnostics.ImportsNotBound,
                    $"no native implementation for '{import.Name}'");

            // Tags first; elements where a tag alone is ambiguous. A reference signature is
            // rejected because no native declares one.
            if (!native.ParamTypes.SequenceEqual(import.ParamTypes.Select(p => p.Tag)) ||
                native.ReturnType != import.ReturnType.Tag)
                throw new LyricRuntimeException(VmDiagnostics.ImportsNotBound,
                    $"native '{import.Name}' has a different signature than the module expects");

            // For an array return type the tag is not enough: 'string[]' and 'char[]' share it.
            if (native.ReturnElement is { } expected
                && import.ReturnType.Element?.Tag != expected)
                throw new LyricRuntimeException(VmDiagnostics.ImportsNotBound,
                    $"native '{import.Name}' returns a different array element type than the "
                    + "module expects");

            // The same ambiguity for array PARAMETERS (v1.14): the element tag has to match too.
            if (native.ParamElements is { } elements)
                for (var p = 0; p < elements.Length; p++)
                    if (elements[p] is { } el && import.ParamTypes[p].Element?.Tag != el)
                        throw new LyricRuntimeException(VmDiagnostics.ImportsNotBound,
                            $"native '{import.Name}': parameter {p + 1} has a different array "
                            + "element type than the module expects");

            // A struct-returning native writes fields by position, and the layout is the
            // module's. Checking it here turns a host/SDK disagreement into a load error with a
            // name in it rather than a wrong value in a frame.
            if (native.StructResult is { } fields)
            {
                var trailing = import.ParamTypes.Count > 0 ? import.ParamTypes[^1] : default;
                if (trailing.Tag != TypeTag.Struct
                    || trailing.TypeIndex < 0 || trailing.TypeIndex >= module.Types.Count
                    || !module.Types[trailing.TypeIndex].FieldTypes
                        .Select(f => f.Tag).SequenceEqual(fields))
                    throw new LyricRuntimeException(VmDiagnostics.ImportsNotBound,
                        $"native '{import.Name}' fills a struct result whose layout does not "
                        + "match what the module declares");
            }

            // For a host type only the name distinguishes it. Without this check a module
            // expecting one host type could be bound to another, and the mismatch would surface as
            // a cast failure inside the host.
            if (native.FullParamTypes is { } declaredParams)
            {
                for (var p = 0; p < declaredParams.Length; p++)
                    RequireSameHostType(import.Name, declaredParams[p], import.ParamTypes[p],
                        $"parameter {p + 1}");

                RequireSameHostType(import.Name, native.FullReturnType!, import.ReturnType,
                    "the return type");
            }

            bound[i] = new BoundNative(import.ParamTypes.Count,
                import.ReturnType.Tag != TypeTag.Void, native.Implementation);
        }

        return bound;
    }

    private static void RequireSameHostType(string import, BytecodeType native,
        BytecodeType expected, string what)
    {
        if (native.Tag != TypeTag.Host && expected.Tag != TypeTag.Host) return;
        if (string.Equals(native.HostName, expected.HostName, StringComparison.Ordinal)) return;

        throw new LyricRuntimeException(VmDiagnostics.ImportsNotBound,
            $"native '{import}': {what} is host type '{native.HostName ?? "(none)"}', but the "
            + $"module expects '{expected.HostName ?? "(none)"}'");
    }

    /// <summary>
    /// The built-in natives of the standalone CLI. <paramref name="output"/> and
    /// <paramref name="error"/> are parameters so tests can collect the output.
    /// </summary>
    /// <param name="input">Where <c>readLine</c> reads from; defaults to <c>Console.In</c>.
    /// </param>
    public static NativeRegistry CreateDefault(
        TextWriter output, TextWriter error, TextReader? input = null)
    {
        var registry = new NativeRegistry();
        var str = new[] { TypeTag.String };
        var none = Array.Empty<TypeTag>();
        var stdin = input ?? Console.In;

        // Since v1.14 the module declares these as PRIVATE raw* natives behind the Display
        // generics; the old public names stay bound so bytecode compiled before the change keeps
        // running. One host function under both names keeps a single truth about the behavior.
        void ConsoleWriter(string name, Func<LyrValue[], LyrValue> implementation)
        {
            registry.Register("std.io.console.raw" + char.ToUpperInvariant(name[0]) + name[1..],
                str, TypeTag.Void, implementation);
            registry.Register("std.io.console." + name, str, TypeTag.Void, implementation);
        }

        ConsoleWriter("print", args => { output.Write(args[0].AsString); return default; });
        // Always '\n', never Environment.NewLine: the output of a Lyric program does not
        // depend on the operating system.
        ConsoleWriter("println",
            args => { output.Write(args[0].AsString); output.Write('\n'); return default; });
        ConsoleWriter("eprintln",
            args => { error.Write(args[0].AsString); error.Write('\n'); return default; });
        // Writes a diagnostic without a line break.
        ConsoleWriter("eprint", args => { error.Write(args[0].AsString); return default; });

        // ---------------------------------------------------------------- input
        //
        // No capability: reading stdin, like writing stdout, is part of the process rather than an
        // access decision. A host that wants to forbid it passes an empty reader.

        // '?string' because EOF is a state, not an error.
        registry.RegisterOptionalReturning("std.io.console.readLine", none, TypeTag.String,
            _ => Optional(stdin.ReadLine()));

        // Everything up to EOF. Returns "" rather than null: nothing and empty mean the same
        // here.
        registry.Register("std.io.console.readAll", none, TypeTag.String,
            _ => LyrValue.FromString(stdin.ReadToEnd()));

        // A single code point. 'Read()' yields UTF-16 units, so a surrogate pair is combined; half
        // a pair is not a valid char.
        registry.RegisterOptionalReturning("std.io.console.readChar", none, TypeTag.Char,
            _ => ReadCodepoint(stdin));

        // Terminal or pipe: whether a prompt belongs in the stream at all.
        registry.Register("std.io.console.isInteractive", none, TypeTag.Bool,
            _ => LyrValue.FromBool(!Console.IsInputRedirected && !Console.IsOutputRedirected));

        // Needed when a prompt is written without a line break, which would otherwise sit in the
        // buffer while the program waits for the answer.
        registry.Register("std.io.console.flush", none, TypeTag.Void,
            _ => { output.Flush(); return default; });

        registry.Register("std.string.concat", new[] { TypeTag.String, TypeTag.String },
            TypeTag.String, args => LyrValue.FromString(args[0].AsString + args[1].AsString));

        // 'ab' * 3. A negative factor yields the empty string; there is no error case for it.
        registry.Register("std.string.repeat", new[] { TypeTag.String, TypeTag.I64 },
            TypeTag.String, args => LyrValue.FromString(
                args[1].AsI64 <= 0 ? string.Empty
                    : string.Concat(Enumerable.Repeat(args[0].AsString, (int)args[1].AsI64))));

        // A panic is not catchable and never returns, so it throws. The loop that holds the frame
        // stack attaches the backtrace.
        registry.Register("std.core.panic", str, TypeTag.Void,
            args => throw new LyricPanic(VmDiagnostics.Panicked, args[0].AsString));

        // A 'resume' on an exhausted coroutine. It is a panic, not a catchable error.
        registry.Register("std.core.coroutineEnded", Array.Empty<TypeTag>(), TypeTag.Void,
            _ => throw new LyricPanic(VmDiagnostics.Panicked,
                "resume on a coroutine that has already finished"));

        // 'std.core' carries PRIVATE duplicates of six of these declarations (fromInt through
        // charAt): the module is the library's root and imports nothing — with an import of
        // std.string there, no module could import std.core back (LYR-RES0005). One host function
        // under both names keeps a single truth about the behavior.
        void Both(string name, TypeTag[] paramTypes, TypeTag returnType,
            Func<LyrValue[], LyrValue> implementation)
        {
            registry.Register("std.string." + name, paramTypes, returnType, implementation);
            registry.Register("std.core." + name, paramTypes, returnType, implementation);
        }

        // Invariant culture: '3.5', not '3,5'. The same .lyrbc produces the same output on every
        // machine.
        Both("fromInt", new[] { TypeTag.I64 }, TypeTag.String,
            args => LyrValue.FromString(args[0].AsI64.ToString(CultureInfo.InvariantCulture)));
        Both("fromFloat", new[] { TypeTag.F64 }, TypeTag.String,
            args => LyrValue.FromString(args[0].AsF64.ToString("R", CultureInfo.InvariantCulture)));
        Both("fromBool", new[] { TypeTag.Bool }, TypeTag.String,
            args => LyrValue.FromString(args[0].AsBool ? "true" : "false"));
        Both("fromChar", new[] { TypeTag.Char }, TypeTag.String,
            args => LyrValue.FromString(char.ConvertFromUtf32((int)args[0].Bits)));

        // --- queries --------------------------------------------------------------------
        //
        // Every position and length counts CODE POINTS, not UTF-16 units and not bytes, matching
        // what iteration yields. The cost is O(n) rather than O(1); there is no 's[i]', so a
        // quadratic index loop cannot be written.

        Both("length", str, TypeTag.I64,
            args => LyrValue.FromI64(CodepointCount(args[0].AsString)));

        Both("charAt", new[] { TypeTag.String, TypeTag.I64 }, TypeTag.Char,
            args => LyrValue.FromBits((ulong)CodepointAt(args[0].AsString, args[1].AsI64)));

        // --- the byte bridge and the array-parameter joiners (v1.14) ---------------------

        registry.RegisterArrayReturning("std.string.utf8Encode", str, TypeTag.U8,
            args => Bytes(System.Text.Encoding.UTF8.GetBytes(args[0].AsString)));

        // STRICT, unlike readText: invalid bytes are the caller's question here, and the answer
        // is null rather than a U+FFFD quietly standing in for the data.
        registry.RegisterWithArrayParams("std.string.utf8Decode",
            new[] { TypeTag.Array }, new TypeTag?[] { TypeTag.U8 },
            TypeTag.Optional, args =>
            {
                try
                {
                    return LyrValue.FromString(new System.Text.UTF8Encoding(
                        encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                        .GetString(ToBytes(args[0])));
                }
                catch (ArgumentException)
                {
                    return default;
                }
            }, returnElement: TypeTag.String);

        // Behind std.string.join and StringBuilder.build: one native call instead of a copy
        // cascade the language cannot avoid without preallocating strings.
        registry.RegisterWithArrayParams("std.string.joinAll",
            new[] { TypeTag.Array, TypeTag.String }, new TypeTag?[] { TypeTag.String, null },
            TypeTag.String, args =>
            {
                var parts = (LyrValue[])args[0].AsObject;
                var texts = new string[parts.Length];
                for (var i = 0; i < parts.Length; i++) texts[i] = parts[i].AsString;
                return LyrValue.FromString(string.Join(args[1].AsString, texts));
            });

        // The inverse of toChars, native since v1.14: one call instead of one string per
        // character.
        registry.RegisterWithArrayParams("std.string.fromChars",
            new[] { TypeTag.Array }, new TypeTag?[] { TypeTag.Char },
            TypeTag.String, args =>
            {
                var chars = (LyrValue[])args[0].AsObject;
                var builder = new System.Text.StringBuilder(chars.Length);
                for (var i = 0; i < chars.Length; i++)
                    builder.Append(char.ConvertFromUtf32((int)chars[i].Bits));
                return LyrValue.FromString(builder.ToString());
            });

        registry.Register("std.string.substring",
            new[] { TypeTag.String, TypeTag.I64, TypeTag.I64 }, TypeTag.String,
            args => LyrValue.FromString(Substring(args[0].AsString, args[1].AsI64, args[2].AsI64)));

        registry.Register("std.string.indexOf", new[] { TypeTag.String, TypeTag.String },
            TypeTag.I64, args => LyrValue.FromI64(IndexOf(args[0].AsString, args[1].AsString)));

        registry.Register("std.string.contains", new[] { TypeTag.String, TypeTag.String },
            TypeTag.Bool,
            args => LyrValue.FromBool(args[0].AsString.Contains(args[1].AsString, StringComparison.Ordinal)));

        registry.Register("std.string.startsWith", new[] { TypeTag.String, TypeTag.String },
            TypeTag.Bool,
            args => LyrValue.FromBool(args[0].AsString.StartsWith(args[1].AsString, StringComparison.Ordinal)));

        registry.Register("std.string.endsWith", new[] { TypeTag.String, TypeTag.String },
            TypeTag.Bool,
            args => LyrValue.FromBool(args[0].AsString.EndsWith(args[1].AsString, StringComparison.Ordinal)));

        registry.Register("std.string.trim", str, TypeTag.String,
            args => LyrValue.FromString(args[0].AsString.Trim()));

        // Ordinal rather than culture-dependent, so the same program yields the same result on
        // every machine.
        registry.Register("std.string.toUpper", str, TypeTag.String,
            args => LyrValue.FromString(args[0].AsString.ToUpperInvariant()));

        registry.Register("std.string.toLower", str, TypeTag.String,
            args => LyrValue.FromString(args[0].AsString.ToLowerInvariant()));

        registry.RegisterArrayReturning("std.string.split",
            new[] { TypeTag.String, TypeTag.String }, TypeTag.String,
            args => Split(args[0].AsString, args[1].AsString));

        // The anchor for 'for (c in s)': one O(n) pass instead of n calls to 'charAt'.
        registry.RegisterArrayReturning("std.string.toChars", str, TypeTag.Char,
            args => ToChars(args[0].AsString));

        // --- std.os (capability-gated) --------------------------------------------------
        //
        // The natives are always registered. The capability decides whether a module requiring
        // them loads at all, not whether the function exists.
        // --- std.math (ungated) ----------------------------------------------------------
        //
        // Special values follow IEEE 754: 'sqrt(-1.0)' is NaN, not a panic.
        var f1 = new[] { TypeTag.F64 };
        var f2 = new[] { TypeTag.F64, TypeTag.F64 };

        registry.Register("std.math.sqrt", f1, TypeTag.F64,
            args => LyrValue.FromF64(Math.Sqrt(args[0].AsF64)));
        registry.Register("std.math.abs", f1, TypeTag.F64,
            args => LyrValue.FromF64(Math.Abs(args[0].AsF64)));
        registry.Register("std.math.floor", f1, TypeTag.F64,
            args => LyrValue.FromF64(Math.Floor(args[0].AsF64)));
        registry.Register("std.math.ceil", f1, TypeTag.F64,
            args => LyrValue.FromF64(Math.Ceiling(args[0].AsF64)));

        // Round half to even, which avoids the systematic bias of always rounding up.
        registry.Register("std.math.round", f1, TypeTag.F64,
            args => LyrValue.FromF64(Math.Round(args[0].AsF64, MidpointRounding.ToEven)));

        registry.Register("std.math.min", f2, TypeTag.F64,
            args => LyrValue.FromF64(Math.Min(args[0].AsF64, args[1].AsF64)));
        registry.Register("std.math.max", f2, TypeTag.F64,
            args => LyrValue.FromF64(Math.Max(args[0].AsF64, args[1].AsF64)));
        registry.Register("std.math.pow", f2, TypeTag.F64,
            args => LyrValue.FromF64(Math.Pow(args[0].AsF64, args[1].AsF64)));

        registry.Register("std.math.sin", f1, TypeTag.F64,
            args => LyrValue.FromF64(Math.Sin(args[0].AsF64)));
        registry.Register("std.math.cos", f1, TypeTag.F64,
            args => LyrValue.FromF64(Math.Cos(args[0].AsF64)));
        registry.Register("std.math.tan", f1, TypeTag.F64,
            args => LyrValue.FromF64(Math.Tan(args[0].AsF64)));
        // log2 and log10 are native although 'log(x)/log(base)' expresses them: the derived form
        // is imprecise. 'log10(1000.0)' computed that way yields 2.9999999999999996, and 'as int'
        // turns that into 2.
        registry.Register("std.string.fromUint", new[] { TypeTag.U64 }, TypeTag.String,
            args => LyrValue.FromString(args[0].AsU64.ToString(CultureInfo.InvariantCulture)));

        registry.Register("std.fmt.formatUint", new[] { TypeTag.U64, TypeTag.String },
            TypeTag.String, args => Formatted(args[0].AsU64, args[1].AsString));

        registry.Register("std.math.log2", f1, TypeTag.F64,
            args => LyrValue.FromF64(Math.Log2(args[0].AsF64)));
        registry.Register("std.math.log10", f1, TypeTag.F64,
            args => LyrValue.FromF64(Math.Log10(args[0].AsF64)));

        registry.Register("std.math.asin", f1, TypeTag.F64,
            args => LyrValue.FromF64(Math.Asin(args[0].AsF64)));
        registry.Register("std.math.acos", f1, TypeTag.F64,
            args => LyrValue.FromF64(Math.Acos(args[0].AsF64)));
        registry.Register("std.math.atan", f1, TypeTag.F64,
            args => LyrValue.FromF64(Math.Atan(args[0].AsF64)));
        registry.Register("std.math.atan2", new[] { TypeTag.F64, TypeTag.F64 }, TypeTag.F64,
            args => LyrValue.FromF64(Math.Atan2(args[0].AsF64, args[1].AsF64)));

        registry.Register("std.math.log", f1, TypeTag.F64,
            args => LyrValue.FromF64(Math.Log(args[0].AsF64)));

        registry.Register("std.os.platform", Array.Empty<TypeTag>(), TypeTag.String,
            _ => LyrValue.FromString(
                OperatingSystem.IsWindows() ? "windows"
                : OperatingSystem.IsLinux() ? "linux"
                : OperatingSystem.IsMacOS() ? "macos"
                : "unknown"));

        // --- std.fmt --------------------------------------------------------------------
        //
        // The specifier language is .NET's, passed through unchanged: N2, F3, D5, X, E2, P1.
        // Always invariant, so a number does not change shape with the machine's locale.
        registry.Register("std.fmt.formatInt", new[] { TypeTag.I64, TypeTag.String },
            TypeTag.String, args => Formatted(args[0].AsI64, args[1].AsString));

        registry.Register("std.fmt.formatFloat", new[] { TypeTag.F64, TypeTag.String },
            TypeTag.String, args => Formatted(args[0].AsF64, args[1].AsString));

        registry.Register("std.fmt.formatBool", new[] { TypeTag.Bool, TypeTag.String },
            TypeTag.String, args => LyrValue.FromString(Padded(args[0].AsBool ? "true" : "false",
                args[1].AsString)));

        registry.Register("std.fmt.formatChar", new[] { TypeTag.Char, TypeTag.String },
            TypeTag.String, args => LyrValue.FromString(Padded(
                char.ConvertFromUtf32((int)args[0].Bits), args[1].AsString)));

        registry.Register("std.fmt.formatString", new[] { TypeTag.String, TypeTag.String },
            TypeTag.String,
            args => LyrValue.FromString(Padded(args[0].AsString, args[1].AsString)));

        registry.RegisterOptionalReturning("std.os.env", str, TypeTag.String,
            args => Optional(Environment.GetEnvironmentVariable(args[0].AsString)));

        registry.Register("std.os.currentDir", Array.Empty<TypeTag>(), TypeTag.String,
            _ => LyrValue.FromString(Directory.GetCurrentDirectory()));

        // Exits immediately: no 'defer' runs and no 'catch' applies. The return type is 'void'
        // rather than 'never', which would let the compiler treat the following code as
        // unreachable.
        registry.Register("std.os.exit", new[] { TypeTag.I64 }, TypeTag.Void,
            args => { Environment.Exit((int)(args[0].AsI64 & 0xFF)); return default; });

        // --- std.io.file (capability-gated: fileAccess) ------------------------------------
        //
        // Failures are return values, not exceptions: a file that does not exist is an ordinary
        // state. The catch is deliberately broad (IO errors, permissions, invalid paths); to the
        // caller they are the same answer.
        registry.RegisterOptionalReturning("std.io.file.readText", str, TypeTag.String,
            args => Optional(TryIo(() => File.ReadAllText(args[0].AsString))));

        registry.Register("std.io.file.writeText", new[] { TypeTag.String, TypeTag.String },
            TypeTag.Bool, args => LyrValue.FromBool(
                TryIo(() => { File.WriteAllText(args[0].AsString, args[1].AsString); return ""; }) is not null));

        registry.Register("std.io.file.appendText", new[] { TypeTag.String, TypeTag.String },
            TypeTag.Bool, args => LyrValue.FromBool(
                TryIo(() => { File.AppendAllText(args[0].AsString, args[1].AsString); return ""; }) is not null));

        registry.Register("std.io.file.exists", str, TypeTag.Bool,
            args => LyrValue.FromBool(File.Exists(args[0].AsString)));

        // 'true' when the file is gone afterwards, including when it never existed.
        registry.Register("std.io.file.remove", str, TypeTag.Bool,
            args => LyrValue.FromBool(
                TryIo(() => { File.Delete(args[0].AsString); return ""; }) is not null));

        registry.RegisterArrayReturning("std.io.file.readLines", str, TypeTag.String,
            args => Lines(TryIo(() => File.ReadAllText(args[0].AsString))));

        // The raw bytes, undecoded — the answer readText cannot give: its UTF-8 decoding turns
        // invalid bytes into U+FFFD. Empty when unreadable, the readLines convention; a caller
        // that needs the difference asks 'exists' first.
        registry.RegisterArrayReturning("std.io.file.readBytes", str, TypeTag.U8,
            args => Bytes(TryIoBytes(() => File.ReadAllBytes(args[0].AsString))));

        // The write side (v1.14) — the first natives with an ARRAY parameter. The failure model
        // is writeText's: a bool, through the same broad TryIo.
        registry.RegisterWithArrayParams("std.io.file.writeBytes",
            new[] { TypeTag.String, TypeTag.Array }, new TypeTag?[] { null, TypeTag.U8 },
            TypeTag.Bool, args => LyrValue.FromBool(
                TryIo(() =>
                {
                    File.WriteAllBytes(args[0].AsString, ToBytes(args[1]));
                    return "";
                }) is not null));

        registry.RegisterWithArrayParams("std.io.file.appendBytes",
            new[] { TypeTag.String, TypeTag.Array }, new TypeTag?[] { null, TypeTag.U8 },
            TypeTag.Bool, args => LyrValue.FromBool(
                TryIo(() =>
                {
                    using var stream = new FileStream(args[0].AsString, FileMode.Append,
                        FileAccess.Write);
                    stream.Write(ToBytes(args[1]));
                    return "";
                }) is not null));

        // ------------------------------------------------------------ std.io.file, continued
        //
        // Everything here runs through 'TryIo': a missing file, a locked directory or a full disk
        // are states, reported through the return value.

        // Not through 'TryIo', which carries a string; this returns a number.
        registry.RegisterOptionalReturning("std.io.file.size", str, TypeTag.I64,
            args =>
            {
                try
                {
                    var info = new FileInfo(args[0].AsString);

                    // 'Some' rather than 'FromI64': for a '?T' over a scalar the marker in 'Ref'
                    // is what signals presence, since every bit pattern is a valid number.
                    return info.Exists ? LyrValue.Some(LyrValue.FromI64(info.Length)) : LyrValue.None;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                              or ArgumentException or NotSupportedException)
                {
                    return LyrValue.None;
                }
            });

        registry.Register("std.io.file.isFile", str, TypeTag.Bool,
            args => LyrValue.FromBool(File.Exists(args[0].AsString)));

        registry.Register("std.io.file.isDirectory", str, TypeTag.Bool,
            args => LyrValue.FromBool(Directory.Exists(args[0].AsString)));

        registry.Register("std.io.file.copy", new[] { TypeTag.String, TypeTag.String },
            TypeTag.Bool, args => LyrValue.FromBool(
                TryIo(() => { File.Copy(args[0].AsString, args[1].AsString, true); return ""; })
                is not null));

        registry.Register("std.io.file.move", new[] { TypeTag.String, TypeTag.String },
            TypeTag.Bool, args => LyrValue.FromBool(
                TryIo(() => { File.Move(args[0].AsString, args[1].AsString, true); return ""; })
                is not null));

        // An existing directory counts as success: the desired state is reached.
        registry.Register("std.io.file.createDir", str, TypeTag.Bool,
            args => LyrValue.FromBool(
                TryIo(() => { Directory.CreateDirectory(args[0].AsString); return ""; })
                is not null));

        registry.Register("std.io.file.createDirAll", str, TypeTag.Bool,
            args => LyrValue.FromBool(
                TryIo(() => { Directory.CreateDirectory(args[0].AsString); return ""; })
                is not null));

        // Empty directories only ('recursive: false'). There is no recursive delete.
        registry.Register("std.io.file.removeDir", str, TypeTag.Bool,
            args => LyrValue.FromBool(
                TryIo(() => { Directory.Delete(args[0].AsString, false); return ""; })
                is not null));

        registry.RegisterArrayReturning("std.io.file.listDir", str, TypeTag.String,
            args =>
            {
                try
                {
                    var namen = Directory.EnumerateFileSystemEntries(args[0].AsString)
                        .Select(Path.GetFileName)
                        .OfType<string>()
                        .OrderBy(n => n, StringComparer.Ordinal)   // deterministic
                        .Select(LyrValue.FromString)
                        .ToArray();
                    return LyrValue.FromObject(namen);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                              or ArgumentException)
                {
                    return LyrValue.FromObject([]);
                }
            });

        registry.Register("std.io.file.tempDir", none, TypeTag.String,
            _ => LyrValue.FromString(Path.GetTempPath()));

        // ------------------------------------------------------ std.os, extended

        registry.RegisterArrayReturning("std.os.args", none, TypeTag.String,
            _ => LyrValue.FromObject(Environment.GetCommandLineArgs()
                .Select(LyrValue.FromString).ToArray()));

        registry.Register("std.os.setEnv", new[] { TypeTag.String, TypeTag.String }, TypeTag.Bool,
            args =>
            {
                try
                {
                    Environment.SetEnvironmentVariable(args[0].AsString, args[1].AsString);
                    return LyrValue.FromBool(true);
                }
                catch (Exception e) when (e is ArgumentException or System.Security.SecurityException)
                {
                    return LyrValue.FromBool(false);
                }
            });

        registry.RegisterOptionalReturning("std.os.hostName", none, TypeTag.String,
            _ => Optional(TryIo(() => Environment.MachineName)));

        registry.RegisterOptionalReturning("std.os.userName", none, TypeTag.String,
            _ => Optional(TryIo(() => Environment.UserName)));

        registry.RegisterOptionalReturning("std.os.homeDir", none, TypeTag.String,
            _ => Optional(TryIo(() =>
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))));

        registry.Register("std.os.cpuCount", none, TypeTag.I64,
            _ => LyrValue.FromI64(Environment.ProcessorCount));

        // std.time's private clock — the same host function as std.os.nowMillis, two names, one
        // truth.
        registry.Register("std.time.nowMillis", none, TypeTag.I64,
            _ => LyrValue.FromI64(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        registry.Register("std.os.nowMillis", none, TypeTag.I64,
            _ => LyrValue.FromI64(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        // Monotonic, with an arbitrary origin, and therefore not comparable with nowMillis: a
        // system clock can jump during a measurement.
        registry.Register("std.os.nowNanos", none, TypeTag.I64,
            _ => LyrValue.FromI64(
                (long)(System.Diagnostics.Stopwatch.GetTimestamp()
                       * (1_000_000_000.0 / System.Diagnostics.Stopwatch.Frequency))));

        registry.Register("std.os.sleep", new[] { TypeTag.I64 }, TypeTag.Void,
            args =>
            {
                var millis = args[0].AsI64;
                if (millis > 0) Thread.Sleep((int)Math.Min(millis, int.MaxValue));
                return default;
            });


        // v1.15: the string METHOD API. Inside 'extend string' a method shadows the free
        // function of the same name, so the methods call raw* twins; the old public names stay
        // bound for bytecode compiled before the change. Same entries, second name each.
        void StringRaw(string name) =>
            registry._natives["std.string.raw" + char.ToUpperInvariant(name[0]) + name[1..]] =
                registry._natives["std.string." + name];

        StringRaw("length");
        StringRaw("charAt");
        StringRaw("substring");
        StringRaw("indexOf");
        StringRaw("contains");
        StringRaw("startsWith");
        StringRaw("endsWith");
        StringRaw("trim");
        StringRaw("toUpper");
        StringRaw("toLower");
        StringRaw("split");
        StringRaw("toChars");
        StringRaw("utf8Encode");

        return registry;
    }

    // ------------------------------------------------------------------ code point helpers

    private static long CodepointCount(string s)
    {
        var n = 0L;
        for (var i = 0; i < s.Length; i += char.IsHighSurrogate(s[i]) ? 2 : 1) n++;
        return n;
    }

    /// <summary>The code point at <paramref name="index"/>, counted in code points. Out of range
    /// is a panic, as for an array index.</summary>
    private static int CodepointAt(string s, long index)
    {
        if (index < 0) throw OutOfRange(index);

        var seen = 0L;
        for (var i = 0; i < s.Length; i += char.IsHighSurrogate(s[i]) ? 2 : 1)
        {
            if (seen == index) return char.ConvertToUtf32(s, i);
            seen++;
        }
        throw OutOfRange(index);
    }

    private static LyricPanic OutOfRange(long index) =>
        new(VmDiagnostics.IndexOutOfRange, $"string index {index} is out of range");

    private static string Substring(string s, long start, long count)
    {
        if (start < 0 || count < 0) throw OutOfRange(start < 0 ? start : count);

        var offsets = Offsets(s);
        if (start > offsets.Count - 1) throw OutOfRange(start);

        var from = offsets[(int)start];
        var end = start + count;
        var to = end >= offsets.Count - 1 ? s.Length : offsets[(int)end];
        return s[from..to];
    }

    /// <summary>The UTF-16 offsets of every code point, plus a sentinel, so a code-point position
    /// becomes a slice without counting from the start each time.</summary>
    private static List<int> Offsets(string s)
    {
        var offsets = new List<int>();
        for (var i = 0; i < s.Length; i += char.IsHighSurrogate(s[i]) ? 2 : 1) offsets.Add(i);
        offsets.Add(s.Length);
        return offsets;
    }

    /// <summary>Position in code points, so the result can be passed to charAt or substring.
    /// Minus one when not found.</summary>
    private static long IndexOf(string s, string needle)
    {
        var at = s.IndexOf(needle, StringComparison.Ordinal);
        if (at < 0) return -1;

        var count = 0L;
        for (var i = 0; i < at; i += char.IsHighSurrogate(s[i]) ? 2 : 1) count++;
        return count;
    }

    private static LyrValue Split(string s, string separator)
    {
        // An empty separator has no sensible answer; it is a panic.
        if (separator.Length == 0)
            throw new LyricPanic(VmDiagnostics.IndexOutOfRange, "split needs a non-empty separator");

        var parts = s.Split(separator, StringSplitOptions.None);
        var values = new LyrValue[parts.Length];
        for (var i = 0; i < parts.Length; i++) values[i] = LyrValue.FromString(parts[i]);
        return LyrValue.FromObject(values);
    }

    // ------------------------------------------------------------------ std.os/std.io.file

    /// <summary>A <c>?string</c> from a possibly absent value: a reference means present, an empty
    /// one means <c>null</c>.</summary>
    private static LyrValue Optional(string? value) =>
        value is null ? default : LyrValue.FromString(value);

    /// <summary>A code point from a <see cref="TextReader"/>, combining surrogate pairs.</summary>
    /// <remarks>.NET's <c>Read()</c> yields UTF-16 units while a Lyric <c>char</c> is a code
    /// point. Without combining the pair, a character beyond the BMP would come back as a lone
    /// surrogate half, which is not a valid <c>char</c>.
    /// </remarks>
    private static LyrValue ReadCodepoint(TextReader reader)
    {
        var first = reader.Read();
        if (first < 0) return default;   // EOF

        if (char.IsHighSurrogate((char)first) && reader.Peek() is var next && next >= 0
            && char.IsLowSurrogate((char)next))
            return LyrValue.FromBits((ulong)char.ConvertToUtf32((char)first, (char)reader.Read()));

        return LyrValue.FromBits((ulong)first);
    }

    /// <summary>Runs a file operation and returns <c>null</c> when it fails.
    ///
    /// <para>The catch is broad: missing file, missing permission, invalid path, device gone.
    /// To the caller they are the same answer.</para></summary>
    private static string? TryIo(Func<string> operation)
    {
        try
        {
            return operation();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Lines without their terminators, so the result does not depend on whether the
    /// file was written with CRLF or LF.</summary>
    /// <summary>As <see cref="TryIo"/>, for an operation whose answer is bytes.</summary>
    private static byte[]? TryIoBytes(Func<byte[]> operation)
    {
        try
        {
            return operation();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The bytes as a <c>uint8[]</c> value; unreadable reads as empty.</summary>
    private static LyrValue Bytes(byte[]? content)
    {
        if (content is null) return LyrValue.FromObject(Array.Empty<LyrValue>());
        var values = new LyrValue[content.Length];
        for (var i = 0; i < content.Length; i++) values[i] = LyrValue.FromBits(content[i]);
        return LyrValue.FromObject(values);
    }

    /// <summary>A <c>uint8[]</c> argument as host bytes — the inverse of <see cref="Bytes"/>,
    /// copied out under the loan contract.</summary>
    private static byte[] ToBytes(LyrValue argument)
    {
        var values = (LyrValue[])argument.AsObject;
        var bytes = new byte[values.Length];
        for (var i = 0; i < values.Length; i++) bytes[i] = (byte)values[i].Bits;
        return bytes;
    }

    private static LyrValue Lines(string? content)
    {
        if (content is null) return LyrValue.FromObject([]);

        var split = content.ReplaceLineEndings("\n").Split('\n');

        // A file ending in a line break has no empty final line.
        var count = split.Length > 0 && split[^1].Length == 0 ? split.Length - 1 : split.Length;

        var values = new LyrValue[count];
        for (var i = 0; i < count; i++) values[i] = LyrValue.FromString(split[i]);
        return LyrValue.FromObject(values);
    }

    // ------------------------------------------------------------------ std.fmt helpers

    /// <summary>A number formatted by a .NET standard spec, invariantly.
    ///
    /// <para>An unknown specifier is a panic rather than an error value: it is a literal in the
    /// source and does not depend on the input.</para></summary>
    private static LyrValue Formatted(IFormattable value, string spec)
    {
        // A bare number is a WIDTH, including for a numeric value: '{n:8}' pads to eight columns
        // on the right, '{n:-8}' on the left. Passed to .NET, '8' would be a custom digit
        // placeholder and '-8' a literal.
        //
        // Handling it here makes the width form apply to every type rather than only to those
        // without .NET standard formats.
        if (IsWidth(spec)) return LyrValue.FromString(Padded(
            value.ToString(null, CultureInfo.InvariantCulture), spec));

        try
        {
            return LyrValue.FromString(value.ToString(spec, CultureInfo.InvariantCulture));
        }
        catch (FormatException)
        {
            throw new LyricPanic(VmDiagnostics.IndexOutOfRange,
                $"'{spec}' is not a valid format spec");
        }
    }

    /// <summary>For types without .NET standard formats the specifier is a width:
    /// <c>{name:10}</c> pads on the right, <c>{name:-10}</c> on the left. An empty specifier
    /// leaves the text as it is.</summary>
    /// <summary>Is the specifier a bare width: digits, optionally with a leading minus?</summary>
    private static bool IsWidth(string spec)
    {
        if (spec.Length == 0) return false;

        var start = spec[0] == '-' ? 1 : 0;
        if (start >= spec.Length) return false;

        for (var i = start; i < spec.Length; i++)
            if (!char.IsAsciiDigit(spec[i]))
                return false;
        return true;
    }

    private static string Padded(string value, string spec)
    {
        if (spec.Length == 0) return value;

        if (!int.TryParse(spec, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture,
                out var width))
            throw new LyricPanic(VmDiagnostics.IndexOutOfRange,
                $"'{spec}' is not a width — for this type a format spec is a number");

        return width < 0 ? value.PadLeft(-width) : value.PadRight(width);
    }

    private static LyrValue ToChars(string s)
    {
        var chars = new List<LyrValue>();
        for (var i = 0; i < s.Length; i += char.IsHighSurrogate(s[i]) ? 2 : 1)
            chars.Add(LyrValue.FromBits((ulong)char.ConvertToUtf32(s, i)));
        return LyrValue.FromObject(chars.ToArray());
    }
}
