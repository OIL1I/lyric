# Lyric — Current State

> This file is the **only** one in the project that changes often. It is updated
> after every finished slice. Claude reads it at the start of a session to know
> where we stand.
>
> Keep the content short. Anything already committed can go —
> `git log --oneline` is the history, not this file.

---

## Current milestone

**M32 — "die Zaehlung" — is RUNNING** (started 2026-08-22, branch
`feature/m32-instruction-count`, ships as v2.12.0, format 3.5 -> 3.6).

**The baseline it starts from**, from `tools/Bench` (Release, two iteration counts differenced
so nothing that happens once is in the figure, instructions per iteration read from an
`ExecutionBudget` rather than counted by hand):

| case | instr/iter | ns/iter | **ns/instr** |
|---|---:|---:|---:|
| loopOnly | 9.0 | 54.8 | **6.09** |
| intAdd | 13.0 | 86.9 | **6.68** |
| floatAdd | 13.0 | 81.2 | **6.25** |
| maskOnly | 15.0 | 109.7 | **7.32** |
| arrayRead | 19.0 | 119.4 | **6.29** |

**The price is flat**: an `add f64` costs what a `br` costs. That is the normal price of a
switch-dispatch stack machine, not a slow interpreter — so the lever is the NUMBER of dispatches,
not their price. The instruction counts agree exactly with the ones Erato's `bench/interp`
reported from the outside, which is the cross-check that the two are measuring the same compiler.

Beside it, the shape that decides slice 5: `Random.nextFloat()` costs ~555 ns, and
`std.random.Random.nextInt` is **53 instructions** for a xorshift64* — six shift-and-xor rounds
written in Lyric.

The delivery list:

- [x] **slice 0** — the spec's minor-version rule says what the format actually does. It read
      "a minor may only add skippable sections", which 3.4 was not — the note beside that version
      said as much, so the document had contradicted itself since the day it shipped, at the one
      place a planner would consult before proposing an opcode. The rule now states the
      compatibility the format has actually delivered: per MODULE, not per version
      (`lyriclang/lyric-spec#7`, merged; mirror synced)
- [x] **slice 1** — the measuring bench: five interpreter cases in `tools/Bench`, Erato's names
      so the numbers are comparable to the report that started this. Two iteration counts
      differenced instead of a baseline case subtracted, and the instructions per iteration are
      READ FROM A BUDGET rather than counted from a disassembly — which is what makes the table
      survive slices 3 and 4, where the number that must fall is the one the harness reports
- [x] **slice 2** — the decode buffer, no format change (maintainer, `066bed9`): a flat
      `VmInstruction[]` instead of an array of `BytecodeInstruction` REFERENCES, and the frame's
      hot state (arrays, instruction list, stack pointer) hoisted into locals of the loop.
      **Measured, and this is the point**: the flat array alone bought NOTHING; the hoisting
      bought 12 % on `loopOnly` and 1–2 % where an instruction does more than move a value. My
      estimate for this slice was "plausibly 1.3–1.8×" and it was wrong in the optimistic
      direction. What it establishes is worth more than the 12 %: the remaining ~6 ns is **the
      dispatch itself**, not the memory around it — so nothing short of executing fewer
      instructions will move this number, which is exactly what slices 3 and 4 do
- [ ] **slice 3** — fusion 1, the compare-branch (format 3.6): `ldloc a; const k; lt; condbr`
      becomes one instruction. The JVM's `if_icmp*`, and the peephole has it easy — the
      `StackScheduler` already knows a comparison has exactly one consumer
- [ ] **slice 4** — fusion 2, the accumulator forms: `ldloc s; const k; add T; stloc s` and the
      local/local shape become one each. The JVM's `iinc`, typed. The 13-instruction loop falls
      to 4, the 9 to 3
- [ ] **slice 5** — `std.random`: 53 dispatches for a xorshift64*, measured. The STATE stays in
      the script, the shift/xor round becomes one native. Build it only if the crossing measures
      cheaper than the dispatches it saves
- [ ] **slice 6** — measure again, CHANGELOG, release, and the gate below

**The gate this milestone exists to inform**: whether a register bytecode (format 4.0, v3.0.0) is
still worth a major afterwards. The honest prognosis is that it is not: Erato's table compares
`acc = acc + 1.5` as 4 instructions against 1, and slice 4 makes it 1 in the stack machine too.
The register machine wins against a NAIVE stack bytecode; against a fused one it wins much less,
and the `StackScheduler` already keeps temps off the slots. The fused opcodes carry register
operands, so slice 4 is not a detour from that decision — it is the experiment that settles it.

**v1.0.0 through v2.0.0 are released** — annotated tags on the remote, each with a release page.
M0–M10 are finished and tagged (`m0`–`m10-complete`, `v0.1.0`/`v0.5.0`/`v0.9.0`). Releases
v1.8.0 through v1.9.1 carried the three toolchain archives plus two installables; since the org
split the editor clients release from their own repositories, and a toolchain release carries
the archives alone.

**M24 — the freeze prep — is BUILT** (2026-08-19, branch `feature/m24-freeze-prep`, four
slices, ships as v1.15.0). The design leftovers settled BEFORE the semantics freeze. The
delivery list:

- [x] `opaque type`: a new identity over the same layout — explicit `as` is the one crossing,
      equality within one alias, everything else refused; native signatures resolve
      module-local aliases, so an SDK handle crosses as a plain number scripts cannot forge.
      Erato's A4, answered end to end (slice 1)
- [x] the string METHOD API: 26 methods via `extend string`, free forms deprecated toward 2.0;
      concat/repeat stay free as operator backing; `import std.string as strings;` is the
      idiom, and an import whose extensions are used counts as used (slice 2)
- [x] a latent lowering bug fell with it: the global initializer could collide with a
      downstream function id once struct-return buffers and extension requests met in one
      compile; ids merge from one counter now, and holes are a named internal error (slice 2)
- [x] iterator method chaining: probed, documented No — sema-legal, refused by the lowering on
      both paths; see §Design decisions, pinned in LoweringTests (slice 3)
- [x] Grammar §TypeAlias, guides 12 and 13, Erato register A4 updated, CHANGELOG as
      Unreleased (slice 4)

**M23 — the std polish — is BUILT** (2026-08-19, branch `feature/m23-std-polish`, four slices,
ships as v1.14.0). Born from a full audit of the actual std, not a wishlist. The delivery list:

