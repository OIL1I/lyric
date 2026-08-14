# Lyric — Current State

> This file is the **only** one in the project that changes often. It is updated
> after every finished slice. Claude reads it at the start of a session to know
> where we stand.
>
> Keep the content short. Anything already committed can go —
> `git log --oneline` is the history, not this file.

---

## Current milestone

**M0–M10 are finished and tagged** (`m0`–`m10-complete`, `v0.1.0`/`v0.5.0`/`v0.9.0`).
**v1.0 is not reached** — what is missing stands under `## What v1.0 still needs`.

2675 tests green **in Debug and Release**, bytecode format **3.0**, **four** binaries plus
`lyrembed.dll`, version **0.9.0**.

**What this state can do**: the whole language of the grammar compiles and runs; a standard library
that largely carries itself (`Map`, `Set`, merge sort, all iterator adapters and the string hash are
written in Lyric); four tools including the REPL; a VS Code extension; and an embedding API with
which a C# host loads scripts, sandboxes them, calls functions out of them and hands its own
functions and types in.

> **The file had grown to 1088 lines by 2026-08-07** and contradicted itself in three places. It has
> been cut back to its own maintenance rule: recent slices, open points, design context. Everything
> else stands in `git log`.

## Recently finished

- [x] **Generic enums** (2026-08-12). 2711 tests green, Debug and Release.
  - `enum Opt<T>` was **not present at all** in the lowering: `TypeTable.InternEnum` threw as soon as
    such an enum appeared even as a parameter type. Now `Opt<int>.Some(5)`, `.None`,
    `Ev<int>.Hit { at = 4 }`, `match` with payload binding and a guard, the recursive `Tree<T>`, a
    generic enum inside a generic function and as a field all work.
  - **It was not a missing feature but missing wiring.** The substitution was there and already
    routed enums correctly; `InternVariant` would always have lowered `Some(T)` to `Some(int)`. What
    was missing were four places that threw the instance away, above all `TypeFacts.SymbolOf`, which
    returns the definition for a `GenericInstance` and loses the type arguments.
  - **My estimate was too high by a factor of three.** I had said "two to three sessions" before I
    had read `Intern`. It was one. The mistake was estimating the size from the symptom rather than
    from the code.
  - **The load-bearing guarantee**: `Opt<int>` and `Opt<string>` get their own variant layouts. If
    they shared an entry, an `i64` would lie in a string slot — in Debug a verifier finding, in
    Release a silently wrong answer. Exactly the asymmetry that made the conformance gap of
    2026-08-11 so expensive.
  - The sharpest trap was the recursion: the id has to stand in the instance registry *before* the
    variants are interned, or `Node(Tree<T>, …)` requests precisely the instance that is being
    created. `InternLayout` showed how.
  - **An older inconsistency was removed along the way**: the struct form had always read the
    expected type (`let e: Ev<int> = Ev.Hit { … }`), the tuple form had not (`Opt.Some(7)` was an
    error). One question, two answers, depending on the shape of the variant. Both now run through
    the same resolution.
  - Remaining: in an **argument position** the context does not reach (`take(Opt.Some(5))`) — the
    expected type is not passed through to there. Recorded as a test rather than as a guess.

- [x] **`Pair<int>.of(3)` works** (2026-08-12) — a static factory on a generic type.
  - The parser read `Pair` as an identifier and `<` as a comparison, then stumbled over the dot.
    **The detection costs no ambiguity**: the `<` counts as a type argument list when it closes
    balanced and a `.` follows — a dot after a comparison chain is not a valid expression anyway.
    The same rule the grammar has drawn for `f<int>()` since 2026-08-07; Rust's `::<>` would be a
    second mechanism for the same concept.
  - **The sema was the actual gap.** `MemberOfType` returned the member type unsubstituted, which
    produced "cannot assign 'int' to 'T'", a message about the consequence. Now `NonValueType`
    carries the resolved instance, and without arguments there is `LYR-SEM0063`, which names the
    cause: the arguments are required, and `Pair.of(3)` does not infer.
  - Found in the lowering: `InstanceTable.RequestMethod` also appended a `this` to a **static**
    method. The verifier saw "passes 1 arg(s), expected 2".
  - **`std.collections` carried the evidence as a comment** — `emptyList` is a free function
    "because a static method on a generic instance is not expressible". That sentence is no longer
    true and now stands correctly; the function stays, because the rework costs every caller and
    gains nothing.
  - **`Opt<int>.Some(5)` is therefore NOT done** and was never the same item: the lowering does not
    know generic enums at all (`TypeTable.InternEnum` throws `LYR-IR0001` as soon as one occurs even
    as a parameter type). Measured, not assumed — see `## Still open`.
  - 24 new tests, of which 9 are parser counter-checks (`a < b > c.d` stays a comparison) and one
    secures `lyrc ast`: the `AstDumper` throws on every node it does not know.

