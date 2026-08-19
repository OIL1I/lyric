# Formatting

One command, one shape:

```bash
lyric fmt src/
```

Files are rewritten in place; a directory means every `.lyr` under it. There are no style
options — the shape is the tool's contract, so no project ever argues about it. What the
formatter enforces is what this guide has shown all along:

```lyr
import std.io.console { println };

fn main(): int {
    let greeting = "four spaces, spaces around operators, one hundred columns";
    println(greeting);
    return 0;
}
```

## What it keeps

Your comments — all three forms, where you put them, trailing ones trailing. Your blank lines
between statements and declarations, capped at one. Your literal spellings: `0xFF` stays
hexadecimal, `1_000_000` keeps its underscores. A file that does not parse is reported and left
exactly as it was: the formatter never writes a guess over your text.

## What it decides

Line breaks against the 100-column limit: what fits stays on its line, what does not breaks
with one element per line — and a broken list gets a trailing comma exactly where the grammar
allows one. A blank line always follows the module header and separates declarations with
bodies; imports sit together.

Two things it decides that are worth knowing in advance. Parentheses that only restate the
precedence table go away — `(a * b) + c` becomes `a * b + c`, and `(a && b) || c` becomes
`a && b || c`, because `&&` binds tighter; parentheses that change the parse always stay. And a
comment written INSIDE an expression surfaces at the end of its statement: comment placement is
line-level, never lost.

## In an editor and in CI

```bash
lyrfmt --stdin < file.lyr
```

reads one stream and writes the result to stdout — the form a save hook wants. And

```bash
lyric fmt --check .
```

writes nothing, lists every file that would change, and exits nonzero when any would — the form
a CI job wants. This repository holds itself to that: the standard library, the examples and
the templates are formatted, and a test fails when they stop being it.
