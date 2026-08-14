# Getting started

A Lyric program is a file with the extension `.lyr`. Execution begins at `main`.

```lyr
import std.io.console { println };

fn main(): int {
    println("Hello, Lyric!");
    return 0;
}
```

Run it:

```bash
lyric run hello.lyr
```

The return value of `main` becomes the process exit code, masked with `& 0xFF`.

## The tools

| Command | Purpose |
|---|---|
| `lyric run <file>` | compile and execute |
| `lyric check <file>` | compile without writing a file |
| `lyric build <file> -o <out>` | compile to `.lyrbc` |
| `lyric disasm <file>` | print the bytecode |
| `lyric repl` | interactive prompt |

`lyric check` stops after semantic analysis. A program it accepts can still be rejected by the
code generator for a construct that is not lowered yet; `lyric run` is the full pipeline.

## Arguments

To receive command-line arguments, declare `main` with one parameter of type `string[]`:

```lyr
import std.io.console { println };

fn main(args: string[]): int {
    println(f"got {args.length} argument(s)");
    return 0;
}
```

Everything after `--` on the command line belongs to the program:

```bash
lyric run wc.lyr -- README.md
```
