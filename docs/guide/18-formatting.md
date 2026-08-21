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

An operator chain counts as such a list. A condition that runs past the limit breaks before
every operator of the same precedence, indented one step:

```lyr
fn playable(width: int, height: int, tiles: int): bool {
    return width > 0
        && height > 0
        && tiles > 0
        && width * height >= tiles
        && width * height - tiles < width;
}
```

Two things about that shape. The operator leads its line rather than trailing the one before it,
so the eye finds what joins two operands without reading to the end of a line first. And the
whole level breaks or none of it does: `a && b && c` is one decision, not two nested ones. A
tighter level inside stays on its line while the looser one around it breaks — the comparisons
above are untouched by the `&&` chain breaking.

The one exception is an operand that breaks by itself. A `match` or `if` expression lays itself
out over several lines whatever the width says, and a chain containing one stays as it is rather
than breaking for a reason that has nothing to do with the width:

```lyr
enum Rarity {
    Common,
    Rare,
}

fn price(base: int, rarity: Rarity): int {
    return base * match (rarity) {
        Common => 1,
        Rare => 3,
    };
}
```

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
