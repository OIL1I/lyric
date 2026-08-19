using Lyric.Core;
using Lyric.Formatting;

namespace Lyric.Tests.Formatting;

/// <summary>
/// Source in, formatted source out — the shape of the output pinned case by case, and every
/// case checked for idempotence: the second pass must change nothing, or the formatter argues
/// with itself in every save hook.
///
/// <para>Comment handling is the next slice; the inputs here carry none.</para>
/// </summary>
public class FormatterTests
{
    private static string Format(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("<test>", source);
        var de = new DiagnosticEngine(sm);
        var formatted = Formatter.Format(sm, id, de);

        Assert.False(de.HasErrors, "the test source did not parse");
        Assert.NotNull(formatted);

        // Idempotence, on every case of this file: format(format(x)) == format(x).
        var sm2 = new SourceManager();
        var id2 = sm2.AddVirtual("<test2>", formatted);
        var second = Formatter.Format(sm2, id2, new DiagnosticEngine(sm2));
        Assert.Equal(formatted, second);

        return formatted;
    }

    [Fact]
    public void Whitespace_is_normalized_and_a_blank_line_survives_as_one()
    {
        Assert.Equal("""
            fn main(): int {
                let x = 1;

                return x;
            }

            """, Format("fn   main( ):int{let x=1;\n\n\n   return x;}"));
    }

    // On the 'bit' case: '&' binds TIGHTER than '==' in this grammar (§6.1, level 8 against 12),
    // unlike in C — the parentheses there are redundant and go like any others.
    [Fact]
    public void Redundant_parentheses_go_and_needed_ones_stay()
    {
        Assert.Equal("""
            fn f(a: int, b: int): int {
                let keep = (a + b) * 2;
                let drop = a * b + 2;
                let bit = a & 3 == 1;
                return keep + drop;
            }

            """, Format("""
            fn f(a: int, b: int): int {
                let keep = ((a + b)) * 2;
                let drop = ((a * b)) + (2);
                let bit = ((a & 3) == 1);
                return (keep) + (drop);
            }
            """));
    }

    [Fact]
    public void Right_associative_coalesce_keeps_its_shape()
    {
        var formatted = Format("""
            fn f(a: ?int, b: ?int, c: int): int {
                let flat = a ?? b ?? c;
                let forced = (a ?? b) ?? c;
                return flat + forced;
            }
            """);

        Assert.Contains("let flat = a ?? b ?? c;", formatted);
        Assert.Contains("let forced = (a ?? b) ?? c;", formatted);
    }

    [Fact]
    public void A_call_that_does_not_fit_breaks_one_argument_per_line()
    {
        var wide = "a" + new string('x', 80);
        var expected = $"fn f(): void {{\n    someFunction(\n        {wide},\n"
                       + "        second,\n        third\n    );\n}\n";

        Assert.Equal(expected, Format($"fn f(): void {{ someFunction({wide}, second, third); }}"));
    }

    [Fact]
    public void A_struct_initializer_fits_flat_and_breaks_with_a_trailing_comma()
    {
        var formatted = Format("""
            struct P {
                x: int,
                y: int,
            }

            fn f(): void {
                let flat = P { x = 1, y = 2 };
                let broken = P { x = 1111111111111111111, y = 2222222222222222222 + 3333333333333333333 + 4444444444 };
            }
            """);

        Assert.Contains("let flat = P { x = 1, y = 2 };", formatted);
        Assert.Contains("""
                let broken = P {
                    x = 1111111111111111111,
                    y = 2222222222222222222 + 3333333333333333333 + 4444444444,
                };
            """, formatted);
    }

    [Fact]
    public void Match_arms_stand_one_per_line()
    {
        Assert.Equal("""
            fn f(n: int): int {
                return match (n) {
                    0 => 1,
                    1 | 2 => 2,
                    3..=9 => 3,
                    _ => {
                        return 4;
                    }
                };
            }

            """, Format("fn f(n: int): int { return match (n) { 0 => 1, 1 | 2 => 2, 3..=9 => 3, _ => { return 4; } }; }"));
    }

    [Fact]
    public void An_enum_parts_variants_from_methods_with_a_semicolon()
    {
        Assert.Equal("""
            enum Shape {
                Circle(float),
                Rectangle(float, float);

                fn area(): float {
                    return match (this) {
                        Circle(r) => 3.14 * r * r,
                        Rectangle(w, h) => w * h,
                    };
                }
            }

            """, Format("""
            enum Shape { Circle(float), Rectangle(float, float);
            fn area(): float { return match (this) { Circle(r) => 3.14 * r * r, Rectangle(w, h) => w * h, }; } }
            """));
    }

    [Fact]
    public void An_else_if_ladder_stays_a_ladder()
    {
        Assert.Equal("""
            fn f(n: int): int {
                if (n < 0) {
                    return -1;
                } else if (n == 0) {
                    return 0;
                } else {
                    return 1;
                }
            }

            """, Format("fn f(n: int): int { if (n<0) { return -1; } else if (n==0) { return 0; } else { return 1; } }"));
    }

