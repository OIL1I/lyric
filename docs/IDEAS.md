# Lyric — Idea Pile

> This file is a raw, unstructured pile of ideas. It is **not** a plan, **not**
> a roadmap, and **not** a commitment.
>
> Rules:
>
> - One bullet per idea. Maximum two sentences.
> - No dates. No priorities. No version targets ("v1.X" not allowed here).
> - When an idea is considered seriously three times, it graduates to a GitHub
>   issue with the `idea` label.
> - **Nothing** in this file ever migrates directly to `ROADMAP.md`. It must
>   pass through the GitHub-issue stage and an explicit design discussion
>   first.
> - This file may grow indefinitely. That is fine. Growth here is not progress
>   and not pressure.
>
> If you find yourself adding structure (headings beyond the categories below,
> tables, priority labels), stop. That structure belongs in an issue, not here.

---

## Language features

<!-- Add bullets here as ideas surface. Examples below; delete once real ones arrive. -->

- _(example, delete me)_ `if-let` shorthand for matching a single pattern: `if let Some(x) = maybe { ... }`
- _(example, delete me)_ Trailing-closure syntax à la Swift: `xs.map { x => x + 1 }`

## Standard library

- _(example, delete me)_ `std.json` parser and serializer
- _(example, delete me)_ `std.datetime` with timezone-aware datetimes

## Tooling

- _(example, delete me)_ LSP server with diagnostics streaming and go-to-definition
- _(example, delete me)_ `lyric fmt` formatter, one-style, non-configurable

## VM / runtime

- _(example, delete me)_ JIT backend via Cranelift for hot loops
- _(example, delete me)_ Generational GC instead of relying on .NET GC

## Wild ideas

<!-- Things that are obviously post-2.0 or speculative. Park them here and forget. -->

- _(example, delete me)_ WebAssembly runtime backend
- _(example, delete me)_ Source-to-source transpilation to C# for AOT
