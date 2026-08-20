# Lyric — Current State

> This file is the **only** one in the project that changes often. It is updated
> after every finished slice. Claude reads it at the start of a session to know
> where we stand.
>
> Keep the content short. Anything already committed can go —
> `git log --oneline` is the history, not this file.

---

## Current milestone

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

4322 tests green **in Debug and Release**, bytecode format **3.2**, **ten** binaries
plus `lyrembed.dll`, version **2.0.1**; the specification in `lyriclang/lyric-spec` is
**NORMATIVE**, its suite stands at 78 cases, and the toolchain's own CI runs it against the
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

- [x] **M27 — the deep audit and its patch wave** (2026-08-20, `fix/v2.0.1-audit-wave`,
  PR #65, released as v2.0.1). Probe-first through numerics, control flow, the type system
  and the runtime boundary: 24 new conformance cases, and seven measured bugs fixed as one
  wave — `..=max` ran zero times (inclusive ranges got their own adapter with a done flag),
  small-width and uint ranges got their own carriers (they were malformed IR under the
  verifier, and uint compared signed beyond 2⁶³), defer became the block affair §7.5 always
  claimed (per iteration, break/continue drain what they leave), `let x = null;`/`let xs
  = [];` report instead of crashing, oversized literals stopped reinterpreting, int-to-float
  adaptation is exact, and `throws` on main is refused. Plus the §11 registry ratchet
  (std.build is host-bound, now said) and two diagnosis-QoI fixes. 4322 tests, 78/78.

- [x] **M26 — v2.0.0** (2026-08-20, five slices, `feature/m26-v2`, PR #64). The specification
  turned NORMATIVE and the clocks ran out: Appendix A catalogues all 180 diagnostic codes
  verified against the emission sites; chapter 7 carries the complete narrowing and
  definite-assignment models (the stale-lambda-proof answer: a checked unwrap, LYR-VM0007);
  the EBNF and the bytecode format are canonical in the spec with checked mirrors here; all
  34 `@Deprecated` declarations plus StringBuilder.length went (registry keeps the native
  names — 1.x bytecode loads); SEM0074 warning → error; `Hashable :: [Equatable]`; pub-roots
  prunes libraries from their surface; and the toolchain CI gates every change against the
  suite, with `//! since:` versioning the cases. 4311 tests, 54/54 conformance, docs 389/389.

- [x] **M25 — the spec draft** (2026-08-19, shipped as v1.16.0, substance in
  `lyriclang/lyric-spec`). Twelve chapters plus the conformance suite and its reference
  runner, CI against the pinned release. The suite earned its keep on day one — resume yields
  T and exhaustion panics, Throwable is a builtin, and `catch (e: Throwable)` compiled but
  never caught (fixed in the toolchain); the maintainer's octal catch forced the full
  compiler audit of the draft, which rewrote chapter 1 and corrected six more chapters.
  Decisions recorded: overflow wraps (frozen), pub-roots YES at 2.0.

- [x] **M24 — the freeze prep** (2026-08-19, four slices, `feature/m24-freeze-prep`). The
  three design leftovers settled before the spec freezes semantics: `opaque type` answers
  Erato's A4 (a handle scripts cannot forge, free at runtime), the string API became methods
  with the free forms on the 2.0 deprecation clock, and iterator chaining got its honest No
  with the probe pinned. The build surfaced a latent function-id collision between the global
  initializer and downstream functions — found by the new density check, fixed at the counter.

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

**The deep audit is FINISHED and its patch wave is released as v2.0.1.** The audit walked
numerics, control flow, the type system's rest and the runtime boundary probe-first — spec
sentence and conformance case before every fix — and everything it caught shipped as one
wave. The conformance gate earned its keep on day one: it caught a case pinned to a
diagnosis the wave itself had (rightly) removed, which a stale local build had let through.
The next milestone is the maintainer's call.

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

**Noticed repeatedly while testing, unresolved**: individual process-spawning tests fail
sporadically under full-suite load and never in isolation — first an LSP test (254/254 green
alone, repeatedly), during M20 once `ProtocolTests.A_panic_looks_the_same_through_a_foreign_runtime`
in Debug (3/3 green alone, full Cli rerun green). The pattern is load, not logic. Worth a look
before it becomes a CI lottery.

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
- **Heterogeneous operator arithmetic: documented No** (M22 probe). Two facts cap it below
  usefulness: a type conforms to `Mul` ONCE (`Mul<Vec2>` beside `Mul<float>` fails the signature
  check — one `mul`, two wanted signatures), and Lyric has no overloading, so `mul(other: float)`
  beside `mul(other: Vec2)` cannot exist either. A two-parameter `Mul<Rhs, Out>` would break every
  existing conformance and still buy only ONE right-hand type per type — `Vec2 * float` OR
  `Vec2 * Vec2`, never both. Real heterogeneity needs overloading or multi-conformance with
  signature dispatch; that is a v3-class question, not a 2.0 item.

## Last relevant commit

`sema, ir, stdlib: the audit's patch wave — seven bugs, one release`
(closes M27; released as v2.0.1 — the audit's first harvest)

---

## How to maintain this file

- After every slice: extend `## Recently finished`, update `## What we are working on`.
- **At most four entries under `## Recently finished`.** The fifth goes — it stands in `git log`.
  This rule existed already; it was ignored for 1088 lines.
- On a milestone change: enter the new milestone at the top.
- Finished points under `## Still open` are to be **deleted**, not struck through.
- **Never** plan new features here.