- [x] std.string stops being quadratic: builder/join fold no more, searches and parsers index
      chars; audit rests (German locals in fmt, a torn doc line, a lying section divider) gone
      (slice 1)
- [x] List.clear keeps its backing; test.assertTrue delegates to core.assert (slice 1)
- [x] print/println/eprint/eprintln generic over Display — println(42) works, write/writeln
      deprecated as the second name for the same thing; old bytecode keeps running (slice 2)
- [x] List insert/removeAt/first/last/reverse/swap; Map getOr/clear/entries; Set clear +
      Iterable; iter flatMap/chunks/reduce/first (slice 2)
- [x] arrays cross the native boundary as PARAMETERS — the format always allowed it; the
      registry checks element tags at bind time now (slice 3)
- [x] writeBytes/appendBytes, utf8Encode/utf8Decode (strict: invalid bytes are null, not
      U+FFFD), joinAll behind join/build, fromChars native (slice 3)
- [x] std.random: the generator moved out of math, plus shuffle/choice/nextGaussian; the math
      twin deprecated for one release (slice 4)
- [x] std.time: Instant/Duration over epoch millis, iso() with floor semantics for pre-epoch
      days; osAccess, deliberately no new capability bit (slice 4)
- [x] doc ratchet 370 → 430, still completeness; stdlib-tests grow file/random/time suites;
      guide 13, CHANGELOG as Unreleased (all slices)

**M22 — the language gaps — is BUILT** (2026-08-19, branch `feature/m22-language-gaps`, four
slices, ships as v1.13.0). The delivery list:

- [x] compound assignment reaches through the operator interfaces for variable targets; field
      and element targets stay diagnosed — the shorthand would evaluate the object or index
      twice (slice 1)
- [x] interface inheritance: ONE parent, implication-only — conformance, constraints,
      defaults, throwability and interface values all reach through the chain; the
      chain-prefix slot layout keeps a parent's default valid behind a child receiver
      (slice 2)
- [x] the parent-list rules: only interfaces, no cycles, at most one entry (LYR-SEM0078), no
      redeclaring a chain member (LYR-SEM0079); LYR-PAR0039 retired from error to feature
      (slice 2)
- [x] `std.core` is the library's root — it imports nothing; `newStringBuilder` got its
      @Deprecated, and the attribute keeps its promise now: no metadata row, no DCE root
      (slice 3)
- [x] heterogeneous arithmetic: the probe ran, the answer is a documented No — see §Design
      decisions (slice 3)
- [x] block lambdas infer their return type from their returns, unified like match arms; the
      open-generic case binds U from the block (slice 4)
- [x] Grammar §3.5, guides 3 and 7, CHANGELOG as Unreleased (slice 4)

**M21 — the std rework — is BUILT** (2026-08-19, branch `feature/m21-std-rework`, four
slices, ships as v1.12.0). The delivery list:

- [x] every public item of the standard library documented; the coverage ratchet pins
      COMPLETENESS (370 of 370), not a number (slice 1)
- [x] the audit fixed inline where behavior-neutral: German locals and two parameters, torn
      fragments, milestone references in comments, the misplaced capacity doc (slice 1)
- [x] the import-std.string crash is a diagnostic naming the trap; a builtin-shadowing import
      warns (LYR-SEM0077) (slice 2)
- [x] readBytes — raw bytes against the U+FFFD limitation; write-side filed, array parameters
      have never crossed the native boundary (slice 2)
- [x] constructors on the types (List/Map/Set.empty, StringBuilder.new, Random.seeded); the
      approved relics deprecated with successors, corpus migrated in the same commit (slice 2)
- [x] @Deprecated may sit on generics (the one row-less exception), and a generic static call
      substitutes the caller's T — two Vm regression tests pin both (slice 2)
- [x] stdlib-tests/: 27 behavioral tests in Lyric, run by lyrtest, wired into dotnet test,
      covered by both corpus invariants (slice 3)
- [x] guide 13 documents constructors-on-types and the stale-copy trap; CHANGELOG as
      Unreleased (slice 4)

**M20 — attributes become load-bearing — is BUILT** (2026-08-19, branch
`feature/m20-attributes`, three slices, ships as v1.11.0). The delivery list:

- [x] `@Deprecated` in std.core; every use warns at the use site, the note points at the
      attribute, `message` names the way forward (slice 1)
- [x] resolved by identity, not by name; self- and sibling-exemption; a deprecated module
      warns at its imports; editors strike uses through (slice 1)
- [x] guide 15 documents the compiler-read set as contract (slice 1)
- [x] `std.test`: the `Test` marker, `assertTrue`, `assertEq` naming both values (slice 2)
- [x] `testRoot` in `lyric.json`; only the runner compiles it — the Go shape (slice 2)
- [x] `lyrtest`, the tenth binary: discovery through the attribute rows, a FRESH instance per
      test, panic = FAIL with frames, exit code carries the verdict (slice 2)
- [x] `HostOptions.SourceRoot` in the embedding API (slice 2)
- [x] `lyric test` in the driver; guide chapter 20; CHANGELOG as Unreleased (slice 3)

**M19 — diagnostics — is BUILT** (2026-08-19, branch `feature/m19-diagnostics`, four slices,
the first milestone of the v2 sequence: v1.10.0). The delivery list, ticked point by point:

- [x] four severities — Info joins — render in text, JSON and over the protocol (slice 1)
- [x] a diagnostic carries notes; the problem-matcher head format is pinned by test (slice 1)
- [x] `--deny-warnings` on check and build; a denied build writes no artifact (slice 1)
- [x] warnings: unused locals/loop/catch/pattern bindings, unused imports, unreachable
      statements, static-extension-through-instance as the deprecation clock; duplicate module
      names are an error with a note (slice 2)
- [x] did-you-mean, previous-declaration and declared-here notes; LYR-SEM0046 carries its way
      out as a note (slice 3)
- [x] the first hint: a `var` through which nothing is ever changed (slice 3)
- [x] editors fade unused code and strike through the deprecated form (slice 4)
- [x] the corpus checks in SILENCE, held by a test the way the formatter holds its shape (slice 4)
- [x] guide chapter 19; CHANGELOG prepared as Unreleased (slice 4)

