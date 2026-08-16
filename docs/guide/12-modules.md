# Modules

A file is a module. The header names it; without a header the name comes from the file name.

```lyr
module app.geometry;

pub struct Point { x: int, y: int, }

pub fn origin(): Point { return Point { x = 0, y = 0 }; }

fn helper(): int { return 1; }
```

`pub` exports. Everything else is visible only inside the module.

## Importing

```lyr
import std.io.console;                     // qualified: console.println(…)
import std.math { sqrt, pi };              // selective
import std.collections as coll;            // alias

fn main(): int {
    console.println(f"{sqrt(4.0)}");
    let list = coll.emptyList<int>();
    return list.length();
}
```

A qualified import binds the last path segment as the name. A selective import binds the listed
names directly. An alias renames the module.

## A program of several files

A module path becomes a file path under the directory of the file being compiled:

```
app.lyr            <- the entry file, and the root
util.lyr           <- import util
shapes/circle.lyr  <- import shapes.circle
```

```text
// util.lyr
module util;

pub fn double(n: int): int { return n * 2; }

// app.lyr
import util { double };

fn main(): int { return double(21); }
```

```bash
lyric run app.lyr
```

The two files above are shown together because neither is a program on its own — which is also why
they are not compiled by the test suite the way every other snippet in this guide is.

Three rules follow from where a module is found:

- **A file must agree with the path it was loaded from.** `util.lyr` declares `module util;` or no
  header at all. A header naming something else is an error, because the path is what the import
  wrote down.
- **`std` belongs to the standard library.** A file at `std/io/console.lyr` beside your program is
  never loaded; the import goes to the standard library. Nothing in your own directory can quietly
  take its place.
- **Only the standard library declares functions without a body.** In your own modules a missing
  body is an error, not an import declaration.

Everything ends up in one `.lyrbc`. There is no separate compilation step per file and no link step.

## Module constants

A module-level `let` is a constant. It is initialized once before `main` runs, in declaration
order.

```lyr
let VERSION = "1.0";
let BANNER = "lyric " + VERSION;

fn main(): int {
    return if (BANNER == "lyric 1.0") 0 else 1;
}
```

An initializer may read a constant declared before it, not one after. There is no module-level
`var`.

## Type aliases

```lyr
type Id = int;

fn describe(id: Id): int {
    let copy: Id = id;
    return copy;
}

fn main(): int { return describe(1); }
```

An alias is a name for a type, not a new type: `Id` and `int` are interchangeable.