- [x] **`List<T>.clear()` and `.toArray()`** (2026-08-12, written by the maintainer).
  - `toArray` is the more interesting part: the return is `T[]`, the backing is `(?T)[]`, and
    **there is no reinterpretation between them**. `!` unwraps a single value, not an array element
    by element — `?T[]` is an *array of optionals* and not an optional array. The first version tried
    `return result!;` and was `LYR-SEM0005`.
  - It is now built as `T[]` from the start. The empty list has no first element to build one from
    and is caught beforehand (`return [];`).
  - Seven tests, among them: the length is `count` and not `capacity` (the same mistake `get` once
    made), the copy is a copy, and `clear` really releases the backing rather than leaving it
    standing behind `count`.

- [x] **`b?.get()` works** (2026-08-12). 2661 tests green, Debug and Release.
  - The sema turned `?.get` into a `?fn() -> int` and then reported `LYR-SEM0013: not callable`, a
    statement about an intermediate type nobody wrote down. Now `CheckCall` unwraps the receiver when
    the callee is a `?.` member and puts the optional around the *result*. The workaround
    (`if (b != null) { b.get() }`) was three times as long.
  - **The call runs through the same resolution as every other one.** All five dispatch routes carry
    it without a line of extra code: a concrete class, a generic instance, an interface (dynamic), a
    constrained type parameter, and a primitive receiver with an extension. A second path would have
    had to answer each of them once more.
  - **The first attempt was exactly that second copy**, only disguised: a special case in the callee
    `switch` that appended the unwrapped receiver. It stood **before** the generics and interface
    detection and hid them — `b?.get()` on a `Box<int>` became *"external or bodiless"*, a diagnostic
    about the wrong cause.
  - The two deviations now hang **on the AST node**: the unwrapped receiver on the target, the return
    type on the call. The case distinction asks the receiver type through a place that unwraps along
    the chain. As parameters it would have cost four more signatures that none of them care about.
  - **An older inconsistency surfaced along the way**: `b?.w` on a field `w: ?int` produced `??int`,
    and the error came one level too late as "cannot assign '?int' to 'int'". Optionals do not nest —
    both places now collapse, sema *and* lowering. Again **one question, two places**; this time both
    found on the first attempt, because the verifier finding (`call dest t61 is i64 but Box.leer
    returns ?i64`) pointed straight at it.
  - An empty receiver **does not evaluate the arguments**. The test measures that with a side effect;
    without it, it would stay green if they were computed before the check.
  - **Where it stops, it says so** (`LYR-SEM0062`): if the member holds a function *value*
    (`f: fn() -> int`), there are two questions and one `?` — whether the receiver is there and
    whether the field is filled. Unwrapping there answers the second one silently with yes; with
    `f: ?fn() -> int` that is a call on null. The message names the way out, and a test checks that
    the way out compiles.
  - The grammar and the nullable chapter of the guide now state the call form.

- [x] **`s = Small { n = 5 };` works** (2026-08-11). 2675 tests green.
  - The grammar allows the expression "in every value position", and the right-hand side of an
    assignment is one. `ParseExprStmt` switched the ambiguity guard off for the **whole** statement —
    it applies to the *beginning*, because a block could stand there. After an `=` no block can.
  - **The message was the real annoyance**: `'Small' is a type, not a value — did you mean
    'Small { . }'?` suggested exactly what already stood there. Known for a long time, and on
    2026-08-07 the maintainer ran into it again while writing a measurement without recognising it.
  - **The counter-check is the more important half**: at the start of a statement it stays blocked, a
    block stays a block, and `c = a < b` stays a comparison. A fix that removed the guard entirely
    cost no diagnostic but a wrong reading.
  - **`Opt<int>.Some(5)` is *not* the same cause** — measured, not assumed. I had estimated both as
    one item; the fix here moved nothing there. The corrected effort stands under `## Still open`.