**M18 — the formatter — is BUILT** (2026-08-19, branch `feature/m18-lyrfmt`, stacked on M17,
four slices). The delivery list, ticked point by point as the milestone rule demands:

- [x] the lexer keeps comments on request; the compile path stays byte-identical (slice 1)
- [x] the document algebra and its renderer — only the renderer measures columns (slice 1)
- [x] the whole AST prints: literals from their spans, parentheses re-derived from §6.1 (slice 2)
- [x] comments survive — all three forms, one positional mechanism; blank lines are the
      user's, capped at one (slice 3)
- [x] the corpus invariants: every `.lyr` in the repository formats, is stable, reparses to
      the same tree, loses no comment (slice 3)
- [x] `lyrfmt` in place / `--check` / `--stdin`, `lyric fmt` in the driver (slice 4)
- [x] the repository formats ITSELF, and a test holds it formatted from now on (slice 4)
- [x] guide chapter 18; CONTRIBUTING's "no formatter in v1" clause retired (slice 4)

**M17 — packing: a program becomes one file — is BUILT** (2026-08-18, branch
`feature/m17-lyrpack`, three slices, PR #50). `lyric pack app.lyr` produces a standalone
executable: a prebuilt stub runtime with the `.lyrbc` and a 24-byte footer appended — a byte
copy, no linker. Two new binaries (`lyrpack`, packer, references Core ALONE; `lyrstub`, the
runtime half of a packed program), format contract in `docs/Pack.md`, guide chapter 17, and
both workflows pack-and-run an example on every platform before archiving.

**M16 — the tooling milestone — is CLOSED** (decided and built 2026-08-18, at the post-v1 pace):
the language server learned the project, the editors learned the server. The delivery list,
ticked point by point as the milestone rule demands:

- [x] project-wide compilation (PR #43)
- [x] rename (PR #44)
- [x] workspace symbols (PR #44)
- [x] semantic tokens (PR #45)
- [x] signature help (PR #46)
- [x] folding (PR #46)
- [x] inlay hints (PR #46)
- [x] restart command and status item in VS Code (PR #47)
- [x] task provider with problem matcher (PR #47)
- [x] snippets (PR #47)
- [x] `.vsix` in the release (PR #47)
- [x] the JetBrains thin plugin (PR #48)

The PR stack #43 ← #44 ← #45 ← #46 ← #47 ← #48 is merged and shipped as **v1.8.0**.

**M14 and M15 are what v1.7.0 shipped, both built 2026-08-18**: the interpreter stops allocating
(frame pooling, inlining, scalar replacement, devirtualization) and the native boundary learns
value structs at 0 B per call. PRs #41 and #42, details under *Recently finished*.

**M13, attributes, is what v1.6.0 shipped**: a program says things about itself that a host can
read. Format 3.2, and the 7-day rule was retired with it — it was pre-v1 scope discipline, and v1.0
has shipped; from here the pace is our own.

**M12, the project system, is what v1.2.0 shipped**: `lyric.json` says what a project is, `build.lyr`
says what to build, `lyric new` writes one, and the tools read all of it.

**v1.5.0 shipped operators**: `==`, the orderings, arithmetic and `as` all resolve through the
interface a type declares — no operator syntax, no new opcode. Method overloading was considered and
rejected; the constraint mechanism is this language's overloading.

**M11, the language server, is CLOSED.** Diagnostics while you type, what a name under the cursor is,
where it was declared, a program followed across its files, documentation on hover, the outline of a
file, every place a name occurs, and completion. v1.3.0 shipped the first seven, v1.4.0 the last.

4522 tests green **in Debug and Release**, bytecode format **3.5**, **eleven** binaries
plus `lyrembed.dll`, version **2.11.0**; the specification in `lyriclang/lyric-spec` is
**NORMATIVE**, its suite stands at 90 cases, and the toolchain's own CI runs it against the
working tree.

**What this state can do**: the whole language of the grammar compiles and runs; a standard library
that largely carries itself (`Map`, `Set`, merge sort, all iterator adapters and the string hash are
written in Lyric); six tools including the REPL, the build runner and a language server that
compiles the PROJECT — references across files in both directions, diagnostics for files nobody has
open, disk changes behind the editor picked up through file watches; a VS Code extension with live
diagnostics; a project that scaffolds, declares its own layout and builds itself with a Lyric
script; and an embedding API with which a C# host loads scripts, sandboxes them, calls functions
out of them and hands its own functions, types and value structs in.

> **The file had grown to 1088 lines by 2026-08-07** and contradicted itself in three places. It has
> been cut back to its own maintenance rule: recent slices, open points, design context. Everything
> else stands in `git log`.

## Recently finished

- [x] **A18 — an opaque type leaves a name in the module** (2026-08-22,
  `feature/a18-opaque-field-names`, v2.11.0, format 3.5). The first format change since 3.1 that
  is skippable in BOTH directions, and the reason is worth keeping: 3.4 put a new form inside a
  section a reader already reads, this one put a new section beside them. Same size of addition,
  opposite compatibility.

- [x] **A17 — an attribute default holds across a module line** (2026-08-22,
  `fix/a17-attribute-default-across-modules`, v2.10.1). One line, and the second time in two
  days that a bug was "checked in the wrong order". The pattern is now explicit in the code: the
  sema walks modules in dependency order, twice, and both places say why.

- [x] **An enum variant is an attribute argument** (2026-08-22,
  `feature/enum-attribute-arguments`, v2.10.0, format 3.4). Worth keeping for the estimate rather
  than the code: when A11 landed I wrote this off as "much bigger" on two grounds, and only one
  of them held. The cheap half was the one I had called expensive — a host needs no variant
  table, because the module already carries the names.

## Measurements

Numbers instead of opinions. Since 2026-08-18 they come from `tools/Bench` — in-process, Release,
100 000 operations per case, minima over nine runs, a scalar loop of the same shape subtracted.
`dotnet run -c Release --project tools/Bench` reproduces them. The M14 baseline:

| Case | ns/op adj. | B/op adj. |
|---|---:|---:|
| call (`fn step(a: float): float`) | 49.9 | **176** |
| struct construction only | 60.6 | 56 |
| construction **plus** method call (`Vec2.add`) | 271.0 | **352** |
| the same through `a + b` (`Add<Vec2>`) | 252.3 | 352 |
| `for-in` over a range (against `while`) | 143.2 | **208** |
| `for-in` over an array (against `while`) | 153.1 | 208 |
| `Set.iter()`, the `callvirt` route (against `while`) | 420.9 | 229 |

One correction to the old numbers: the 112 B for "construction only" was a four-field shape; the
two-field `Vec2` is 56 B. And one to the harness: the interpreter loop is one shared method that
tiered compilation keeps improving while the harness runs, so the cases are measured round-robin
over three cycles, minimum per case — sequentially, the scalar baseline came out slower than the
loop doing the same work plus a call.

**After slice 1 (frame pooling):** call **176 → 0 B**; `for-in` range **208 → 0.1 B** — the
208 B were the frame trio alone; a `Some` over a scalar never allocated, disproving slice 0's
guess. **After slice 2 (inlining), adjusted ns/op:** `Vec2.add` 271 → **112**, `for-in` range
143 → **68**, array 153 → **94**; the ~7 ns residue on a bare call is the spliced
parameter/return traffic through locals.

**After slice 3 (scalar replacement), against the slice-0 baseline, adjusted:**

| Case | baseline | now |
|---|---:|---:|
| construction only | 60.6 ns / 56 B | **18.2 ns / 0 B** |
| `Vec2.add` plus assignment | 271.0 ns / 352 B | **8.4 ns / 0 B** |
| the same through `a + b` | 252.3 ns / 352 B | **6.1 ns / 0 B** |
| `for-in` over a range | 143.2 ns / 208 B | **40.5 ns / 0 B** |
| `for-in` over an array | 153.1 ns / 208 B | **109.7 ns / 0.3 B** |

The `Vec2` gate is met: expression-shaped struct code allocates NOTHING and runs ~30–40× the
baseline. The range loop is 0 B but 1.85× a `while` — the optional ops and the extra block hops
remain; honest, and material for a later peephole rather than this milestone.

**After slice 4 (devirtualization):** the `Set.iter()` loop carries no `callvirt` anymore — the
receiver's single `mkiface` proves the concrete type, and the loop direct-calls
`SetIterator<int>.next` (too big to inline; the call is pooled). The gate was structural and is
met; the time gain drowns in the probing work the loop actually does. One find on the way, caught
by the verifier: a DEFAULT-method slot takes the fat pointer, not the concrete value — `this` in
a default method dispatches virtually.

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

**Compiler latency, in-process** (2026-08-14, Release, 15 runs). A full `Check`, standard library
included:

| What | median | min |
|---|---|---|
| 6 lines | 14.3 ms | 7.1 ms |
| ~85 lines with a standard library import | 16.0 ms | 9.2 ms |

The same work through `lyrc check` measures **181 ms** and **212 ms**, against **40 ms** for
`--version` alone. The difference is process start and JIT warm-up: a long-lived process pays it
once, a batch invocation pays it every time. **Measure in the process that will do the work.** The
batch number is an upper bound, and here it was off by a factor of ten in the direction that would
have bought an incremental compiler nobody needs.

**The workspace compile confirms it** (2026-08-18, in-process, Release, 15 runs): a 14-file
project through `CheckProject` measures **median 41.7 ms** against **40.1 ms** for a single file
of it. The standard library dominates; the project's size is in the noise, and the incremental
compiler stays unwarranted at project scale too.

## What we are working on

**A18 — an opaque type leaves a name in the module — is RELEASED as v2.11.0** (2026-08-22,
format 3.5). The register's second finding from the save system, and the one that cost a
decision rather than a line. What Erato asked for was "a name in the Types table"; the Types
table has no row for an alias, and referring to one from a field type would need a new type
TAG — a tag is not skippable, because every reader of a signature has to know all of them. So it
became a section of its own (id 14), which an older reader steps over without noticing.

**The shape of the answer is the interesting part**: the field stays an `i64`. The section adds a
NAME beside the type rather than a type, which is the only form that keeps the opacity free at
runtime — the property A4 was built for — while making it visible to a host. A trace, not a
mechanism.

**A17 — an attribute default across a module line — is RELEASED as v2.10.1** (2026-08-22).
Erato filed it hours after the 2.10.0 re-pin: the enum argument worked written and failed
defaulted, so `@Saved` — the first line of every example — did not compile once the class sat in
another module than the attribute. **It is A16 one level down**: declarations were checked in
DISCOVERY order, so a use could be checked before the declaration whose default it depends on,
and a name in that default had no meaning yet. The same walk fixes it, and the same sentence
holds — what a file means must not depend on who was checked first.

**Worth keeping**: A16 changed the globals loop and left the declarations loop alone, because
globals were what the finding was about. One order was right and its neighbour was not, in the
same method, two lines apart.

**An enum variant is an attribute argument — RELEASED as v2.10.0** (2026-08-22, maintainer's
request). The form I weighed and rejected when A11 landed: back then the note said "the row
carries only scalars and strings, and the host would have to interpret variants — much bigger".
Half of that was true and half was an estimate that never got made. The row DID need a new
form, so the format moved 3.3 → 3.4; the host side did NOT need interpreting, because the
field's type names the enum and the enum's entry names its variants — the reader resolves
`Layout.Separate` from what the module already carries and hands the host a name and a tag.

**The compatibility line is the one to remember**: this is the first format change a 3.3 reader
cannot ignore. The Attributes section is skippable as a whole, but a reader that DOES read it
meets a tag it has no case for — so a module whose rows use an enum value does not load on an
older runtime, while one without such a value still does. Same forward path as `co.next()` in
2.2.0, and stated in both the format chapter and the changelog rather than discovered.

**A15 — a debug adapter that attaches — is RELEASED as v2.9.0** (2026-08-22). The register
called it a convenience rather than a blockade, and it was right about the protocol half: the
messages, the session, the `StopReason` translation and the variable references all stood. What
it was NOT right about is where the work sat — the interesting part is not `attach`, it is what
happens when a session ENDS without the program ending. `DebugController.Detach()` is the new
piece: breakpoints go, a parked thread is released, the event stream closes. Without it an
editor that crashes leaves a game standing at its breakpoint for good.

Two questions the register listed as "only an embedder can answer" answered themselves once the
shape was right: WHICH session knows a file is decided by the connection it arrived on (one
server per controller), and `stackTrace` on a running program already failed honestly, because
the controller's inspection surface has always thrown when not paused. The third — a scene
change replacing the controller under a still-connected editor — stays the embedder's, as the
register says.

**A16 — the initialization order follows the imports — is RELEASED as v2.8.0** (2026-08-22).
Erato filed it after E15, and it is the first of their findings that was a real defect rather
than a missing shape: global initialization ran in DISCOVERY order from the entry file, so a
third module decided whether a second one compiled, and the same file compiled or not depending
on who compiled it. Their player compiles every file as its own entry — to read its attribute
rows — so "compiled as an entry" is the normal case for them, and `lyric check main.lyr` said
`ok` about files the player rejected.

The fix is one walk: `Compilation.InitializationOrder()`, a DFS post-order over the import edges,
used by the sema (which decides `LYR-SEM0057`) and by `GlobalTable` (which emits `<globals>`) —
one order for both, or they would drift. **What the register got right and the old comment did
not**: an import IS the dependency statement and it was already there to be read, and the
comment's own citation of Go named the answer it was not taking. **Found while building**: import
cycles are already `LYR-RES0005`, so the walk's cycle guard is not tolerance but survival — the
sema still runs on the broken graph, and a recursing walk would hang instead of letting the
diagnostic through.

**A15 — a DAP `attach` — is NOT built and is not planned yet.** The register calls it "eine
Bequemlichkeit, keine Blockade" and Erato has shipped its own adapter (730 lines against
`DapServer`'s 527). What an `attach` would save is the next embedder writing them again; what it
needs first is an answer to questions only an embedder has — which of five running sessions knows
this file, what `stackTrace` says while the program runs, what happens when the editor
disappears. Worth a design round, not a slice.

**The editor clients are catching up — jetbrains-lyric gains run and debug** (2026-08-22, PR
`lyriclang/jetbrains-lyric#1`, ships as 1.3.0). The plugin could not start a program at all. It has
a run configuration now — `lyric run`, the driver, with the compiler's diagnostics clickable in the
console — and the same configuration under Debug drives `lyrdbg` through the platform's OWN DAP
client: no `XDebugProcess`, no second protocol implementation, the mirror of the LSP wiring one
door further. Refused on purpose and visible in the UI: no breakpoint conditions (the adapter
evaluates names, not expressions) and no exception breakpoints (a panic ends a program instead of
stopping it).

**What the new `verifyPlugin` gate found on its FIRST run**: the plugin was incompatible with the
2026.1 baseline it claims, and had been since the org split. Compiled against 2026.2, the Kotlin
override binds `LspIntegrationProvider.LspClientStarter` — the name the LSP interfaces were renamed
to there — and 2026.1 has no such class, so opening a `.lyr` file would have thrown
`NoSuchClassError`. The rule that follows: **compile against the OLDEST supported release and
verify both ends**. Old names resolve in the new IDE; new names do not resolve in the old one.

**v2.7.1 fell out of the same wiring**: `lyrdbg` refused `setExceptionBreakpoints`, a request an
editor sends during configuration whether or not filters are offered. VS Code never sends it, so
one client was never going to find it.

**What is left for the clients**: neither has ever had a release. Both `release.yml` wait on a
`vX.Y.Z` tag, both READMEs promise an installable, and since the split there is none to download.
Versions stand at 1.3.0 on both sides, and the JetBrains checklist has still never been run against
a released zip.

**A14 — the debugger reaches an invoked function — is RELEASED as v2.7.0** (2026-08-21). Erato
filed it after E13, and it is the same class as A13 one door further: everything M30 built was
offered at `RunEntry` alone, and an embedded script has no `main` to start. One overload —
`Invoke(index, controller, args)`, the shape the budget got in 2.4 — and the whole machinery is
reachable. Nothing in `DebugController` needed changing: its command surface was already a
semaphore and two volatile fields, so a host driving the call from its own thread works as it
stands.

**What was asked for and NOT built**, because the register calls it a wish rather than a
requirement: a controller a host can drive without a second thread, so a window could keep
drawing while the script stands still. That needs an interpreter that returns to its host
mid-instruction and resumes later; this one keeps its frame stack on the CLR stack, which is a
different machine. Guide 21 states it.

**A13 — the compilation error with a place — is RELEASED as v2.6.0** (2026-08-21). Erato filed
it after E12: `EmbeddingException` handed out compiler `Diagnostic`s whose `Span` is an index
into the compilation's source manager, and that manager never left the toolchain — so a host
caught a code and a message, in a project of thirteen files. The place is resolved AT THE THROW
now, into `ScriptDiagnostic` (file, line, column, notes and all). Three options stood in the
register; this is the first one it asked for, and the other two — handing out the manager, or a
rendered blob — were refused for the same reason: a manager outlives nothing and a blob cannot
be jumped to.

**Found beside it, unfiled**: `ScriptPanicException` did not carry the backtrace either. Erato
reached it through `InnerException` — that means naming `LyricPanic`, a runtime type the whole
embedding API exists so a host need NOT reference. `Backtrace` is a property now.

**The formatter breaks operator chains — RELEASED as v2.5.1** (2026-08-21). The last item of
Erato's E7 note, cut out of M31 and done on its own. `BinaryDoc` held no line opportunity at
all, so a chain could not break however long it grew; it flattens the level now and breaks
before every operator, indented one step, operator-leading. **The prediction that cut it from
M31 was wrong**: it changes "the shape of every formatted file in the corpus" — measured, it
changes NOTHING in this repository's corpus, and the only file that moved during development
was `examples/inventory.lyr`, which the match-expression carve-out then put back. The carve-out
is the design finding: a group around a chain containing a hard line can never be flat, so
`return base * match (r) { … }` would have broken its operator off for a reason that has
nothing to do with the width. `Doc.WillBreak` answers that question now, where Prettier's
`willBreak` sits.

**A12 — the foreign value struct — is RELEASED as v2.5.0** (2026-08-21): Erato filed it after
E10, and it is the sibling of A9 in the same file. `NativeStructParameter` looked its type up
with `module.Members.LookupLocal` and required a single-segment path, so an imported or
qualified struct never matched and fell through to `LYR-IR0001`. It asks the resolver now, as
`ResolveLocalAliases` has since 2.2.1. **The 2.2.1 changelog called that restriction
"documented and deliberate" and it was not** — the reason given there explains why the alias
fix did not carry structs along, not why a foreign struct may not be flattened. No spec change:
value structs in native signatures are toolchain surface, and the spec says nothing about them.
Guide 13 gained the `…Int` family table in the same wave — `clampInt` existed all along, but
nothing named the convention where someone would look.

**M31 is BUILT and RELEASED as v2.4.0** (2026-08-21). Details under §Current milestone. What
is left for Erato: the register's A10/A11/A12 entries want closing — all three are delivered.

**The v2.3.1 bug wave is BUILT** (2026-08-21, branch `fix/v2.3.1-bug-wave`, three slices).
Four of the five findings of the post-2.3.0 audit, fixed and pinned: the inherited default
through a child-typed value (#71 — the cause was the DEVIRTUALIZER, not the lowering the issue
suspected, and the constraint route had a second gap beside it), the array-literal element that
lowered without its context (#72 — the same fix carries the interface lift nobody had filed),
the shared console code page behind years of sporadic process-test failures (#74), and the
uppercase float exponent (#75, with the rendering now specified in §11 and routed through one
place). Spec PR: `lyriclang/lyric-spec#1`, suite 85 → 88.

**#73 stays open, deliberately.** The audit called it a design round and it is one — sharper
than filed: the `throws` of a coroutine function is checked at the CALL, which cannot throw,
and unchecked at the `resume`/`next()`, which can. The local case only looks right because the
try covering the call usually covers the resumes too. Every route out of that scope — an
optional, a field, `next()` through a field — reaches `LYR-VM0010`, which Appendix A still
calls "reachable only for a hand-built module". Fixing it means deciding what throwability a
coroutine VALUE carries; that is a language change, not a patch.

Decided 2026-08-21: **document now, carry the type change as its own milestone.** Spec chapter
10 states the gap and Appendix A stops claiming VM0010 is unreachable (`lyric-spec#2`); guide
11 gets the user-facing half — wrap the pull, not only the call. Two answers were weighed and
refused in the text: every pull throwing taxes the ordinary coroutine, and refusing a throwing
coroutine a field would refuse programs whose RUNTIME behaviour is already correct.

**M30 — the debugger — is RELEASED as v2.3.0** (2026-08-20). Details under §Current milestone.
Found on the way and fixed in its own commit: `tools/Bench` had not compiled since the 2.0
deprecation removal (`set_iter` still imported `emptySet`) — every bench run died before the
first number, which is also why the bench gate for the policy loop had no pre-existing baseline
to lean on.

Next: the file-error design round (A) — small vs. big unification — whenever the maintainer
calls it. Erato-side: engine.task and engine.assets rework.

**A9 — the imported-alias fix — is RELEASED as v2.2.1** (2026-08-20): Erato's register filed
A9 the day of the 2.2.0 re-pin — an opaque type imported from a sibling SDK module did not
resolve in a native signature (`LYR-IR0001`), while §3.5 promises the resolution without a
module restriction; the A4 mechanism was purely syntactic and module-local. Fixed with a
binding fallback in `ResolveLocalAliases`: selective and qualified imports, scalar and array
positions, alias chains across modules. Structs in native signatures stay module-local by
design. No spec change — the spec was already right; pinned by an Embedding test (a
conformance case cannot express native roots).

**M29 — the A8 wave — is RELEASED as v2.2.0** (2026-08-20, PR #68). Both coroutine edges
Erato's register filed under A8, plus one the work uncovered:

- [x] slice 1: `Coroutine<T>` lowers as a FIELD type — the AST path `TypeTable.Lower(TypeNode)`
      learns the special case the sema and the LyrType path always had; `List<Coroutine<T>>`
      through the type-argument path too. The closure idiom stops being mandatory.
- [x] slice 2: `co.next()` — the safe pull beside the panicking `resume`. `?T` (bool for
      `Coroutine<void>`); refused on `Coroutine<?T>` (LYR-SEM0080), where null is ambiguous.
      The body takes a lenient flag; its done-exits read a zeroed state field instead of
      manufacturing values; the marker is read from outside by the compiler-bound native
      `std.core.coroutineIsDone` (per-signature import entries — format stays 3.2, an old
      runtime rejects at binding with the import's name, the designed forward path; a module
      not using `next()` keeps loading everywhere). `resume` semantics, frames and panic are
      UNTOUCHED; `isDone` alone was probed and rejected — a pull coroutine cannot answer
      "will there be another value" without pulling (no mainstream generator API has hasNext).
      Spec §10/§4.4/§11/Appendix A amended, suite 81 → 85 (`//! since: 2.2.0`), 85/85 against
      the working tree.
- [x] found on the way, fixed in slice 2: a bare `return;` MID-BODY in a coroutine — §10
      always allowed it — emitted a valueless `ret` from a value-yielding body: internal
      verifier error in Debug, malformed bytecode in Release, exhaustion never marked. It is
      the run-through exit now, shared by all three ways out.

**M28 — the ergonomics wave — is RELEASED as v2.1.0** (2026-08-20, PR #66). Two additive changes the audit measured before they were built:

- [x] `@Deprecated` reaches members: methods, fields, static lets, extend methods — the ONE
      attribute a member admits (no member rows in the format); interface members refused;
      no metadata row, pinned (slice 1)
- [x] the adaptation context propagates structurally: array elements and match/if arms check
      against a §3.1 context, literals adapt, misfits error AT the element or arm; the
      contexted mismatch diagnosis moved from SEM0016 to SEM0001 at the arm; array literals
      at a variadic position keep their shape-decides rule by taking no expectation (slice 2)

Next after the release: the file-error design round (A) — small vs. big unification —
whenever the maintainer calls it.

**M24 was merged and released as v1.15.0** (PR #62) — the freeze prep. With it the
pre-freeze design space is closed; next is v1.16, the spec draft (non-normative) plus the seed
of the conformance suite, and the semantics freeze begins there. The scope came
from a line-by-line audit of the standard library after the first extension list turned out to
describe modules that already existed. Deferred by decision: the string method API via `extend`
and iterator chaining (each needs its own design round plus a probe), the three-convention
file-error cleanup (a 2.0 cut), and member-level `@Deprecated` (the attribute cannot sit on a
member yet — surfaced when StringBuilder.length wanted one).

**The repository moved and the clients moved out** (2026-08-19): the project lives in the
`lyriclang` org — `lyriclang/lyric` is the toolchain, ONE repository with ONE version, and the
editor clients are their own repositories (`vscode-lyric`, `jetbrains-lyric`), split with their
history, versioned on their own cadence, releasing their own installables. The TextMate
grammar's canonical home is `tooling/textmate` HERE, beside the lexer `GrammarTests` pins it
against; each client carries a working copy its `grammar-sync` CI job diffs against this one.
The changelog note about where the installables went is written (Unreleased entry); Erato pins
checked-in binaries (`lib/lyric`), so nothing breaks — its README gets the new URL with the
next pin update.

**M17 and M18 shipped together as v1.9.0** (2026-08-19) — the PR stack #50 (packing) ← #51
(formatter), merged in that order; release commit and annotated tag explicitly delegated for
this release, normally the maintainer's step. The release workflow gates itself: it packs and
runs an example on every platform before an archive exists.

**M17's deliberate limits**: one platform per pack (a foreign platform packs via `--stub` with
that platform's stub out of its archive — no `--target` until someone needs it); the stub ships
untrimmed (measured 73.5 → 13.0 MB, decision material above); capability narrowing at pack time
is a footer field for a future minor. **And one limit the release gate found rather than the
plan**: a packed Mach-O fails codesign's strict validation, so macOS cannot RUN packed programs
yet — the payload has to become a real Mach-O segment (deno's route). The pipeline documents
the state honestly: macOS verifies pack-succeeds plus the signed bare stub, Windows and Linux
run the packed result.

**M18's deliberate limits**: precedence-redundant parentheses vanish (the AST has no node for
them — keeping them means a parser change, material for the scope check if it itches); a
comment inside an expression surfaces at its statement's end. The
`textDocument/formatting` gap closed right after the release: the server answers with one
whole-document edit off the CURRENT buffer, a buffer that does not parse gets no edits, and
the client's tab preferences are read for nothing — one shape is the contract in the editor
too (`feature/lsp-formatting`).

**Deviation from the plan, recorded**: no own `Lyric.Formatting` library — the formatter is a
namespace in `lyrfe`, because both consumers (lyrfmt, lyrls) already share that assembly and a
fourth library bought naming trouble for zero separation.

**M16 is closed and released as v1.8.0.** What remains from it: the first manual run of the
JetBrains checklist (plugin README) against the released zip, in a 2026.1+ IDE.

The open points for the **2026-09-06 scope check**: heterogeneous arithmetic, compound
assignment through the interfaces, the first compiler-read attribute (v1.11 material), the
`for-in` peephole, Erato's A4 (an opaque `Entity`) and the E4-side adoption — plus, from M16:
parameter-name inlay hints and semantic-token deltas if a measurement ever asks. M19 closed two
former entries: the static-extension asymmetry warns as a deprecation now (the error lands with
2.0), and duplicate module names are `LYR-RES0007`.

**Not renameable, recorded**: a module (rename the file), an enum variant's payload field (no
symbol exists for it), anything whose declaring module is native. Renaming across `build.lyr` is
not covered — the build script sits outside the source root and compiles as its own unit; its
diagnostics say so on the next compile.

**Erato's A2 is answered in its useful direction** — the host declares the value types in its
SDK, the script uses them, nothing allocates. What remains on the register's list for Lyric is
A4 (an opaque `Entity`) and the E4-side adoption. The other open points — heterogeneous
arithmetic, compound assignment through the interfaces, the static-extension asymmetry,
the first compiler-read attribute, the `for-in` peephole — stay material for the **2026-09-06**
scope check.

**One limit stays**: a generic call shows the DECLARED signature, because the
substitution is private to the type checker and a second one in the server would be a second answer
to what `T` became. Measured by a test rather than left as an intention.

**The open question to answer before E4**: the lifetime and identity of a host object across the
boundary — does the host keep it alive or the VM? That is the one place in M10 where I have no
answer yet, and it belongs asked before E4 starts.

## Still open

**Tooling and format:**

- **A `v1.0.1` runtime cannot read a module with a source map.** The skip that lets a reader step
  over a section it does not know was broken until 3.1, so the forward compatibility the format
  promises does not hold for the one release before it. `--no-source-map` produces a module those
  runtimes accept. Nothing can be done on their side; it is recorded so the next format addition is
  not mistaken for the same bug.
- **A module without `main` keeps the whole well-known standard library.** Measured 2026-08-15: a
  library module exporting one `int` function compiles to **7886 bytes and ~54 functions** from
  `std.string`, `std.core`, `std.iter`, `std.fmt` and `std.collections`, none of which it uses. The
  same file with a `main` that uses `println` is **315 bytes and one function**.
  - Not a bug in what the reachability analysis does — it trims from the ENTRY POINT, and a library
    has none, so nothing is unreachable. `WellKnownModules` loads those five unconditionally because
    the f-string lowering calls into them.
  - The roots for a library would sensibly be its `pub` declarations. **Whether that is the right
    rule is a decision, not a measurement**: it would make a library's surface decide its contents,
    and a host calling an unexported function through the embedding API would then find it missing.
  - It is the point at which a binary library would carry half a standard library with it, so it
    belongs answered before that is ever a goal.
- **`TypeResult._refs` holds declarations beside uses**, because the definite-assignment
  analysis binds a `BindingStmt`, a `Param`, a `ForInStmt` and the pattern bindings to the symbol
  they themselves declare. Since M19 the separation rule has its first consumer: the
  `WarningAnalyzer` splits the two by `ReferenceEquals(symbol.Declaration, node)`, exactly as the
  table's own documentation prescribes. No split table needed after all.
  - *The receiver-kind question is out of it since v1.4.0 slice 1*, which is what made the table safe
    to add to.
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
- **A `FileId` is an index into ONE `SourceManager`, and a `Span` carries one.** That couples every
  span to the manager it was made in, which is why the compiler builds a fresh manager per run and
  why parsed ASTs cannot be shared between runs however immutable they are. Anything long-lived that
  wants to cache across compiles has to cache the manager with them.
- **Line endings are a test contract, not a taste**: `.gitattributes` forces `eol=lf` in the working
  tree as well, because the goldens compare span offsets. **Do not remove it** — without it 14 golden
  tests fail in every fresh clone and the `windows-latest` job breaks.
- **Working mode** (scope check 2026-08-02, still in force): Claude plans *and* implements, the
  maintainer reviews — a deliberate deviation from `CLAUDE.md` §Collaboration, where the plan comes
  from Claude and the code from the user. **`CLAUDE.md` names this entry as the one that overrides
  it**, so the deviation lives in one place and is lifted by deleting this bullet. What to watch is
  whether the understanding of the code keeps up with its size. The changelog starts at `v1.0.0`;
  before it the annotated tag message is the release note.
- **At the end of every milestone the delivery list is to be ticked off point by point, not the exit
  criterion alone.** M5 and M6 each silently failed to deliver part of their items; the gap disguised
  itself as a clean diagnostic. For the same reason **six** gates were re-cut in M7, because they
  required language features of later slices.
- **Interface inheritance is single-parent and implication-only** (M22): a parent's default method
  runs behind a child-typed receiver, and only the chain-prefix slot layout keeps the parent's slot
  indexes valid there — several parents would need thunks. Redeclaring a chain member is refused
  instead of getting override semantics: without vtable overriding, the same call would dispatch
  differently through the child and through the parent. A child interface VALUE does not convert to
  the parent's type; implication holds for implementing types. `std.core` adopted
  `Hashable :: [Equatable]` with 2.0, as planned.
- **pub declarations are a library's reachability roots — decided YES, LANDED with 2.0**
  (maintainer, 2026-08-19; built 2026-08-20). Before the rule a module without `main` kept the
  well-known standard library wholesale (measured: 7886 bytes for a one-function library).
  Now a library's `pub` surface decides its contents; the raw lowering API keeps the old
  keep-everything behavior for bare snippets, pinned in ExportRootTests. It waited for 2.0
  because it is observable: a host calling a function the surface does not reach finds it
  missing.
- **Iterator method chaining: documented No for now** (M24 probe). `xs.iter().map(f).take(3)`
  wants generic default methods on `Iterator<T>`. The sema ACCEPTS them already; the lowering
  refuses on both paths — an interface VALUE fails at instance interning (`fn(T) -> U` in the
  slot signatures, the same wall as generic interface values over struct arguments), and even
  the monomorphized constraint path has no lowering for a default body with its own type
  parameters. Building both means interface-instance layout work plus monomorphized defaults,
  with an open vtable question (there are no generic slots — such defaults could never be
  overridden). Milestone-sized; the spec documents free adapters as THE form, and an
  `IrPinTests` entry keeps today's refusal visible instead of accidental.
- **THE v3 BASKET AND THE PATCH TRAIN BEFORE IT** (maintainer, 2026-08-22). Everything this
  file has deferred goes into v3.0.0, and OVERLOADING joins it. The order is fixed and the
  reason is that a major should be short: **anything that does not need a major ships FIRST, as
  its own patch** — iterator chaining, the new file-error API, member-`@Deprecated` on interface
  members, the ignored non-interface entries in a `::` list, multiple interface parents. Each of
  those deprecates whatever it replaces in the standard library as it lands, and the removal is
  the major, as at 2.0.
  - **One exception, and it is new for this project**: a form that is replaced ONLY by
    overloading is NOT removed at the major. Overloading arrives WITH v3.0.0, so its
    replacements are the same age as the break; deleting them in the same release would give a
    user no version in which both exist. Those forms carry a KEPT-UNTIL promise through v3.5,
    and the promise is written down at the declaration rather than in a release note.
  - **What must still be decided, before any of it**: `@Deprecated` carries a message and
    nothing else, so a promise "kept until 3.5" has no form. See the next entry.
  - **A design round comes before the major**, overloading included, and it has to answer what
    overloading means for a language whose whole dispatch story is generics plus constraints —
    Rule 2 is at stake, and it is the mechanism Oil died of.

- **`@Deprecated` needs a `until` field before the patch train starts** (open, 2026-08-22). A
  kept-until promise that lives in a release note is a promise nobody can check. The smallest
  form that makes it real: one more field on the existing attribute, and a compiler check that
  a promise whose version has ARRIVED is an error at build time — the ratchet removes the form
  instead of a person remembering to. NOT a second attribute (`@Sunset`, `@Until`): that would
  be a second mechanism for "this is going away".

- **`unsafe` blocks: documented No, with the measurement** (2026-08-22). What an `unsafe` would
  remove — the bounds check in `ldelem`, the panic in `optget` — is a compare and a branch
  INSIDE a dispatch that costs ~23 cycles. It buys about one percent and pays with the property
  the embedding is sold on: `Capability.None` means nothing if a script may read past an array.
  A C-shaped answer to a dispatch-shaped problem.

- **`@Inline` as a language form: documented No** (2026-08-22). The inliner has a budget since
  M14 and `nextFloat` was inlined without being asked; `nextInt` was not, at 53 instructions.
  Forcing it saves ONE call per 53 dispatches — it cannot move a 555 ns measurement. If the
  budget is wrong that is a measurement and a constant, not a keyword, and it would make the
  second compiler-read attribute out of a mechanism that is supposed to describe and do nothing.

- **Heterogeneous operator arithmetic: documented No** (M22 probe). Two facts cap it below
  usefulness: a type conforms to `Mul` ONCE (`Mul<Vec2>` beside `Mul<float>` fails the signature
  check — one `mul`, two wanted signatures), and Lyric has no overloading, so `mul(other: float)`
  beside `mul(other: Vec2)` cannot exist either. A two-parameter `Mul<Rhs, Out>` would break every
  existing conformance and still buy only ONE right-hand type per type — `Vec2 * float` OR
  `Vec2 * Vec2`, never both. Real heterogeneity needs overloading or multi-conformance with
  signature dispatch; that is a v3-class question, not a 2.0 item.

## Last relevant commit

`dap: an exception-breakpoint request is answered, not refused`
(released as v2.7.1 — found by wiring a second editor to the adapter)

---

## How to maintain this file

- After every slice: extend `## Recently finished`, update `## What we are working on`.
- **At most four entries under `## Recently finished`.** The fifth goes — it stands in `git log`.
  This rule existed already; it was ignored for 1088 lines.
- On a milestone change: enter the new milestone at the top.
- Finished points under `## Still open` are to be **deleted**, not struck through.
- **Never** plan new features here.
