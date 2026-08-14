# Contributing to Lyric

This is currently a solo project. The rules below are the maintainer's
self-binding contract to avoid the failure modes of a previous language
project ("Oil"), where scope creep and parallel-mechanism creep killed
forward progress.

Even when read by future contributors, the rules below are non-negotiable
until v1.0 ships.

---

## The Three Rules

### Rule 1 — No post-v1 roadmap document

Until v1.0 is released, the file `POST-V1-ROADMAP.md` **does not exist**.

Ideas for post-v1 features go into GitHub issues with the `idea` label —
still not a plan, just a structured discussion.

Reason: Oil grew a 2761-line post-v1 roadmap that absorbed all design
energy and prevented v1.0 from ever shipping. We will not repeat that.

### Rule 2 — One mechanism per concept

Each language concept has exactly one mechanism in the language:

| Concept | Single mechanism |
|---|---|
| Error handling | Typed exceptions with `throws` |
| Cleanup on scope exit | `defer` only (no `finally`) |
| Memory management | GC only (no manual/borrow/refcount) |
| Polymorphism | Interfaces with default methods + `::` declaration |
| Concurrency | Single-threaded + coroutines (no native threads) |
| FFI / host integration | Host-controlled bindings + capability gating |

Adding a parallel mechanism for any of these (e.g. `Result<T, E>` alongside
exceptions, `finally` alongside `defer`) is a breaking design change and
requires a written ADR plus 30 days of consideration.

### Rule 3 — Every milestone ships something

A milestone is not done until:

1. Its exit criteria, as recorded in [`STATUS.md`](STATUS.md), are met.
2. A git tag exists.
3. Someone could clone the repo, follow the README, and *do something* with
   it — even if that something is small (e.g. tokenize a file).

No milestone may be marked done by intent alone. There must be an
artifact.

---

## How to add a language feature before v1.0

Don't, unless it's already in [`docs/Grammar.md`](docs/Grammar.md).

If you really must add something not currently in v1:

1. Open a GitHub issue using the **Feature Idea** template. Fill in all
   required fields. "It would be nice" is not a problem statement.
2. Wait at least **7 days** before opening a PR. The waiting period is
   mandatory even for the maintainer. If after 7 days you still consider
   it essential, proceed.
3. The PR must include:
   - A change to `docs/Grammar.md` reflecting the new feature.
   - A change to `docs/guide/` with a user-facing explanation and example.
   - Tests covering the new behavior.

If the change would push v1.0 by more than 4 weeks, it is rejected by
default and goes to an `idea` issue for post-v1.

---

## How to fix a bug

Bugs do not need the 7-day wait. The standard flow:

1. Add a failing test that demonstrates the bug.
2. Make the test pass.
3. Open a PR. Include the issue number if there is one.

---

## Scope check ritual

On the first Sunday of each month, do a **scope check**:

1. Read the current milestone and its estimate in [`STATUS.md`](STATUS.md).
2. Compare the actual elapsed time with the estimate.
3. If you are >50% over the estimate: honestly evaluate which features can
   be cut.
4. If you are >100% over: re-cut the milestone and record the change in
   `STATUS.md`.

This is the **only** legitimate place for plan adjustment. Plan changes
made on impulse (e.g. "I just thought of something better") are forbidden.

---

## Code style

| Topic | Convention |
|---|---|
| Naming (C# code) | Standard .NET: `PascalCase` types/methods, `_camelCase` private fields, `camelCase` parameters |
| Naming (Lyric stdlib code in `.lyr`) | `PascalCase` types, `camelCase` everything else (see `docs/Grammar.md`) |
| Indentation | 4 spaces, no tabs |
| Line length | Soft 100, hard 120 |
| Trailing commas | Allowed in multi-line lists/blocks |
| `var` (C#) | Prefer when type is obvious from RHS; use explicit type otherwise |
| Comments | English, describing the technique and the logic. No justifications, no project history, no milestone or decision references. |

No formatter is enforced in v1 (a `lyric fmt` tool may come post-v1).
Follow these rules manually.

---

## Testing

Each subsystem has its own test project:

- `tests/Lyric.Tests.Core/` — `SourceManager`, `DiagnosticEngine`, `Span`
- `tests/Lyric.Tests.Lexing/` — tokenizer
- `tests/Lyric.Tests.Parsing/` — AST construction
- `tests/Lyric.Tests.Resolver/` — name resolution
- `tests/Lyric.Tests.Sema/` — type checking
- `tests/Lyric.Tests.Ir/` — AST to IR lowering
- `tests/Lyric.Tests.Bytecode/` — the `.lyrbc` format, writer and reader
- `tests/Lyric.Tests.Vm/` — bytecode execution
- `tests/Lyric.Tests.Embedding/` — the host API
- `tests/Lyric.Tests.Cli/` — the binaries end to end, plus the examples

Tests use xUnit. Golden tests compare against snapshot files in
`tests/<project>/golden/`; set `LYRIC_UPDATE_SNAPSHOTS=1` to rewrite them.

Before committing: `dotnet test` must pass.

---

## Commits

Conventional-ish, but loose. Format:

```
<area>: <short imperative description>

[optional body explaining why, not what]
```

Examples:

```
lexer: handle nested block comments
sema: detect non-exhaustive match with missing variants
docs: clarify defer ordering with multiple defers
```

The area is the subsystem the change lands in.

---

## Releases

Tags follow `vMAJOR.MINOR.PATCH` semver, with all three components written
out from v1.0 on. Every release has an **annotated tag**; its message is the
release note: what the version delivers, and what it cannot do yet.

Two channels, both described in the [README](README.md#releases):

- **stable** — pushing an annotated `vX.Y.Z` tag runs `.github/workflows/release.yml`,
  which verifies on Linux and Windows, packages `win-x64`, `linux-x64` and
  `osx-arm64`, and publishes the archives as a GitHub release.
- **nightly** — `.github/workflows/nightly.yml` builds `main` once a day and
  replaces the `nightly` prerelease. No compatibility promise.

**The changelog starts at `v1.0.0`.** A changelog answers "what changed for me
since last time", and that question presupposes something to be compatible with.
Pre-1.0 there was no such promise, neither for the `.lyrbc` format nor for the
language itself, so those releases carry their notes in their annotated tags and
appear in no entry.

From `v1.0.0` on, every release has three things: the tag, a GitHub release page,
and an entry in [`CHANGELOG.md`](CHANGELOG.md). An entry lists what changed for
someone USING the toolchain — the language, the standard library, the bytecode
format, the command line, the embedding API. Compiler internals stay in `git log`.

---

## Questions

Open a GitHub discussion, not an issue. Issues are for actionable work.