- [x] **Two diagnostics that pointed at the wrong cause** (2026-08-11). 2668 tests green.
  - **An attribute on a parameter** was read as a parameter name; afterwards the body was missing and
    the compiler spoke of *native declarations* to someone who wanted to write `@noCapture`. Now the
    same message as on a declaration (`LYR-PAR0038`), and the body is kept: a test checks that it
    stays at **one** message.
  - **`interface B :: [A]`** ran into a message about parameter parentheses. Now `LYR-PAR0039`, and
    it names the way out, because there is one: `std.core` solves the same with two constraints side
    by side. The conformance list is read and discarded — **one diagnostic per cause**, or the parser
    would stumble over `[A]` a second time.
  - Neither costs expressiveness: both forms stay rejected. It cost time — a diagnostic pointing at
    the wrong place is more expensive than none, because that is where you search.
  - The counter-check stands beside it: `class K :: [A]` stays valid. Without it half the standard
    library would be a syntax error and the test would still be green.

- [x] **Conformance checks its type arguments** (2026-08-11). 2656 tests green.
  - **The most serious finding of this work, and it stood in this file as a triviality.** Until then
    conformance compared only the interface *symbol*: `class Ones :: [Src<int>]` satisfied a
    `<T :: [Src<string>]>`, and the body put an `i64` into a `string` slot.
  - **Debug caught it in the verifier. Release — what actually ships — ran through** and gave a
    silently wrong answer; the bytecode loader did not catch it either. Not a missing feature but a
    type checker accepting a program whose types do not hold. That .NET contains the damage (an empty
    string rather than a memory fault) is luck of the value representation.
  - **The same gap sat in two places**: at the constraint *and* at the assignment to an interface
    type, both running through the same comparison. The ninth time for that pattern in this project.
  - The full substitution map is passed through rather than one parameter after another: a constraint
    may name the remaining type parameters (`<K, V :: [Map<K, V>]>`), and `Eq<T>` is only with
    `T := int` the question that is really asked.
  - **Ten tests, both directions.** `Map<K :: [Hashable<K>, Equatable<K>]>` and `Iterator<T>` are the
    heaviest users of generic constraints in the standard library and stayed untouched — without the
    counter-checks a fix that rejects too much would be indistinguishable from a correct one.

- [x] **The two crashes from the v1.0 list** (2026-08-11). 2646 tests green.
  - **`do { return … } while (…)`** was a compiler crash: the body, the condition and the exit were
    all three created up front, and if the body terminated, two blocks were left without
    predecessors — which the verifier rejects, because there is no `SimplifyCfg` pass. They are now
    created **on demand**.
  - **The trap was the condition under which that is decided.** STATUS described the case for a long
    time as "the body terminates", which it cannot be pinned to:
    `do { if (c) { break; } return 2; }` does **not** fall through and reaches the exit anyway.
    *Is the block reachable* and *does the body fall through* are two questions; only the first
    counts. A test stands exactly for that, and a fix that is too simple is green with the first one
    and red with it.
  - **The same solution for the third time**: the merge block of `match` and the one of `try` were
    the same mistake. The lesson already stood in this file on 2026-08-07 — *a merge block belongs on
    demand as a matter of principle*. `do-while` was the third case, and nobody had looked at it.
  - **The second "crash" was none any more.** `DeclaredTypes.Lower` has long returned a diagnostic
    with a position, on the import path as on the host method path, both measured. The STATUS entry
    was stale; I had carried it unchecked as blocking in the last report.

## Measurements

Numbers instead of opinions. Taken 2026-08-07, Release, 100 000 iterations, adjusted for a scalar
loop of the same length.

