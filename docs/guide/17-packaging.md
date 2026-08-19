# Packing a program

A Lyric program normally travels as a `.lyrbc` module and runs wherever a `lyrvm` is. Packing
removes that condition: one command wraps your program and the runtime into a single executable
that runs on a machine that has never heard of Lyric.

```bash
lyric pack app.lyr
./app arg1 arg2
```

That is the whole workflow. `lyric pack` compiles the file and hands the module to `lyrpack`,
which copies a prebuilt runtime — the *stub* — and appends the module to it. No compiler ships
inside the result, nothing is installed on the target machine, and packing takes about as long
as copying the file, because that is what it is.

An already compiled module packs directly, which is how a `build.lyr` project packs its
artifact:

```bash
lyric build
lyric pack out/app.lyrbc -o dist/app
```

## What a packed program is

It is your program, owning the whole command line. Every argument goes to `main` unchanged —
there is no `--` protocol and no wrapper option, not even `--help`. If your program wants a
help text, it prints one:

```lyr
import std.io.console;

fn main(args: string[]): int {
    if (args.length == 0) {
        console.println("usage: app <name>");
        return 2;
    }

    for (name in args) {
        console.println("hello, " + name);
    }
    return 0;
}
```

The exit code is `main`'s return value, a panic exits with 101 and prints its backtrace to
stderr, and the source map travels inside the module, so the backtrace names your lines — the
program behaves exactly as it did under `lyric run`, because it is the same runtime executing
the same bytes.

A packed program runs with **every capability**, like any standalone program. The sandbox
belongs to hosts that embed the runtime; a program someone ships as an executable answers to
the operating system, the way every executable does.

## What to know before shipping

- **One platform per file.** The stub is a native executable, so a pack is for one platform.
  The toolchain archive carries stubs under `stubs/<rid>/`, and `--stub` points at any of them
  explicitly.
- **Size.** The stub contains the whole runtime, so even `return 0;` is megabytes. The program
  on top costs what the module costs — usually kilobytes.
- **macOS cannot run packed programs yet.** A Mach-O declares its own size, appended bytes put
  the file beyond it, and `codesign` refuses to bless the result — so the packed file is killed
  on start and cannot be re-signed either. Pack FOR macOS is planned (the payload has to become
  a real Mach-O segment); pack ON macOS for the other platforms works today, with their stubs
  and `--stub`.
- **Packing is not verification.** `lyrpack` copies bytes and does not look inside the module;
  a broken module packs fine and reports at first start. `lyrvm verify` answers ahead of time.

The byte-level layout — the footer, its versioning, and what the stub does in which failure —
is specified in [Pack.md](../Pack.md).
