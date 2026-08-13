using Lyric.Resolver;

namespace Lyric.Ir.Lowering;

/// <summary>
/// Produces the names under which functions stand in the IR. They are not cosmetic: they become the
/// symbol names in the bytecode, and the verifier rejects collisions, because two functions under the
/// same name would be a silent wrong call.
///
/// <para>The scheme is <c>&lt;module path&gt;.&lt;function&gt;</c>, so <c>main.add</c>. The module path
/// is already unique — one file is one module — so that suffices.</para>
///
/// <para>For monomorphization an instance needs its type arguments in the name, or
/// <c>max&lt;int&gt;</c> and <c>max&lt;float&gt;</c> collide. That belongs here and nowhere else, which
/// is why the mangling lives in a class of its own rather than as string interpolation in the
/// lowerer.</para>
/// </summary>
internal static class NameMangling
{
    public static string ForFunction(ModuleSymbol module, string functionName) =>
        $"{module.FullName}.{functionName}";

    /// <summary>A method: <c>&lt;module&gt;.&lt;type&gt;.&lt;method&gt;</c>. The type name has to be in
    /// it, or <c>Account.get</c> and <c>Player.get</c> collide, and the verifier rejects duplicate
    /// function names, because they would be a silent wrong call.</summary>
    public static string ForMethod(ModuleSymbol module, string typeName, string methodName) =>
        $"{module.FullName}.{typeName}.{methodName}";

    /// <summary>An extension method:
    /// <c>&lt;declaring module&gt;.&lt;extend&gt;.&lt;target&gt;.&lt;method&gt;</c>.
    ///
    /// <para>Two things distinguish this from <see cref="ForMethod"/>. First, the DECLARING module
    /// stands here rather than that of the target type: <c>extend string</c> may stand in any number of
    /// modules, and the target type may belong to none of them. Second, the <c>&lt;extend&gt;</c> infix:
    /// an extension may shadow a member of the same name — the sema does NOT report that, it simply lets
    /// the own member win. Without the infix both would be called <c>main.Player.get</c>, and the
    /// verifier rejects duplicate function names: a cleanly type-checked program would crash in the
    /// lowering.</para>
    ///
    /// <para>The angle brackets are no accident — an identifier cannot contain them, so the name is not
    /// producible in source. The same convention as for <c>&lt;globals&gt;</c>.</para></summary>
    public static string ForExtension(ModuleSymbol declaringModule, string targetName, string methodName) =>
        $"{declaringModule.FullName}.<extend>.{targetName}.{methodName}";
}