| What | Bytes per operation |
|---|---|
| Struct construction **plus** method call (`Vec2.add`) | **352 B** |
| call only (`fn step(a: float): float`) | **176 B** |
| struct construction only | **112 B** |
| scalar baseline | 9 064 B *in total* |

**The VM is allocation-free at its core** — a loop with floating-point arithmetic allocates nothing
worth mentioning over 100 000 passes. Everything above that is calls and objects.

**Half the bytes have nothing to do with structs**: `Frame.For` allocates three objects per call
(frame, slots, stack). That fixes the order for a later optimization — **frame pooling, then
inlining, then scalar replacement**, not the other way round: the value built in `add` **escapes**
(it is returned), so escape analysis without prior inlining finds nothing. **None of it is built in
v1.**

Within the frame budget: 1000 entities × 10 vector operations × 60 fps ≈ 211 MB/s, roughly one gen0
collection per frame. Gen0 is short — **no reason to move vector mathematics behind natives.**

Measured further: `for-in` over a range costs **1.28×** against a `while` loop. The verifier is
**~50 %** of the lowering time in Debug, not ~90 % — the old claim never had a source. A Release
profile is still outstanding.

## What we are working on

**M10 is finished**, inventory included. **v1.0 is not** — what is missing stands below under
`## What v1.0 still needs`: a `CHANGELOG.md`, platform-specific binaries, a documentation site, and
the decision which of the open language gaps block v1.

**The open question to answer before E4**: the lifetime and identity of a host object across the
boundary — does the host keep it alive or the VM? That is the one place in M10 where I have no
answer yet, and it belongs asked before E4 starts.

**The reachability analysis is in place** — pulled forward, because `std.string` made the effect
painfully visible for the first time: two tests holding that "a hello world carries no string
machinery" became false. Writing them green would have meant giving up a promise rather than
redeeming it.

**The `v0.9.0` tag is set** (annotated, its message is the release note — CONTRIBUTING §Releases, no
`CHANGELOG.md` before v1.0), together with `m9-complete`. Both point at the **first state on which
all three CI jobs are green** — not at "M9: polish" of 2026-08-07, where `dotnet test` was red in
Release and the shipping build did not build. Putting a tag there would be the "done by intent
alone" that Rule 3 forbids.

They briefly stood one commit earlier and were moved: there the tests were green but the publish job
was red. **A tag is the one thing that cannot be quietly corrected afterwards** — which is why moving
it was right and would not have been a week later.

## What v1.0 still needs

**M0–M10 are finished in substance.** The release is not, and the list is short enough to work
through point by point.

**Process (CONTRIBUTING):**

- ~~milestone tags~~ **done** (2026-08-11): `m5-complete`, `m7-complete`, `m8-complete`,
  `m10-complete` and `v0.5.0` have been added. They point at the **historical** completion commits
  rather than at HEAD — a tag marks when a milestone was finished, and hanging it at the end made the
  history unusable. (`m9-complete` is the justified exception: it was moved deliberately, because M9
  was *not* finished in substance at that point.) Rule 3 is thereby satisfied for M0–M10.
- **`CHANGELOG.md`** — §Releases: *"From `v1.0.0` on: tag, GitHub release page, and a `CHANGELOG.md`
  entry."* Before v1.0 there deliberately was none; from v1.0 there is one.
- **GitHub release page** for the `v1.0.0` tag.

**Artifact:**

- **Binaries for Windows/Linux/macOS** via `dotnet publish -r …`. `publish.proj` ships
  **framework-dependent** today and without a RID matrix — it needs a .NET 10 runtime on the target
  machine.
- **Documentation site** (static HTML out of the docs). There is none.

**The two crashes are fixed** (2026-08-11). What remains under `## Still open` are limits **with a
diagnostic** (`Opt<int>.Some(5)`, `@noCapture`, interface inheritance) — they cost expressiveness,
not a crash. **Whether they block v1 is a decision and not a measurement.**

## Still open

**From the M10 plan, found while measuring:**

- **The member separator is written for block bodies.** A bodiless method in a class needs `int;,` —
  a semicolon *and* a comma in a row. Remeasured 2026-08-14: **effectively unreachable**. A bodiless
  method in a class is `LYR-SEM0051` outside the standard library, and no standard library class
  declares one. A latent inconsistency of one line in `ParseTypeMembers`, not a gap anyone can hit.

