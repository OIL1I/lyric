namespace Lyric.Core;

/// <summary>
/// The toolchain version: one number for every binary of the suite.
///
/// <para>Separate from the bytecode format version: a third-party runtime carries its own
/// toolchain version and the same format version.</para>
/// </summary>
public static class ToolchainVersion
{
    /// <summary>
    /// Raised at a release tag, here and in <c>Directory.Build.props</c>. MSBuild cannot read a C#
    /// constant, so both exist and a test compares them against the generated assembly attribute.
    ///
    /// <para>The editor clients (<c>lyriclang/vscode-lyric</c>, <c>lyriclang/jetbrains-lyric</c>)
    /// version independently, in their own repositories.</para>
    /// </summary>
    public const string Value = "2.1.0";
}
