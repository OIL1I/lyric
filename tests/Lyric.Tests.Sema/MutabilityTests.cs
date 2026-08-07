using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Sema;

/// <summary>
/// Was `let` bei einem Referenztyp bedeutet — ADR-020.
///
/// <para>Es bindet den <b>Namen</b>, nicht den Inhalt. Ein `class`-Objekt und ein `T[]` sind
/// beide Referenztypen (ADR-016) und verhalten sich deshalb gleich: der Name lässt sich nicht
/// neu binden, das Objekt dahinter schon.</para>
///
/// <para><b>Diese Datei entstand, weil es sie nicht gab.</b> Die alte Regel — „Container muss
/// mut sein" (`Sprache.md` §6.4) — stand seit P2 in der Spec und war durch <b>keinen einzigen
/// Test</b> abgesichert. Genau deshalb überlebte ihr Widerspruch zwei Meilensteine: sie verbot
/// `xs[0] = 9`, ließ aber `ps[0].hp = 9` durch, schützte also nichts.</para>
/// </summary>
public class MutabilityTests
{
    private static DiagnosticEngine Check(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        Semantics.Analyze(comp, comp.Resolve(), de);
        return de;
    }

    private static void Allowed(string source)
    {
        var de = Check(source);
        Assert.False(de.HasErrors,
            string.Join("\n", de.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }

    private static void Rejected(string source) =>
        Assert.Contains(Check(source).Diagnostics, d => d.Code == "LYR-SEM0019");

    // ------------------------------------------------------------------ die drei Fälle

    [Fact]
    public void A_class_field_is_writable_through_a_let_binding() =>
        Allowed("""
            class P { hp: int }
            fn main(): int { let p = P { hp = 1 }; p.hp = 9; return p.hp; }
            """);

    [Fact]
    public void An_array_element_is_writable_through_a_let_binding() =>
        // Das ist die Änderung von ADR-020. Vorher LYR-SEM0019 — und zwar als einziger der drei
        // Fälle, obwohl alle drei denselben Referenztyp anfassen.
        Allowed("fn main(): int { let xs = [1, 2]; xs[0] = 9; return xs[0]; }");

    [Fact]
    public void A_field_of_an_array_element_was_always_writable() =>
        // Der Fall, der die alte Regel entwertet hat: über das `let`-Array hindurch ließ sich
        // der Inhalt eines Elements ändern. Verboten war genau die eine Operation, die man
        // umgehen konnte, indem man ein Element mit einem Feld nahm.
        Allowed("""
            class P { hp: int }
            fn main(): int { let ps = [P { hp = 1 }]; ps[0].hp = 9; return ps[0].hp; }
            """);

    // ------------------------------------------------------------------ was weiterhin gilt

    [Fact]
    public void A_let_binding_cannot_be_rebound() =>
        // Der Kern der Regel, und er bleibt: `let` hält den NAMEN fest. Ohne diesen Test wäre
        // ADR-020 auch dann grün, wenn `let` gar nichts mehr bedeutete.
        Rejected("fn main(): int { let x = 1; x = 2; return x; }");

    [Fact]
    public void An_array_bound_with_let_cannot_be_rebound() =>
        Rejected("fn main(): int { let xs = [1, 2]; xs = [3]; return xs[0]; }");

    [Fact]
    public void A_parameter_cannot_be_assigned() =>
        // Parameter sind unveränderlich (§6.4) — davon ändert ADR-020 nichts.
        Rejected("fn f(n: int): int { n = 2; return n; }\nfn main(): int { return f(1); }");

    [Fact]
    public void A_struct_field_still_needs_a_mutable_base() =>
        // Ein `struct` ist ein WERT, kein Referenztyp — für ihn gilt die Kette weiter. ADR-020
        // spricht ausdrücklich nur über Referenztypen, und ohne diesen Test wäre nicht
        // festgehalten, dass die Unterscheidung überhaupt noch existiert.
        Rejected("""
            struct V { x: int, }
            fn main(): int { let v = V { x = 1 }; v.x = 9; return v.x; }
            """);

    [Fact]
    public void A_var_struct_field_is_writable() =>
        // Die Gegenprobe dazu: mit `var` geht es. Ohne sie bliebe der Test darüber auch grün,
        // wenn struct-Felder gar nicht mehr schreibbar wären.
        Allowed("""
            struct V { x: int, }
            fn main(): int { var v = V { x = 1 }; v.x = 9; return v.x; }
            """);
}
