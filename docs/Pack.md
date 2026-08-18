# The Pack Format

How a Lyric program becomes one executable file. This document is the contract between
`lyrpack`, which writes packed executables, and `lyrstub`, which is one. The module inside is an
ordinary `.lyrbc` and is specified in [`Bytecode.md`](Bytecode.md); nothing here reaches into it.

## Layout

A packed executable is three regions, in order:

```
┌────────────────────────────┐
│ stub executable            │  the platform's lyrstub, byte for byte
├────────────────────────────┤
│ payload                    │  one .lyrbc module, byte for byte
├────────────────────────────┤
│ footer                     │  24 bytes, see below
└────────────────────────────┘
```

The stub is a self-contained single-file build of `lyrstub` for one platform; the toolchain
archive carries it under `stubs/<rid>/`. Nothing is patched into it: packing is a byte copy with
two appends, which is why it needs no linker, no code signing step and no knowledge of the
executable format it wraps. The operating system loads the file as the executable it begins
with; the trailing bytes are cargo it never looks at.

## The footer

Fixed 24 bytes at the very end of the file, little-endian:

| Offset from end | Size | Field | Content |
|---|---|---|---|
| −24 | u32 | version | footer layout version, currently **1** |
| −20 | u32 | reserved | written as 0, **ignored on read** |
| −16 | u64 | length | payload length in bytes, > 0 |
| −8 | 8 bytes | magic | ASCII `LYRPACK1` |

The footer sits at the end so the stub needs no knowledge of its own size: read the last 24
bytes, check the magic, and the payload lies directly before the footer, `length` bytes long.

The version covers the footer LAYOUT alone. The payload's format version stands in the `.lyrbc`
header and is judged by the runtime inside the stub, with the same rules any runtime applies.
The reserved field is written as zero and ignored on read, so a future minor can use it without
breaking a deployed stub.

Three answers when the stub inspects its own file, and they are deliberately distinct:

- **No magic** — the stub is empty: it explains itself and exits with the usage code. A
  truncated pack also lands here, because truncation eats the footer; the message allows for
  both.
- **Magic, but a version this reader does not know, a zero length, or a length reaching outside
  the file** — the pack is damaged: reported as such, nothing is executed.
- **A payload with sound bounds** — it is read and handed to the runtime. Whether it is a VALID
  module is the runtime's question, answered with the same diagnostics `lyrvm run` gives.

## What a packed program is

- **It owns its command line.** Every argument goes to `main(args: string[])` unchanged — no
  `--` protocol, no stub options, not even `--help`. A shipped program that could be talked out
  of running by a flag its author never defined would be the wrong surprise.
- **It runs with every capability**, exactly as `lyrvm run` without `--grant` does: packing is
  the standalone mode in one file. Narrowing a packed program would be a packing-time decision
  recorded in the footer — a future minor, not a runtime flag an end user could edit away.
- **Its exit codes are the runner contract** (`Bytecode.md` §8): `main`'s return value, 101 on
  a panic, 1 when the module fails to load, 2 for the empty stub.
- **stdout carries program output only**; everything the stub or runtime has to say goes to
  stderr.

## What packing is not

- **Not compilation.** `lyrpack` takes a `.lyrbc` and does not look inside it beyond the
  extension; a file of garbage packs successfully and fails at first start, with the loader's
  diagnostics. `lyrvm verify` answers the question ahead of time. `lyric pack app.lyr` compiles
  and packs in one step.
- **Not linking.** The module travels whole, the stub travels whole. There is no dead-code step
  beyond what the compiler already did, and two packed programs share nothing.
- **Not signing.** Appending bytes invalidates an existing code signature on platforms that
  have one; macOS in particular re-verifies. Re-signing after the pack is the shipper's step —
  `codesign --force --sign - <file>` for the ad-hoc case.

## Failure duties

`lyrpack` refuses to overwrite either of its inputs, and a pack that fails midway deletes its
half-written output: a file that starts and then reports itself empty or damaged must not be
what a failed build leaves behind.