**Language gaps to close before v1:**

- **The expected type does not reach an argument position.** Remeasured 2026-08-14, and the earlier
  entry was wrong in both directions: the **return position does reach** —
  `fn g(): O<int> { return O.S(1); }` compiles — while an argument position does not.
  `f(O.S(1))` is `LYR-SEM0063`, `f(O.S { v = 1 })` is `LYR-SEM0026`, `f(O.N)` is `LYR-SEM0063`.
  The way out works: `f(O<int>.S(1))`.
- **A generic struct initializer draws its instance from NO context.** A gap of its own rather than a
  shade of the one above, found 2026-08-14: `let p: P<int> = P { v = 1 }` is `LYR-SEM0001`
  (*cannot assign 'P&lt;&gt;' to 'P&lt;int&gt;'*) even WITH the annotation that carries the enum
  case. Only `P<int> { v = 1 }` works.
- ~~**`lyric check` runs only up to the sema**~~ **done** (2026-08-14). It runs the lowering and the
  verifier and stops before the bytes, so `check` and `build` answer the same question. 82 places in
  the lowering can report a limit and none was reachable from `check`.
  `tests/Lyric.Tests.Cli/CheckAgreesWithBuildTests.cs` compares the two **exit codes** rather than
  pinning a diagnostic code, so it keeps holding as the limits close one by one.
- ~~**A `type` alias carries at two places only**~~ **done** (2026-08-14). The sema always replaced
  an alias by what it names; the lowering reached `InternNonGeneric` with a symbol that has no
  layout. It now resolves the alias in both places it can arrive — as a type and as a type ARGUMENT,
  the second so `List<Id>` keys the same instance as `List<int>` instead of interning twice. An
  alias leaves no entry in the module; both are held by a test.
  - **The slice found something worse than the gap**: `type A = B; type B = A;` was not a diagnostic
    but a STACK OVERFLOW in the sema — uncatchable in .NET, so the compiler process died rather than
    the compilation failing. Now `LYR-SEM0064`, reported once per alias rather than once per use.
    Guarding the sema is also what makes the lowering fix safe: a cycle never reaches it, because
    errors stop the pipeline before lowering.
- ~~**`static fn` does not parse in an enum, interface or extend body**~~ **done** (2026-08-14).
  Wider than recorded: all three go through `ParseMethodSequence`. Enum and extend now accept it —
  the enum needed the parser alone, the extend needed one more place in the sema, where static
  lookup consulted the type's own members only while the instance path had consulted the extension
  registry all along. The lowering was ready for both.
  - **An interface stays rejected**, and that is the finding of the slice: accepting it there put a
    receiverless function into a vtable slot and CRASHED THE VERIFIER once the type was used as an
    interface value — worse than the parse error it replaced, and in Release, where the verifier can
    be off, it would have been malformed bytecode. Now `LYR-PAR0041`, one message, naming the way
    out. `docs/Grammar.md` §3.5 records the restriction; the production alone did not imply it.
  - `static let` in one of those bodies gave **21 messages for one cause** and now gives one
    (`LYR-PAR0040`): a StaticBinding is a member of a struct or class body only.
- **A block lambda does not deliver its return type to the inference**: `(n: int) => n` binds `U`,
  `(n: int) => { return n; }` does not. *Not a gap but a documented limit* — `LYR-SEM0046` says so
  and suggests the annotation, and that works. It stands here because I wrongly reported it as a bug
  on 2026-08-08.

- **`?T[] ?? []`** and **`size`** are done.

- **There is no interface inheritance** (`interface A :: [B]` is `LYR-PAR0039` with a message that
  names the way out). Noticed while building the constraint rules, which presupposed it. Whether v1
  needs it is open — `Hashable` would need it only to imply `Equatable`. No program is unwritable
  without it: `std.core` requires both side by side.
- **`string < string` and `==` on user types are rejected** (`LYR-SEM0003` / `LYR-SEM0055`).
  Deliberate and temporary: operator overloading is the first topic after v1.0, and the diagnostic
  points at it. Until then an ordinary method.

