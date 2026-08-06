using Lyric.Resolver;

namespace Lyric.Ir.Lowering;

/// <summary>
/// Erzeugt die Namen, unter denen Funktionen in der IR stehen. Sie sind nicht kosmetisch:
/// aus ihnen werden die Symbolnamen im Bytecode (ADR-013), und der Verifier lehnt
/// Kollisionen ab, weil zwei Funktionen unter demselben Namen ein stiller Falsch-Call wären.
///
/// <para>Schema heute: <c>&lt;modulpfad&gt;.&lt;funktion&gt;</c>, also <c>main.add</c>. Der Modulpfad
/// ist schon eindeutig (eine Datei = ein Modul, ADR-012), damit reicht das für alles, was P4
/// lowert.</para>
///
/// <para>Erweiterung für die Monomorphisierung: eine Instanz braucht ihre Typargumente im Namen,
/// sonst fallen <c>max&lt;int&gt;</c> und <c>max&lt;float&gt;</c> zusammen. Das gehört hierher und
/// nirgends sonst — genau deshalb steht das Mangling in einer eigenen Klasse und nicht als
/// String-Interpolation im Lowerer.</para>
/// </summary>
internal static class NameMangling
{
    public static string ForFunction(ModuleSymbol module, string functionName) =>
        $"{module.FullName}.{functionName}";

    /// <summary>Eine Methode: <c>&lt;modul&gt;.&lt;Typ&gt;.&lt;methode&gt;</c>. Der Typname muss
    /// hinein, sonst kollidieren <c>Account.get</c> und <c>Player.get</c> — und der Verifier lehnt
    /// doppelte Funktionsnamen ab, weil sie ein stiller Falsch-Call wären.</summary>
    public static string ForMethod(ModuleSymbol module, string typeName, string methodName) =>
        $"{module.FullName}.{typeName}.{methodName}";
}
