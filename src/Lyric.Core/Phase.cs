namespace Lyric.Core;

/// <summary>
/// The pipeline steps as a user sees them.
///
/// <para>The boundaries are where <c>SourceCompiler</c> calls the libraries in turn. Anything
/// finer would have to thread a progress notion through lexer, parser, resolver and sema.</para>
/// </summary>
public enum Phase
{
    /// <summary>Read the source file from disk.</summary>
    Read,

    /// <summary>Tokenize and parse.</summary>
    Parse,

    /// <summary>Load imported modules.</summary>
    Load,

    /// <summary>Resolve names, build symbol tables.</summary>
    Resolve,

    /// <summary>Type checking.</summary>
    Check,

    /// <summary>AST to mid-level IR.</summary>
    Lower,

    /// <summary>Checking the IR invariants. A phase of its own, so the share it takes appears in every
    /// <c>--verbose</c> run.</summary>
    Verify,

/// <summary>IR to <c>.lyrbc</c> bytes.</summary>
    Emit,
}

/// <summary>
/// Which phases THIS BUILD actually runs.
///
/// <para>The list is no constant: the verifier runs in debug builds only, as LLVM's does in assert
/// builds; the reasoning is at <c>ModuleLowerer.VerifyByDefault</c>. It stands here rather than in the
/// frontend, because the tooling tests need it too — they drive the binaries as processes and
/// deliberately do not reference the frontend.</para>
///
/// <para>Written twice, it drifts: the test of the <c>--verbose</c> table carried the phase list as a
/// literal and was therefore red in release while debug stayed green. A rule two places have to know
/// belongs at the one place both of them see.</para>
/// </summary>
public static class Pipeline
{
    /// <summary>Does this build check the IR invariants after the lowering?</summary>
    public static bool VerifiesIr =>
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>The phases in pipeline order, without the ones this build skips.</summary>
    public static IReadOnlyList<Phase> OfThisBuild { get; } =
        Enum.GetValues<Phase>()
            .Where(phase => phase != Phase.Verify || VerifiesIr)
            .ToArray();
}

/// <summary>How a phase is named in the output.</summary>
public static class PhaseNames
{
    /// <summary>The short form for the timing table, lower-case like a command.</summary>
    public static string Short(Phase phase) => phase switch
    {
        Phase.Read => "read",
        Phase.Parse => "parse",
        Phase.Load => "load",
        Phase.Resolve => "resolve",
        Phase.Check => "check",
        Phase.Lower => "lower",
        Phase.Verify => "verify",
        Phase.Emit => "emit",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "unhandled phase"),
    };

    /// <summary>The progressive form for the live line: what the compiler is doing right now.</summary>
    public static string Progressive(Phase phase) => phase switch
    {
        Phase.Read => "Reading",
        Phase.Parse => "Parsing",
        Phase.Load => "Loading",
        Phase.Resolve => "Resolving",
        Phase.Check => "Checking",
        Phase.Lower => "Lowering",
        Phase.Verify => "Verifying",
        Phase.Emit => "Emitting",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "unhandled phase"),
    };
}