    [Fact]
    public void Imports_sit_together_and_declarations_breathe()
    {
        Assert.Equal("""
            import std.io.console;
            import std.collections { emptyList, emptyMap };

            fn a(): void { }

            fn b(): void { }

            """, Format("""
            import std.io.console;

            import std.collections {emptyList,emptyMap};
            fn a(): void {}
            fn b(): void {}
            """));
    }

    [Fact]
    public void Literal_spelling_survives()
    {
        var formatted = Format("""
            fn f(): void {
                let hex = 0xFF_EC;
                let grouped = 1_000_000;
                let suffixed = 3u8;
                let sci = 1.5e-3f32;
                let s = "a\tb";
                let c = '\n';
                let msg = f"n = {grouped:N0}, done";
            }
            """);

        Assert.Contains("0xFF_EC", formatted);
        Assert.Contains("1_000_000", formatted);
        Assert.Contains("3u8", formatted);
        Assert.Contains("1.5e-3f32", formatted);
        Assert.Contains("\"a\\tb\"", formatted);
        Assert.Contains("'\\n'", formatted);
        Assert.Contains("f\"n = {grouped:N0}, done\"", formatted);
    }

    [Fact]
    public void Types_keep_their_binding_parentheses()
    {
        var formatted = Format("""
            fn f(a: ?int[], b: (?int)[], c: (fn(int) -> bool)[], d: fn(int) -> int[]): void { }
            """);

        Assert.Contains("a: ?int[]", formatted);
        Assert.Contains("b: (?int)[]", formatted);
        Assert.Contains("c: (fn(int) -> bool)[]", formatted);
        Assert.Contains("d: fn(int) -> int[]", formatted);
    }

    [Fact]
    public void A_lambda_as_an_operand_is_parenthesized_and_as_an_argument_is_not()
    {
        var formatted = Format("""
            fn apply(f: fn(int) -> int, n: int): int {
                return f(n);
            }

            fn g(): int {
                let direct = apply((n: int) => n * 2, 10);
                return direct;
            }
            """);

        Assert.Contains("apply((n: int) => n * 2, 10)", formatted);
    }

    [Fact]
    public void Members_with_bodies_get_air_and_fields_sit_together()
    {
        Assert.Equal("""
            pub struct Vec2 :: [Add<Vec2>] {
                x: float,
                y: float,

                fn add(other: Vec2): Vec2 {
                    return Vec2 { x = this.x + other.x, y = this.y + other.y };
                }
            }

            """, Format("""
            pub struct Vec2::[Add<Vec2>] { x: float, y: float,
            fn add(other: Vec2): Vec2 { return Vec2 { x = this.x + other.x, y = this.y + other.y }; } }
            """));
    }

    [Fact]
    public void Coroutines_defer_and_try_round_trip()
    {
        Assert.Equal("""
            fn f(): void throws {
                defer {
                    cleanup();
                }
                try {
                    risky();
                } catch (e: IoError) {
                    handle(e);
                } catch (_) {
                    swallow();
                }
            }

            """, Format("""
            fn f(): void throws { defer { cleanup(); } try { risky(); }
            catch (e: IoError) { handle(e); } catch (_) { swallow(); } }
            """));
    }

    [Fact]
    public void Attributes_stand_on_their_own_lines()
    {
        Assert.Equal("""
            @Component
            pub struct Health {
                value: int,
                max: int = 100,
            }

            @System { order = 10 }
            pub fn tick(dt: float): void { }

            """, Format("""
            @Component pub struct Health { value: int, max: int = 100 }
            @System{order=10} pub fn tick(dt: float): void {}
            """));
    }

    [Fact]
    public void A_module_header_leads_with_a_blank_line_after_it()
    {
        Assert.Equal("""
            module geometry.shapes;

            pub fn area(r: float): float {
                return 3.14 * r * r;
            }

            """, Format("module geometry.shapes;\npub fn area(r: float): float { return 3.14*r*r; }"));
    }

    [Fact]
    public void Generic_constraints_and_type_arguments_round_trip()
    {
        var formatted = Format("""
            fn total<T :: [Add<T>]>(values: T[], zero: T): T {
                var sum = zero;
                for (v in values) {
                    sum = sum + v;
                }
                return sum;
            }

            fn use(): int {
                return total<int>([1, 2, 3], 0);
            }
            """);

        Assert.Contains("fn total<T :: [Add<T>]>(values: T[], zero: T): T", formatted);
        Assert.Contains("total<int>([1, 2, 3], 0)", formatted);
    }

    [Fact]
    public void An_opaque_type_alias_round_trips()
    {
        Assert.Equal("""
            opaque type Entity = int;

            pub type Meters = int;

            """, Format("opaque   type Entity=int;\npub type Meters = int;"));
    }

    [Fact]
    public void An_interface_parent_list_round_trips()
    {
        Assert.Equal("""
            interface Named {
                fn name(): string;
            }

            interface Labeled :: [Named] {
                fn label(): string;
            }

            """, Format("interface Named{fn name():string;}\ninterface Labeled::[Named]{fn label():string;}"));
    }
}
