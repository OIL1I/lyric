using Lyric.Bytecode;
using Lyric.Compiler;
using Lyric.Core;
using Lyric.Vm;

namespace Lyric.Embedding;

/// <summary>
/// A Lyric runtime under the control of a .NET host.
///
/// <para>The host decides what a script may reach. <see cref="Compile"/> is frontend,
/// <see cref="Run"/> is runtime, and the state lives between them, which is why this is its own
/// assembly rather than part of the runtime.</para>
///
/// <para>One VM is one sandbox: two VMs in the same process share no capabilities, no registry
/// and no loaded modules.</para>
/// </summary>
public sealed class LangVm
{
    /// <summary>The module name registered host functions appear under. A script writes
    /// <c>import host;</c> or <c>import host { playSound };</c>.</summary>
    public const string HostModule = "host";

    private readonly HostOptions _options;
    private readonly NativeRegistry _natives;
    private readonly Dictionary<string, HostFunction> _hostFunctions = new(StringComparer.Ordinal);

    /// <summary>Qualified names registered through <see cref="RegisterNative"/>. Only the names: the
    /// declarations belong to the files a native root ships, not to this class.</summary>
    private readonly HashSet<string> _natived = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, string> _hostTypes = [];
    private readonly Dictionary<string, List<HostFunction>> _hostMethods =
        new(StringComparer.Ordinal);

    /// <param name="options">Defaults to no capability and no output. See
    /// <see cref="HostOptions"/>.</param>
    public LangVm(HostOptions? options = null)
    {
        _options = options ?? new HostOptions();
        _natives = NativeRegistry.CreateDefault(
            _options.Output ?? TextWriter.Null,
            _options.Error ?? TextWriter.Null);
    }

    /// <summary>What scripts of this VM may reach.</summary>
    public Capability Capabilities => _options.Capabilities;

    /// <summary>
    /// Makes a .NET type visible to scripts of this VM as the opaque type
    /// <c>host.&lt;name&gt;</c>.
    ///
    /// <para>A script can receive one and pass it on. It has no field access and cannot construct
    /// one (<c>LYR-SEM0061</c>). The garbage collector keeps the object alive as long as a Lyric
    /// value reaches it; there is no release or revocation protocol.</para>
    /// </summary>
    /// <exception cref="ArgumentException">The name or the type is already registered.</exception>
    /// <param name="configure">What a script may do with the type. Without a configurator it is
    /// purely opaque.</param>
    public void RegisterType<T>(string name, Action<HostTypeBuilder<T>>? configure = null)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (_hostTypes.ContainsValue(name) || _hostFunctions.ContainsKey(name))
            throw new ArgumentException($"the name '{name}' is already taken", nameof(name));

        // No silent overwrite: which of two names for the same .NET type wins would otherwise
        // depend on registration order.
        if (!_hostTypes.TryAdd(typeof(T), name))
            throw new ArgumentException(
                $"'{typeof(T).Name}' is already registered as '{_hostTypes[typeof(T)]}'",
                nameof(name));

        if (configure is null) return;

        var builder = new HostTypeBuilder<T>();
        configure(builder);

        var methods = new List<HostFunction>();
        foreach (var (methodName, implementation, mutates) in builder.Methods)
        {
            var method = HostFunction.Method(name, methodName, implementation, mutates, _hostTypes);

            if (methods.Any(m => string.Equals(m.Name, method.Name, StringComparison.Ordinal)))
                throw new ArgumentException(
                    $"'{name}' already has a method named '{methodName}'", nameof(configure));

            methods.Add(method);

            // The name follows the ordinary mangling rule, <module>.<Type>.<method>. A test binds
            // this spelling to the one the lowering produces.
            _natives.RegisterWithTypes($"{HostModule}.{name}.{methodName}",
                method.ParameterTypes, method.ReturnType, method.Bridge);
        }

