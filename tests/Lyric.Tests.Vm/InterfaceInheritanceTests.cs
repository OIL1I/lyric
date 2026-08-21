using System.Runtime.CompilerServices;
using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Lyric.Vm;

namespace Lyric.Tests.Vm;

/// <summary>
/// Interface inheritance at RUNTIME: the chain-prefix slot layout is what makes a parent's
/// default run correctly behind a child-typed receiver, a child value answer the parent's
/// members, and a concrete type carry into the parent's interface type. Each test here pins one
/// of those dispatch paths end to end.
/// </summary>
public class InterfaceInheritanceTests
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
        return Interpreter.Run(BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir!)),
            NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null)).AsI64;
    }

    private const string Chain =
        """
        interface Score {
            fn points(): int;
        }

        interface Boosted :: [Score] {
            fn boosted(): int {
                return this.points() * 10;
            }
        }

        struct Player :: [Boosted] {
            base: int,

            fn points(): int {
                return this.base;
            }
        }

        """;

    [Fact]
    public void An_inherited_default_calls_the_parents_member_through_a_constraint()
    {
        // 'boosted' is Boosted's default; inside it, 'this' is a Boosted value whose method
        // table must answer the PARENT's 'points' — the chain-prefix layout at work.
        Assert.Equal(80, Run(Chain +
            """
            fn total<T :: [Boosted]>(x: T): int {
                return x.boosted() + x.points();
            }

            fn main(): int {
                return total(Player { base = 7 }) + 3;
            }
            """));
    }

    /// <summary>The mirror image of <see cref="Chain"/>: the DEFAULT sits on the parent and the
    /// abstract member below it, so every call through a child-typed value reaches a function
    /// whose own receiver is the parent's interface type.</summary>
    private const string DefaultOnParent =
        """
        interface Score {
            fn points(): int;

            fn doubled(): int {
                return this.points() * 2;
            }
        }

        interface Ranked :: [Score] {
            fn rank(): int;
        }

        struct Player :: [Ranked] {
            base: int,

            fn points(): int {
                return this.base;
            }

            fn rank(): int {
                return 1;
            }
        }

        """;

    [Fact]
    public void A_child_interface_value_runs_the_parents_default()
    {
        // The receiver is a Ranked value, the default belongs to Score. Both the dispatch and the
        // 'this.points()' inside the default have to survive the difference.
        Assert.Equal(14, Run(DefaultOnParent +
            """
            fn main(): int {
                let r: Ranked = Player { base = 7 };
                return r.doubled();
            }
            """));
    }

    [Fact]
    public void A_constraint_on_the_child_reaches_the_parents_default()
    {
        Assert.Equal(15, Run(DefaultOnParent +
            """
            fn scored<T :: [Ranked]>(x: T): int {
                return x.doubled() + x.rank();
            }

            fn main(): int {
                return scored(Player { base = 7 });
            }
            """));
    }

    [Fact]
    public void A_chain_of_three_reaches_the_grandparents_default()
    {
        Assert.Equal(31, Run(
            """
            interface A {
                fn base(): int;

                fn twice(): int {
                    return this.base() * 2;
                }
            }

            interface B :: [A] {
                fn mid(): int {
                    return this.twice() + 1;
                }
            }

            interface C :: [B] {
                fn leaf(): int;
            }

            class K :: [C] {
                fn base(): int {
                    return 3;
                }

                fn leaf(): int {
                    return 9;
                }
            }

            fn main(): int {
                let k: C = K { };
                return k.twice() + k.mid() + k.leaf() * 2;
            }
            """));
    }

    [Fact]
    public void An_array_of_child_interface_values_dispatches_per_element()
    {
        // The element position is a context: the class values are lifted into interface values
        // there, and the parent's default is reached through each of them.
        Assert.Equal(20, Run(DefaultOnParent +
            """
            fn main(): int {
                let xs: Ranked[] = [Player { base = 3 }, Player { base = 7 }];

                var sum = 0;
                for (x in xs) {
                    sum = sum + x.doubled();
                }
                return sum;
            }
            """));
    }

    [Fact]
    public void A_concrete_value_carries_into_the_implied_parent_interface()
    {
        Assert.Equal(7, Run(Chain +
            """
            fn main(): int {
                let s: Score = Player { base = 7 };
                return s.points();
            }
            """));
    }

    [Fact]
    public void A_child_interface_value_answers_the_parents_member()
    {
        Assert.Equal(77, Run(Chain +
            """
            fn main(): int {
                let b: Boosted = Player { base = 7 };
                return b.boosted() + b.points();
            }
            """));
    }

    [Fact]
    public void For_in_runs_over_an_iterator_inherited_through_the_chain()
    {
        Assert.Equal(6, Run(
            """
            import std.iter { Iterator };

            interface Counting :: [Iterator<int>] {
                mut fn reset(): void;
            }

            class UpTo :: [Counting] {
                limit: int,
                current: int,

                mut fn next(): ?int {
                    if (this.current >= this.limit) {
                        return null;
                    }
                    this.current = this.current + 1;
                    return this.current;
                }

                mut fn reset(): void {
                    this.current = 0;
                }
            }

            fn main(): int {
                var sum = 0;
                for (n in UpTo { limit = 3, current = 0 }) {
                    sum = sum + n;
                }
                return sum;
            }
            """));
    }

    [Fact]
    public void A_throw_reaches_through_the_chain_to_Throwable()
    {
        Assert.Equal(502, Run(
            """
            interface AppError :: [Throwable] {
                fn code(): int;
            }

            class NetError :: [AppError] {
                fn message(): string {
                    return "down";
                }

                fn code(): int {
                    return 502;
                }
            }

            fn risky(): int throws NetError {
                throw NetError { };
            }

            fn main(): int {
                try {
                    return risky();
                } catch (e: NetError) {
                    return e.code();
                }
            }
            """));
    }
}
