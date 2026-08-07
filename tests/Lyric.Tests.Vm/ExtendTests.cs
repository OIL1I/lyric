using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Lyric.Vm;
using System.Runtime.CompilerServices;

namespace Lyric.Tests.Vm;

/// <summary>
/// `extend`-Blöcke (Sprache.md §3.6) — inhärent (P9a) und über ein Interface (P9b).
///
/// <para>Eine Extension-Methode ist eine gewöhnliche Funktion mit dem Empfänger als Parameter 0
/// (ADR-014). Kein neuer IR-Typ, kein Opcode, kein Format-Bump: der inhärente Aufruf ist ein
/// direkter <c>call</c>, weil der Compiler statisch weiß, welcher Typ am Empfänger steht. Die
/// Interface-Form füllt dieselbe vtable-Zeile wie eine deklarierte Konformanz — welcher der beiden
/// Wege sie begründet hat, ist zur Laufzeit nicht mehr unterscheidbar und soll es nicht sein.</para>
///
/// <para><b>Drei Tests hier sind wichtiger als die übrigen</b>, und jeder misst etwas, das ohne
/// ihn still falsch wäre. Erstens: eine Extension verdrängt einen gleichnamigen Member
/// <b>nicht</b> (§3.5/§3.6 — die Sema meldet den Fall nicht, sie lässt nur den eigenen Member
/// gewinnen); ohne den <c>&lt;extend&gt;</c>-Infix im Mangling stürzte ein sauber typgeprüftes
/// Programm im Verifier ab. Zweitens: ein Builtin-Empfänger kommt als Argument 0 an — das ist beim
/// Bauen fehlgeschlagen, weil ein Skalar kein <c>NamedRef</c> ist und deshalb in den
/// Typ-/Modul-Zweig der Aufruf-Lowerung fiel. Drittens: die Konformanz-Tests führen <b>zwei</b>
/// Implementierungen, eine deklariert und eine per <c>extend</c>; mit nur einer bliebe der Test
/// auch dann grün, wenn der Dispatch statisch an die erstbeste Funktion bände (Lehre aus P3).</para>
/// </summary>
public class ExtendTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static long Run(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de)
        {
            ModuleLoader = StdlibLoader.ForRoot(Path.Combine(RepoRoot(), "stdlib"), sm, de),
        };
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        var binding = comp.Resolve();
        var types = Semantics.Analyze(comp, binding, de);

        var writer = new StringWriter();
        de.RenderText(writer);
        Assert.False(de.HasErrors, "source did not compile:\n" + writer);

        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true);
        Assert.NotNull(ir);
        return Interpreter.Run(BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir!))).AsI64;
    }

    // ------------------------------------------------------------------ Empfängertypen

    [Fact]
    public void A_class_can_be_extended() =>
        Assert.Equal(42, Run("""
            class Player { hp: int }
            extend Player { fn doubled(): int { return this.hp * 2; } }
            fn main(): int { let p = Player { hp = 21 }; return p.doubled(); }
            """));

    [Fact]
    public void A_struct_can_be_extended() =>
        // Der Empfänger einer struct-Extension ist der Wert selbst — dieselbe Konvention wie bei
        // einer struct-Methode, und aus demselben Grund kein Sonderfall im Lowering.
        Assert.Equal(7, Run("""
            struct Vec { x: int, y: int, }
            extend Vec { fn sum(): int { return this.x + this.y; } }
            fn main(): int { let v = Vec { x = 3, y = 4 }; return v.sum(); }
            """));

    [Fact]
    public void An_enum_can_be_extended() =>
        Assert.Equal(2, Run("""
            enum Color { Red, Green }
            extend Color {
                fn rank(): int {
                    match (this) { Color.Red => { return 1; }, Color.Green => { return 2; } }
                }
            }
            fn main(): int { let c = Color.Green; return c.rank(); }
            """));

    [Fact]
    public void A_builtin_scalar_can_be_extended() =>
        // DER Test des Slice. Ein 'int' als Parameter 0 braucht kein Boxing und keinen Fat
        // Pointer — genau deshalb ist die inhärente Form billig und die Interface-Form (P9b) nicht.
        Assert.Equal(42, Run("""
            extend int { fn double(): int { return this * 2; } }
            fn main(): int { let n = 21; return n.double(); }
            """));

    [Fact]
    public void A_builtin_reference_type_can_be_extended() =>
        // 'string' ist der andere Builtin-Fall: eine Referenz statt eines Skalars. Der Rumpf
        // verzichtet bewusst auf Konkatenation — die lowert zu 'std.string.concat', und dieses
        // Harness bindet keine Natives. Gemessen werden soll der Empfänger, nicht die Stdlib.
        Assert.Equal(4, Run("""
            extend string { fn tag(): int { return 4; } }
            fn main(): int { let s = "ab"; return s.tag(); }
            """));

    // ------------------------------------------------------------------ Namensraum

    [Fact]
    public void An_extension_does_not_displace_a_member_of_the_same_name() =>
        // §3.5/§3.6: eigenes Member schlägt Extension. Die Sema meldet die Verdeckung NICHT — sie
        // lässt die Klassenmethode gewinnen und macht die Extension zu totem Code. Ohne den
        // <extend>-Infix im Mangling hießen beide 'test.Player.get', und der Verifier lehnt
        // doppelte Funktionsnamen ab: ein sauber typgeprüftes Programm stürzte im Lowering ab.
        Assert.Equal(1, Run("""
            class Player { hp: int, fn get(): int { return this.hp; } }
            extend Player { fn get(): int { return 99; } }
            fn main(): int { let p = Player { hp = 1 }; return p.get(); }
            """));

    [Fact]
    public void Two_extensions_on_the_same_type_coexist() =>
        Assert.Equal(30, Run("""
            class Item { n: int }
            extend Item { fn a(): int { return this.n * 2; } }
            extend Item { fn b(): int { return this.n * 4; } }
            fn main(): int { let i = Item { n = 5 }; return i.a() + i.b(); }
            """));

    [Fact]
    public void An_extension_method_can_call_another_one() =>
        // Beide bekommen ihre FunctionId in Pass 1, bevor ein Rumpf gelowert wird — sonst
        // scheiterte der Vorwärts-Aufruf hier, genau wie bei gewöhnlichen Funktionen.
        Assert.Equal(20, Run("""
            class Item { n: int }
            extend Item {
                fn once(): int { return this.n * 2; }
                fn twice(): int { return this.once() * 2; }
            }
            fn main(): int { let i = Item { n = 5 }; return i.twice(); }
            """));

    [Fact]
    public void An_extension_takes_parameters_after_the_receiver() =>
        // Der Empfänger ist Parameter 0, die geschriebenen Parameter folgen. Ein Test ohne
        // Parameter bliebe auch grün, wenn die Reihenfolge vertauscht wäre.
        Assert.Equal(23, Run("""
            class Item { n: int }
            extend Item { fn plus(k: int): int { return this.n + k; } }
            fn main(): int { let i = Item { n = 20 }; return i.plus(3); }
            """));

    // ------------------------------------------------------------------ extend T :: [I]  (P9b)

    [Fact]
    public void An_extension_can_supply_interface_conformance() =>
        // ZWEI Implementierungen, und die Lehre stammt aus P3: mit nur einer bliebe der Test auch
        // dann gruen, wenn der Dispatch statisch an die erstbeste Funktion baende. Eine davon
        // kommt aus einem extend-Block, die andere ist deklariert — beide muessen dieselbe
        // vtable-Zeile fuellen, denn zur Laufzeit ist nicht mehr unterscheidbar, welcher Weg die
        // Konformanz begruendet hat.
        Assert.Equal(30, Run("""
            interface Scored { fn score(): int; }
            class A { n: int }
            class B :: [Scored] { n: int, fn score(): int { return 20; } }
            extend A :: [Scored] { fn score(): int { return 10; } }
            fn total(x: Scored, y: Scored): int { return x.score() + y.score(); }
            fn main(): int {
                let a = A { n = 1 };
                let b = B { n = 2 };
                return total(a, b);
            }
            """));

    [Fact]
    public void Extension_conformance_satisfies_an_assignment() =>
        // Bis P9b gab es zwei Antworten auf "erfuellt T das Interface I": Constraints kannten
        // Extensions, Zuweisungen nicht. Dieser Test und der naechste messen dieselbe Konformanz
        // ueber die beiden Pfade, die auseinandergelaufen waren.
        Assert.Equal(7, Run("""
            interface Scored { fn score(): int; }
            class A { n: int }
            extend A :: [Scored] { fn score(): int { return 7; } }
            fn main(): int { let s: Scored = A { n = 1 }; return s.score(); }
            """));

    [Fact]
    public void Extension_conformance_satisfies_a_constraint() =>
        Assert.Equal(7, Run("""
            interface Scored { fn score(): int; }
            class A { n: int }
            extend A :: [Scored] { fn score(): int { return 7; } }
            fn get<T :: [Scored]>(x: T): int { return x.score(); }
            fn main(): int { return get(A { n = 1 }); }
            """));

    [Fact]
    public void An_own_member_still_beats_the_extension_in_the_vtable() =>
        // §3.5 gilt auch fuer die vtable-Zeile, nicht nur fuer den direkten Aufruf: die
        // Klassenmethode fuellt den Slot, die gleichnamige Extension bleibt toter Code.
        Assert.Equal(3, Run("""
            interface Scored { fn score(): int; }
            class A :: [Scored] { n: int, fn score(): int { return 3; } }
            extend A { fn score(): int { return 99; } }
            fn main(): int { let s: Scored = A { n = 1 }; return s.score(); }
            """));

    // ------------------------------------------------------------------ am M7-Gate gefunden

    // Die drei folgenden Faelle haben mit 'extend' nichts zu tun — sie kamen ans Licht, als
    // 'examples/inventory.lyr' zum ersten Mal durchlief. Das ist der Zweck eines Gates: ein
    // Programm, das mehrere Slices gleichzeitig belastet, findet die Kanten dazwischen. Alle drei
    // waren Luecken aus P2b/P3/P8, nicht aus P9.

    [Fact]
    public void An_interface_default_method_works_on_a_concrete_receiver() =>
        // Die Default-Methode gehoert dem INTERFACE, ihr 'this' ist der Interface-Typ — ein
        // direkter Aufruf fuehrt nicht hin. Der Empfaenger wird gehoben (mkiface), dann callvirt.
        // Ohne den Fix: "call to main.Priced.isFree: arg 0 is val ty0, expected dyn ty1".
        Assert.Equal(7, Run("""
            interface Priced {
                fn price(): int;
                fn isFree(): bool { return this.price() == 0; }
            }
            struct Item :: [Priced] { n: int, fn price(): int { return this.n; } }
            fn main(): int { let it = Item { n = 0 }; if (it.isFree()) { return 7; } return 1; }
            """));

    [Fact]
    public void An_own_member_still_beats_the_default_on_a_concrete_receiver() =>
        // Die Gegenprobe zu §3.5. Ohne sie bliebe der Test darueber auch gruen, wenn JEDER
        // Aufruf gehoben wuerde — und dann liefe eine ueberschriebene Methode nie.
        Assert.Equal(3, Run("""
            interface Priced {
                fn price(): int;
                fn isFree(): bool { return true; }
            }
            struct Item :: [Priced] {
                n: int,
                fn price(): int { return this.n; }
                fn isFree(): bool { return false; }
            }
            fn main(): int { let it = Item { n = 1 }; if (it.isFree()) { return 9; } return 3; }
            """));

    [Fact]
    public void A_match_over_an_optional_tests_and_unwraps() =>
        // Zwei Fehler in einem Ausdruck: 'null' als Muster wurde als GLEICHHEITSVERGLEICH
        // gelowert (es gibt keinen null-Operanden — es ist 'optissome'), und die Bindung im
        // anderen Arm speicherte das '?T' in einen 'T'-Slot. Die Sema gibt dem Namen den
        // eingeengten Typ; ausgepackt werden muss trotzdem, weil das Narrowing eine Aussage
        // ueber den Kontrollfluss ist und nicht ueber den Speicher.
        Assert.Equal(5, Run("""
            struct Item { n: int, }
            fn find(): ?Item { return Item { n = 5 }; }
            fn main(): int {
                let a = find();
                return match (a) { null => 0, it => it.n };
            }
            """));

    [Fact]
    public void A_match_over_an_optional_takes_the_null_arm_when_empty() =>
        // Die Gegenprobe: ohne sie bliebe der Test darueber gruen, wenn 'null' auf alles passte.
        Assert.Equal(4, Run("""
            struct Item { n: int, }
            fn find(): ?Item { return null; }
            fn main(): int {
                let a = find();
                return match (a) { null => 4, it => it.n };
            }
            """));

    [Fact]
    public void For_in_over_an_array_works_inside_a_generic_function() =>
        // In einer monomorphisierten Instanz muss der Iterator mit dem KONKRETEN Elementtyp
        // interniert werden. Ohne die Substitution suchte die Typtabelle nach einer Klasse
        // namens 'T' und warf — dieselbe Stelle, an der auch der Rueckgabetyp sie treffen muss.
        Assert.Equal(6, Run("""
            interface Priced { fn price(): int; }
            struct Item :: [Priced] { n: int, fn price(): int { return this.n; } }
            fn total<T :: [Priced]>(xs: T[]): int {
                var sum = 0;
                for (x in xs) { sum += x.price(); }
                return sum;
            }
            fn main(): int { return total([Item { n = 2 }, Item { n = 4 }]); }
            """));

    // Kein Test fuer 'static fn' in einem extend-Block: Sprache.md §3.6 laesst dort
    // 'FunctionDecl' zu, und 'static' ist per ADR-014 ein MEMBER-Marker, der nicht dazugehoert.
    // Der Parser lehnt entsprechend mit LYR-PAR0008 ab. Ob das gewollt ist, ist eine Sprachfrage
    // und keine Lowering-Frage — hier wird sie nicht beantwortet.
}