        _hostMethods[name] = methods;
    }

    /// <summary>
    /// Makes a .NET function visible to scripts of this VM as <c>host.&lt;name&gt;</c>.
    ///
    /// <para>Call it before compiling. The signature is derived from the delegate and written as a
    /// declaration into a synthetic <c>host</c> module, which the compiler reads like any standard
    /// library declaration.</para>
    ///
    /// <para>The script must import <c>host</c>; there is no implicit namespace.</para>
    /// </summary>
    /// <exception cref="ArgumentException">A parameter or return type cannot cross the boundary,
    /// or the name is already registered.</exception>
    public void RegisterFunction(string name, Delegate implementation)
    {
        var function = HostFunction.From(name, implementation, _hostTypes);

        // No silent overwrite: two registrations of the same name are a host error.
        if (!_hostFunctions.TryAdd(name, function))
            throw new ArgumentException(
                $"a host function named '{name}' is already registered", nameof(name));

        // With the full types: a host type is distinguished by name, and a tag comparison would
        // treat two of them as the same.
        _natives.RegisterWithTypes($"{HostModule}.{name}",
            function.ParameterTypes, function.ReturnType, function.Bridge);
    }

    /// <summary>
    /// The implementation of a function a native root DECLARES, under the same qualified name.
    ///
    /// <para>The counterpart of <see cref="HostOptions.NativeRoots"/>: there the SDK ships
    /// <c>engine/input.lyr</c> with <c>pub fn keyDown(key: int): bool;</c>, here the host supplies
    /// what it does, as <c>engine.input.keyDown</c>. The compiler binds natives by qualified name,
    /// so the two spellings have to be the same one.</para>
    ///
    /// <para>Unlike <see cref="RegisterFunction"/> this generates no declaration. The declaration is
    /// the file; a second one here would be the same signature written twice, which is the thing a
    /// native root exists to avoid.</para>
    /// </summary>
    /// <exception cref="ArgumentException">The name is not qualified, names the generated
    /// <c>host</c> module, or is already registered.</exception>
    public void RegisterNative(string qualifiedName, Delegate implementation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedName);

        var separator = qualifiedName.LastIndexOf('.');
        if (separator <= 0 || separator == qualifiedName.Length - 1)
            throw new ArgumentException(
                $"'{qualifiedName}' is not qualified: a native declared in a root is named "
                + "'<module>.<function>', which is the spelling the compiler looks it up by",
                nameof(qualifiedName));

        // 'host' is generated from RegisterFunction. Writing into it from here would make two
        // mechanisms responsible for one module, and the generated source would not mention it.
        if (qualifiedName.StartsWith(HostModule + ".", StringComparison.Ordinal))
            throw new ArgumentException(
                $"'{HostModule}' is the module RegisterFunction generates; use that one, or declare "
                + "this function in a native root under its own module path", nameof(qualifiedName));

        // The bare name, so the signature is derived exactly as for any other host function; the
        // module path is not part of what a delegate looks like.
        var function = HostFunction.From(qualifiedName[(separator + 1)..], implementation, _hostTypes);

        // No silent overwrite, the same rule as for RegisterFunction.
        if (!_natived.Add(qualifiedName))
            throw new ArgumentException(
                $"a native named '{qualifiedName}' is already registered", nameof(qualifiedName));

        _natives.RegisterWithTypes(qualifiedName,
            function.ParameterTypes, function.ReturnType, function.Bridge);
    }

    /// <summary>The source of the synthetic <c>host</c> module, or <c>null</c> while nothing is
    /// registered. It is the Lyric code the script compiles against.</summary>
    public string? HostModuleSource
    {
        get
        {
            if (_hostFunctions.Count == 0 && _hostTypes.Count == 0) return null;

            // Sorted rather than in registration order, so the same set of functions yields the
            // same source and therefore the same bytes.
            var types = string.Join(Environment.NewLine, _hostTypes.Values
                .OrderBy(n => n, StringComparer.Ordinal)
                // A class without fields in a native module is a host type: there is no layout
                // this module knows. Its methods are natives and live in the host.
                .Select(DeclareType));

            var declarations = string.Join(Environment.NewLine, _hostFunctions.Values
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .Select(f => f.Declaration));

            // Empty sections are dropped; a host prints this source.
            var body = string.Join(Environment.NewLine + Environment.NewLine,
                new[] { types, declarations }.Where(part => part.Length > 0));

            return $"""
                // Generated by LangVm: what the host offers this script.
                module {HostModule};

                {body}
                """;
        }
    }

    private string DeclareType(string name)
    {
        if (!_hostMethods.TryGetValue(name, out var methods) || methods.Count == 0)
            return $"pub class {name} {{ }}";

        var body = string.Join(Environment.NewLine, methods
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .Select(m => "    " + m.Declaration));

        return $"pub class {name} {{{Environment.NewLine}{body}{Environment.NewLine}}}";
    }

    /// <summary>Compiles source held in memory.</summary>
    /// <param name="moduleName">The module name. Required: there is no file path to derive it
    /// from, and two scripts under the same name would collide.</param>
    /// <exception cref="EmbeddingException">The compilation reported errors.</exception>
    public ScriptModule Compile(string source, string moduleName) =>
        Build(ScriptSource.FromText(moduleName, source), moduleName, origin: null);

    /// <summary>Compiles a file. The module name follows the path.</summary>
    /// <inheritdoc cref="Compile(string, string)"/>
    public ScriptModule CompileFile(string path)
    {
        // The name is fixed here rather than left to the resolver, whose default would be 'main'
        // for every file.
        var name = Path.GetFileNameWithoutExtension(path);
        return Build(ScriptSource.FromDisk(path, name), name, origin: path);
    }

    /// <summary>
    /// Runs the module's <c>main</c> and returns its exit code.
    ///
    /// <para>The capability check happens here, at load time: the requirement is recorded in the
    /// module, and a host may load bytes that no compiler of its own produced.</para>
    /// </summary>
    /// <exception cref="ScriptException">No entry point, or a missing capability.</exception>
    /// <exception cref="ScriptPanicException">The script panicked.</exception>
    public int Run(ScriptModule module, params string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(module);
        try
        {
            return (int)Interpreter
                .Run(module.Loaded, arguments, _natives, _options.Capabilities)
                .AsI64;
        }
        catch (LyricPanic panic)
        {
            // Translated at the host boundary rather than passed through: 'LyricPanic' lives in
            // the runtime assembly, which a host does not reference.
            throw new ScriptPanicException(panic.Code, panic.Message, panic);
        }
        catch (LyricRuntimeException runtime)
        {
            throw new ScriptException(runtime.Code, runtime.Message, runtime);
        }
    }

    /// <summary>Compiles and runs, for a script that only runs once.</summary>
    /// <inheritdoc cref="Run(ScriptModule, string[])"/>
    public int RunScript(string path, params string[] arguments) =>
        Run(CompileFile(path), arguments);

    /// <summary>
    /// Loads a module and runs its constant initializer, producing the form a host calls
    /// functions on.
    ///
    /// <para>Separate from <see cref="Run"/>: that executes a program once, while an instance
    /// lives on. The module constants are computed once and every later call sees that state.
    /// </para>
    ///
    /// <para>A module without an entry point is the normal case here.</para>
    /// </summary>
    /// <exception cref="ScriptException">A missing capability, or an import that cannot be bound.
    /// </exception>
    public ScriptInstance Instantiate(ScriptModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        try
        {
            return new ScriptInstance(this, module,
                LoadedProgram.Load(module.Loaded, _natives, _options.Capabilities));
        }
        catch (LyricPanic panic)
        {
            // The constant initializer is ordinary Lyric code and can panic like any other.
            throw new ScriptPanicException(panic.Code, panic.Message, panic);
        }
        catch (LyricRuntimeException runtime)
        {
            throw new ScriptException(runtime.Code, runtime.Message, runtime);
        }
    }

    private ScriptModule Build(ScriptSource source, string name, string? origin)
    {
        var result = SourceCompiler.Compile(source, new CompilerOptions
        {
            StdlibRoot = _options.StdlibRoot,
            NativeRoots = _options.NativeRoots,
            NativeModules = HostModuleSource is { } host
                ? new Dictionary<string, string>(StringComparer.Ordinal) { [HostModule] = host }
                : null,
        });

        // 'Ok' rather than 'Bytes is not null': a compilation can produce bytes and report errors
        // at the same time.
        if (!result.Ok || result.Bytes is null)
            throw new EmbeddingException(
                $"'{name}' did not compile ({result.Diagnostics.ErrorCount} error(s))",
                result.Diagnostics.Diagnostics);

        // Loaded and validated at compile time rather than at run time, so a module that is never
        // executed is still known to be broken.
        return new ScriptModule(name, result.Bytes, BytecodeReader.ReadOrThrow(result.Bytes),
            origin);
    }
}