**Tooling and format:**

- **The source map section** (id 6) is reserved and described but is not written — panics therefore
  show the function, not the line.
- **Section byte sizes are missing from `lyrvm info`**: the reader discards them after parsing.
  Retrofitting them would mean extending the model with provenance data — a decision of its own.
- **Measure the verifier share in a Release profile** — the Debug numbers are riddled with JIT
  warm-up and serve only as an order of magnitude.

## Design decisions (context)

- AST = immutable records; symbols = mutable classes; binding and types through side tables
  (Roslyn style).
- Builtins as the root scope; two-pass declaration; structured flow analysis (no CFG).
- Type system rules in `docs/Grammar.md`; **`ErrorType` means exclusively "already reported here"** —
  not "unknown". Checked mechanically.
- Generics: monomorphization. The only option that fits this VM — C# reifies and needs a JIT, Java
  erases and pays with boxing; both presuppose that the runtime knows types, and a Lyric value
  carries no type tag.
- **A value carries no type tag.** Every opcode carries its tag in the instruction stream, and the
  dispatch stays static. From that follows the fat-pointer pattern shared by interfaces, closures and
  coroutines: a reference plus a word in `LyrValue`.
- **IR**: the type fields on the instructions are copies for the printer, the temp table is the
  authority — that the two agree is the core job of the verifier.
- **Total functions over today's type universe throw in the `default`** rather than returning a
  substitute value (`IrType.Equal`, `IrNames.*`, `TypeLowering.Lower`, `IrPrinter.TypeStr`,
  `IrBinKind.FromAst`). The throw names the place to follow up when extending. The exception is
  `IrVerifier.Show` — there a throw would hide the finding. *(A `default` that silently does nothing
  has already desynchronised the instruction stream once: `CodeDecoder.SkipType`.)*
- **`IrShape` is the single source for operands, dest and successors**, **`IrNames` the single one
  for scalar names and mnemonics.** Two copies of those switch blocks would be silently wrong code.
- **Lowering**: statements return "does the control flow fall through?"; values crossing block
  boundaries travel through (possibly synthetic) locals, never through temps — **which is exactly why
  this IR needs no phi**. Block density and `Entry == bb0` are structurally guaranteed in the
  `BlockBuilder` rather than checked.
- **Two error classes in the lowering**: valid Lyric the backend state cannot do → `LYR-IR0001` with
  a position; an internal inconsistency → `InternalCompilationException`. **Deliberately exactly one
  IR code** — codes are stable identifiers, the gaps are temporary. `LYR-IR0002..0010` stay free.
  Likewise: a retired number (`LYR-CLI0007`) is **never** issued again.
- **Line endings are a test contract, not a taste**: `.gitattributes` forces `eol=lf` in the working
  tree as well, because the goldens compare span offsets. **Do not remove it** — without it 14 golden
  tests fail in every fresh clone and the `windows-latest` job breaks.
- **Working mode** (scope check 2026-08-02, still in force): Claude plans *and* implements, the
  maintainer reviews — a deliberate deviation from `CLAUDE.md` §Collaboration, where the plan comes
  from Claude and the code from the user. What to watch is whether the understanding of the code
  keeps up with its size. **No `CHANGELOG.md` before `v1.0.0`**; the annotated tag message is the
  release note.
- **At the end of every milestone the delivery list is to be ticked off point by point, not the exit
  criterion alone.** M5 and M6 each silently failed to deliver part of their items; the gap disguised
  itself as a clean diagnostic. For the same reason **six** gates were re-cut in M7, because they
  required language features of later slices.

## Last relevant commit

`sema, ir: a type alias resolves everywhere, and a cyclic one is a diagnostic`

---

## How to maintain this file

- After every slice: extend `## Recently finished`, update `## What we are working on`.
- **At most four entries under `## Recently finished`.** The fifth goes — it stands in `git log`.
  This rule existed already; it was ignored for 1088 lines.
- On a milestone change: enter the new milestone at the top.
- Finished points under `## Still open` are to be **deleted**, not struck through.
- **Never** plan new features here.
